using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.Core.Media;
using System;
using System.Threading.Tasks;

namespace FreeVideoStudio.App.Controls;

public readonly record struct AudioFixDecision(bool Apply, AudioFixPrompt Remember);

public partial class AudioFixPromptWindow : Window
{
    private CheckBox? _alwaysCheck;
    private CheckBox? _neverCheck;
    private bool _suppressCheckSync;

    public AudioFixDecision Decision { get; private set; } = new(false, AudioFixPrompt.Ask);

    public AudioFixPromptWindow()
    {
        InitializeComponent();
        DialogFit.Apply(this);

        _alwaysCheck = this.FindControl<CheckBox>("AlwaysCheck");
        _neverCheck = this.FindControl<CheckBox>("NeverCheck");

        if (_alwaysCheck != null)
            _alwaysCheck.IsCheckedChanged += (_, _) =>
            {
                if (_suppressCheckSync) return;
                if (_alwaysCheck.IsChecked == true)
                {
                    _suppressCheckSync = true;
                    if (_neverCheck != null) _neverCheck.IsChecked = false;
                    _suppressCheckSync = false;
                }
            };

        if (_neverCheck != null)
            _neverCheck.IsCheckedChanged += (_, _) =>
            {
                if (_suppressCheckSync) return;
                if (_neverCheck.IsChecked == true)
                {
                    _suppressCheckSync = true;
                    if (_alwaysCheck != null) _alwaysCheck.IsChecked = false;
                    _suppressCheckSync = false;
                }
            };

        var yes = this.FindControl<Button>("YesBtn");
        if (yes != null) yes.Click += (_, _) => Finish(apply: true);

        var no = this.FindControl<Button>("NoBtn");
        if (no != null) no.Click += (_, _) => Finish(apply: false);

        AttachTitleBarDrag();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void Finish(bool apply)
    {
        AudioFixPrompt remember = AudioFixPrompt.Ask;
        if (apply && _alwaysCheck?.IsChecked == true) remember = AudioFixPrompt.AlwaysApply;
        else if (!apply && _neverCheck?.IsChecked == true) remember = AudioFixPrompt.NeverApply;

        Decision = new AudioFixDecision(apply, remember);
        Close();
    }

    private void AttachTitleBarDrag()
    {
        var titleBar = this.FindControl<TextBlock>("DialogTitle");
        if (titleBar?.Parent is Border border)
        {
            border.PointerPressed += (_, e) =>
            {
                if (e.ClickCount < 2) BeginMoveDrag(e);
            };
        }
    }

    public static AudioFixPromptWindow ForHarshPeaks(LoudnessReading reading)
    {
        var win = new AudioFixPromptWindow();

        win.Title = "Warning — Sudden Loud Moment";
        win.SetText(
            title: "Warning — Sudden Loud Moment",
            message: "The uploaded video contains a sudden moment that is far louder than the rest of it. " +
                     "On headphones this can genuinely hurt, because it arrives without warning.",
            question: "Would you like to soften that spike so it no longer jumps far above the rest of the video?",
            detail: $"Loudest instant: {reading.TruePeakDbtp:F1} dBTP, which is about " +
                    $"{reading.PeakAboveAverageLu:F0} dB above this video's own average of {reading.IntegratedLufs:F1} LUFS.",
            yesTip: "Softens only the sudden loud moments. The rest of the video is left untouched.",
            noTip: "Leaves the loud moment as you recorded it (the always-on safety limiter still stops it from clipping).");

        return win;
    }

    private void SetText(string title, string message, string question, string detail, string yesTip, string noTip)
    {
        var t = this.FindControl<TextBlock>("DialogTitle");
        if (t != null) t.Text = title;

        var m = this.FindControl<TextBlock>("MessageText");
        if (m != null) m.Text = message;

        var q = this.FindControl<TextBlock>("QuestionText");
        if (q != null) q.Text = question;

        var d = this.FindControl<TextBlock>("DetailText");
        if (d != null) d.Text = detail;

        var yes = this.FindControl<Button>("YesBtn");
        if (yes != null) ToolTip.SetTip(yes, yesTip);

        var no = this.FindControl<Button>("NoBtn");
        if (no != null) ToolTip.SetTip(no, noTip);
    }

    public static async Task<bool> ResolveAsync(
        Window owner,
        AudioFixPrompt current,
        Func<AudioFixPromptWindow> buildDialog,
        Action<AudioFixPrompt> persist,
        string logTag)
    {
        switch (current)
        {
            case AudioFixPrompt.AlwaysApply:
                RuntimeLog.Info(logTag, "Applying the fix automatically (user chose 'always' previously).");
                return true;

            case AudioFixPrompt.NeverApply:
                RuntimeLog.Info(logTag, "Skipping the fix (user chose 'never' previously).");
                return false;
        }

        var dialog = buildDialog();
        await dialog.ShowDialog(owner);

        var decision = dialog.Decision;
        if (decision.Remember != AudioFixPrompt.Ask)
        {
            persist(decision.Remember);
            RuntimeLog.Info(logTag, $"User answered {(decision.Apply ? "YES" : "NO")} and asked to remember it as {decision.Remember}.");
        }
        else
        {
            RuntimeLog.Info(logTag, $"User answered {(decision.Apply ? "YES" : "NO")} for this video only.");
        }

        return decision.Apply;
    }
}
