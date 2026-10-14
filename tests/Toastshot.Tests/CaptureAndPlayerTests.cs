using System.IO;
using System.Windows.Media.Imaging;
using Toastshot.Capture;
using Toastshot.Gif;

namespace Toastshot.Tests;

public sealed class CaptureAndPlayerTests
{
    // These read a few screen pixels into memory only; nothing is saved or shown.
    [Trait("Needs", "Desktop")]
    [Fact]
    public void Screen_Capture_Returns_Requested_Size_And_Crop_Is_Independent()
    {
        var screen = ScreenCapture.VirtualScreen;
        Assert.False(screen.IsEmpty);
        var region = new PixelRect(screen.X, screen.Y, 40, 30);

        var shot = ScreenCapture.CaptureRegion(region);
        Assert.Equal((40, 30), (shot.PixelWidth, shot.PixelHeight));
        Assert.True(shot.IsFrozen);

        var crop = ScreenCapture.Crop(shot, new PixelRect(10, 5, 20, 15));
        Assert.Equal((20, 15), (crop.PixelWidth, crop.PixelHeight));
        Assert.Equal(TestUtil.Pixels(shot).Skip((5 * 40 + 10) * 4).Take(20 * 4).Where((_, i) => i % 4 != 3),
            TestUtil.Pixels(crop).Take(20 * 4).Where((_, i) => i % 4 != 3));
    }

    [Trait("Needs", "Desktop")]
    [Fact]
    public void Screen_Frame_Source_Captures_Repeatedly()
    {
        var screen = ScreenCapture.VirtualScreen;
        using var source = new ScreenFrameSource(new PixelRect(screen.X, screen.Y, 24, 16), drawCursor: true);
        var buffer = new byte[24 * 16 * 4];
        source.Capture(buffer);
        source.Capture(buffer); // reusable
        Assert.Equal(24, source.Width);
        Assert.Equal(16, source.Height);
    }

    [Trait("Needs", "Desktop")]
    [Fact]
    public async Task Real_Screen_Recording_Produces_A_Playable_Gif()
    {
        var screen = ScreenCapture.VirtualScreen;
        var path = TestUtil.TempFile(".gif");
        try
        {
            GifRecording result;
            await using (var recorder = new GifRecorder(new ScreenFrameSource(new PixelRect(screen.X, screen.Y, 64, 48), drawCursor: true),
                             path, framesPerSecond: 15, maxDuration: TimeSpan.FromSeconds(10)))
            {
                recorder.Start();
                await Task.Delay(400);
                result = await recorder.StopAsync();
            }

            Assert.True(result.FrameCount >= 1);
            TestUtil.RunSta(() =>
            {
                using var player = new GifPlayer(path);
                Assert.Equal((64, 48), (player.Width, player.Height));
                Assert.Equal(result.FrameCount, player.FrameCount);
            });
        }
        finally
        {
            File.Delete(path); // holds screen pixels: never keep it
        }
    }

    [Fact]
    public void Player_Composites_Partial_Frames_Into_Full_Images() => TestUtil.RunSta(() =>
    {
        const int w = 30, h = 20;
        var first = new byte[w * h * 4];
        for (var i = 0; i < first.Length; i += 4)
        {
            first[i] = 200; // blue background
            first[i + 3] = 255;
        }

        var second = (byte[])first.Clone();
        for (var y = 4; y < 9; y++)
        {
            for (var x = 6; x < 12; x++)
            {
                second[(y * w + x) * 4 + 2] = 255; // add red
            }
        }

        var path = TestUtil.TempFile(".gif");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var writer = new GifWriter(File.Create(path), w, h))
        {
            writer.WriteFrame(first, w * 4, new PixelRect(0, 0, w, h), 10);
            writer.WriteFrame(second, w * 4, FrameDiff.ChangedRegion(first, second, w, h, w * 4)!.Value, 10);
        }

        using (var player = new GifPlayer(path))
        {
            Assert.Equal(2, player.FrameCount);
            Assert.Equal(TimeSpan.FromMilliseconds(200), player.Duration);
            AssertColours(first, TestUtil.Pixels((BitmapSource)player.Source));
            player.Step();
            AssertColours(second, TestUtil.Pixels((BitmapSource)player.Source)); // unchanged area kept from frame 1
            player.Step();
            AssertColours(first, TestUtil.Pixels((BitmapSource)player.Source)); // loops
        }

        File.Delete(path);
    });

    private static void AssertColours(byte[] expected, byte[] actual)
    {
        for (var i = 0; i < expected.Length; i += 4)
        {
            Assert.True(expected[i] == actual[i] && expected[i + 1] == actual[i + 1] && expected[i + 2] == actual[i + 2],
                $"Pixel {i / 4} differs.");
        }
    }
}
