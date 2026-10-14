using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Toastshot.Services;

namespace Toastshot.Gif;

/// <summary>How a GIF is made smaller: fewer pixels (<see cref="Scale"/>) and fewer frames (keep every <see cref="FrameStep"/>th).</summary>
public readonly record struct ShrinkPlan(double Scale, int FrameStep);

public sealed record ShrinkResult(string Path, int Width, int Height, int FrameCount, TimeSpan Duration, long FileBytes, ShrinkPlan Plan);

/// <summary>
/// Re-encodes a finished GIF smaller: to fit a file size (drop frames down to about 12 fps first, then scale the
/// picture), or at a fixed scale. Frames are decoded and composited one at a time, so memory stays small.
/// </summary>
public static class GifShrinker
{
    private const double MinScale = 0.1;
    private const double MinFramesPerSecond = 12;

    /// <summary>
    /// Re-encodes <paramref name="path"/> into <paramref name="outputPath"/> at no more than <paramref name="maxBytes"/>
    /// if it can (otherwise as small as the limits allow). Returns null when the GIF already fits.
    /// </summary>
    public static ShrinkResult? FitToSize(string path, long maxBytes, string outputPath, CancellationToken cancel = default, GifEncoderOptions? encoder = null)
    {
        var originalBytes = new FileInfo(path).Length;
        if (originalBytes <= maxBytes)
        {
            return null;
        }

        var (frames, duration) = Inspect(path);
        var fps = duration.TotalSeconds > 0 ? frames / duration.TotalSeconds : 10;
        var plan = FirstGuess(fps, (double)maxBytes / originalBytes);

        // Search the scale between "fits" and "too big" until the file uses most of the budget: bytes saved by the
        // encoder become pixels. Each try goes to its own file; the best one that fits becomes the output.
        ShrinkResult? best = null, last = null;
        double? tooBig = null;
        var tries = new List<string>();
        try
        {
            for (var attempt = 0; attempt < 6; attempt++)
            {
                cancel.ThrowIfCancellationRequested();
                var file = $"{outputPath}.{attempt}.tmp";
                tries.Add(file);
                last = Encode(path, plan, file, cancel, encoder);
                if (last.FileBytes <= maxBytes)
                {
                    if (best is null || last.Plan.Scale > best.Plan.Scale)
                    {
                        best = last;
                    }

                    if (last.FileBytes >= maxBytes * 0.9 || plan.Scale >= 1)
                    {
                        break; // close enough to the limit, or already full size
                    }

                    // Room to spare: grow (bytes follow scale squared), but stay under a size known to be too big.
                    var grow = Math.Min(1, plan.Scale * Math.Sqrt((double)maxBytes / last.FileBytes) * 0.97);
                    var next = tooBig is { } over ? Math.Min(grow, (plan.Scale + over) / 2) : grow;
                    if (next <= plan.Scale * 1.02)
                    {
                        break;
                    }

                    plan = plan with { Scale = next };
                }
                else
                {
                    tooBig = plan.Scale;
                    if (best is not null)
                    {
                        var between = (best.Plan.Scale + plan.Scale) / 2;
                        if (plan.Scale - best.Plan.Scale < 0.02)
                        {
                            break;
                        }

                        plan = plan with { Scale = between };
                        continue;
                    }

                    // Bytes follow pixel count (scale squared); aim a little under so the next try lands.
                    var scale = plan.Scale * Math.Sqrt((double)maxBytes / last.FileBytes) * 0.95;
                    if (scale < MinScale && fps / (plan.FrameStep + 1) >= 5)
                    {
                        plan = new ShrinkPlan(Math.Max(MinScale, scale * 1.3), plan.FrameStep + 1);
                    }
                    else if (plan.Scale <= MinScale)
                    {
                        break; // as small as it goes
                    }
                    else
                    {
                        plan = plan with { Scale = Math.Max(MinScale, scale) };
                    }
                }
            }

            var chosen = best ?? last!;
            File.Move(chosen.Path, outputPath, overwrite: true);
            return chosen with { Path = outputPath };
        }
        finally
        {
            foreach (var file in tries)
            {
                TempFiles.TryDelete(file);
            }
        }
    }

