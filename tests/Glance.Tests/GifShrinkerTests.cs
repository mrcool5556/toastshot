using System.IO;
using Glance.Gif;

namespace Glance.Tests;

public sealed class GifShrinkerTests
{
    /// <summary>A hard-to-compress GIF: every frame is fresh noise over a moving gradient, ~33 fps.</summary>
    private static string NoisyGif(int width, int height, int frames)
    {
        var path = TestUtil.TempFile(".gif");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var random = new Random(7);
        var pixels = new byte[width * height * 4];
        using var writer = new GifWriter(File.Create(path), width, height);
        for (var f = 0; f < frames; f++)
        {
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var p = (y * width + x) * 4;
                    var noise = random.Next(48);
                    pixels[p] = (byte)((x + f * 3) % 200 + noise);
                    pixels[p + 1] = (byte)((y * 2) % 200 + noise);
                    pixels[p + 2] = (byte)((x + y + f) % 200 + noise);
                    pixels[p + 3] = 255;
                }
            }

            writer.WriteFrame(pixels, width * 4, new PixelRect(0, 0, width, height), 3);
        }

        return path;
    }

    [Fact]
    public void Fit_To_Size_Gets_Under_The_Limit_And_Keeps_The_Length() => TestUtil.RunSta(() =>
    {
        var source = NoisyGif(320, 200, 40);
        var originalBytes = new FileInfo(source).Length;
        var output = TestUtil.TempFile(".gif");
        var limit = originalBytes / 5;

        var result = GifShrinker.FitToSize(source, limit, output);

        Assert.NotNull(result);
        Assert.True(result!.FileBytes <= limit, $"{result.FileBytes} > {limit}");
        Assert.True(result.Width < 320 || result.FrameCount < 40);
        Assert.Equal(result.FrameCount, GifPlayer.CountFrames(output));       // a valid GIF
        Assert.InRange(result.Duration.TotalSeconds, 40 * 0.03 * 0.9, 40 * 0.03 * 1.1); // same length, fewer frames
        File.Delete(source);
        File.Delete(output);
    });

    [Fact]
    public void Fit_To_Size_Leaves_A_Gif_That_Already_Fits() => TestUtil.RunSta(() =>
    {
        var source = NoisyGif(40, 30, 3);
        Assert.Null(GifShrinker.FitToSize(source, new FileInfo(source).Length + 1, TestUtil.TempFile(".gif")));
        File.Delete(source);
    });

    [Fact]
    public void Resize_Halves_The_Picture_And_Keeps_Every_Frame() => TestUtil.RunSta(() =>
    {
        var source = NoisyGif(200, 120, 10);
        var output = TestUtil.TempFile(".gif");

        var result = GifShrinker.Resize(source, 0.5, output);

        Assert.Equal((100, 60), (result.Width, result.Height));
        Assert.Equal(10, result.FrameCount);
        Assert.True(result.FileBytes < new FileInfo(source).Length);
        File.Delete(source);
        File.Delete(output);
    });

    [Theory]
    [InlineData(30, 0.25, 2)] // 30 fps, needs a quarter: half the frames (15 fps), then scale
    [InlineData(15, 0.25, 1)] // 15 fps: halving would go under 12 fps, so scale only
    [InlineData(30, 0.9, 1)]  // a small cut: scale only
    public void First_Guess_Drops_Frames_Only_Down_To_12_Fps(double fps, double ratio, int step)
    {
        var plan = GifShrinker.FirstGuess(fps, ratio);
        Assert.Equal(step, plan.FrameStep);
        Assert.InRange(plan.Scale, 0.1, 1);
    }
}
