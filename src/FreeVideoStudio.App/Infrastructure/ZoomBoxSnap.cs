using System;
using Avalonia;

namespace FreeVideoStudio.App.Infrastructure;

internal enum ZoomGuideState { Hidden, Near, Snapped }

internal readonly record struct ZoomSnapResult(Rect Box, ZoomGuideState Vertical, ZoomGuideState Horizontal);

internal static class ZoomBoxSnap
{
    internal const double AxisSnapPx = 8;
    internal const double CenterSnapPx = 14;
    internal const double GuideShowPx = 28;

    internal static ZoomSnapResult Snap(Rect box, Rect bounds, bool allowSnap)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return new ZoomSnapResult(box, ZoomGuideState.Hidden, ZoomGuideState.Hidden);
        double cx = bounds.X + bounds.Width / 2.0, cy = bounds.Y + bounds.Height / 2.0;
        double dx = cx - (box.X + box.Width / 2.0);
        double dy = cy - (box.Y + box.Height / 2.0);
        double ax = Math.Abs(dx), ay = Math.Abs(dy);
        bool both = allowSnap && Math.Sqrt(dx * dx + dy * dy) <= CenterSnapPx;
        bool snapX = allowSnap && (both || ax <= AxisSnapPx);
        bool snapY = allowSnap && (both || ay <= AxisSnapPx);
        double nx = snapX ? box.X + dx : box.X;
        double ny = snapY ? box.Y + dy : box.Y;
        nx = Math.Clamp(nx, bounds.X, Math.Max(bounds.X, bounds.Right - box.Width));
        ny = Math.Clamp(ny, bounds.Y, Math.Max(bounds.Y, bounds.Bottom - box.Height));
        var snapped = new Rect(nx, ny, box.Width, box.Height);
        double rdx = Math.Abs(cx - (snapped.X + snapped.Width / 2.0));
        double rdy = Math.Abs(cy - (snapped.Y + snapped.Height / 2.0));
        ZoomGuideState v = rdx < 0.5 ? ZoomGuideState.Snapped : rdx <= GuideShowPx ? ZoomGuideState.Near : ZoomGuideState.Hidden;
        ZoomGuideState h = rdy < 0.5 ? ZoomGuideState.Snapped : rdy <= GuideShowPx ? ZoomGuideState.Near : ZoomGuideState.Hidden;
        return new ZoomSnapResult(snapped, v, h);
    }
}
