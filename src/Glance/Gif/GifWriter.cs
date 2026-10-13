using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Glance.Gif;

/// <summary>
/// Writes an animated GIF89a. Each frame may cover only part of the canvas (the area that changed);
/// frames use "do not dispose", so unchanged pixels keep showing the previous frame.
/// </summary>
public sealed class GifWriter : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly LzwEncoder _lzw = new();
    private readonly GifEncoderOptions? _options;
    private byte[]? _shown; // what a viewer sees after the last frame (BGRA), when transparency is used
    private bool _finished;

    public int Width { get; }
    public int Height { get; }
    public int FrameCount { get; private set; }

    /// <param name="loopCount">0 loops forever.</param>
    /// <param name="options">Quality settings; null writes every pixel of each region with a median-cut palette.</param>
    public GifWriter(Stream stream, int width, int height, int loopCount = 0, bool leaveOpen = false, GifEncoderOptions? options = null)
    {
        _options = options;
        if (width is < 1 or > ushort.MaxValue || height is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "GIF dimensions must be 1-65535.");
        }

        _stream = stream;
        _leaveOpen = leaveOpen;
        Width = width;
        Height = height;

        _stream.Write(Encoding.ASCII.GetBytes("GIF89a"));
        WriteUInt16(width);
        WriteUInt16(height);
        _stream.WriteByte(0x70); // no global colour table, 8-bit colour resolution
        _stream.WriteByte(0);    // background colour index
        _stream.WriteByte(0);    // pixel aspect ratio

        // NETSCAPE2.0 application extension: loop count.
        _stream.WriteByte(0x21);
        _stream.WriteByte(0xFF);
        _stream.WriteByte(11);
        _stream.Write(Encoding.ASCII.GetBytes("NETSCAPE2.0"));
        _stream.WriteByte(3);
        _stream.WriteByte(1);
        WriteUInt16(loopCount);
        _stream.WriteByte(0);
    }

    /// <summary>Writes <paramref name="region"/> of a full-canvas BGRA buffer as the next frame.</summary>
    /// <param name="delayCentiseconds">How long this frame stays on screen (GIF unit: 1/100 s).</param>
    public void WriteFrame(ReadOnlySpan<byte> bgra, int stride, PixelRect region, int delayCentiseconds)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        if (region.IsEmpty || region.X < 0 || region.Y < 0 || region.Right > Width || region.Bottom > Height)
        {
            throw new ArgumentOutOfRangeException(nameof(region), "Frame region must lie inside the canvas.");
        }

        if (_options is not null)
        {
            WriteFrameWithOptions(bgra, stride, region, delayCentiseconds, _options);
            return;
        }

        var image = ColorQuantizer.Quantize(bgra, stride, region);
        var tableBits = 1;
        while (1 << tableBits < image.ColorCount)
        {
            tableBits++;
        }

        // Graphic control extension: disposal 1 (do not dispose), no transparency.
        _stream.WriteByte(0x21);
        _stream.WriteByte(0xF9);
        _stream.WriteByte(4);
        _stream.WriteByte(0x04);
        WriteUInt16(Math.Clamp(delayCentiseconds, 0, ushort.MaxValue));
        _stream.WriteByte(0);
        _stream.WriteByte(0);

        // Image descriptor with a local colour table.
        _stream.WriteByte(0x2C);
        WriteUInt16(region.X);
        WriteUInt16(region.Y);
        WriteUInt16(region.Width);
        WriteUInt16(region.Height);
        _stream.WriteByte((byte)(0x80 | (tableBits - 1)));

        var table = new byte[(1 << tableBits) * 3];
        Array.Copy(image.Palette, table, image.ColorCount * 3);
        _stream.Write(table);

        _lzw.Encode(image.Indices, Math.Max(2, tableBits), _stream);
        FrameCount++;
    }

    private void WriteFrameWithOptions(ReadOnlySpan<byte> bgra, int stride, PixelRect region, int delay, GifEncoderOptions options)
    {
        var useTransparency = options.Tolerance >= 0 && _shown is not null;
        _shown ??= new byte[Width * Height * 4];

        // Which pixels need writing: those that differ from what is on screen by more than the tolerance.
        var mask = new bool[region.Width * region.Height];
        int left = region.Width, top = region.Height, right = -1, bottom = -1;
        var k = 0;
        for (var y = 0; y < region.Height; y++)
        {
            var src = (region.Y + y) * stride + region.X * 4;
            var shown = ((region.Y + y) * Width + region.X) * 4;
            for (var x = 0; x < region.Width; x++, k++, src += 4, shown += 4)
            {
                if (useTransparency
                    && Math.Abs(bgra[src] - _shown[shown]) <= options.Tolerance
                    && Math.Abs(bgra[src + 1] - _shown[shown + 1]) <= options.Tolerance
                    && Math.Abs(bgra[src + 2] - _shown[shown + 2]) <= options.Tolerance)
                {
                    continue;
                }

                mask[k] = true;
                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
        }

        if (right < 0)
        {
            WriteEmptyFrame(delay); // nothing visible changed: a 1-pixel transparent frame just holds the time
            return;
        }

        // Crop to the pixels that change.
        var crop = new PixelRect(region.X + left, region.Y + top, right - left + 1, bottom - top + 1);
        var cropMask = new bool[crop.Width * crop.Height];
        var anyTransparent = false;
        for (var y = 0; y < crop.Height; y++)
        {
            for (var x = 0; x < crop.Width; x++)
            {
                var on = mask[(top + y) * region.Width + left + x];
                cropMask[y * crop.Width + x] = on;
                anyTransparent |= !on;
            }
        }

        var image = FrameQuantizer.Quantize(bgra, stride, crop, anyTransparent ? cropMask : null, anyTransparent ? 255 : 256,
            options.RefinePasses, options.Dither);
        var transparentIndex = anyTransparent ? image.ColorCount : -1;
        var colors = image.ColorCount + (anyTransparent ? 1 : 0);
        var tableBits = 1;
        while (1 << tableBits < colors)
        {
            tableBits++;
        }

        var indices = image.Indices;
        for (var y = 0; y < crop.Height; y++)
        {
            var shown = ((crop.Y + y) * Width + crop.X) * 4;
            for (var x = 0; x < crop.Width; x++, shown += 4)
            {
                var i = y * crop.Width + x;
                if (!cropMask[i])
                {
                    indices[i] = (byte)transparentIndex;
                    continue;
                }

                // Remember the colour actually shown, so later comparisons can't drift.
                var c = indices[i] * 3;
                _shown[shown] = image.Palette[c + 2];
                _shown[shown + 1] = image.Palette[c + 1];
                _shown[shown + 2] = image.Palette[c];
            }
        }

        WriteImage(crop, delay, tableBits, image.Palette, image.ColorCount, indices, transparentIndex);
    }

    private void WriteEmptyFrame(int delay) =>
        WriteImage(new PixelRect(0, 0, 1, 1), delay, 1, new byte[6], 1, [1], transparentIndex: 1);

    private void WriteImage(PixelRect rect, int delay, int tableBits, byte[] palette, int colorCount, byte[] indices, int transparentIndex)
    {
        // Graphic control extension: disposal 1 (do not dispose), with a transparent index when there is one.
        _stream.WriteByte(0x21);
        _stream.WriteByte(0xF9);
        _stream.WriteByte(4);
        _stream.WriteByte((byte)(0x04 | (transparentIndex >= 0 ? 0x01 : 0)));
        WriteUInt16(Math.Clamp(delay, 0, ushort.MaxValue));
        _stream.WriteByte((byte)Math.Max(0, transparentIndex));
        _stream.WriteByte(0);

        _stream.WriteByte(0x2C);
        WriteUInt16(rect.X);
        WriteUInt16(rect.Y);
        WriteUInt16(rect.Width);
        WriteUInt16(rect.Height);
        _stream.WriteByte((byte)(0x80 | (tableBits - 1)));
        var table = new byte[(1 << tableBits) * 3];
        Array.Copy(palette, table, Math.Min(palette.Length, colorCount * 3));
        _stream.Write(table);
        _lzw.Encode(indices, Math.Max(2, tableBits), _stream);
        FrameCount++;
    }

    /// <summary>Writes the trailer. Called by <see cref="Dispose"/> if not called explicitly.</summary>
    public void Finish()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        _stream.WriteByte(0x3B);
        _stream.Flush();
    }

    public void Dispose()
    {
        Finish();
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }
    }

    private void WriteUInt16(int value)
    {
        _stream.WriteByte((byte)value);
        _stream.WriteByte((byte)(value >> 8));
    }
}