    /// <summary>Re-encodes at a fixed scale, keeping every frame.</summary>
    public static ShrinkResult Resize(string path, double scale, string outputPath, CancellationToken cancel = default, GifEncoderOptions? encoder = null) =>
        Encode(path, new ShrinkPlan(Math.Clamp(scale, MinScale, 1), 1), outputPath, cancel, encoder);

    /// <summary>
    /// Fewer frames costs less than fewer pixels (motion stays readable), so drop frames (never below 12 fps)
    /// when a big cut is needed, then make up the rest with scale.
    /// </summary>
    internal static ShrinkPlan FirstGuess(double fps, double ratio)
    {
        var step = 1;
        while (ratio * step < 0.6 && fps / (step + 1) >= MinFramesPerSecond)
        {
            step++;
        }

        // Dropping frames saves less than its share: the frames left differ more from each other.
        var remaining = ratio * (1 + (step - 1) * 0.8);
        var scale = Math.Clamp(Math.Sqrt(Math.Min(1, remaining)) * 0.92, MinScale, 1);
        return new ShrinkPlan(scale, step);
    }

    internal static (int Frames, TimeSpan Duration) Inspect(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
        var cs = decoder.Frames.Sum(DelayCs);
        return (decoder.Frames.Count, TimeSpan.FromMilliseconds(cs * 10));
    }

    /// <param name="encoder">Encoder quality settings; null means <see cref="GifEncoderOptions.Quality"/>.</param>
    internal static ShrinkResult Encode(string path, ShrinkPlan plan, string outputPath, CancellationToken cancel, GifEncoderOptions? encoder = null)
    {
        var (width, height) = Size(path);
        var outWidth = Math.Max(1, (int)Math.Round(width * plan.Scale));
        var outHeight = Math.Max(1, (int)Math.Round(height * plan.Scale));
        var outStride = outWidth * 4;
        var full = new PixelRect(0, 0, outWidth, outHeight);

        byte[]? pending = null, written = null;
        var pendingDelay = 0;
        var frames = 0;
        var totalCs = 0;

        var options = encoder == GifEncoderOptions.Basic ? null : encoder ?? GifEncoderOptions.Quality;
        using (var writer = new GifWriter(File.Create(outputPath), outWidth, outHeight, options: options))
        {
            void Flush()
            {
                if (pending is null)
                {
                    return;
                }

                var region = written is null ? full : FrameDiff.ChangedRegion(written, pending, outWidth, outHeight, outStride) ?? new PixelRect(0, 0, 1, 1);
                var delay = Math.Max(2, pendingDelay);
                writer.WriteFrame(pending, outStride, region, delay);
                totalCs += delay;
                frames++;
                written = pending;
                pending = null;
            }

            var i = -1;
            foreach (var (canvas, delay) in ReadFrames(path))
            {
                i++;
                cancel.ThrowIfCancellationRequested();
                if (i % plan.FrameStep != 0)
                {
                    pendingDelay += delay; // a dropped frame's time goes to the frame before it
                    continue;
                }

                var next = Scale(canvas, width, height, outWidth, outHeight);
                if (pending is not null && next.AsSpan().SequenceEqual(pending))
                {
                    pendingDelay += delay; // nothing changed: show the pending frame longer
                    continue;
                }

                Flush();
                pending = next;
                pendingDelay = delay;
            }

            Flush();
            writer.Finish();
        }

        return new ShrinkResult(outputPath, outWidth, outHeight, frames, TimeSpan.FromMilliseconds(totalCs * 10), new FileInfo(outputPath).Length, plan);
    }

    /// <summary>Area-average downscale (each output pixel is the mean of the source pixels it covers).</summary>
    internal static byte[] Scale(byte[] src, int width, int height, int outWidth, int outHeight)
    {
        var dst = new byte[outWidth * outHeight * 4];
        if (outWidth == width && outHeight == height)
        {
            Buffer.BlockCopy(src, 0, dst, 0, dst.Length);
            return dst;
        }

        for (var y = 0; y < outHeight; y++)
        {
            var y0 = y * height / outHeight;
            var y1 = Math.Max(y0 + 1, (y + 1) * height / outHeight);
            for (var x = 0; x < outWidth; x++)
            {
                var x0 = x * width / outWidth;
                var x1 = Math.Max(x0 + 1, (x + 1) * width / outWidth);
                int b = 0, g = 0, rr = 0, n = 0;
                for (var sy = y0; sy < y1; sy++)
                {
                    var row = sy * width * 4;
                    for (var sx = x0; sx < x1; sx++)
                    {
                        var p = row + sx * 4;
                        b += src[p];
                        g += src[p + 1];
                        rr += src[p + 2];
                        n++;
                    }
                }

                var d = (y * outWidth + x) * 4;
                dst[d] = (byte)(b / n);
                dst[d + 1] = (byte)(g / n);
                dst[d + 2] = (byte)(rr / n);
                dst[d + 3] = 255;
            }
        }

        return dst;
    }

