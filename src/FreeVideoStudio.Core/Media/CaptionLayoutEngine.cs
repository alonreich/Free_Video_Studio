using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace FreeVideoStudio.Core.Media;

public sealed record CaptionLayoutLine(
    double X,
    double Y,
    double Width,
    double Height,
    string Text,
    IReadOnlyList<MappedCaptionWord> Words);

public sealed record CaptionLayout(
    int LineId,
    double OutputStartSec,
    double OutputEndSec,
    double BoxX,
    double BoxY,
    double BoxWidth,
    double BoxHeight,
    int FontSize,
    IReadOnlyList<CaptionLayoutLine> Lines);

public static class CaptionLayoutEngine
{
    public const int PortraitWidth = 1080;
    public const int PortraitHeight = 1920;
    public const int PortraitVoidTop = 150;
    public const int PortraitVoidBottom = 150;

    public const int LandscapeWidth = 1920;
    public const int LandscapeHeight = 1080;

    public const int MaxWordsPerLine = 8;
    public const int MaxLinesOnScreen = 2;

    public const int PortraitFontSize = 76;

    public const int LandscapeFontSize = 64;

    public const double DefaultCenterFraction = 2.0 / 3.0;

    public const double HudGapPx = 24.0;

    public const double MiddleTopFraction = 0.54;

    public static double SizeScale(CaptionSize size) => size switch
    {
        CaptionSize.Small => 0.8,
        CaptionSize.Large => 1.25,
        _ => 1.0,
    };

    public static int BaseFontSize(bool isPortrait, CaptionSize size) =>
        (int)Math.Round((isPortrait ? PortraitFontSize : LandscapeFontSize) * SizeScale(size));

    public static CaptionLayout ComputeLayout(
        MappedCaptionLine captionLine,
        int outputW,
        int outputH,
        bool isPortrait,
        CaptionPosition position = CaptionPosition.BottomContent,
        IReadOnlyList<(double X, double Y, double W, double H)>? hudRects = null,
        string fontFamily = "Arial",
        CaptionSize size = CaptionSize.Medium)
    {
        if (outputW <= 0) outputW = isPortrait ? PortraitWidth : LandscapeWidth;
        if (outputH <= 0) outputH = isPortrait ? PortraitHeight : LandscapeHeight;

        int baseFontSize = BaseFontSize(isPortrait, size);
        int fontSize = baseFontSize;
        double sideMargin = isPortrait ? 60.0 : 160.0;
        double maxTextWidth = Math.Max(200.0, outputW - (sideMargin * 2));

        using var typeface = SKTypeface.FromFamilyName(fontFamily, SKFontStyle.Bold) ?? SKTypeface.Default;
        using var font = new SKFont(typeface, fontSize) { Edging = SKFontEdging.Antialias };
        using var paint = new SKPaint();

        var wrappedLines = WrapCaptionWords(captionLine.Words, font, paint, (float)maxTextWidth);

        if (wrappedLines.Count > MaxLinesOnScreen && fontSize > 48)
        {
            fontSize = (int)(baseFontSize * 0.85);
            font.Size = fontSize;
            wrappedLines = WrapCaptionWords(captionLine.Words, font, paint, (float)maxTextWidth);
        }

        int shrinkFloor = Math.Max(MinFontSize, (int)(baseFontSize * ShrinkFloorFraction));
        while (wrappedLines.Count > MaxLinesOnScreen && fontSize > shrinkFloor && captionLine.Words.Count <= MaxWordsPerLine * MaxLinesOnScreen)
        {
            fontSize = Math.Max(shrinkFloor, (int)(fontSize * 0.92));
            font.Size = fontSize;
            wrappedLines = WrapCaptionWords(captionLine.Words, font, paint, (float)maxTextWidth);
        }

        if (wrappedLines.Count > MaxLinesOnScreen)
        {
            wrappedLines = FoldIntoTwoRows(captionLine.Words, font, paint, (float)maxTextWidth);
            fontSize = (int)font.Size;
        }

        double totalTextHeight = wrappedLines.Sum(l => l.Height);
        double maxLineWidth = wrappedLines.Count > 0 ? wrappedLines.Max(l => l.Width) : 0;
        double boxWidth = Math.Min(maxTextWidth, maxLineWidth + 40.0);
        double boxHeight = totalTextHeight + 24.0;
        double boxX = (outputW - boxWidth) / 2.0;
        double boxY = ComputeDefaultBoxY(outputH, boxHeight, isPortrait, position);

        if (hudRects != null && hudRects.Count > 0 && position != CaptionPosition.BottomVoid)
        {
            boxY = AvoidHudCollisions(boxX, boxY, boxWidth, boxHeight, outputH, isPortrait, position, hudRects);
        }

        double lineCursorY = boxY + 12.0;
        var positionedLines = new List<CaptionLayoutLine>(wrappedLines.Count);

        foreach (var line in wrappedLines)
        {
            double lineX = (outputW - line.Width) / 2.0;
            positionedLines.Add(new CaptionLayoutLine(
                lineX,
                lineCursorY,
                line.Width,
                line.Height,
                line.Text,
                line.Words));
            lineCursorY += line.Height;
        }

        return new CaptionLayout(
            captionLine.Id,
            captionLine.OutputStartSec,
            captionLine.OutputEndSec,
            boxX,
            boxY,
            boxWidth,
            boxHeight,
            fontSize,
            positionedLines);
    }

