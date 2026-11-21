using System;
using System.Collections.Generic;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.Core.LocalAi;

public sealed record LocalDiscoveredCandidate(
    int Id,
    string Label,
    int FrameIndex,
    int Ymin,
    int Xmin,
    int Ymax,
    int Xmax,
    double Score,
    bool IsDetectorMatch = false);

public static class LocalSubjectDiscovery
{
    public const string DiscoveryTag = "LOCALDISCOVER_01";
    public const int GridW = 32;
    public const int GridH = 18;
    public const int MaxCandidates = 6;

    public static IReadOnlyList<LocalDiscoveredCandidate> DiscoverOnFrames(
        IReadOnlyList<byte[]> frameRgbs,
        int width,
        int height,
        IReadOnlyList<(byte[] PrevRgb, byte[] NextRgb)>? motionPairs,
        AiGameProfile profile,
        YoloOnnxDetector? detector = null,
        Sam2BoxTracker? sam2 = null)
    {
        var results = new List<LocalDiscoveredCandidate>();
        if (frameRgbs == null || frameRgbs.Count == 0 || width <= 0 || height <= 0) return results;

        var hudZones = AiHudAvoidZones.DefaultsFor(profile);
        var raw = new List<(int FrameIndex, PixelBox Box, string Category, bool IsMoving, double Score, bool IsDetector)>();

        for (int f = 0; f < frameRgbs.Count; f++)
        {
            byte[] rgb = frameRgbs[f];
            if (rgb == null || rgb.Length < width * height * 3) continue;
            int frameIdx = f + 1;

            if (detector != null)
            {
                var detected = detector.Detect(rgb, width, height, width, height, profile);
                foreach (var d in detected)
                {
                    if (OverlapsHud(d.Box, width, height, hudZones)) continue;
                    PixelBox refined = RefineWithSam2(sam2, rgb, width, height, d.Box);
                    raw.Add((frameIdx, refined, "Detected " + d.Category, true, d.Confidence + 0.35, true));
                }
            }

            byte[]? prev = motionPairs != null && f < motionPairs.Count ? motionPairs[f].PrevRgb : null;
            byte[]? next = motionPairs != null && f < motionPairs.Count ? motionPairs[f].NextRgb : null;
            var spots = FindMotionAndSalienceSpots(rgb, prev, next, width, height, profile, hudZones);
            foreach (var s in spots)
            {
                PixelBox refined = RefineWithSam2(sam2, rgb, width, height, s.Box);
                if (LocalTrackingMath.LooksLikeOwnBody(profile, refined, width, height)) continue;
                string category = s.IsMoving ? "Motion region" : "Standout region";
                raw.Add((frameIdx, refined, category, s.IsMoving, s.Score, false));
            }
        }

        raw.Sort((a, b) => b.Score.CompareTo(a.Score));
        var selected = new List<(int FrameIndex, PixelBox Box, string Category, bool IsMoving, double Score, bool IsDetector)>();
        foreach (var cand in raw)
        {
            bool duplicate = false;
            foreach (var kept in selected)
            {
                if (kept.FrameIndex == cand.FrameIndex && LocalTrackingMath.Iou(kept.Box, cand.Box) >= 0.30)
                {
                    duplicate = true;
                    break;
                }
                if (kept.FrameIndex != cand.FrameIndex && LocalTrackingMath.Iou(kept.Box, cand.Box) >= 0.65)
                {
                    duplicate = true;
                    break;
                }
            }
            if (!duplicate)
            {
                selected.Add(cand);
                if (selected.Count >= MaxCandidates) break;
            }
        }

        if (selected.Count == 0 && frameRgbs.Count > 0)
        {
            var fallbackSpots = BuildDefaultRegions(width, height, profile);
            for (int i = 0; i < fallbackSpots.Count && selected.Count < 3; i++)
            {
                int fi = Math.Min(frameRgbs.Count, (i % 3) + 1);
                selected.Add((fi, fallbackSpots[i].Box, "Suggested area", false, 0.25, false));
            }
        }

        for (int i = 0; i < selected.Count; i++)
        {
            var item = selected[i];
            int id = i + 1;
            string pos = DescribePosition(item.Box, width, height);
            string label = $"{item.Category} #{id} ({pos})";
            int xmin = ClampGrid(item.Box.X1, width);
            int ymin = ClampGrid(item.Box.Y1, height);
            int xmax = Math.Max(xmin + 10, ClampGrid(item.Box.X2, width));
            int ymax = Math.Max(ymin + 10, ClampGrid(item.Box.Y2, height));
            results.Add(new LocalDiscoveredCandidate(
                id, label, item.FrameIndex,
                Math.Clamp(ymin, 0, 990), Math.Clamp(xmin, 0, 990),
                Math.Clamp(ymax, 10, 1000), Math.Clamp(xmax, 10, 1000),
                item.Score,
                item.IsDetector));
        }

        return results;
    }

