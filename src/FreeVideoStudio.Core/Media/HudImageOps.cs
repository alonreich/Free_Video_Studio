using System;
using System.Collections.Generic;

namespace FreeVideoStudio.Core.Media;

internal static class HudImageOps
{
    public static byte[] BgrToGray(byte[] bgr, int width, int height)
    {
        var gray = new byte[width * height];
        for (int i = 0, p = 0; i < gray.Length; i++, p += 3)
        {
            gray[i] = (byte)((bgr[p] * 0.114 + bgr[p + 1] * 0.587 + bgr[p + 2] * 0.299) + 0.5);
        }
        return gray;
    }

    public static byte[] BgrToHsv(byte[] bgr, int width, int height)
    {
        var hsv = new byte[width * height * 3];
        for (int p = 0; p < hsv.Length; p += 3)
        {
            int b = bgr[p];
            int g = bgr[p + 1];
            int r = bgr[p + 2];

            int max = r > g ? (r > b ? r : b) : (g > b ? g : b);
            int min = r < g ? (r < b ? r : b) : (g < b ? g : b);
            int delta = max - min;

            int v = max;
            int sat = max == 0 ? 0 : (int)((delta * 255.0) / max + 0.5);

            double h;
            if (delta == 0) h = 0;
            else if (max == r) h = 60.0 * (g - b) / delta;
            else if (max == g) h = 120.0 + 60.0 * (b - r) / delta;
            else h = 240.0 + 60.0 * (r - g) / delta;

            if (h < 0) h += 360.0;

            hsv[p] = (byte)(h * 0.5 + 0.5);
            hsv[p + 1] = (byte)sat;
            hsv[p + 2] = (byte)v;
        }
        return hsv;
    }

    private static double[] GaussianKernel(int size, double sigma)
    {
        if (sigma <= 0) sigma = 0.3 * ((size - 1) * 0.5 - 1) + 0.8;

        var k = new double[size];
        int half = size / 2;
        double sum = 0;
        double twoSigmaSq = 2.0 * sigma * sigma;

        for (int i = 0; i < size; i++)
        {
            int d = i - half;
            k[i] = Math.Exp(-(d * d) / twoSigmaSq);
            sum += k[i];
        }
        for (int i = 0; i < size; i++) k[i] /= sum;
        return k;
    }

