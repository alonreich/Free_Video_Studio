using System;
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

/// <summary>
/// AICAPTION_LANE_01 — Dialog window allowing users to edit text, adjust start/end timing,
/// regenerate single line via AI, or delete an individual commentary caption cue.
/// </summary>
public partial class EditCaptionDialogWindow : Window
{
    private readonly CaptionLine _originalLine;
    public EditCaptionResult? Result { get; private set; }

    public EditCaptionDialogWindow() : this(new CaptionLine(1, 0.0, 2.0, "Sample Caption"))
    {
    }

    public EditCaptionDialogWindow(CaptionLine line)
    {
        _originalLine = line ?? throw new ArgumentNullException(nameof(line));
        InitializeComponent();

        if (ResolveControl<TextBlock>("DialogTitleText") is { } title)
            title.Text = $"Edit Commentary Caption (Line #{line.Id})";

        if (ResolveControl<TextBox>("StartTimeBox") is { } startBox)
            startBox.Text = line.SourceStartSec.ToString("0.00", CultureInfo.InvariantCulture);

        if (ResolveControl<TextBox>("EndTimeBox") is { } endBox)
            endBox.Text = line.SourceEndSec.ToString("0.00", CultureInfo.InvariantCulture);

        if (ResolveControl<TextBox>("CaptionTextBox") is { } textBox)
            textBox.Text = line.Text;

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

        if (ResolveControl<Button>("RegenerateBtn") is { } regenBtn)
        {
            regenBtn.Click += (_, _) =>
            {
                double start = ParseTime(ResolveControl<TextBox>("StartTimeBox")?.Text, line.SourceStartSec);
                double end = ParseTime(ResolveControl<TextBox>("EndTimeBox")?.Text, line.SourceEndSec);
                Result = new EditCaptionResult(EditCaptionAction.Regenerate, line.Text, start, end);
                Close();
            };
        }

        if (ResolveControl<Button>("SaveBtn") is { } saveBtn)
        {
            saveBtn.Click += (_, _) =>
            {
                string text = ResolveControl<TextBox>("CaptionTextBox")?.Text?.Trim() ?? string.Empty;
                double start = ParseTime(ResolveControl<TextBox>("StartTimeBox")?.Text, line.SourceStartSec);
                double end = ParseTime(ResolveControl<TextBox>("EndTimeBox")?.Text, line.SourceEndSec);

                if (end <= start + 0.1)
                {
                    end = start + 1.2;
                }

                Result = new EditCaptionResult(EditCaptionAction.Save, text, start, end);
                Close();
            };
        }
    }

    private T? ResolveControl<T>(string name) where T : class => this.FindNameScope()?.Find(name) as T;

    private static double ParseTime(string? text, double fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            return Math.Max(0, parsed);
        return fallback;
    }
}
