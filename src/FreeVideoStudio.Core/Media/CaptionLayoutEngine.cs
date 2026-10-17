// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md (TL-CAPTIONS)
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace FreeVideoStudio.Core.Media;

/// <summary>
/// AICAPTION_LAYOUT_01 — one laid-out subtitle line with exact positioning and wrapped words.
/// </summary>
public sealed record CaptionLayoutLine(
    double X,
    double Y,
    double Width,
    double Height,
    string Text,
    IReadOnlyList<MappedCaptionWord> Words);

/// <summary>
/// AICAPTION_LAYOUT_01 — complete layout result for one caption line on the target canvas.
/// </summary>
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

/// <summary>
/// AICAPTION_LAYOUT_01 — Pure geometric layout engine for live commentary captions.
/// Handles font measurement, word-wrapping (max 8 words/line, max 2 lines), portrait/landscape canvas
/// rules (avoiding 150 px top/bottom voids in portrait), and dynamic collision avoidance over HUD rects.
/// </summary>
public static class CaptionLayoutEngine
{
    public const int PortraitWidth = 1080;
    public const int PortraitHeight = 1920;
    public const int PortraitVoidTop = 150;
    public const int PortraitVoidBottom = 150;
    public const int PortraitContentHeight = 1620; // 1920 - 300

    public const int LandscapeWidth = 1920;
    public const int LandscapeHeight = 1080;

    public const int MaxWordsPerLine = 8;
    public const int MaxLinesOnScreen = 2;

    /// <summary>AICAPTION_LAYOUT_02 — default caption font size on a 1080x1920 portrait canvas (was 54).</summary>
    public const int PortraitFontSize = 76;

    /// <summary>AICAPTION_LAYOUT_02 — default caption font size on a 1920x1080 landscape canvas (was 58).</summary>
    public const int LandscapeFontSize = 64;

    /// <summary>
    /// AICAPTION_LAYOUT_02 — where the DEFAULT (BottomContent) caption's CENTRE sits, as a fraction of the canvas
    /// height: exactly on the line between the upper two-thirds and the lower third. Above the bottom HUD band
    /// (health / shield / stamina / hotbar) and below the action in the middle of the screen. HUD pieces that
    /// still overlap push it further up (<see cref="AvoidHudCollisions"/>).
    /// </summary>
    public const double DefaultCenterFraction = 2.0 / 3.0;

    /// <summary>AICAPTION_LAYOUT_02 — the gap kept between a caption and a HUD piece it was moved off.</summary>
    public const double HudGapPx = 24.0;

    /// <summary>
    /// CAPTIONPLACE_01 — the TOP of a <see cref="CaptionPosition.Middle"/> caption box, as a fraction of the canvas
    /// height: just under the centre, so the crosshair / aim point in the middle of the screen stays clear while
    /// the text still sits where the eyes are. HUD pieces it overlaps push it DOWN.
    /// </summary>
    public const double MiddleTopFraction = 0.54;

    /// <summary>CAPTIONSIZE_01 — text size multiplier. Medium (1.0) is the size used before the option existed.</summary>
    public static double SizeScale(CaptionSize size) => size switch
    {
        CaptionSize.Small => 0.8,
        CaptionSize.Large => 1.25,
        _ => 1.0,
    };

    /// <summary>CAPTIONSIZE_01 — the base font size for a canvas and a size choice.</summary>
    public static int BaseFontSize(bool isPortrait, CaptionSize size) =>
        (int)Math.Round((isPortrait ? PortraitFontSize : LandscapeFontSize) * SizeScale(size));

    /// <summary>
    /// Computes the layout for a single <see cref="MappedCaptionLine"/> given canvas dimensions, mode,
    /// placement preset, and optional active HUD bounding boxes in output canvas pixels.
    /// </summary>
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

        // If wrapped into more than 2 lines, try scaling font size down to fit within max 2 lines
        if (wrappedLines.Count > MaxLinesOnScreen && fontSize > 48)
        {
            fontSize = (int)(baseFontSize * 0.85);
            font.Size = fontSize;
            wrappedLines = WrapCaptionWords(captionLine.Words, font, paint, (float)maxTextWidth);
        }