    // ---------- Decoding (the same compositing as GifPlayer) ----------

    /// <summary>The GIF's canvas size.</summary>
    internal static (int Width, int Height) Size(string path)
    {
        using var input = File.OpenRead(path);
        return ScreenSize(new GifBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None));
    }

    /// <summary>Every frame as the full composited canvas (BGRA) with its delay. The buffer is reused: copy it to keep it.</summary>
    internal static IEnumerable<(byte[] Canvas, int DelayCs)> ReadFrames(string path)
    {
        using var input = File.OpenRead(path);
        var decoder = new GifBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
        var (width, height) = ScreenSize(decoder);
        var canvas = new byte[width * height * 4];
        (Int32Rect Rect, int Disposal)? previous = null;
        for (var i = 0; i < decoder.Frames.Count; i++)
        {
            var frame = decoder.Frames[i];
            if (i == 0)
            {
                Array.Clear(canvas);
            }
            else if (previous is { Disposal: 2 } prev)
            {
                ClearRect(canvas, width, height, prev.Rect);
            }

            var rect = FrameRect(frame);
            Draw(canvas, width, height, frame, rect);
            previous = (rect, Query<byte>(frame, "/grctlext/Disposal"));
            yield return (canvas, DelayCs(frame));
        }
    }

    private static void Draw(byte[] canvas, int width, int height, BitmapFrame frame, Int32Rect rect)
    {
        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var stride = bgra.PixelWidth * 4;
        var src = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(src, stride, 0);
        var w = Math.Min(bgra.PixelWidth, width - rect.X);
        var h = Math.Min(bgra.PixelHeight, height - rect.Y);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var s = y * stride + x * 4;
                if (src[s + 3] == 0)
                {
                    continue; // transparent: keep what is underneath
                }

                var d = ((rect.Y + y) * width + rect.X + x) * 4;
                canvas[d] = src[s];
                canvas[d + 1] = src[s + 1];
                canvas[d + 2] = src[s + 2];
                canvas[d + 3] = 255;
            }
        }
    }

    private static void ClearRect(byte[] canvas, int width, int height, Int32Rect rect)
    {
        for (var y = rect.Y; y < Math.Min(height, rect.Y + rect.Height); y++)
        {
            var length = Math.Min(rect.Width, width - rect.X) * 4;
            Array.Clear(canvas, (y * width + rect.X) * 4, Math.Max(0, length));
        }
    }

    private static (int Width, int Height) ScreenSize(GifBitmapDecoder decoder)
    {
        try
        {
            if (decoder.Metadata?.GetQuery("/logscrdesc/Width") is ushort w && decoder.Metadata.GetQuery("/logscrdesc/Height") is ushort h && w > 0 && h > 0)
            {
                return (w, h);
            }
        }
        catch (NotSupportedException)
        {
        }

        return (decoder.Frames[0].PixelWidth, decoder.Frames[0].PixelHeight);
    }

    private static Int32Rect FrameRect(BitmapFrame frame) =>
        new(Query<ushort>(frame, "/imgdesc/Left"), Query<ushort>(frame, "/imgdesc/Top"), frame.PixelWidth, frame.PixelHeight);

    /// <summary>Delay in centiseconds; under 2 is played as 10 by browsers, so count it that way.</summary>
    private static int DelayCs(BitmapFrame frame)
    {
        var cs = Query<ushort>(frame, "/grctlext/Delay");
        return cs < 2 ? 10 : cs;
    }

    private static T Query<T>(BitmapFrame frame, string query) where T : struct
    {
        try
        {
            return frame.Metadata is BitmapMetadata m && m.GetQuery(query) is T value ? value : default;
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            return default;
        }
    }
}
