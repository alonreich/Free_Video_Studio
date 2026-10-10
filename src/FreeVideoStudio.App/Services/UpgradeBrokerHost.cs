// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using FreeVideoStudio.App.ViewModels;

namespace FreeVideoStudio.App.Services;

/// <summary>
/// UPGRADEUX_01 — runs the upgrade broker (<c>--upgrade-broker</c>) WITH A WINDOW.
///
/// <para>══════════════════════════════════════════════════════════════════════════════════════
/// WHAT WAS WRONG. <see cref="UpgradeCoordinator.RunAsync"/> ran in a process started with
/// <c>CreateNoWindow</c> and returned before Avalonia was ever initialised. The old
/// <c>DeploymentProgressWindow</c> was only reachable through the retired
/// <c>--install-worker</c> path, which nothing launches any more. So every install and every
/// update — the moment the app closed — went dark: a Windows permission prompt out of nowhere,
/// then a minute or more of nothing while hundreds of MB were unpacked, copied and hashed.
/// ══════════════════════════════════════════════════════════════════════════════════════</para>
///
/// <para>The window is display-only. The coordinator's protocol, journals, rollback and order are
/// unchanged; this host only gives them a <see cref="IUpgradeProgress"/> that draws. If the UI
/// libraries cannot be loaded (for example the Skia natives are missing beside the staged copy),
/// the broker still runs, headless, with native message boxes (<see cref="HeadlessUpgradeProgress"/>).
/// It runs BEFORE any settings are loaded and must never load them: user data may be mid-migration.</para>
/// </summary>
internal static class UpgradeBrokerHost
{
    private static string[] _args = [];
    private static Task<int>? _work;
    private static WindowProgress? _progress;

    /// <summary>True in the broker process. <c>AvaloniaApp</c> then skips settings, theme and sounds.</summary>
    public static bool IsActive { get; private set; }

    public static async Task<int> RunAsync(string[] args)
    {
        _args = args;
        IsActive = true;

        // UPGRADEUX_06 — started straight from the downloaded installer, the broker finds its two
        // window libraries in the staging folder the launcher prepared (validated: %TEMP%\FVS_Upgrade).
        string? stage = UpgradeCoordinator.StageFromArgs(args);
        if (stage != null && !NativeHelpers.SetDllDirectory(stage))
            RuntimeLog.Info("Upgrade window", "Could not point the window libraries at the staging folder.");

        try
        {
            // Must run before any await: Avalonia starts on the process's main thread.
            int code = AppBuilder.Configure<AvaloniaApp>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace(Avalonia.Logging.LogEventLevel.Warning)
                .StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
            if (_work != null) return code;
            RuntimeLog.Fail("Upgrade window", "The window host ended before the update started; continuing without a window.");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("Upgrade window", "The update window could not be shown; continuing without it: " + ex);
            if (_work != null)
            {
                _progress?.FallBackToNative();
                return await _work.ConfigureAwait(false);
            }
        }
        return await UpgradeCoordinator.RunAsync(args, HeadlessUpgradeProgress.Instance).ConfigureAwait(false);
    }

