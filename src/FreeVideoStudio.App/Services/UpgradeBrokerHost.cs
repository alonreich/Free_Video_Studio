using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using FreeVideoStudio.App.ViewModels;

namespace FreeVideoStudio.App.Services;

internal static class UpgradeBrokerHost
{
    private static string[] _args = [];
    private static Task<int>? _work;
    private static WindowProgress? _progress;

    public static bool IsActive { get; private set; }

    public static async Task<int> RunAsync(string[] args)
    {
        _args = args;
        IsActive = true;

        string? stage = UpgradeCoordinator.StageFromArgs(args);
        if (stage != null && !NativeHelpers.SetDllDirectory(stage))
            RuntimeLog.Info("Upgrade window", "Could not point the window libraries at the staging folder.");

        try
        {
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
                if (!progress.Finished)
                    await progress.FailedAsync("The update stopped unexpectedly",
                        "Something went wrong before the update could start, so nothing was changed. "
                      + "Please try again. If it keeps happening, restart your computer first.").ConfigureAwait(false);
            }

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

    private sealed class WindowProgress : IUpgradeProgress
    {
        private readonly UpgradeProgressViewModel _vm;
        private readonly UpgradeProgressWindow _window;
        private volatile bool _native, _succeeded, _finished;

        public bool SucceededShown => _succeeded;

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
