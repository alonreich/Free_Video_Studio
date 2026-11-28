using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FreeVideoStudio.App.Infrastructure;

public static class HudChoiceMemory
{
    public const string Tag = "HUDREMEMBER_01";

    internal static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(800);

    private static readonly object Gate = new();
    private static readonly Dictionary<(string Profile, string Key), bool> Pending = new();
    private static Timer? _timer;

    public static void Record(string profile, string key, bool on)
    {
        if (string.IsNullOrWhiteSpace(profile) || string.IsNullOrWhiteSpace(key)) return;
        lock (Gate)
        {
            Pending[(profile, key)] = on;
            _timer ??= new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    public static Task FlushAsync() => Task.Run(Flush);

    private static void Flush()
    {
        List<KeyValuePair<(string Profile, string Key), bool>> batch;
        lock (Gate)
        {
            if (Pending.Count == 0) return;
            batch = new List<KeyValuePair<(string Profile, string Key), bool>>(Pending);
            Pending.Clear();
        }
        try
        {
            bool ok = SettingsManager.Update(s =>
            {
                foreach (var pair in batch) s.Defaults.RememberHudElement(pair.Key.Profile, pair.Key.Key, pair.Value);
            });
            if (!ok) RuntimeLog.Info("UI", $"{Tag}: the last HUD switch choices could not be remembered this time.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        {
            RuntimeLog.Fail("UI", $"{Tag}: remembering HUD switch choices failed ({ex.Message}).");
        }
    }
}
