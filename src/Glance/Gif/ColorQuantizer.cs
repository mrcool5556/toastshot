namespace Glance.Gif;

/// <summary>A palette of at most 256 colours plus one palette index per pixel.</summary>
public sealed record QuantizedImage(byte[] Palette, int ColorCount, byte[] Indices);

/// <summary>
/// Reduces a BGRA region to an indexed image. Screen content usually has few colours, so an exact
/// palette is tried first; otherwise a median cut over a 15-bit histogram is used (no dithering,
/// which looks noisy on UI and bloats GIFs). Alpha is ignored.
/// </summary>
public static class ColorQuantizer
{
    public const int MaxColors = 256;

    public static QuantizedImage Quantize(ReadOnlySpan<byte> bgra, int stride, PixelRect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            throw new ArgumentException("Region is empty.", nameof(rect));
        }

        return TryExact(bgra, stride, rect) ?? MedianCut(bgra, stride, rect);
    }

    private static QuantizedImage? TryExact(ReadOnlySpan<byte> bgra, int stride, PixelRect rect)
    {
        var lookup = new Dictionary<int, byte>(MaxColors);
        var palette = new byte[MaxColors * 3];
        var indices = new byte[rect.Width * rect.Height];
        var lastColor = -1;
        byte lastIndex = 0;
        var n = 0;

        for (var y = 0; y < rect.Height; y++)
        {
            var row = (rect.Y + y) * stride + rect.X * 4;
            for (var x = 0; x < rect.Width; x++)
            {
                var p = row + x * 4;
                var color = bgra[p + 2] << 16 | bgra[p + 1] << 8 | bgra[p];
                if (color != lastColor)
                {
                    if (!lookup.TryGetValue(color, out lastIndex))
                    {
                        if (lookup.Count == MaxColors)
                        {
                            return null;
                        }

                        lastIndex = (byte)lookup.Count;
                        lookup[color] = lastIndex;
                        palette[lastIndex * 3] = (byte)(color >> 16);
                        palette[lastIndex * 3 + 1] = (byte)(color >> 8);
                        palette[lastIndex * 3 + 2] = (byte)color;
                    }

                    lastColor = color;
                }

                indices[n++] = lastIndex;
            }
        }

        return new QuantizedImage(palette, lookup.Count, indices);
    }

    private static int Bin(byte r, byte g, byte b) => (r >> 3) << 10 | (g >> 3) << 5 | (b >> 3);

    private static QuantizedImage MedianCut(ReadOnlySpan<byte> bgra, int stride, PixelRect rect)
    {
        const int Bins = 1 << 15;
        var counts = new int[Bins];
        var sumR = new long[Bins];
        var sumG = new long[Bins];
        var sumB = new long[Bins];

        for (var y = 0; y < rect.Height; y++)
        {
            var row = (rect.Y + y) * stride + rect.X * 4;
            for (var x = 0; x < rect.Width; x++)
            {
                var p = row + x * 4;
                byte b = bgra[p], g = bgra[p + 1], r = bgra[p + 2];
                var bin = Bin(r, g, b);
                counts[bin]++;
                sumR[bin] += r;
                sumG[bin] += g;
                sumB[bin] += b;
            }
        }

        var used = new List<int>();
        for (var i = 0; i < Bins; i++)
        {
            if (counts[i] > 0)
            {
                used.Add(i);
            }
        }

        var entries = used.ToArray();
        var boxes = new List<(int Start, int End)> { (0, entries.Length) };

        while (boxes.Count < MaxColors)
        {
            // Split the box with the widest channel range, weighted by how many pixels it covers.
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
                    best = i;
                    bestScore = score;
                    bestChannel = channel;
                }
            }

            if (best < 0)
            {
                break;
            }

            var box = boxes[best];
            var shift = bestChannel * 5;
            Array.Sort(entries, box.Start, box.End - box.Start,
                Comparer<int>.Create((a, c) => ((a >> shift) & 31).CompareTo((c >> shift) & 31)));

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

        var palette = new byte[boxes.Count * 3];
        var binToIndex = new byte[Bins];
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
                binToIndex[bin] = (byte)i;
            }

            palette[i * 3] = (byte)(r / n);
            palette[i * 3 + 1] = (byte)(g / n);
            palette[i * 3 + 2] = (byte)(b / n);
        }

        var indices = new byte[rect.Width * rect.Height];
        var k = 0;
        for (var y = 0; y < rect.Height; y++)
        {
            var row = (rect.Y + y) * stride + rect.X * 4;
            for (var x = 0; x < rect.Width; x++)
            {
                var p = row + x * 4;
                indices[k++] = binToIndex[Bin(bgra[p + 2], bgra[p + 1], bgra[p])];
            }
        }

        return new QuantizedImage(palette, boxes.Count, indices);
    }

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
                if (v < min[c]) min[c] = v;
                if (v > max[c]) max[c] = v;
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