    private static double Fit(double value, double lo, double hi, int outputH, double boxHeight) =>
        hi >= lo ? Math.Clamp(value, lo, hi) : Math.Max(0.0, (outputH - boxHeight) / 2.0);

    private static double ComputeDefaultBoxY(int outputH, double boxHeight, bool isPortrait, CaptionPosition position)
    {
        double defaultY = outputH * DefaultCenterFraction - boxHeight / 2.0;
        double middleY = outputH * MiddleTopFraction;
        if (isPortrait)
        {
            return position switch
            {
                CaptionPosition.Top => PortraitVoidTop + 40.0,
                CaptionPosition.BottomVoid => (outputH - PortraitVoidBottom) + ((PortraitVoidBottom - boxHeight) / 2.0),
                CaptionPosition.Middle => Fit(middleY, PortraitVoidTop + 20.0, outputH - PortraitVoidBottom - boxHeight - 20.0, outputH, boxHeight),
                _ => Fit(defaultY, PortraitVoidTop + 20.0, outputH - PortraitVoidBottom - boxHeight - 20.0, outputH, boxHeight),
            };
        }

        return position switch
        {
            CaptionPosition.Top => 60.0,
            CaptionPosition.Middle => Fit(middleY, 40.0, outputH - boxHeight - 40.0, outputH, boxHeight),
            _ => Fit(defaultY, 40.0, outputH - boxHeight - 40.0, outputH, boxHeight),
        };
    }

    private static double AvoidHudCollisions(
        double boxX,
        double boxY,
        double boxW,
        double boxH,
        int outputH,
        bool isPortrait,
        CaptionPosition position,
        IReadOnlyList<(double X, double Y, double W, double H)> hudRects)
    {
        double currentY = boxY;
        double topLimit = isPortrait ? PortraitVoidTop + 20.0 : 40.0;
        double bottomLimit = isPortrait ? outputH - PortraitVoidBottom : outputH - 40.0;

        for (int pass = 0; pass < 8; pass++)
        {
            bool moved = false;
            foreach (var hud in hudRects)
            {
                if (hud.W <= 1 || hud.H <= 1) continue;

                bool intersectsX = (boxX < hud.X + hud.W) && (boxX + boxW > hud.X);
                bool intersectsY = (currentY < hud.Y + hud.H + HudGapPx) && (currentY + boxH + HudGapPx > hud.Y);
                if (!intersectsX || !intersectsY) continue;

                double newY = position is CaptionPosition.Top or CaptionPosition.Middle
                    ? hud.Y + hud.H + HudGapPx
                    : hud.Y - boxH - HudGapPx;
                if (newY >= topLimit && newY + boxH <= bottomLimit && Math.Abs(newY - currentY) > 0.5)
                {
                    currentY = newY;
                    moved = true;
                }
            }
            if (!moved) break;
        }

        return currentY;
    }