    /// <summary>Called from <c>AvaloniaApp.OnFrameworkInitializationCompleted</c> in the broker process.</summary>
    public static void Attach(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var vm = new UpgradeProgressViewModel();
        var window = new UpgradeProgressWindow { DataContext = vm };
        var progress = new WindowProgress(vm, window);
        _progress = progress;

        _work = Task.Run(() => UpgradeCoordinator.RunAsync(_args, progress));
        _ = _work.ContinueWith(async finished =>
        {
            int code = 1;
            if (finished.IsCompletedSuccessfully) code = finished.Result;
            else
            {
                RuntimeLog.Fail("Upgrade", finished.Exception?.ToString() ?? "The update stopped unexpectedly.");
                // No failure is silent, even one the coordinator could not report itself.
                if (!progress.Finished)
                    await progress.FailedAsync("The update stopped unexpectedly",
                        "Something went wrong before the update could start, so nothing was changed. "
                      + "Please try again. If it keeps happening, restart your computer first.").ConfigureAwait(false);
            }

            // A success stays on screen long enough to be read; the new app is already opening.
            // (Read from the progress object, not the view-model: the UI post may not have run yet.)
            if (progress.SucceededShown) await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                window.AllowClose = true;
                try { window.Close(); }
                catch (Exception ex) { RuntimeLog.Swallowed(ex); }
                desktop.Shutdown(code);
            });
        }, TaskScheduler.Default);
    }

    /// <summary>The window-backed <see cref="IUpgradeProgress"/>. Every call marshals to the UI thread.</summary>
    private sealed class WindowProgress : IUpgradeProgress
    {
        private readonly UpgradeProgressViewModel _vm;
        private readonly UpgradeProgressWindow _window;
        private volatile bool _native, _succeeded, _finished;

        /// <summary>True once <see cref="Succeeded"/> was called (set before the UI catches up).</summary>
        public bool SucceededShown => _succeeded;

        /// <summary>True once a final success or failure has been shown.</summary>
        public bool Finished => _finished;
        private TaskCompletionSource<RunningAppChoice>? _answer;
        private TaskCompletionSource? _dismissed;

        public WindowProgress(UpgradeProgressViewModel vm, UpgradeProgressWindow window)
        {
            _vm = vm;
            _window = window;
            vm.Answered += choice =>
            {
                vm.StopAsking();
                _answer?.TrySetResult(choice == UpgradeProgressViewModel.RunningAppChoiceValue.Continue
                    ? RunningAppChoice.CloseAndContinue : RunningAppChoice.Cancel);
            };
            vm.CloseRequested += () => _dismissed?.TrySetResult();
        }

        /// <summary>The UI died mid-update: answer anything still pending with native dialogs.</summary>
        public void FallBackToNative()
        {
            _native = true;
            if (_answer is { Task.IsCompleted: false } pending)
                _ = HeadlessUpgradeProgress.Instance.AskToCloseRunningAppAsync(1, false)
                    .ContinueWith(t => pending.TrySetResult(t.Result), TaskScheduler.Default);
            _dismissed?.TrySetResult();
        }

        private void Ui(Action action)
        {
            if (_native) return;
            Dispatcher.UIThread.Post(() =>
            {
                try { action(); }
                catch (Exception ex) { RuntimeLog.Swallowed(ex); }
            });
        }

        public void Begin(UpgradeKind kind, bool waitsForApp)
            => Ui(() => _vm.SetSteps(kind, waitsForApp));

        public void Show() => Ui(() =>
        {
            if (!_window.IsVisible) _window.Show();
            _window.Activate();
        });

        public void Step(UpgradeStep step, string? detail = null, double? fraction = null)
            => Ui(() => _vm.MoveTo(step, detail, fraction));

        public Task<RunningAppChoice> AskToCloseRunningAppAsync(int openHere, bool openInOtherAccount)
        {
            if (_native) return HeadlessUpgradeProgress.Instance.AskToCloseRunningAppAsync(openHere, openInOtherAccount);
            _answer = new TaskCompletionSource<RunningAppChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
            Ui(() =>
            {
                _vm.Ask(UpgradeText.RunningAppQuestion(openHere, openInOtherAccount), canClose: openHere > 0);
                if (!_window.IsVisible) _window.Show();
                _window.Activate();
            });
            return _answer.Task;
        }

        public void Succeeded(string message)
        {
            _succeeded = true;
            _finished = true;
            Ui(() => _vm.Succeed(message));
        }

        public Task FailedAsync(string title, string message)
        {
            _finished = true;
            if (_native) return HeadlessUpgradeProgress.Instance.FailedAsync(title, message);
            _dismissed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Ui(() =>
            {
                _vm.Fail(title, message);
                if (!_window.IsVisible) _window.Show();
                _window.Activate();
            });
            return _dismissed.Task;
        }
    }
}