    public static LocalDiscoveredCandidate SnapAtPoint(
        ReadOnlySpan<byte> rgb,
        int width,
        int height,
        int frameIndex,
        int clickX1000,
        int clickY1000,
        int nextId,
        AiGameProfile profile,
        Sam2BoxTracker? sam2 = null)
    {
        int cx1000 = Math.Clamp(clickX1000, 20, 980);
        int cy1000 = Math.Clamp(clickY1000, 20, 980);
        double px = cx1000 / 1000.0 * Math.Max(1, width);
        double py = cy1000 / 1000.0 * Math.Max(1, height);

        PixelBox box;
        if (!rgb.IsEmpty && width > 0 && height > 0 && rgb.Length >= width * height * 3)
        {
            box = GrowColorRegion(rgb, width, height, (int)Math.Round(px), (int)Math.Round(py), profile);
            box = RefineWithSam2(sam2, rgb, width, height, box);
        }
        else
        {
            double halfW = width * 0.08;
            double halfH = height * (profile == AiGameProfile.Generic ? 0.14 : 0.18);
            box = new PixelBox(px - halfW, py - halfH, px + halfW, py + halfH).Clamp(Math.Max(1, width), Math.Max(1, height));
        }

        int xmin = Math.Clamp(ClampGrid(box.X1, Math.Max(1, width)), 0, 960);
        int ymin = Math.Clamp(ClampGrid(box.Y1, Math.Max(1, height)), 0, 960);
        int xmax = Math.Clamp(Math.Max(xmin + 40, ClampGrid(box.X2, Math.Max(1, width))), 40, 1000);
        int ymax = Math.Clamp(Math.Max(ymin + 50, ClampGrid(box.Y2, Math.Max(1, height))), 50, 1000);
        string kind = profile == AiGameProfile.Generic ? "Selected Subject" : "Selected Character";
        string pos = DescribePosition(box, Math.Max(1, width), Math.Max(1, height));
        return new LocalDiscoveredCandidate(nextId, $"{kind} #{nextId} ({pos})", Math.Clamp(frameIndex, 1, 3), ymin, xmin, ymax, xmax, 1.0);
    }

