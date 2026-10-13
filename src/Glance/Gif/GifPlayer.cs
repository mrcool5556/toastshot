using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Glance.Gif;

/// <summary>
/// Plays an animated GIF into an <see cref="Image"/> (WPF's Image only shows the first frame).
/// Frames are decoded one at a time and composited, so long recordings don't need lots of memory.
/// </summary>
internal sealed class GifPlayer : IDisposable
{
    private readonly FileStream _stream;
    private readonly GifBitmapDecoder _decoder;
    private readonly WriteableBitmap _canvas;
    private readonly byte[] _pixels;
    private readonly DispatcherTimer _timer = new();
    private int _index = -1;
    private (Int32Rect Rect, int Disposal)? _previous;

    public int Width { get; }
    public int Height { get; }
    public int FrameCount => _decoder.Frames.Count;
    public ImageSource Source => _canvas;
    public TimeSpan Duration { get; }

    public GifPlayer(string path)
    {
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        _decoder = new GifBitmapDecoder(_stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
        (Width, Height) = ScreenSize(_decoder);
        _canvas = new WriteableBitmap(Width, Height, 96, 96, PixelFormats.Bgra32, null);
        _pixels = new byte[Width * Height * 4];
        Duration = TimeSpan.FromMilliseconds(Enumerable.Range(0, FrameCount).Sum(i => Delay(_decoder.Frames[i])));
        _timer.Tick += (_, _) => Advance();
        Advance();
    }

    public static int CountFrames(string path)
    {
        using var stream = File.OpenRead(path);
        return new GifBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.None).Frames.Count;
    }

    public void Play()
    {
        if (FrameCount > 1)
        {
            _timer.Start();
        }
    }

    public void Pause() => _timer.Stop();

    /// <summary>Shows the next frame immediately (for tests and stepping).</summary>
    internal void Step() => Advance();

    private void Advance()
    {
        _index = (_index + 1) % FrameCount;
        if (_index == 0)
        {
            Array.Clear(_pixels);
            _previous = null;
        }
        else if (_previous is { Disposal: 2 } prev)
        {
            ClearRect(prev.Rect); // "restore to background"
        }

        var frame = _decoder.Frames[_index];
        var rect = FrameRect(frame);
        DrawFrame(frame, rect);
        _previous = (rect, Disposal(frame));
        _canvas.WritePixels(new Int32Rect(0, 0, Width, Height), _pixels, Width * 4, 0);

        _timer.Interval = TimeSpan.FromMilliseconds(Delay(frame));
    }

    /// <summary>Alpha-aware copy: transparent pixels keep what is underneath.</summary>
    private void DrawFrame(BitmapFrame frame, Int32Rect rect)
    {
        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var stride = bgra.PixelWidth * 4;
        var src = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(src, stride, 0);
        var w = Math.Min(bgra.PixelWidth, Width - rect.X);
        var h = Math.Min(bgra.PixelHeight, Height - rect.Y);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var s = y * stride + x * 4;
                if (src[s + 3] == 0)
                {
                    continue;
                }

                var d = ((rect.Y + y) * Width + rect.X + x) * 4;
                _pixels[d] = src[s];
                _pixels[d + 1] = src[s + 1];
                _pixels[d + 2] = src[s + 2];
                _pixels[d + 3] = 255;
            }
        }
    }

    private void ClearRect(Int32Rect rect)
    {
        for (var y = rect.Y; y < Math.Min(Height, rect.Y + rect.Height); y++)
        {
            var start = (y * Width + rect.X) * 4;
            var length = Math.Min(rect.Width, Width - rect.X) * 4;
            Array.Clear(_pixels, start, Math.Max(0, length));
        }
    }

    private static (int Width, int Height) ScreenSize(GifBitmapDecoder decoder)
    {
        var first = decoder.Frames[0];
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

        return (first.PixelWidth, first.PixelHeight);
    }

    private static Int32Rect FrameRect(BitmapFrame frame) =>
        new(Query<ushort>(frame, "/imgdesc/Left"), Query<ushort>(frame, "/imgdesc/Top"), frame.PixelWidth, frame.PixelHeight);

    private static int Disposal(BitmapFrame frame) => Query<byte>(frame, "/grctlext/Disposal");

    /// <summary>Frame delay in ms; tiny delays are shown at 100 ms like browsers do.</summary>
    private static int Delay(BitmapFrame frame)
    {
        var cs = Query<ushort>(frame, "/grctlext/Delay");
        return cs < 2 ? 100 : cs * 10;
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

    public void Dispose()
    {
        _timer.Stop();
        _stream.Dispose();
    }
}
