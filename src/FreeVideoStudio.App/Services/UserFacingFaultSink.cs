using System;
using System.Collections.Generic;
using Avalonia.Threading;
using FreeVideoStudio.App.Abstractions;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.Core.Abstractions;

namespace FreeVideoStudio.App.Services;

public sealed class UserFacingFaultSink : IFaultSink
{
    private const int DegradedRepeatWindowSeconds = 8;

    private const int FatalRepeatWindowSeconds = 60;

    private readonly IUserNotifier _notifier;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _lastShown = new(StringComparer.Ordinal);

    public UserFacingFaultSink(IUserNotifier notifier)
        => _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));

    public void Report(Fault fault)
    {
        try
        {
            ReportCore(fault);
        }
        catch (Exception ex)
        {
            RuntimeLog.EmergencyWrite("FAULT SINK", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void ReportCore(Fault fault)
    {
        string detail = string.IsNullOrWhiteSpace(fault.TechnicalDetail)
            ? fault.Exception?.Message ?? "(no detail)"
            : fault.TechnicalDetail!;

        switch (fault.Tier)
        {
            case FaultTier.Recoverable:
                FaultCounters.Record(fault);
                if (RuntimeLog.TryPassThrottle("REC|" + fault.Area + "|" + CallSiteKey(detail), out int held))
                {
                    RuntimeLog.Info(fault.Area, held > 0
                        ? $"[RECOVERABLE] {detail} (+{held} from this site in the last 30s)"
                        : $"[RECOVERABLE] {detail}");
                }
                if (fault.Exception is not null)
                    RuntimeLog.Debug(fault.Area, fault.Exception.ToString());
                return;

            case FaultTier.Degraded:
                FaultCounters.Record(fault);
                RuntimeLog.Fail(fault.Area, $"[DEGRADED] {fault.UserMessage} :: {detail}");
                if (fault.Exception is not null)
                    RuntimeLog.Debug(fault.Area, fault.Exception.ToString());

                if (ShouldSurface(fault, DegradedRepeatWindowSeconds))
                    _notifier.Notify(fault.UserMessage, NoticeKind.Warning);
                return;

            case FaultTier.Fatal:
                FaultCounters.Record(fault);
                RuntimeLog.Fail(fault.Area, $"[FATAL] {fault.UserMessage} :: {detail}");
                if (fault.Exception is not null)
                    RuntimeLog.Fail(fault.Area, fault.Exception);

                if (ShouldSurface(fault, FatalRepeatWindowSeconds))
                    _notifier.Alert(TitleFor(fault.Area), fault.UserMessage);
                return;

            default:
                RuntimeLog.Fail(fault.Area, $"[UNCLASSIFIED tier={fault.Tier}] {fault.UserMessage} :: {detail}");
                _notifier.Notify(fault.UserMessage, NoticeKind.Warning);
                return;
        }
    }

    private static string CallSiteKey(string detail)
    {
        int dash = detail.IndexOf(" — ", StringComparison.Ordinal);
        if (dash > 0) return detail[..dash];
        return detail.Length > 48 ? detail[..48] : detail;
    }

    private bool ShouldSurface(Fault fault, int windowSeconds)
    {
        if (string.IsNullOrWhiteSpace(fault.UserMessage)) return false;

        string key = $"{(int)fault.Tier}\u0000{fault.Area}\u0000{fault.UserMessage}";
        DateTime now = DateTime.UtcNow;

        lock (_gate)
        {
            if (_lastShown.TryGetValue(key, out DateTime previous)
                && (now - previous).TotalSeconds < windowSeconds)
            {
                return false;
            }

            _lastShown[key] = now;

            if (_lastShown.Count > 256)
            {
                DateTime cutoff = now.AddSeconds(-Math.Max(DegradedRepeatWindowSeconds, FatalRepeatWindowSeconds));
                List<string> stale = new();
                foreach (KeyValuePair<string, DateTime> entry in _lastShown)
                    if (entry.Value < cutoff) stale.Add(entry.Key);
                foreach (string s in stale) _lastShown.Remove(s);

                if (_lastShown.Count > 256) _lastShown.Clear();
            }

            return true;
        }
    }

    private static string TitleFor(string area) => area switch
    {
        "EXPORT"  => "The export could not continue",
        "PROJECT" => "Your project could not be saved",
        "UPDATE"  => "The update could not be installed",
        "AUDIO"   => "The audio step could not continue",
        _         => "Something went wrong",
    };
}

public sealed class AvaloniaUserNotifier : IUserNotifier
{
    private readonly IActiveWindowProvider _windows;

    public AvaloniaUserNotifier(IActiveWindowProvider windows)
        => _windows = windows ?? throw new ArgumentNullException(nameof(windows));

    public void Notify(string text, NoticeKind kind = NoticeKind.Info)
    {
        var window = _windows.ActiveWindow;
        if (window is null)
        {
            RuntimeLog.Info("NOTICE", $"(no window) {text}");
            return;
        }

        FloatingNotice.Show(window, text, kind);
    }

    public void Alert(string title, string message)
    {
        string body = message + Environment.NewLine + Environment.NewLine + $"Log: {RuntimeLog.LogPath}";

        if (Dispatcher.UIThread.CheckAccess())
        {
            NativeDialog.ShowError(body, title);
            return;
        }

        Dispatcher.UIThread.Post(() => NativeDialog.ShowError(body, title));
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText)
    {
        var window = _windows.ActiveWindow;
        if (window is null)
        {
            return await Dispatcher.UIThread.InvokeAsync(() => NativeDialog.ShowQuestion(message, title));
        }

        return await ConfirmDialogWindow.AskAsync(window, message, title, confirmText, cancelText);
    }

    public async Task<ConfirmDialogWindow.SaveChangesChoice> AskSaveChangesAsync(string title, string message)
    {
        var window = _windows.ActiveWindow;
        if (window is null)
        {
            bool save = await Dispatcher.UIThread.InvokeAsync(() =>
                NativeDialog.ShowQuestion(message + Environment.NewLine + Environment.NewLine + "Yes saves your work. No keeps the editor open.", title));
            return save ? ConfirmDialogWindow.SaveChangesChoice.Save : ConfirmDialogWindow.SaveChangesChoice.BackToEditing;
        }

        return await ConfirmDialogWindow.AskSaveChangesWithMessageAsync(window, title, message);
    }
}
