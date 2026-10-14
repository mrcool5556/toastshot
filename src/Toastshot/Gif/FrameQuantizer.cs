namespace Toastshot.Gif;

/// <summary>How hard the GIF encoder works for quality per byte.</summary>
/// <param name="Tolerance">
/// Pixels within this distance (largest channel difference) of what is already on screen are written as transparent,
/// so the previous frame shows through and they cost almost nothing. -1 turns it off (every pixel in the region is written).
/// </param>
/// <param name="RefinePasses">k-means passes that move each palette colour to the centre of the pixels it stands for.</param>
/// <param name="Dither">A fixed 4×4 ordered pattern that smooths gradients; it doesn't flicker between frames.</param>
public sealed record GifEncoderOptions(int Tolerance = -1, int RefinePasses = 0, bool Dither = false)
{
    /// <summary>For shrinking finished recordings: near-identical pixels reused, refined palettes, no dithering.</summary>
    public static GifEncoderOptions Quality { get; } = new(Tolerance: 8, RefinePasses: 1, Dither: false);

    /// <summary>Asks the shrinker for the plain encoder (whole regions, median cut): for comparisons.</summary>
    internal static GifEncoderOptions Basic { get; } = new(Tolerance: int.MinValue);
}

/// <summary>
/// Palette and indices for the pixels of a region that are marked opaque. Exact palette when the colours fit, else
/// median cut over a 15-bit histogram, optionally refined with k-means, mapped by nearest colour.
/// </summary>
public static class FrameQuantizer
{
    private const int Bins = 1 << 15;

    // 4×4 Bayer matrix, centred on zero, in 1/16 steps.
    private static readonly int[] Bayer = [0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5];

    /// <param name="mask">True where the pixel is written (row-major over <paramref name="rect"/>); null means all.</param>
    /// <param name="maxColors">At most this many colours (255 leaves room for a transparent index).</param>
    public static QuantizedImage Quantize(ReadOnlySpan<byte> bgra, int stride, PixelRect rect, bool[]? mask, int maxColors,
        int refinePasses, bool dither)
    {
        var exact = TryExact(bgra, stride, rect, mask, maxColors);
        if (exact is not null)
        {
            return exact;
        }

        var counts = new int[Bins];
        var sumR = new long[Bins];
        var sumG = new long[Bins];
        var sumB = new long[Bins];
        var k = 0;
        for (var y = 0; y < rect.Height; y++)
        {
            var row = (rect.Y + y) * stride + rect.X * 4;
            for (var x = 0; x < rect.Width; x++, k++)
            {
                if (mask is not null && !mask[k])
                {
                    continue;
                }

                var p = row + x * 4;
                byte b = bgra[p], g = bgra[p + 1], r = bgra[p + 2];
                var bin = Bin(r, g, b);
                counts[bin]++;
                sumR[bin] += r;
                sumG[bin] += g;
                sumB[bin] += b;
            }
        }

        var entries = Enumerable.Range(0, Bins).Where(i => counts[i] > 0).ToArray();
        var palette = MedianCut(entries, counts, sumR, sumG, sumB, maxColors, out var colorCount);

        // Bin colours (means) for nearest-colour mapping and refinement.
        var binR = new byte[Bins];
        var binG = new byte[Bins];
        var binB = new byte[Bins];
        foreach (var bin in entries)
        {
            binR[bin] = (byte)(sumR[bin] / counts[bin]);
            binG[bin] = (byte)(sumG[bin] / counts[bin]);
            binB[bin] = (byte)(sumB[bin] / counts[bin]);
        }

        for (var pass = 0; pass < refinePasses; pass++)
        {
            var nr = new long[colorCount];
            var ng = new long[colorCount];
            var nb = new long[colorCount];
            var nn = new long[colorCount];
            foreach (var bin in entries)
            {
                var i = Nearest(palette, colorCount, binR[bin], binG[bin], binB[bin]);
                nr[i] += sumR[bin];
                ng[i] += sumG[bin];
                nb[i] += sumB[bin];
                nn[i] += counts[bin];
            }

            for (var i = 0; i < colorCount; i++)
            {
                if (nn[i] > 0)
                {
                    palette[i * 3] = (byte)(nr[i] / nn[i]);
                    palette[i * 3 + 1] = (byte)(ng[i] / nn[i]);
                    palette[i * 3 + 2] = (byte)(nb[i] / nn[i]);
                }
            }
        }

        // Nearest palette colour per 15-bit bin, filled on demand (dithering reaches bins no pixel had).
        var lookup = new short[Bins];
        Array.Fill(lookup, (short)-1);
        byte Map(int r, int g, int b)
        {
            var bin = Bin((byte)r, (byte)g, (byte)b);
            var hit = lookup[bin];
            if (hit < 0)
            {
                hit = (short)Nearest(palette, colorCount, (byte)r, (byte)g, (byte)b);
                lookup[bin] = hit;
            }

            return (byte)hit;
        }

        var indices = new byte[rect.Width * rect.Height];
        k = 0;
        for (var y = 0; y < rect.Height; y++)
        {
            var row = (rect.Y + y) * stride + rect.X * 4;
            for (var x = 0; x < rect.Width; x++, k++)
            {
                if (mask is not null && !mask[k])
                {
                    continue;
                }

                var p = row + x * 4;
                int b = bgra[p], g = bgra[p + 1], r = bgra[p + 2];
                if (dither)
                {
                    // Position-fixed offset of about ±1 histogram step, so it doesn't crawl from frame to frame.
                    var offset = (Bayer[(((rect.Y + y) & 3) << 2) | ((rect.X + x) & 3)] - 8) / 2;
                    r = Math.Clamp(r + offset, 0, 255);
                    g = Math.Clamp(g + offset, 0, 255);
                    b = Math.Clamp(b + offset, 0, 255);
                }

                indices[k] = Map(r, g, b);
            }
        }

        return new QuantizedImage(palette, colorCount, indices);
    }

