using System.IO;
using System.Windows.Media.Imaging;
using Toastshot.Gif;

namespace Toastshot.Tests;

public sealed class GifTests
{
    private static byte[] Canvas(int width, int height, Func<int, int, (byte R, byte G, byte B)> color)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b) = color(x, y);
                var i = (y * width + x) * 4;
                pixels[i] = b;
                pixels[i + 1] = g;
                pixels[i + 2] = r;
                pixels[i + 3] = 255;
            }
        }

        return pixels;
    }

    private static byte[] Write(int width, int height, Action<GifWriter> frames)
    {
        using var stream = new MemoryStream();
        using (var writer = new GifWriter(stream, width, height, leaveOpen: true))
        {
            frames(writer);
        }

        return stream.ToArray();
    }

    private static void AssertSameColours(byte[] expected, byte[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i += 4)
        {
            if (expected[i] != actual[i] || expected[i + 1] != actual[i + 1] || expected[i + 2] != actual[i + 2])
            {
                Assert.Fail($"Pixel {i / 4} differs: expected {expected[i + 2]},{expected[i + 1]},{expected[i]} got {actual[i + 2]},{actual[i + 1]},{actual[i]}");
            }
        }
    }

    [Fact]
    public void Few_Colours_Round_Trip_Exactly_With_Partial_Second_Frame()
    {
        const int w = 40, h = 30;
        var first = Canvas(w, h, (x, y) => x < 20 ? ((byte)255, (byte)0, (byte)0) : y < 15 ? ((byte)0, (byte)128, (byte)255) : ((byte)10, (byte)20, (byte)30));
        var second = (byte[])first.Clone();
        var region = new PixelRect(5, 6, 7, 8);
        for (var y = region.Y; y < region.Bottom; y++)
        {
            for (var x = region.X; x < region.Right; x++)
            {
                second[(y * w + x) * 4 + 1] = 200;
            }
        }

        var gif = Write(w, h, writer =>
        {
            writer.WriteFrame(first, w * 4, new PixelRect(0, 0, w, h), 25);
            writer.WriteFrame(second, w * 4, FrameDiff.ChangedRegion(first, second, w, h, w * 4)!.Value, 50);
        });

        var decoder = TestUtil.DecodeGif(gif);
        Assert.Equal(2, decoder.Frames.Count);
        AssertSameColours(first, TestUtil.Pixels(decoder.Frames[0]));

        var frame2 = decoder.Frames[1];
        var meta = (BitmapMetadata)frame2.Metadata;
        Assert.Equal((ushort)5, meta.GetQuery("/imgdesc/Left"));
        Assert.Equal((ushort)6, meta.GetQuery("/imgdesc/Top"));
        Assert.Equal(7, frame2.PixelWidth);
        Assert.Equal(8, frame2.PixelHeight);
        Assert.Equal((ushort)50, meta.GetQuery("/grctlext/Delay"));
        Assert.Equal((ushort)25, ((BitmapMetadata)decoder.Frames[0].Metadata).GetQuery("/grctlext/Delay"));
    }

    /// <summary>
    /// Random sizes and palette sizes exercise every LZW code width, table resets (clear codes)
    /// and the end-of-data code; Windows' decoder must reproduce every pixel.
    /// </summary>
    [Fact]
    public void Indexed_Images_Round_Trip_Exactly_Through_Windows_Decoder()
    {
        var random = new Random(1234);
        for (var run = 0; run < 60; run++)
        {
            var w = random.Next(1, 160);
            var h = random.Next(1, 160);
            var colours = run % 10 == 0 ? 1 : random.Next(2, 257);
            var palette = Enumerable.Range(0, colours)
                .Select(i => ((byte)(i * 37 % 256), (byte)(i * 91 % 256), (byte)(i * 13 % 256 ^ (i >> 4))))
                .Distinct()
                .ToArray();
            // Mix runs (compressible) and noise (fills the LZW table quickly).
            var pixels = Canvas(w, h, (x, y) => (x / 7 + y) % 3 == 0 ? palette[(x / 5) % palette.Length] : palette[random.Next(palette.Length)]);

            var gif = Write(w, h, writer => writer.WriteFrame(pixels, w * 4, new PixelRect(0, 0, w, h), 10));
            var decoded = TestUtil.DecodeGif(gif);
            Assert.Single(decoded.Frames);
            AssertSameColours(pixels, TestUtil.Pixels(decoded.Frames[0]));
        }
    }

    [Fact]
    public void Many_Colours_Are_Quantized_Closely()
    {
        const int w = 128, h = 96;
        var pixels = Canvas(w, h, (x, y) => ((byte)(x * 2), (byte)(y * 2), (byte)((x + y) % 256)));
        var gif = Write(w, h, writer => writer.WriteFrame(pixels, w * 4, new PixelRect(0, 0, w, h), 10));

        var decoded = TestUtil.Pixels(TestUtil.DecodeGif(gif).Frames[0]);
        double totalError = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            totalError += Math.Abs(pixels[i] - decoded[i]) + Math.Abs(pixels[i + 1] - decoded[i + 1]) + Math.Abs(pixels[i + 2] - decoded[i + 2]);
        }

        var meanPerChannel = totalError / (w * h * 3);
        Assert.True(meanPerChannel < 6, $"Mean channel error {meanPerChannel:0.00} is too high.");
    }

    [Fact]
    public void FrameDiff_Finds_Changed_Box_And_Ignores_Alpha()
    {
        const int w = 20, h = 10;
        var a = Canvas(w, h, (_, _) => ((byte)1, (byte)2, (byte)3));
        var b = (byte[])a.Clone();
        Assert.Null(FrameDiff.ChangedRegion(a, b, w, h, w * 4));

        b[(4 * w + 7) * 4 + 3] = 0; // alpha only
        Assert.Null(FrameDiff.ChangedRegion(a, b, w, h, w * 4));

        b[(4 * w + 7) * 4] = 99;
        b[(6 * w + 2) * 4 + 2] = 99;
        Assert.Equal(new PixelRect(2, 4, 6, 3), FrameDiff.ChangedRegion(a, b, w, h, w * 4));
    }

    [Fact]
    public void Writer_Rejects_Region_Outside_Canvas()
    {
        using var writer = new GifWriter(new MemoryStream(), 10, 10);
        var pixels = new byte[10 * 10 * 4];
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.WriteFrame(pixels, 40, new PixelRect(5, 5, 6, 1), 10));
    }

    private sealed class FakeSource(int width, int height, Func<int, byte> shade) : IFrameSource
    {
        private int _n;
        public int Width => width;
        public int Height => height;
        public int Captures => _n;

        public void Capture(Span<byte> bgra)
        {
            var value = shade(_n++);
            for (var i = 0; i < bgra.Length; i += 4)
            {
                bgra[i] = value;
                bgra[i + 1] = (byte)(i / 4 % width);
                bgra[i + 2] = 50;
                bgra[i + 3] = 255;
            }
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task Recorder_Writes_A_Decodable_Animation_And_Merges_Still_Frames()
    {
        var path = TestUtil.TempFile(".gif");
        // Changes every 5th capture; identical captures in between must be merged.
        var source = new FakeSource(64, 48, n => (byte)(n / 5 * 40));
        await using var recorder = new GifRecorder(source, path, framesPerSecond: 50, maxDuration: TimeSpan.FromSeconds(20));
        recorder.Start();
        await Task.Delay(700);
        var result = await recorder.StopAsync();

        try
        {
            Assert.True(File.Exists(path));
            Assert.Equal(64, result.Width);
            Assert.Equal(48, result.Height);
            Assert.True(result.FrameCount >= 2, $"Expected several frames, got {result.FrameCount}.");
            Assert.True(result.FrameCount < source.Captures, "Identical frames should be merged.");
            Assert.InRange(result.Duration.TotalMilliseconds, 500, 2000);

            var decoder = TestUtil.DecodeGif(File.ReadAllBytes(path));
            Assert.Equal(result.FrameCount, decoder.Frames.Count);
            var totalCs = decoder.Frames.Sum(f => (ushort)((BitmapMetadata)f.Metadata).GetQuery("/grctlext/Delay"));
            Assert.Equal((int)Math.Round(result.Duration.TotalMilliseconds / 10), totalCs);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Recorder_Stops_Itself_At_Max_Duration()
    {
        var path = TestUtil.TempFile(".gif");
        var reached = new TaskCompletionSource();
        await using var recorder = new GifRecorder(new FakeSource(16, 16, n => (byte)n), path, 20, TimeSpan.FromMilliseconds(300));
        recorder.MaxDurationReached += (_, _) => reached.TrySetResult();
        recorder.Start();
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var result = await recorder.StopAsync();
        Assert.InRange(result.Duration.TotalMilliseconds, 250, 500);
        File.Delete(path);
    }
}
