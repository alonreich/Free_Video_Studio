using System;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Infrastructure;

public static class MasterVolumeUi
{
    public static readonly Geometry SpeakerOn =
        Geometry.Parse("M3,7 L6,7 L10,3 L10,13 L6,9 L3,9 Z M13,5 A4,4 0 0,1 13,11 M16,2 A8,8 0 0,1 16,14");
    public static readonly Geometry SpeakerMuted =
        Geometry.Parse("M3,7 L6,7 L10,3 L10,13 L6,9 L3,9 Z M12,5 L16,13 M16,5 L12,13");

    public static void Bind(Window owner, Slider? slider, TextBlock? badge, Avalonia.Controls.Shapes.Path? icon, Button? speakerButton,
        Action applyPlayers, Action? onUserTouch = null)
    {
        bool syncing = false;

        void Paint()
        {
            int level = MpvIpcClient.GlobalMasterVolume;
            bool muted = MpvIpcClient.GlobalMuted;
            if (slider != null && Math.Abs(slider.Value - level) > 0.5)
            {
                syncing = true;
                try { slider.Value = level; }
                finally { syncing = false; }
            }
            if (badge != null) badge.Text = muted ? "MUTE" : $"{level}%";
            if (icon != null) icon.Data = muted || level == 0 ? SpeakerMuted : SpeakerOn;
            if (slider != null) slider.Opacity = muted ? 0.55 : 1.0;
        }

        void OnMasterChanged(int _)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => OnMasterChanged(0));
                return;
            }
            Paint();
            try { applyPlayers(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        }

        if (slider != null)
        {
            slider.PropertyChanged += (_, e) =>
            {
                if (e.Property != Avalonia.Controls.Primitives.RangeBase.ValueProperty || syncing) return;
                int vol = (int)Math.Round(slider.Value);
                if (MpvIpcClient.GlobalMuted && vol > 0) MpvIpcClient.SetGlobalMuted(false);
                MpvIpcClient.SetGlobalMasterVolume(vol);
            };
            if (onUserTouch != null)
            {
                slider.PointerPressed += (_, _) => onUserTouch();
                slider.PointerWheelChanged += (_, _) => onUserTouch();
            }
        }

        if (speakerButton != null)
        {
            speakerButton.Click += (_, _) =>
            {
                onUserTouch?.Invoke();
                ToggleMute();
            };
        }

        MpvIpcClient.GlobalMasterVolumeChanged += OnMasterChanged;
        owner.Closed += (_, _) => MpvIpcClient.GlobalMasterVolumeChanged -= OnMasterChanged;
        Paint();
    }

    public static void ToggleMute()
    {
        if (MpvIpcClient.GlobalMuted)
        {
            MpvIpcClient.SetGlobalMuted(false);
            if (MpvIpcClient.GlobalMasterVolume == 0) MpvIpcClient.SetGlobalMasterVolume(50);
        }
        else
        {
            MpvIpcClient.SetGlobalMuted(true);
        }
    }

    public static void Nudge(int delta)
    {
        int next = Math.Clamp(MpvIpcClient.GlobalMasterVolume + delta, 0, 100);
        if (MpvIpcClient.GlobalMuted && delta > 0) MpvIpcClient.SetGlobalMuted(false);
        MpvIpcClient.SetGlobalMasterVolume(next);
    }
}

public static class MasterVolumePersistence
{
    private static System.Threading.Timer? _timer;
    private static int _savedLevel = -1;
    private static bool? _savedMuted;
    private static readonly object Gate = new();

    public static void Start()
    {
        lock (Gate)
        {
            if (_timer != null) return;
            _savedLevel = MpvIpcClient.GlobalMasterVolume;
            _savedMuted = MpvIpcClient.GlobalMuted;
            _timer = new System.Threading.Timer(_ => _ = FlushAsync(), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        }
        MpvIpcClient.GlobalMasterVolumeChanged += _ =>
        {
            lock (Gate) _timer?.Change(600, System.Threading.Timeout.Infinite);
        };
    }

    private static async System.Threading.Tasks.Task FlushAsync()
    {
        int level = MpvIpcClient.GlobalMasterVolume;
        bool muted = MpvIpcClient.GlobalMuted;
        try
        {
            if (level != _savedLevel)
            {
                _savedLevel = level;
                await new FreeVideoStudio.Core.Ipc.StateTransferStore(FreeVideoStudio.Core.Infrastructure.ApplicationPaths.CreateDefault())
                    .UpdatePropertiesAsync(new System.Text.Json.Nodes.JsonObject { ["MainVolume"] = (double)level })
                    .ConfigureAwait(false);
            }
            if (_savedMuted != muted)
            {
                _savedMuted = muted;
                SettingsManager.Update(s => s.PreviewMuted = muted);
            }
        }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }
    }
}
