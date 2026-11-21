﻿using System;
using System.Globalization;
using Avalonia.Controls;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Controls;

public enum EditCaptionAction
{
    Save,
    Delete,
    Regenerate
}

public sealed record EditCaptionResult(
    EditCaptionAction Action,
    string Text,
    double StartSec,
    double EndSec);

public readonly record struct CaptionEditValidation(bool IsValid, double StartSec, double EndSec, string? Problem);

public partial class EditCaptionDialogWindow : Window
{
    internal const double MinimumLineSeconds = 0.1;
    internal const string BoundsTag = "CAPTIONEDIT_02";
    internal const double BoundsToleranceSeconds = 0.01;

    public EditCaptionResult? Result { get; private set; }

    public EditCaptionDialogWindow() : this(new CaptionLine(1, 0.0, 2.0, "Sample Caption"))
    {
    }

    public EditCaptionDialogWindow(CaptionLine line, double minSec = 0, double? maxSec = null)
    {
        ArgumentNullException.ThrowIfNull(line);
        InitializeComponent();
        DialogFit.Apply(this, drag: true);

        if (ResolveControl<TextBlock>("DialogTitleText") is { } title)
            title.Text = $"Edit Commentary Caption (Line #{line.Id})";

        var startBox = ResolveControl<TextBox>("StartTimeBox");
        var endBox = ResolveControl<TextBox>("EndTimeBox");
        var textBox = ResolveControl<TextBox>("CaptionTextBox");
        var saveBtn = ResolveControl<Button>("SaveBtn");
        var regenBtn = ResolveControl<Button>("RegenerateBtn");
        var problemText = ResolveControl<TextBlock>("ValidationText");

        if (startBox != null) startBox.Text = line.SourceStartSec.ToString("0.00", CultureInfo.InvariantCulture);
        if (endBox != null) endBox.Text = line.SourceEndSec.ToString("0.00", CultureInfo.InvariantCulture);
        if (textBox != null) textBox.Text = line.Text;

        CaptionEditValidation Current(bool requireText) =>
            Validate(startBox?.Text, endBox?.Text, textBox?.Text, requireText, minSec, maxSec);

        void Refresh()
        {
            var forSave = Current(requireText: true);
            var forTiming = Current(requireText: false);
            if (saveBtn != null) saveBtn.IsEnabled = forSave.IsValid;
            if (regenBtn != null) regenBtn.IsEnabled = forTiming.IsValid;
            if (problemText != null)
            {
                problemText.Text = forSave.Problem == null ? string.Empty : "⚠ " + forSave.Problem;
                problemText.IsVisible = forSave.Problem != null;
            }
        }

        if (startBox != null) startBox.TextChanged += (_, _) => Refresh();
        if (endBox != null) endBox.TextChanged += (_, _) => Refresh();
        if (textBox != null) textBox.TextChanged += (_, _) => Refresh();
        Refresh();

        if (ResolveControl<Button>("CancelBtn") is { } cancelBtn)
        {
            cancelBtn.Click += (_, _) =>
            {
                Result = null;
                Close();
            };
        }

        if (ResolveControl<Button>("DeleteBtn") is { } delBtn)
        {
            delBtn.Click += (_, _) =>
            {
                Result = new EditCaptionResult(EditCaptionAction.Delete, line.Text, line.SourceStartSec, line.SourceEndSec);
                Close();
            };
        }

        if (regenBtn != null)
        {
            regenBtn.Click += (_, _) =>
            {
                var v = Current(requireText: false);
                if (!v.IsValid) { Refresh(); return; }
                Result = new EditCaptionResult(EditCaptionAction.Regenerate, line.Text, v.StartSec, v.EndSec);
                Close();
            };
        }

        if (saveBtn != null)
        {
            saveBtn.Click += (_, _) =>
            {
                var v = Current(requireText: true);
                if (!v.IsValid) { Refresh(); return; }
                Result = new EditCaptionResult(EditCaptionAction.Save, textBox?.Text?.Trim() ?? string.Empty, v.StartSec, v.EndSec);
                Close();
            };
        }
    }

    public static CaptionEditValidation Validate(string? startText, string? endText, string? captionText, bool requireText = true,
        double minSec = 0, double? maxSec = null)
    {
        if (!TryParseTime(startText, out double start))
            return new(false, 0, 0, "Start time is not a number. Use seconds, for example 12.5 or 0:12.5.");
        if (!TryParseTime(endText, out double end))
            return new(false, start, 0, "End time is not a number. Use seconds, for example 14 or 0:14.");
        if (end < start + MinimumLineSeconds)
            return new(false, start, end, "End time must be after the start time.");
        if (start < minSec - BoundsToleranceSeconds)
            return new(false, start, end, $"Start time is before the video starts ({FormatSeconds(minSec)} s).");
        if (maxSec is double max && end > max + BoundsToleranceSeconds)
            return new(false, start, end, $"End time is past the end of the video ({FormatSeconds(max)} s).");
        if (requireText && string.IsNullOrWhiteSpace(captionText))
            return new(false, start, end, "The caption is empty. Type some words, or use Delete line to remove it.");
        return new(true, start, end, null);
    }

    private static string FormatSeconds(double seconds) => seconds.ToString("0.00", CultureInfo.InvariantCulture);

    internal static bool TryParseTime(string? text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.Trim().Replace(',', '.');
        double minutes = 0;
        int colon = t.LastIndexOf(':');
        if (colon >= 0)
        {
            if (!double.TryParse(t[..colon], NumberStyles.Float, CultureInfo.InvariantCulture, out minutes) || minutes < 0)
                return false;
            t = t[(colon + 1)..];
        }
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double secs) ||
            !double.IsFinite(secs) || secs < 0 || (colon >= 0 && secs >= 60))
            return false;
        seconds = minutes * 60 + secs;
        return double.IsFinite(seconds);
    }

    private T? ResolveControl<T>(string name) where T : class => this.FindNameScope()?.Find(name) as T;
}
