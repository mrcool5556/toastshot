using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Toastshot.Gif;
using Windows.Media.Editing;
using Windows.Storage;

namespace Toastshot.Tests;

/// <summary>Needs a real Windows desktop session (clipboard, screen, Media Foundation); CI runs without these.</summary>
[Trait("Needs", "Desktop")]
public sealed class Mp4ExporterTests
{
    [Fact]
    public async Task Exports_A_Playable_Upright_Mp4()
    {
        // A GIF with a red top half and a blue bottom half (odd size, to check the even-size crop).
        const int width = 161, height = 121;
        var gif = TestUtil.TempFile(".gif");
        Directory.CreateDirectory(Path.GetDirectoryName(gif)!);
        var pixels = new byte[width * height * 4];
        using (var writer = new GifWriter(File.Create(gif), width, height))
        {
            for (var f = 0; f < 12; f++)
            {
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var p = (y * width + x) * 4;
                        var top = y < height / 2;
                        pixels[p] = (byte)(top ? 0 : 220);         // blue
                        pixels[p + 1] = (byte)(f * 10);            // a little change per frame
                        pixels[p + 2] = (byte)(top ? 220 : 0);     // red
                        pixels[p + 3] = 255;
                    }
                }

                writer.WriteFrame(pixels, width * 4, new PixelRect(0, 0, width, height), 8);
            }
        }

        var mp4 = Path.ChangeExtension(TestUtil.TempFile(".mp4"), ".mp4");

        // Off the UI thread, as the app does it.
        var result = await Task.Run(() => Mp4Exporter.ExportAsync(gif, mp4));

        Assert.Equal((160, 120), (result.Width, result.Height));
        Assert.True(result.FileBytes > 1000);

        // Read a frame back: red must be at the top.
        var clip = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(mp4));
        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        Assert.InRange(clip.OriginalDuration.TotalSeconds, 0.8, 1.2); // 12 frames × 80 ms
        using var thumbnail = await composition.GetThumbnailAsync(TimeSpan.FromMilliseconds(400), 160, 120, VideoFramePrecision.NearestFrame);
        using var stream = thumbnail.AsStreamForRead();
        var frame = new FormatConvertedBitmap(BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad), PixelFormats.Bgra32, null, 0);
        var px = new byte[frame.PixelWidth * frame.PixelHeight * 4];
        frame.CopyPixels(px, frame.PixelWidth * 4, 0);
        int At(int x, int y, int c) => px[(y * frame.PixelWidth + x) * 4 + c];
        Assert.True(At(80, 20, 2) > 150 && At(80, 20, 0) < 100, $"top should be red: top BGR {At(80, 20, 0)},{At(80, 20, 1)},{At(80, 20, 2)} bottom BGR {At(80, 100, 0)},{At(80, 100, 1)},{At(80, 100, 2)} size {frame.PixelWidth}x{frame.PixelHeight}");
        Assert.True(At(80, 100, 0) > 150 && At(80, 100, 2) < 100, "bottom should be blue");

        File.Delete(gif);
        File.Delete(mp4);
    }
}