    private static List<(PixelBox Box, bool IsMoving, double Score)> FindMotionAndSalienceSpots(
        byte[] rgb,
        byte[]? prevRgb,
        byte[]? nextRgb,
        int width,
        int height,
        AiGameProfile profile,
        IReadOnlyList<AiNormalizedRect> hudZones)
    {
        double[,] cellMotion = new double[GridH, GridW];
        double[,] cellContrast = new double[GridH, GridW];
        bool hasMotionPair = (prevRgb != null && prevRgb.Length >= width * height * 3)
                          || (nextRgb != null && nextRgb.Length >= width * height * 3);

        var motionValues = new List<double>(GridW * GridH);
        double globalR = 0, globalG = 0, globalB = 0;
        int totalSamples = 0;
        int stepY = Math.Max(1, height / (GridH * 4));
        int stepX = Math.Max(1, width / (GridW * 4));

        for (int y = 0; y < height; y += stepY)
        {
            int row = y * width;
            for (int x = 0; x < width; x += stepX)
            {
                int idx = (row + x) * 3;
                globalR += rgb[idx];
                globalG += rgb[idx + 1];
                globalB += rgb[idx + 2];
                totalSamples++;
            }
        }
        if (totalSamples > 0)
        {
            globalR /= totalSamples;
            globalG /= totalSamples;
            globalB /= totalSamples;
        }

        for (int gy = 0; gy < GridH; gy++)
        {
            int y0 = gy * height / GridH;
            int y1 = Math.Max(y0 + 1, (gy + 1) * height / GridH);
            for (int gx = 0; gx < GridW; gx++)
            {
                int x0 = gx * width / GridW;
                int x1 = Math.Max(x0 + 1, (gx + 1) * width / GridW);
                double mSum = 0, cSum = 0;
                int count = 0;
                int sy = Math.Max(1, (y1 - y0) / 4);
                int sx = Math.Max(1, (x1 - x0) / 4);
                for (int y = y0; y < y1; y += sy)
                {
                    int row = y * width;
                    for (int x = x0; x < x1; x += sx)
                    {
                        int idx = (row + x) * 3;
                        int r = rgb[idx], g = rgb[idx + 1], b = rgb[idx + 2];
                        cSum += (Math.Abs(r - globalR) + Math.Abs(g - globalG) + Math.Abs(b - globalB)) / (3.0 * 255.0);
                        if (prevRgb != null && prevRgb.Length >= width * height * 3)
                        {
                            mSum += (Math.Abs(r - prevRgb[idx]) + Math.Abs(g - prevRgb[idx + 1]) + Math.Abs(b - prevRgb[idx + 2])) / (3.0 * 255.0);
                        }
                        if (nextRgb != null && nextRgb.Length >= width * height * 3)
                        {
                            mSum += (Math.Abs(r - nextRgb[idx]) + Math.Abs(g - nextRgb[idx + 1]) + Math.Abs(b - nextRgb[idx + 2])) / (3.0 * 255.0);
                        }
                        count++;
                    }
                }
                if (count > 0)
                {
                    double m = mSum / count;
                    double c = cSum / count;
                    cellMotion[gy, gx] = m;
                    cellContrast[gy, gx] = c;
                    motionValues.Add(m);
                }
            }
        }

        double bgMotion = 0;
        if (motionValues.Count > 0)
        {
            motionValues.Sort();
            bgMotion = motionValues[motionValues.Count / 2];
        }

        double[,] scoreGrid = new double[GridH, GridW];
        for (int gy = 0; gy < GridH; gy++)
        {
            double ny = (gy + 0.5) / GridH;
            if (ny < 0.06 || ny > 0.94) continue;
            for (int gx = 0; gx < GridW; gx++)
            {
                double nx = (gx + 0.5) / GridW;
                if (nx < 0.04 || nx > 0.96) continue;
                var cellBox = new PixelBox(gx * width / (double)GridW, gy * height / (double)GridH, (gx + 1) * width / (double)GridW, (gy + 1) * height / (double)GridH);
                if (OverlapsHud(cellBox, width, height, hudZones)) continue;
                if (LocalTrackingMath.LooksLikeOwnBody(profile, cellBox, width, height)) continue;

                double residualMotion = Math.Max(0, cellMotion[gy, gx] - bgMotion * 1.15);
                double centerWeight = 1.0 - 0.25 * (Math.Abs(nx - 0.5) + Math.Abs(ny - 0.45));
                scoreGrid[gy, gx] = hasMotionPair
                    ? (residualMotion * 2.4 + cellContrast[gy, gx] * 0.45) * centerWeight
                    : cellContrast[gy, gx] * centerWeight;
            }
        }

        var spots = new List<(PixelBox Box, bool IsMoving, double Score)>();
        bool[,] used = new bool[GridH, GridW];
        for (int pick = 0; pick < 3; pick++)
        {
            int bestGx = -1, bestGy = -1;
            double bestVal = 0.04;
            for (int gy = 1; gy < GridH - 1; gy++)
            {
                for (int gx = 1; gx < GridW - 1; gx++)
                {
                    if (used[gy, gx]) continue;
                    double v = scoreGrid[gy, gx];
                    if (v > bestVal)
                    {
                        bestVal = v;
                        bestGx = gx;
                        bestGy = gy;
                    }
                }
            }
            if (bestGx < 0) break;

            int minGx = bestGx, maxGx = bestGx, minGy = bestGy, maxGy = bestGy;
            double threshold = bestVal * 0.48;
            for (int dy = -2; dy <= 2; dy++)
            {
                int ny = bestGy + dy;
                if (ny < 0 || ny >= GridH) continue;
                for (int dx = -2; dx <= 2; dx++)
                {
                    int nx = bestGx + dx;
                    if (nx < 0 || nx >= GridW) continue;
                    used[ny, nx] = true;
                    if (scoreGrid[ny, nx] >= threshold)
                    {
                        if (nx < minGx) minGx = nx;
                        if (nx > maxGx) maxGx = nx;
                        if (ny < minGy) minGy = ny;
                        if (ny > maxGy) maxGy = ny;
                    }
                }
            }

            double x1 = minGx * width / (double)GridW;
            double y1 = minGy * height / (double)GridH;
            double x2 = (maxGx + 1) * width / (double)GridW;
            double y2 = (maxGy + 1) * height / (double)GridH;
            double minW = width * 0.08;
            double minH = height * (profile == AiGameProfile.Generic ? 0.16 : 0.20);
            double cx = (x1 + x2) * 0.5;
            double cy = (y1 + y2) * 0.5;
            double w = Math.Clamp(Math.Max(x2 - x1, minW), minW, width * 0.16);
            double h = Math.Clamp(Math.Max(y2 - y1, minH), minH, height * 0.34);
            var candidateBox = new PixelBox(cx - w * 0.5, cy - h * 0.5, cx + w * 0.5, cy + h * 0.5).Clamp(width, height);
            if (LocalTrackingMath.LooksLikeOwnBody(profile, candidateBox, width, height)) continue;

            bool moving = hasMotionPair && (cellMotion[bestGy, bestGx] - bgMotion) > 0.035;
            spots.Add((candidateBox, moving, bestVal));
        }

        return spots;
    }

