using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace Toastshot.Gif;

/// <summary>Fills a BGRA buffer (stride = width * 4) with the current image of a fixed region.</summary>
public interface IFrameSource : IDisposable
{
    int Width { get; }
    int Height { get; }
    void Capture(Span<byte> bgra);
}

public sealed record GifRecording(string Path, int Width, int Height, int FrameCount, TimeSpan Duration, long FileBytes);

/// <summary>
/// Records frames from an <see cref="IFrameSource"/> into a GIF file. Capture and encoding run on
/// separate threads joined by a small bounded queue: if encoding falls behind, frames are dropped
/// (the previous frame simply stays up longer) rather than memory growing. Identical frames are merged.
/// </summary>
public sealed class GifRecorder : IAsyncDisposable
{
    private readonly IFrameSource _source;
    private readonly string _outputPath;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _maxDuration;
    private readonly Channel<Frame> _frames = Channel.CreateBounded<Frame>(new BoundedChannelOptions(6)
    {
        SingleReader = true,
        SingleWriter = true,
        FullMode = BoundedChannelFullMode.Wait
    });

    private readonly Stopwatch _clock = new();
    private readonly CancellationTokenSource _stop = new();
    private Thread? _captureThread;
    private Task<GifRecording>? _encodeTask;
    private int _captured;
    private int _dropped;
    private TimeSpan _stoppedAt;

    /// <summary>Raised on the capture thread when <c>maxDuration</c> is reached.</summary>
    public event EventHandler? MaxDurationReached;

    public GifRecorder(IFrameSource source, string outputPath, int framesPerSecond, TimeSpan maxDuration)
    {
        if (framesPerSecond is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond), "Use 1-50 frames per second.");
        }

        _source = source;
        _outputPath = outputPath;
        _interval = TimeSpan.FromSeconds(1.0 / framesPerSecond);
        _maxDuration = maxDuration;
    }

    public TimeSpan Elapsed => _clock.Elapsed;
    public int FramesCaptured => Volatile.Read(ref _captured);
    public int FramesDropped => Volatile.Read(ref _dropped);
    public bool IsRecording => _captureThread is not null && !_stop.IsCancellationRequested;

    public void Start()
    {
        if (_captureThread is not null)
        {
            throw new InvalidOperationException("Recording already started.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_outputPath))!);
        _encodeTask = Task.Factory.StartNew(EncodeLoop, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        _clock.Start();
        _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "Toastshot GIF capture" };
        _captureThread.Start();
    }

    /// <summary>Stops capturing and waits for the GIF to be finished.</summary>
    public async Task<GifRecording> StopAsync()
    {
        if (_encodeTask is null)
        {
            throw new InvalidOperationException("Recording was never started.");
        }

        if (!_stop.IsCancellationRequested)
        {
            _stoppedAt = _clock.Elapsed;
            _stop.Cancel();
        }

        _captureThread?.Join();
        return await _encodeTask.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_encodeTask is not null)
        {
            try
            {
                await StopAsync().ConfigureAwait(false);
            }
            catch
            {
                // Disposal must not throw; a failed recording has already surfaced through StopAsync.
            }
        }

        _source.Dispose();
        _stop.Dispose();
    }

    private void CaptureLoop()
    {
        var size = _source.Width * _source.Height * 4;
        _ = NativeTiming.timeBeginPeriod(1);
        try
        {
            var next = TimeSpan.Zero;
            while (!_stop.IsCancellationRequested)
            {
                var now = _clock.Elapsed;
                if (now >= _maxDuration)
                {
                    _stoppedAt = _maxDuration;
                    _stop.Cancel();
                    MaxDurationReached?.Invoke(this, EventArgs.Empty);
                    break;
                }

                if (now < next)
                {
                    _stop.Token.WaitHandle.WaitOne(next - now);
                    continue;
                }

                var buffer = ArrayPool<byte>.Shared.Rent(size);
                var at = _clock.Elapsed;
                _source.Capture(buffer.AsSpan(0, size));
                if (_frames.Writer.TryWrite(new Frame(buffer, at)))
                {
                    Interlocked.Increment(ref _captured);
                }
                else
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    Interlocked.Increment(ref _dropped);
                }

                // Schedule from the ideal timeline, but never try to "catch up" with a burst.
                next += _interval;
                if (next < _clock.Elapsed)
                {
                    next = _clock.Elapsed;
                }
            }
        }
        catch (Exception ex)
        {
            _frames.Writer.TryComplete(ex);
            return;
        }
        finally
        {
            _ = NativeTiming.timeEndPeriod(1);
        }

        _frames.Writer.TryComplete();
    }

    private async Task<GifRecording> EncodeLoop()
    {
        int width = _source.Width, height = _source.Height, stride = width * 4, size = stride * height;
        Frame? pending = null;
        PixelRect pendingRegion = default;
        var written = 0;
        var timelineCs = 0; // where the next frame starts, in GIF centiseconds

        try
        {
            using (var writer = new GifWriter(File.Create(_outputPath), width, height))
            {
                void WritePending(TimeSpan until)
                {
                    var endCs = (int)Math.Round(until.TotalMilliseconds / 10);
                    // Browsers treat delays under 2 cs as 10 cs, so never write less than 2.
                    var delay = Math.Max(2, endCs - timelineCs);
                    writer.WriteFrame(pending!.Value.Pixels.AsSpan(0, size), stride, pendingRegion, delay);
                    timelineCs += delay;
                    written++;
                }

                await foreach (var frame in _frames.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    if (pending is not { } prev)
                    {
                        pending = frame;
                        pendingRegion = new PixelRect(0, 0, width, height);
                        continue;
                    }

                    var changed = FrameDiff.ChangedRegion(prev.Pixels.AsSpan(0, size), frame.Pixels.AsSpan(0, size), width, height, stride);
                    if (changed is null)
                    {
                        // Nothing moved: keep showing the pending frame for longer.
                        ArrayPool<byte>.Shared.Return(frame.Pixels);
                        continue;
                    }

                    WritePending(frame.At);
                    ArrayPool<byte>.Shared.Return(prev.Pixels);
                    pending = frame;
                    pendingRegion = changed.Value;
                }

                if (pending is { } last)
                {
                    var end = _stoppedAt > last.At ? _stoppedAt : last.At + _interval;
                    WritePending(end);
                    ArrayPool<byte>.Shared.Return(last.Pixels);
                    pending = null;
                }

                writer.Finish();
            }

            return new GifRecording(_outputPath, width, height, written,
                TimeSpan.FromMilliseconds(timelineCs * 10), new FileInfo(_outputPath).Length);
        }
        catch
        {
            if (pending is { } p)
            {
                ArrayPool<byte>.Shared.Return(p.Pixels);
            }

            // Drain so pooled buffers go back and the capture thread is never blocked.
            while (_frames.Reader.TryRead(out var f))
            {
                ArrayPool<byte>.Shared.Return(f.Pixels);
            }

            _stop.Cancel();
            TryDelete(_outputPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private readonly record struct Frame(byte[] Pixels, TimeSpan At);

    private static class NativeTiming
    {
        // Raises timer resolution while recording so frame pacing is accurate.
        [DllImport("winmm.dll")]
        public static extern uint timeBeginPeriod(uint period);

        [DllImport("winmm.dll")]
        public static extern uint timeEndPeriod(uint period);
    }
}
