using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using FreeVideoStudio.App.Infrastructure;

namespace FreeVideoStudio.App;

public sealed class PreviewDetachController
{
    public const string MainWindowKey = "PreviewMonitorWindowBounds";
    public const string GranularKey = "GranularPreviewMonitorBounds";
    public const string MusicWizardKey = "MusicWizardPreviewMonitorBounds";
    public const string VoiceOverKey = "VoiceOverPreviewMonitorBounds";
    public const string MergerKey = "MergerPreviewMonitorBounds";

    private readonly Window _owner;
    private readonly string _boundsKey;
    private readonly string _title;
    private readonly Func<Control?> _content;

    private PreviewMonitorWindow? _monitor;

    private Panel? _originPanel;
    private int _originIndex = -1;
    private Decorator? _originDecorator;
    private ContentControl? _originContent;
    private Thickness _originMargin;

    private bool _transitioning;

    public event Action<bool>? StateChanged;

    public bool IsDetached => _monitor != null;

    public PreviewMonitorWindow? Monitor => _monitor;

    public PreviewDetachController(Window owner, string boundsKey, string title, Func<Control?> contentAccessor)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _boundsKey = boundsKey;
        _title = title;
        _content = contentAccessor ?? throw new ArgumentNullException(nameof(contentAccessor));

        _owner.Closing += (_, _) =>
        {
            try { Attach(); }
            catch (Exception ex) { RuntimeLog.Fail("UI", $"Could not reattach the preview before closing: {ex.Message}"); }
        };
    }

    public bool CanDetach => IsDetached || _content() != null;

    public event Action<string>? DetachUnavailable;

    public void SyncButton(Button? btn)
    {
        if (btn == null) return;
        btn.Content = IsDetached ? "◱ Attach Monitor" : "◳ Detach Monitor";
        bool can = CanDetach;
        btn.IsEnabled = can;
        ToolTip.SetTip(btn, can
            ? "Pop the preview out into its own floating window. It remembers its size, position and which screen you left it on."
            : "The video preview is still loading — there is nothing to pop out yet.");
    }

    public bool Toggle() => IsDetached ? Attach() : Detach();

    public bool Detach()
    {
        if (_monitor != null || _transitioning) return false;

        var content = _content();
        if (content == null)
        {
            const string why = "The video preview is still loading — there is nothing to pop out yet.";
            RuntimeLog.Info("UI", $"Detach requested with no preview available ({_boundsKey}).");
            DetachUnavailable?.Invoke(why);
            return false;
        }

        _transitioning = true;
        try
        {
            if (!CaptureOriginAndRemove(content)) return false;

            _originMargin = content.Margin;
            content.Margin = new Thickness(0);

            _monitor = new PreviewMonitorWindow(_boundsKey, _title) { Controller = this };
            _monitor.VideoContainerControl.Child = content;
            PlaceOnFirstDetach(_monitor);
            _monitor.Show(_owner);

            RuntimeLog.Info("UI", $"Detached the preview into its own window ({_boundsKey}).");
        }
        finally { _transitioning = false; }

        StateChanged?.Invoke(true);
        return true;
    }

    public bool Attach()
    {
        if (_monitor == null || _transitioning) return false;

        _transitioning = true;
        try
        {
            var monitor = _monitor;
            _monitor = null;

            var content = monitor.VideoContainerControl.Child as Control;
            monitor.VideoContainerControl.Child = null;

            if (content != null)
            {
                content.Margin = _originMargin;
                RestoreToOrigin(content);
            }

            try { monitor.Close(); }
            catch (Exception ex) { RuntimeLog.Fail("UI", $"Could not close the detached preview window: {ex.Message}"); }

            RuntimeLog.Info("UI", $"Reattached the preview to its home window ({_boundsKey}).");
        }
        finally
        {
            _originPanel = null;
            _originIndex = -1;
            _originDecorator = null;
            _originContent = null;
            _transitioning = false;
        }

        StateChanged?.Invoke(false);
        return true;
    }

    private void PlaceOnFirstDetach(Window monitor)
    {
        if (monitor.WindowStartupLocation != WindowStartupLocation.CenterScreen) return;

        try
        {
            monitor.WindowStartupLocation = WindowStartupLocation.Manual;
            double w = double.IsNaN(monitor.Width) ? 1280 : monitor.Width;
            double h = double.IsNaN(monitor.Height) ? 750 : monitor.Height;

            int px = _owner.Position.X + (int)((_owner.Bounds.Width - w) / 2);
            int py = Math.Max(0, _owner.Position.Y + (int)((_owner.Bounds.Height - h) / 2) - 150);

            var screen = _owner.Screens?.ScreenFromWindow(_owner) ?? _owner.Screens?.Primary;
            if (screen != null)
            {
                var b = screen.Bounds;
                px = Math.Max(b.X, Math.Min(px, b.X + b.Width - (int)w));
                py = Math.Max(b.Y, Math.Min(py, b.Y + b.Height - (int)h));
            }

            monitor.Position = new PixelPoint(px, py);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("UI", $"Could not place the detached preview window: {ex.Message}");
        }
    }

    private bool CaptureOriginAndRemove(Control content)
    {
        switch (content.Parent)
        {
            case Panel panel:
                _originPanel = panel;
                _originIndex = panel.Children.IndexOf(content);
                panel.Children.Remove(content);
                return true;

            case Decorator decorator:
                _originDecorator = decorator;
                decorator.Child = null;
                return true;

            case ContentControl cc:
                _originContent = cc;
                cc.Content = null;
                return true;

            default:
                RuntimeLog.Fail("UI", "The preview could not be detached: its container is not a type this can put it back into.");
                return false;
        }
    }

    private void RestoreToOrigin(Control content)
    {
        if (_originPanel != null)
        {
            int index = Math.Clamp(_originIndex < 0 ? 0 : _originIndex, 0, _originPanel.Children.Count);
            _originPanel.Children.Insert(index, content);
        }
        else if (_originDecorator != null)
        {
            _originDecorator.Child = content;
        }
        else if (_originContent != null)
        {
            _originContent.Content = content;
        }
    }
}
