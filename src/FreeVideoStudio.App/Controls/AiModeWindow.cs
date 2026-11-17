using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.LocalAi;

namespace FreeVideoStudio.App.Controls;

public sealed class AiModeWindow : Window
{
    internal const string FixTag = "AIMODE_01";

    private readonly RadioButton _local;
    private readonly RadioButton _online;
    private readonly RadioButton _both;
    private readonly bool _pcAvailable;
    private readonly TextBlock _status;
    private readonly ProgressBar _bar;
    private readonly Button _next;
    private readonly Button _cancel;
    private CancellationTokenSource? _downloadCts;

    public bool Finished { get; private set; }

    public AiModeWindow()
    {
        Title = "How Should AI Work?";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeResources.Brush(this, "AppSurfaceBrush", Brushes.Black);
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = 30;

        _pcAvailable = AiSetup.PcAvailable;
        string pcNote = _pcAvailable ? string.Empty : "Not available yet.";
        string size = AiSetup.PackSizeText();
        string download = _pcAvailable && !LocalAiPackService.IsReady && size.Length > 0
            ? $" One-time download, {size}."
            : string.Empty;

        _local = Option("On this PC only", "Nothing leaves your PC.",
            "100% free. Works: Smart Zoom on a box you draw, Magic Wand. Everything else needs online AI." + download, pcNote, _pcAvailable);
        _online = Option("Online only", "Your video goes to the AI company you pick (Google, Anthropic or OpenAI).",
            "Free with Google's free plan or your ChatGPT Plus/Pro plan. Other keys cost a few cents per use. On Google's free plan, Google may use what you send to improve its products.", string.Empty, true);
        _both = Option("Recommended: Both", "Online AI says who to follow; your PC does the heavy work.",
            "Cheap: online AI sees only a few pictures." + download, pcNote, _pcAvailable);

        AiMode current = AiSetup.Current();
        if (current == AiMode.LocalOnly && _local.IsEnabled) _local.IsChecked = true;
        else if (current == AiMode.OnlineOnly) _online.IsChecked = true;
        else if (current == AiMode.Hybrid && _both.IsEnabled) _both.IsChecked = true;

        _status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
            Foreground = ThemeResources.Brush(this, "AppTextSecondaryBrush", Brushes.Gray),
        };
        _bar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 6, IsVisible = false, Margin = new Thickness(0, 6, 0, 0) };

        _cancel = new Button { Content = "Not now", Margin = new Thickness(0, 0, 8, 0) };
        _cancel.Classes.Add("Secondary");
        _next = new Button { Content = "Next", IsDefault = true, IsEnabled = Selected() != AiMode.NotChosen };
        _next.Classes.Add("Primary");
        foreach (var rb in new[] { _local, _online, _both })
            rb.IsCheckedChanged += (_, _) => _next.IsEnabled = _downloadCts == null && Selected() != AiMode.NotChosen;
        _cancel.Click += (_, _) =>
        {
            if (_downloadCts != null) { _downloadCts.Cancel(); return; }
            Close();
        };
        _next.Click += async (_, _) => await NextAsync();
        Closed += (_, _) => _downloadCts?.Cancel();

        var title = new TextBlock
        {
            Text = "How should AI work?",
            FontWeight = FontWeight.Bold,
            [!TextBlock.FontSizeProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AppFontSize18"),
            Foreground = ThemeResources.Brush(this, "AppTextPrimaryBrush", Brushes.White),
        };
        var hint = new TextBlock
        {
            Text = "Pick one. You can change it any time in Settings › AI.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 12),
            Foreground = ThemeResources.Brush(this, "AppTextSecondaryBrush", Brushes.Gray),
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(_cancel);
        buttons.Children.Add(_next);

        var root = new StackPanel { Margin = new Thickness(20), Spacing = 8 };
        root.Children.Add(title);
        root.Children.Add(hint);
        root.Children.Add(_local);
        root.Children.Add(_online);
        root.Children.Add(_both);
        root.Children.Add(_status);
        root.Children.Add(_bar);
        root.Children.Add(buttons);
        Content = DialogChrome.Wrap(this, "How Should AI Work?", root);
    }

    private RadioButton Option(string heading, string line, string small, string note, bool enabled)
    {
        var body = new StackPanel { Spacing = 2 };
        body.Children.Add(new TextBlock
        {
            Text = heading,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeResources.Brush(this, "AppTextPrimaryBrush", Brushes.White),
        });
        body.Children.Add(new TextBlock
        {
            Text = line,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeResources.Brush(this, "AppTextPrimaryBrush", Brushes.White),
        });
        body.Children.Add(new TextBlock
        {
            Text = small,
            [!TextBlock.FontSizeProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AppFontSize11"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeResources.Brush(this, "AppTextSecondaryBrush", Brushes.Gray),
        });
        if (!string.IsNullOrEmpty(note))
        {
            body.Children.Add(new TextBlock
            {
                Text = note,
                [!TextBlock.FontSizeProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AppFontSize11"),
                Foreground = ThemeResources.Brush(this, "AppWarningBrush", Brushes.Orange),
            });
        }
        return new RadioButton
        {
            GroupName = "AiMode",
            Content = body,
            IsEnabled = enabled,
            Padding = new Thickness(6, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
    }

    private AiMode Selected()
        => _local.IsChecked == true ? AiMode.LocalOnly
         : _online.IsChecked == true ? AiMode.OnlineOnly
         : _both.IsChecked == true ? AiMode.Hybrid
         : AiMode.NotChosen;

    private void Say(string text, string brushKey = "AppTextSecondaryBrush")
    {
        _status.Text = text;
        _status.Foreground = ThemeResources.Brush(this, brushKey, Brushes.Gray);
    }

    private void LockChoices(bool locked)
    {
        _local.IsEnabled = !locked && _pcAvailable;
        _online.IsEnabled = !locked;
        _both.IsEnabled = !locked && _pcAvailable;
    }

    private async Task NextAsync()
    {
        AiMode mode = Selected();
        if (mode == AiMode.NotChosen) return;
        _next.IsEnabled = false;
        try
        {
            if (AiSetup.UsesPc(mode) && !LocalAiPackService.IsReady && !await DownloadAsync()) return;

            if (AiSetup.UsesOnline(mode) && !AiSetup.HasStoredKey(SettingsManager.Instance))
            {
                Say("Next: pick your AI company and paste its key.");
                var keys = new AiSetupWizardWindow(SettingsManager.Instance.AiDefaultUsage?.Vendor ?? AiVendor.Google, null, chooseCompanyFirst: true);
                await keys.ShowDialog<bool?>(this);
                if (!AiSetup.HasStoredKey(SettingsManager.Instance))
                {
                    Say("Not connected yet, so nothing was saved. Press Next to try again.", "AppWarningBrush");
                    return;
                }
            }

            SettingsManager.Update(s => s.AiMode = mode);
            Finished = true;
            Close();
        }
        finally
        {
            if (!Finished) _next.IsEnabled = Selected() != AiMode.NotChosen;
        }
    }

    private async Task<bool> DownloadAsync()
    {
        if (!LocalAiPackManifest.IsPublished)
        {
            Say("The PC download is not available yet. Pick 'Online only' for now.", "AppWarningBrush");
            return false;
        }
        _downloadCts = new CancellationTokenSource();
        _bar.Value = 0;
        _bar.IsVisible = true;
        _cancel.Content = "Stop download";
        LockChoices(true);
        string size = AiSetup.PackSizeText();
        Say(size.Length > 0 ? $"Downloading the PC AI ({size})…" : "Downloading the PC AI…");
        try
        {
            var status = await LocalAiPackService.InstallAsync(p => Dispatcher.UIThread.Post(() =>
            {
                if (p.Total > 0) _bar.Value = p.Done * 100.0 / p.Total;
                Say(p.Problem ?? (p.Total > 0
                    ? $"Downloading the PC AI… {(int)Math.Round(p.Done * 100.0 / p.Total)}%"
                    : $"Downloading {p.FileIndex} of {p.FileCount}…"));
            }), _downloadCts.Token);
            if (status.IsReady) return true;
            Say("The download did not finish: " + status.Message + " Nothing was saved. Press Next to try again.", "AppDangerBrush");
            return false;
        }
        catch (OperationCanceledException)
        {
            Say("Download stopped. Nothing was saved. Press Next to continue it.", "AppWarningBrush");
            return false;
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.Net.Http.HttpRequestException or UnauthorizedAccessException)
        {
            RuntimeLog.Fail("AiMode", "PC AI download failed: " + ex.Message);
            Say("The download failed. " + UserFacingError.Describe(ex) + " Nothing was saved; press Next to try again.", "AppDangerBrush");
            return false;
        }
        finally
        {
            _downloadCts?.Dispose();
            _downloadCts = null;
            _bar.IsVisible = false;
            _cancel.Content = "Not now";
            LockChoices(false);
        }
    }

    public static bool SkipsChoice(bool pcAvailable, AiMode saved) => !pcAvailable && saved == AiMode.NotChosen;

    private static async Task<bool> ConnectOnlineOnlyAsync(Window owner)
    {
        if (!AiSetup.HasStoredKey(SettingsManager.Instance))
        {
            var keys = new AiSetupWizardWindow(SettingsManager.Instance.AiDefaultUsage?.Vendor ?? AiVendor.Google, null, chooseCompanyFirst: true);
            await keys.ShowDialog<bool?>(owner);
            if (!AiSetup.HasStoredKey(SettingsManager.Instance)) return false;
        }
        return SettingsManager.Update(s => s.AiMode = AiMode.OnlineOnly);
    }

    public static async Task<bool> RunAsync(Window owner)
    {
        if (SkipsChoice(AiSetup.PcAvailable, SettingsManager.Instance.AiMode))
            return await ConnectOnlineOnlyAsync(owner);
        var dlg = new AiModeWindow();
        await dlg.ShowDialog(owner);
        return dlg.Finished;
    }

    public static async Task<bool> EnsureSetUpAsync(Window owner)
    {
        if (AiSetup.CurrentIsComplete()) return true;
        if (!await RunAsync(owner)) return false;
        return AiSetup.CurrentIsComplete();
    }
}