    private static QuantizedImage? TryExact(ReadOnlySpan<byte> bgra, int stride, PixelRect rect, bool[]? mask, int maxColors)
    {
        var lookup = new Dictionary<int, byte>(maxColors);
        var palette = new byte[256 * 3];
        var indices = new byte[rect.Width * rect.Height];
        var k = 0;
        for (var y = 0; y < rect.Height; y++)
        {
            var row = (rect.Y + y) * stride + rect.X * 4;
            for (var x = 0; x < rect.Width; x++, k++)
            {
                if (mask is not null && !mask[k])
                {
                    continue;
                }

                var p = row + x * 4;
                var color = bgra[p + 2] << 16 | bgra[p + 1] << 8 | bgra[p];
                if (!lookup.TryGetValue(color, out var index))
                {
                    if (lookup.Count == maxColors)
                    {
                        return null;
                    }

                    index = (byte)lookup.Count;
                    lookup[color] = index;
                    palette[index * 3] = (byte)(color >> 16);
                    palette[index * 3 + 1] = (byte)(color >> 8);
                    palette[index * 3 + 2] = (byte)color;
                }

                indices[k] = index;
            }
        }

        return new QuantizedImage(palette, Math.Max(1, lookup.Count), indices);
    }

    private static byte[] MedianCut(int[] entries, int[] counts, long[] sumR, long[] sumG, long[] sumB, int maxColors, out int colorCount)
    {
        var boxes = new List<(int Start, int End)> { (0, entries.Length) };
        while (boxes.Count < maxColors)
        {
            var best = -1;
            long bestScore = 0;
            var bestChannel = 0;
            for (var i = 0; i < boxes.Count; i++)
            {
                var (start, end) = boxes[i];
                if (end - start < 2)
                {
                    continue;
                }

                var (channel, range) = WidestChannel(entries, start, end);
                long pixels = 0;
                for (var j = start; j < end; j++)
                {
                    pixels += counts[entries[j]];
                }

                var score = range * (long)Math.Sqrt(pixels);
                if (score > bestScore)
                {
                    (best, bestScore, bestChannel) = (i, score, channel);
                }
            }

            if (best < 0)
            {
                break;
            }

            var box = boxes[best];
            var shift = bestChannel * 5;
            Array.Sort(entries, box.Start, box.End - box.Start, Comparer<int>.Create((a, c) => ((a >> shift) & 31).CompareTo((c >> shift) & 31)));
            long total = 0;
            for (var j = box.Start; j < box.End; j++)
            {
                total += counts[entries[j]];
            }

            long acc = 0;
            var split = box.Start + 1;
            for (var j = box.Start; j < box.End - 1; j++)
            {
                acc += counts[entries[j]];
                split = j + 1;
                if (acc * 2 >= total)
                {
                    break;
                }
            }

            boxes[best] = (box.Start, split);
            boxes.Add((split, box.End));
        }

        colorCount = boxes.Count;
        var palette = new byte[256 * 3];
        for (var i = 0; i < boxes.Count; i++)
        {
            long n = 0, r = 0, g = 0, b = 0;
            for (var j = boxes[i].Start; j < boxes[i].End; j++)
            {
                var bin = entries[j];
                n += counts[bin];
                r += sumR[bin];
                g += sumG[bin];
                b += sumB[bin];
            }

            palette[i * 3] = (byte)(r / n);
            palette[i * 3 + 1] = (byte)(g / n);
            palette[i * 3 + 2] = (byte)(b / n);
        }

        return palette;
    }

    private static int Nearest(byte[] palette, int count, byte r, byte g, byte b)
    {
        var best = 0;
        var bestDistance = int.MaxValue;
        for (var i = 0; i < count; i++)
        {
            int dr = palette[i * 3] - r, dg = palette[i * 3 + 1] - g, db = palette[i * 3 + 2] - b;
            var d = dr * dr * 3 + dg * dg * 4 + db * db * 2; // rough perceptual weights
            if (d < bestDistance)
            {
                (best, bestDistance) = (i, d);
            }
        }

        return best;
    }

    private static int Bin(byte r, byte g, byte b) => (r >> 3) << 10 | (g >> 3) << 5 | (b >> 3);

    /// <summary>Channel 0 = blue, 1 = green, 2 = red (matches the bit layout of <see cref="Bin"/>).</summary>
    private static (int Channel, int Range) WidestChannel(int[] entries, int start, int end)
    {
        Span<int> min = stackalloc int[] { 31, 31, 31 };
        Span<int> max = stackalloc int[] { 0, 0, 0 };
        for (var j = start; j < end; j++)
        {
            for (var c = 0; c < 3; c++)
            {
                var v = (entries[j] >> (c * 5)) & 31;
                min[c] = Math.Min(min[c], v);
                max[c] = Math.Max(max[c], v);
            }
        }

        var channel = 0;
        for (var c = 1; c < 3; c++)
        {
            if (max[c] - min[c] > max[channel] - min[channel])
            {
                channel = c;
            }
        }

        return (channel, max[channel] - min[channel]);
    }
}
