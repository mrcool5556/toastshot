using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace Toastshot.Gif;

public sealed record Mp4Result(string Path, int Width, int Height, long FileBytes);

/// <summary>
/// Turns a GIF into an H.264 MP4 with the encoder built into Windows (Media Foundation), at full size and frame
/// rate. Video is far more efficient than GIF and has no 256-colour limit. Frames are decoded on one thread and
/// handed to the encoder through a small queue, so memory stays small.
/// </summary>
public static class Mp4Exporter
{
    private sealed record Frame(byte[] Pixels, TimeSpan Start, TimeSpan Duration);

    public static async Task<Mp4Result> ExportAsync(string gifPath, string mp4Path, CancellationToken cancel = default)
    {
        var (width, height) = GifShrinker.Size(gifPath);
        // H.264 needs even dimensions: drop a column or row if needed.
        var w = Math.Max(2, width & ~1);
        var h = Math.Max(2, height & ~1);

        using var queue = new BlockingCollection<Frame>(boundedCapacity: 8);
        var producer = Task.Factory.StartNew(() =>
        {
            try
            {
                var start = TimeSpan.Zero;
                foreach (var (canvas, delayCs) in GifShrinker.ReadFrames(gifPath))
                {
                    cancel.ThrowIfCancellationRequested();
                    var pixels = new byte[w * h * 4];
                    // Uncompressed RGB video is stored bottom row first, so hand the rows over flipped.
                    for (var y = 0; y < h; y++)
                    {
                        Buffer.BlockCopy(canvas, y * width * 4, pixels, (h - 1 - y) * w * 4, w * 4);
                    }

                    var duration = TimeSpan.FromMilliseconds(delayCs * 10);
                    queue.Add(new Frame(pixels, start, duration), cancel);
                    start += duration;
                }
            }
            finally
            {
                queue.CompleteAdding();
            }
        }, cancel, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        var (frames, length) = GifShrinker.Inspect(gifPath);
        var fps = (uint)Math.Clamp(Math.Round(length.TotalSeconds > 0 ? frames / length.TotalSeconds : 15), 1, 60);

        var input = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)w, (uint)h);
        var source = new MediaStreamSource(new VideoStreamDescriptor(input)) { BufferTime = TimeSpan.Zero };
        source.Starting += (_, e) => e.Request.SetActualStartPosition(TimeSpan.Zero);
        source.SampleRequested += (_, e) =>
        {
            // Null ends the stream once every frame has been handed over.
            if (queue.TryTake(out var frame, Timeout.Infinite, cancel))
            {
                var sample = MediaStreamSample.CreateFromBuffer(frame.Pixels.AsBuffer(), frame.Start);
                sample.Duration = frame.Duration;
                e.Request.Sample = sample;
            }
        };

        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Audio = null;
        profile.Video.Width = (uint)w;
        profile.Video.Height = (uint)h;
        profile.Video.FrameRate.Numerator = fps;
        profile.Video.FrameRate.Denominator = 1;
        // About 0.2 bits per pixel per frame: crisp for screen and game footage, a few MB for a short clip.
        profile.Video.Bitrate = (uint)Math.Clamp(w * h * fps * 0.2, 1_000_000, 40_000_000);

        File.Delete(mp4Path);
        await File.WriteAllBytesAsync(mp4Path, [], cancel);
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(mp4Path));
        using (var output = await file.OpenAsync(FileAccessMode.ReadWrite))
        {
            var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
            var prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(source, output, profile);
            if (!prepared.CanTranscode)
            {
                throw new InvalidOperationException($"Windows can't make an MP4 here ({prepared.FailureReason}).");
            }

            await prepared.TranscodeAsync().AsTask(cancel);
        }

        await producer;
        return new Mp4Result(mp4Path, w, h, new FileInfo(mp4Path).Length);
    }
}