    private static double[] GaussianBlurToDouble(byte[] src, int width, int height, int size, double sigma)
    {
        double[] k = GaussianKernel(size, sigma);
        int half = size / 2;

        var tmp = new double[width * height];
        var dst = new double[width * height];

        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                double acc = 0;
                for (int t = -half; t <= half; t++)
                {
                    int sx = x + t;
                    if (sx < 0) sx = 0;
                    else if (sx >= width) sx = width - 1;
                    acc += k[t + half] * src[row + sx];
                }
                tmp[row + x] = acc;
            }
        }

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double acc = 0;
                for (int t = -half; t <= half; t++)
                {
                    int sy = y + t;
                    if (sy < 0) sy = 0;
                    else if (sy >= height) sy = height - 1;
                    acc += k[t + half] * tmp[sy * width + x];
                }
                dst[y * width + x] = acc;
            }
        }

        return dst;
    }

    public static byte[] GaussianBlur(byte[] src, int width, int height, int size, double sigma)
    {
        double[] blurred = GaussianBlurToDouble(src, width, height, size, sigma);
        var dst = new byte[src.Length];
        for (int i = 0; i < dst.Length; i++)
        {
            int v = (int)(blurred[i] + 0.5);
            dst[i] = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
        }
        return dst;
    }

    public static byte[] AdaptiveThresholdGaussianInv(byte[] src, int width, int height, int blockSize, double c)
    {
        if ((blockSize & 1) == 0) blockSize++;

        double[] mean = GaussianBlurToDouble(src, width, height, blockSize, 0);
        var dst = new byte[src.Length];
        for (int i = 0; i < dst.Length; i++)
        {
            dst[i] = src[i] > mean[i] - c ? (byte)0 : (byte)255;
        }
        return dst;
    }

    public static byte[] ThresholdBinary(byte[] src, byte thresh)
    {
        var dst = new byte[src.Length];
        for (int i = 0; i < dst.Length; i++) dst[i] = src[i] > thresh ? (byte)255 : (byte)0;
        return dst;
    }

    public static byte[] NormalizeMinMaxToByte(double[] src, bool invert = false)
    {
        double min = double.MaxValue, max = double.MinValue;
        foreach (double v in src)
        {
            if (v < min) min = v;
            if (v > max) max = v;
        }

        var dst = new byte[src.Length];
        double range = max - min;
        if (range <= double.Epsilon)
        {
            if (invert) Array.Fill(dst, (byte)255);
            return dst;
        }

        double scale = 255.0 / range;
        if (invert)
        {
            for (int i = 0; i < src.Length; i++)
            {
                int v = 255 - (int)((src[i] - min) * scale + 0.5);
                dst[i] = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
            }
        }
        else
        {
            for (int i = 0; i < src.Length; i++)
            {
                int v = (int)((src[i] - min) * scale + 0.5);
                dst[i] = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
            }
        }
        return dst;
    }

    public static byte[] Dilate(byte[] src, int width, int height, int kw, int kh)
        => RankFilter(src, width, height, kw, kh, max: true);

    public static byte[] Erode(byte[] src, int width, int height, int kw, int kh)
        => RankFilter(src, width, height, kw, kh, max: false);

    public static byte[] MorphClose(byte[] src, int width, int height, int kw, int kh)
        => Erode(Dilate(src, width, height, kw, kh), width, height, kw, kh);

    public static byte[] MorphOpen(byte[] src, int width, int height, int kw, int kh)
        => Dilate(Erode(src, width, height, kw, kh), width, height, kw, kh);

    private static byte[] RankFilter(byte[] src, int width, int height, int kw, int kh, bool max)
    {
        int halfW = kw / 2;
        int halfH = kh / 2;

        var tmp = new byte[src.Length];
        var dst = new byte[src.Length];

        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int lo = x - halfW; if (lo < 0) lo = 0;
                int hi = x + halfW; if (hi >= width) hi = width - 1;

                byte best = src[row + lo];
                for (int sx = lo + 1; sx <= hi; sx++)
                {
                    byte v = src[row + sx];
                    if (max ? v > best : v < best) best = v;
                }
                tmp[row + x] = best;
            }
        }

        for (int y = 0; y < height; y++)
        {
            int lo = y - halfH; if (lo < 0) lo = 0;
            int hi = y + halfH; if (hi >= height) hi = height - 1;

            for (int x = 0; x < width; x++)
            {
                byte best = tmp[lo * width + x];
                for (int sy = lo + 1; sy <= hi; sy++)
                {
                    byte v = tmp[sy * width + x];
                    if (max ? v > best : v < best) best = v;
                }
                dst[y * width + x] = best;
            }
        }

        return dst;
    }

    public static byte[] Or(byte[] a, byte[] b)
    {
        var dst = new byte[a.Length];
        for (int i = 0; i < dst.Length; i++) dst[i] = (byte)(a[i] | b[i]);
        return dst;
    }

    public static byte[] And(byte[] a, byte[] b)
    {
        var dst = new byte[a.Length];
        for (int i = 0; i < dst.Length; i++) dst[i] = (byte)(a[i] & b[i]);
        return dst;
    }

    public static byte[] Canny(byte[] src, int width, int height, int low, int high)
    {
        var mag = new int[width * height];
        var dir = new byte[width * height];

        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                int i = y * width + x;
                int tl = src[i - width - 1], t = src[i - width], tr = src[i - width + 1];
                int l = src[i - 1], r = src[i + 1];
                int bl = src[i + width - 1], b = src[i + width], br = src[i + width + 1];

                int gx = (tr + 2 * r + br) - (tl + 2 * l + bl);
                int gy = (bl + 2 * b + br) - (tl + 2 * t + tr);

                mag[i] = Math.Abs(gx) + Math.Abs(gy);

                int ax = Math.Abs(gx), ay = Math.Abs(gy);
                if (ay <= ax * 0.4142135623730951) dir[i] = 0;
                else if (ax <= ay * 0.4142135623730951) dir[i] = 2;
                else dir[i] = (byte)((gx > 0) == (gy > 0) ? 1 : 3);
            }
        }

        var keep = new bool[width * height];
        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                int i = y * width + x;
                int m = mag[i];
                if (m < low) continue;

                int a, b2;
                switch (dir[i])
                {
                    case 0: a = mag[i - 1]; b2 = mag[i + 1]; break;
                    case 2: a = mag[i - width]; b2 = mag[i + width]; break;
                    case 1: a = mag[i - width + 1]; b2 = mag[i + width - 1]; break;
                    default: a = mag[i - width - 1]; b2 = mag[i + width + 1]; break;
                }

                if (m >= a && m >= b2) keep[i] = true;
            }
        }

        var dst = new byte[width * height];
        var stack = new Stack<int>();

        for (int i = 0; i < dst.Length; i++)
        {
            if (keep[i] && mag[i] >= high && dst[i] == 0)
            {
                dst[i] = 255;
                stack.Push(i);

                while (stack.Count > 0)
                {
                    int p = stack.Pop();
                    int py = p / width, px = p - py * width;

                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int ny = py + dy;
                        if (ny < 1 || ny >= height - 1) continue;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = px + dx;
                            if (nx < 1 || nx >= width - 1) continue;

                            int q = ny * width + nx;
                            if (dst[q] != 0 || !keep[q] || mag[q] < low) continue;

                            dst[q] = 255;
                            stack.Push(q);
                        }
                    }
                }
            }
        }

        return dst;
    }

    public static (int[] Gx, int[] Gy) Sobel(byte[] src, int width, int height)
    {
        var gx = new int[width * height];
        var gy = new int[width * height];

        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                int i = y * width + x;
                int tl = src[i - width - 1], t = src[i - width], tr = src[i - width + 1];
                int l = src[i - 1], r = src[i + 1];
                int bl = src[i + width - 1], b = src[i + width], br = src[i + width + 1];

                gx[i] = (tr + 2 * r + br) - (tl + 2 * l + bl);
                gy[i] = (bl + 2 * b + br) - (tl + 2 * t + tr);
            }
        }

        return (gx, gy);
    }

    internal readonly record struct Blob(int X, int Y, int Width, int Height, int Area);

    public static List<Blob> FindBlobs(byte[] mask, int width, int height)
    {
        var blobs = new List<Blob>();
        var seen = new bool[mask.Length];
        var stack = new Stack<int>();

        for (int start = 0; start < mask.Length; start++)
        {
            if (mask[start] == 0 || seen[start]) continue;

            seen[start] = true;
            stack.Push(start);

            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            int area = 0;

            while (stack.Count > 0)
            {
                int p = stack.Pop();
                int py = p / width, px = p - py * width;

                area++;
                if (px < minX) minX = px;
                if (px > maxX) maxX = px;
                if (py < minY) minY = py;
                if (py > maxY) maxY = py;

                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = py + dy;
                    if (ny < 0 || ny >= height) continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = px + dx;
                        if (nx < 0 || nx >= width) continue;

                        int q = ny * width + nx;
                        if (seen[q] || mask[q] == 0) continue;

                        seen[q] = true;
                        stack.Push(q);
                    }
                }
            }

            blobs.Add(new Blob(minX, minY, maxX - minX + 1, maxY - minY + 1, area));
        }

        return blobs;
    }

    public static (int NonZero, int Total) CountNonZero(byte[] mask, int width, int height, int x, int y, int w, int h)
    {
        int x0 = Math.Max(0, x);
        int y0 = Math.Max(0, y);
        int x1 = Math.Min(width, x + w);
        int y1 = Math.Min(height, y + h);

        if (x1 <= x0 || y1 <= y0) return (0, 0);

        int count = 0;
        for (int yy = y0; yy < y1; yy++)
        {
            int row = yy * width;
            for (int xx = x0; xx < x1; xx++)
            {
                if (mask[row + xx] != 0) count++;
            }
        }

        return (count, (x1 - x0) * (y1 - y0));
    }
}