    private static List<CaptionLayoutLine> WrapCaptionWords(
        IReadOnlyList<MappedCaptionWord> words,
        SKFont font,
        SKPaint paint,
        float maxWidth)
    {
        var lines = new List<CaptionLayoutLine>();
        if (words == null || words.Count == 0) return lines;

        float lineHeight = font.Size * 1.25f;
        var currentWords = new List<MappedCaptionWord>();
        float currentWidth = 0;

        foreach (var word in words)
        {
            string testLine = currentWords.Count == 0
                ? word.Text
                : string.Join(" ", currentWords.Select(w => w.Text)) + " " + word.Text;

            float measured = MeasureTextWidth(testLine, font, paint);

            if ((measured > maxWidth || currentWords.Count >= MaxWordsPerLine) && currentWords.Count > 0)
            {
                string lineText = string.Join(" ", currentWords.Select(w => w.Text));
                lines.Add(new CaptionLayoutLine(0, 0, currentWidth, lineHeight, lineText, currentWords));
                currentWords = new List<MappedCaptionWord> { word };
                currentWidth = MeasureTextWidth(word.Text, font, paint);
            }
            else
            {
                currentWords.Add(word);
                currentWidth = measured;
            }
        }

        if (currentWords.Count > 0)
        {
            string lineText = string.Join(" ", currentWords.Select(w => w.Text));
            lines.Add(new CaptionLayoutLine(0, 0, currentWidth, lineHeight, lineText, currentWords));
        }

        return lines;
    }

    public const string TwoRowFitTag = "CAPTIONFIT_02";
    public const int MinFontSize = 8;
    public const double ShrinkFloorFraction = 0.6;

    private static List<CaptionLayoutLine> FoldIntoTwoRows(
        IReadOnlyList<MappedCaptionWord> words,
        SKFont font,
        SKPaint paint,
        float maxWidth)
    {
        var all = words.ToList();
        var widths = new float[all.Count];
        float space = MeasureTextWidth("a a", font, paint) - MeasureTextWidth("aa", font, paint);
        if (space <= 0) space = font.Size * 0.28f;
        float total = 0;
        for (int i = 0; i < all.Count; i++)
        {
            widths[i] = MeasureTextWidth(all[i].Text, font, paint);
            total += widths[i] + (i > 0 ? space : 0);
        }

        int split = 1;
        float best = float.MaxValue;
        float left = 0;
        for (int k = 1; k < all.Count; k++)
        {
            left += widths[k - 1] + (k > 1 ? space : 0);
            float right = total - left - space;
            float widest = Math.Max(left, right);
            if (widest < best) { best = widest; split = k; }
        }

        var first = all.GetRange(0, split);
        var second = all.GetRange(split, all.Count - split);
        string firstText = string.Join(" ", first.Select(w => w.Text));
        string secondText = string.Join(" ", second.Select(w => w.Text));

        float firstWidth = MeasureTextWidth(firstText, font, paint);
        float secondWidth = MeasureTextWidth(secondText, font, paint);
        for (int pass = 0; pass < 4 && Math.Max(firstWidth, secondWidth) > maxWidth && font.Size > 1; pass++)
        {
            font.Size = Math.Max(1, (int)Math.Floor(font.Size * maxWidth / Math.Max(firstWidth, secondWidth)));
            firstWidth = MeasureTextWidth(firstText, font, paint);
            secondWidth = MeasureTextWidth(secondText, font, paint);
        }

        float lineHeight = font.Size * 1.25f;
        return new List<CaptionLayoutLine>
        {
            new CaptionLayoutLine(0, 0, firstWidth, lineHeight, firstText, first),
            new CaptionLayoutLine(0, 0, secondWidth, lineHeight, secondText, second)
        };
    }

    private static float MeasureTextWidth(string text, SKFont font, SKPaint paint)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        font.MeasureText(text, out SKRect bounds, paint);
        if (bounds.Width > 0.001f) return bounds.Width;

        return text.Length * (font.Size * 0.55f);
    }
}
