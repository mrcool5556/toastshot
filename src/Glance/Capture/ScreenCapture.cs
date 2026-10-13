using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Glance.Gif;
using Drawing = System.Drawing;
using DrawingImaging = System.Drawing.Imaging;

namespace Glance.Capture;

public static class ScreenCapture
{
    /// <summary>The bounding box of every monitor, in device pixels (origin may be negative).</summary>
    public static PixelRect VirtualScreen => new(
        Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN),
        Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN),
        Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN),
        Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN));

    /// <summary>Grabs a device-pixel region of the desktop as a frozen 96-DPI image (1 pixel = 1 DIP).</summary>
    public static BitmapSource CaptureRegion(PixelRect region)
    {
        if (region.IsEmpty)
        {
            throw new ArgumentException("Region is empty.", nameof(region));
        }

        using var bitmap = new Drawing.Bitmap(region.Width, region.Height, DrawingImaging.PixelFormat.Format32bppRgb);
        using (var g = Drawing.Graphics.FromImage(bitmap))
        {
            CopyScreen(g, region);
        }

        return ToBitmapSource(bitmap);
    }

    /// <summary>
    /// Copies screen pixels with SRCCOPY | CAPTUREBLT so layered windows (menus, tooltips, transparent
    /// apps) are included. Graphics.CopyFromScreen rejects that flag combination, so BitBlt is called directly.
    /// </summary>
    internal static void CopyScreen(Drawing.Graphics target, PixelRect region)
    {
        var screen = Native.GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero)
        {
            throw new InvalidOperationException("Couldn't access the screen.");
        }

        try
        {
            var hdc = target.GetHdc();
            try
            {
                if (!Native.BitBlt(hdc, 0, 0, region.Width, region.Height, screen, region.X, region.Y, Native.SRCCOPY | Native.CAPTUREBLT))
                {
                    throw new InvalidOperationException($"Screen capture failed (error {Marshal.GetLastWin32Error()}).");
                }
            }
            finally
            {
                target.ReleaseHdc(hdc);
            }
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public static BitmapSource CaptureVirtualScreen() => CaptureRegion(VirtualScreen);

    /// <summary>Copies part of a bitmap into a new, independent frozen bitmap.</summary>
    public static BitmapSource Crop(BitmapSource source, PixelRect region)
    {
        var rect = new Int32Rect(region.X, region.Y, region.Width, region.Height);
        var stride = region.Width * 4;
        var pixels = new byte[stride * region.Height];
        var bgr = source.Format == PixelFormats.Bgr32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgr32, null, 0);
        bgr.CopyPixels(rect, pixels, stride, 0);
        var result = BitmapSource.Create(region.Width, region.Height, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    internal static BitmapSource ToBitmapSource(Drawing.Bitmap bitmap)
    {
        var rect = new Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, DrawingImaging.ImageLockMode.ReadOnly, DrawingImaging.PixelFormat.Format32bppRgb);
        try
        {
            // Bgr32 ignores the alpha byte, which screen capture leaves undefined.
            var source = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Bgr32, null,
                data.Scan0, data.Stride * bitmap.Height, data.Stride);
            source.Freeze();
            return source;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    internal static class Native
    {
        public const int SM_XVIRTUALSCREEN = 76;
        public const int SM_YVIRTUALSCREEN = 77;
        public const int SM_CXVIRTUALSCREEN = 78;
        public const int SM_CYVIRTUALSCREEN = 79;

        public const int SRCCOPY = 0x00CC0020;
        public const int CAPTUREBLT = 0x40000000;

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, int rop);
    }
}

/// <summary>Captures a fixed screen region repeatedly for GIF recording, optionally drawing the mouse cursor.</summary>
public sealed class ScreenFrameSource : IFrameSource
{
    private readonly PixelRect _region;
    private readonly bool _drawCursor;
    private readonly Drawing.Bitmap _bitmap;
    private readonly Drawing.Graphics _graphics;

    public ScreenFrameSource(PixelRect region, bool drawCursor)
    {
        if (region.IsEmpty)
        {
            throw new ArgumentException("Region is empty.", nameof(region));
        }

        _region = region;
        _drawCursor = drawCursor;
        _bitmap = new Drawing.Bitmap(region.Width, region.Height, DrawingImaging.PixelFormat.Format32bppRgb);
        _graphics = Drawing.Graphics.FromImage(_bitmap);
    }

    public int Width => _region.Width;
    public int Height => _region.Height;

    public unsafe void Capture(Span<byte> bgra)
    {
        ScreenCapture.CopyScreen(_graphics, _region);
        if (_drawCursor)
        {
            DrawCursor();
        }

        var data = _bitmap.LockBits(new Drawing.Rectangle(0, 0, Width, Height),
            DrawingImaging.ImageLockMode.ReadOnly, DrawingImaging.PixelFormat.Format32bppRgb);
        try
        {
            var rowBytes = Width * 4;
            for (var y = 0; y < Height; y++)
            {
                var row = new ReadOnlySpan<byte>((void*)(data.Scan0 + y * data.Stride), rowBytes);
                row.CopyTo(bgra.Slice(y * rowBytes, rowBytes));
            }
        }
        finally
        {
            _bitmap.UnlockBits(data);
        }
    }

    private void DrawCursor()
    {
        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref info) || (info.flags & CURSOR_SHOWING) == 0 || info.hCursor == IntPtr.Zero)
        {
            return;
        }

        var hotX = 0;
        var hotY = 0;
        if (GetIconInfo(info.hCursor, out var icon))
        {
            hotX = icon.xHotspot;
            hotY = icon.yHotspot;
            if (icon.hbmMask != IntPtr.Zero) DeleteObject(icon.hbmMask);
            if (icon.hbmColor != IntPtr.Zero) DeleteObject(icon.hbmColor);
        }

        var x = info.ptScreenPos.X - _region.X - hotX;
        var y = info.ptScreenPos.Y - _region.Y - hotY;
        var hdc = _graphics.GetHdc();
        try
        {
            DrawIconEx(hdc, x, y, info.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
        }
        finally
        {
            _graphics.ReleaseHdc(hdc);
        }
    }

    public void Dispose()
    {
        _graphics.Dispose();
        _bitmap.Dispose();
    }

    private const int CURSOR_SHOWING = 0x1;
    private const int DI_NORMAL = 0x3;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO info);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO info);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int cx, int cy, int step, IntPtr brush, int flags);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}