    private static PixelBox GrowColorRegion(ReadOnlySpan<byte> rgb, int width, int height, int seedX, int seedY, AiGameProfile profile)
    {
        seedX = Math.Clamp(seedX, 0, width - 1);
        seedY = Math.Clamp(seedY, 0, height - 1);
        int seedIdx = (seedY * width + seedX) * 3;
        int sr = rgb[seedIdx], sg = rgb[seedIdx + 1], sb = rgb[seedIdx + 2];

        int maxRadiusX = Math.Max(12, (int)(width * 0.08));
        int maxRadiusY = Math.Max(18, (int)(height * (profile == AiGameProfile.Generic ? 0.15 : 0.18)));
        int x0 = Math.Max(0, seedX - maxRadiusX);
        int x1 = Math.Min(width - 1, seedX + maxRadiusX);
        int y0 = Math.Max(0, seedY - maxRadiusY);
        int y1 = Math.Min(height - 1, seedY + maxRadiusY);

        int minX = seedX, maxX = seedX, minY = seedY, maxY = seedY;
        int step = Math.Max(1, Math.Min(width, height) / 180);
        const int colorThreshold = 75;

        for (int y = y0; y <= y1; y += step)
        {
            int row = y * width;
            for (int x = x0; x <= x1; x += step)
            {
                int idx = (row + x) * 3;
                int dist = Math.Abs(rgb[idx] - sr) + Math.Abs(rgb[idx + 1] - sg) + Math.Abs(rgb[idx + 2] - sb);
                double normDx = (x - seedX) / (double)maxRadiusX;
                double normDy = (y - seedY) / (double)maxRadiusY;
                if (dist <= colorThreshold && (normDx * normDx + normDy * normDy) <= 1.0)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }

        double minW = width * 0.08;
        double minH = height * (profile == AiGameProfile.Generic ? 0.16 : 0.20);
        double cx = seedX * 0.65 + (minX + maxX) * 0.5 * 0.35;
        double cy = seedY * 0.65 + (minY + maxY) * 0.5 * 0.35;
        double bw = Math.Clamp(Math.Max(maxX - minX, minW), minW, width * 0.16);
        double bh = Math.Clamp(Math.Max(maxY - minY, minH), minH, height * 0.34);
        return new PixelBox(cx - bw * 0.5, cy - bh * 0.5, cx + bw * 0.5, cy + bh * 0.5).Clamp(width, height);
    }

    private static PixelBox RefineWithSam2(Sam2BoxTracker? sam2, ReadOnlySpan<byte> rgb, int width, int height, PixelBox initial)
    {
        if (sam2 == null) return initial;
        int s = sam2.InputSize;
        if (width != s || height != s || rgb.Length < s * s * 3) return initial;
        sam2.Forget();
        var (candidates, objectLogit) = sam2.Step(rgb, initial, initial.Cx, initial.Cy, width, height);
        if (objectLogit <= 0 || candidates.Count == 0) return initial;
        MaskCandidate best = candidates[0];
        for (int i = 1; i < candidates.Count; i++)
        {
            if (candidates[i].Score > best.Score) best = candidates[i];
        }
        return best.Score >= LocalTrackingMath.MinScore && !best.Box.IsEmpty ? best.Box.Clamp(width, height) : initial;
    }

    private static bool OverlapsHud(PixelBox box, int width, int height, IReadOnlyList<AiNormalizedRect> hudZones)
    {
        if (hudZones == null || hudZones.Count == 0 || width <= 0 || height <= 0) return false;
        double cx = box.Cx / width;
        double cy = box.Cy / height;
        foreach (var z in hudZones)
        {
            if (cx >= z.X && cx <= z.X + z.W && cy >= z.Y && cy <= z.Y + z.H)
                return true;
        }
        return false;
    }

    private static List<(PixelBox Box, string Category)> BuildDefaultRegions(int width, int height, AiGameProfile profile)
    {
        string kind = profile == AiGameProfile.Generic ? "Subject" : "Character";
        double h = height * 0.28;
        double w = width * 0.14;
        double cy = height * 0.42;
        return new List<(PixelBox, string)>
        {
            (new PixelBox(width * 0.50 - w * 0.5, cy - h * 0.5, width * 0.50 + w * 0.5, cy + h * 0.5).Clamp(width, height), kind),
            (new PixelBox(width * 0.28 - w * 0.5, cy - h * 0.5, width * 0.28 + w * 0.5, cy + h * 0.5).Clamp(width, height), kind),
            (new PixelBox(width * 0.72 - w * 0.5, cy - h * 0.5, width * 0.72 + w * 0.5, cy + h * 0.5).Clamp(width, height), kind),
        };
    }

    private static string DescribePosition(PixelBox box, int width, int height)
    {
        double nx = box.Cx / Math.Max(1, width);
        double ny = box.Cy / Math.Max(1, height);
        string horiz = nx < 0.36 ? "Left" : nx > 0.64 ? "Right" : "Center";
        string vert = ny < 0.34 ? "Upper " : ny > 0.68 ? "Lower " : "";
        return (vert + horiz).Trim();
    }

    private static int ClampGrid(double px, int size)
        => (int)Math.Round(Math.Clamp(px / Math.Max(1, size) * 1000.0, 0, 1000));
}