public static class FrameDiff
{
    /// <summary>The smallest rectangle containing every pixel that differs (alpha ignored), or null if identical.</summary>
    public static PixelRect? ChangedRegion(ReadOnlySpan<byte> previous, ReadOnlySpan<byte> current, int width, int height, int stride)
    {
        var top = -1;
        var bottom = -1;
        for (var y = 0; y < height; y++)
        {
            if (!RowEqual(previous, current, y, width, stride))
            {
                if (top < 0)
                {
                    top = y;
                }

                bottom = y;
            }
        }

        if (top < 0)
        {
            return null;
        }

        var left = width;
        var right = -1;
        for (var y = top; y <= bottom; y++)
        {
            var a = MemoryMarshal.Cast<byte, uint>(previous.Slice(y * stride, width * 4));
            var b = MemoryMarshal.Cast<byte, uint>(current.Slice(y * stride, width * 4));
            for (var x = 0; x < left; x++)
            {
                if (((a[x] ^ b[x]) & 0x00FFFFFF) != 0)
                {
                    left = x;
                    break;
                }
            }

            for (var x = width - 1; x > right; x--)
            {
                if (((a[x] ^ b[x]) & 0x00FFFFFF) != 0)
                {
                    right = x;
                    break;
                }
            }
        }

        return new PixelRect(left, top, right - left + 1, bottom - top + 1);
    }

    private static bool RowEqual(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int y, int width, int stride)
    {
        var ra = a.Slice(y * stride, width * 4);
        var rb = b.Slice(y * stride, width * 4);
        if (ra.SequenceEqual(rb))
        {
            return true;
        }

        // Rows can differ only in alpha (screen captures leave it undefined); compare colour only.
        var ua = MemoryMarshal.Cast<byte, uint>(ra);
        var ub = MemoryMarshal.Cast<byte, uint>(rb);
        for (var x = 0; x < width; x++)
        {
            if (((ua[x] ^ ub[x]) & 0x00FFFFFF) != 0)
            {
                return false;
            }
        }

        return true;
    }
}