        // Clamp to MaxLinesOnScreen if still overflowing
        if (wrappedLines.Count > MaxLinesOnScreen)
        {
            // Coalesce excess into the second line
            var firstLine = wrappedLines[0];
            var remainderWords = new List<MappedCaptionWord>();
            for (int i = 1; i < wrappedLines.Count; i++)
                remainderWords.AddRange(wrappedLines[i].Words);

            string secondLineText = string.Join(" ", remainderWords.Select(w => w.Text));
            float secondLineWidth = MeasureTextWidth(secondLineText, font, paint);
            float lineHeight = fontSize * 1.25f;

            wrappedLines = new List<CaptionLayoutLine>
            {
                firstLine,
                new CaptionLayoutLine(0, 0, secondLineWidth, lineHeight, secondLineText, remainderWords)
            };
        }

        double totalTextHeight = wrappedLines.Sum(l => l.Height);
        double maxLineWidth = wrappedLines.Count > 0 ? wrappedLines.Max(l => l.Width) : 0;
        double boxWidth = Math.Min(maxTextWidth, maxLineWidth + 40.0);
        double boxHeight = totalTextHeight + 24.0;
        double boxX = (outputW - boxWidth) / 2.0;
        double boxY = ComputeDefaultBoxY(outputH, boxHeight, isPortrait, position);

        // Dynamic collision avoidance over active HUD rectangles
        if (hudRects != null && hudRects.Count > 0 && position != CaptionPosition.BottomVoid)
        {
            boxY = AvoidHudCollisions(boxX, boxY, boxWidth, boxHeight, outputH, isPortrait, position, hudRects);
        }

        // Position individual lines vertically centered within the box
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

    private static double ComputeDefaultBoxY(int outputH, double boxHeight, bool isPortrait, CaptionPosition position)
    {
        // AICAPTION_LAYOUT_02 — BottomContent (the default) is centred on the 2/3 line of the canvas.
        double defaultY = outputH * DefaultCenterFraction - boxHeight / 2.0;
        // CAPTIONPLACE_01 — Middle: the box's TOP sits just under the centre line.
        double middleY = outputH * MiddleTopFraction;
        if (isPortrait)
        {
            return position switch
            {
                CaptionPosition.Top => PortraitVoidTop + 40.0,
                CaptionPosition.BottomVoid => (outputH - PortraitVoidBottom) + ((PortraitVoidBottom - boxHeight) / 2.0),
                CaptionPosition.Middle => Math.Clamp(middleY, PortraitVoidTop + 20.0, outputH - PortraitVoidBottom - boxHeight - 20.0),
                _ => Math.Clamp(defaultY, PortraitVoidTop + 20.0, outputH - PortraitVoidBottom - boxHeight - 20.0),
            };
        }

        // Landscape
        return position switch
        {
            CaptionPosition.Top => 60.0,
            CaptionPosition.Middle => Math.Clamp(middleY, 40.0, outputH - boxHeight - 40.0),
            _ => Math.Clamp(defaultY, 40.0, outputH - boxHeight - 40.0),
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

        // AICAPTION_LAYOUT_02 — repeat until clear: moving off one HUD piece can land on another (the old single
        // pass stopped after the first move). Bounded so a crowded layout can never loop.
        for (int pass = 0; pass < 8; pass++)
        {
            bool moved = false;
            foreach (var hud in hudRects)
            {
                if (hud.W <= 1 || hud.H <= 1) continue;

                bool intersectsX = (boxX < hud.X + hud.W) && (boxX + boxW > hud.X);
                bool intersectsY = (currentY < hud.Y + hud.H + HudGapPx) && (currentY + boxH + HudGapPx > hud.Y);
                if (!intersectsX || !intersectsY) continue;

                // Top and Middle move DOWN off a HUD piece (Middle must not climb onto the crosshair).
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

    private static float MeasureTextWidth(string text, SKFont font, SKPaint paint)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        font.MeasureText(text, out SKRect bounds, paint);
        if (bounds.Width > 0.001f) return bounds.Width;

        // Fallback approximation if font measurement returns zero (e.g. headless without font cache)
        return text.Length * (font.Size * 0.55f);
    }
}
