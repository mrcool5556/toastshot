using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Glance.Tests;

internal static class TestUtil
{
    /// <summary>WPF objects (visuals, RenderTargetBitmap) need an STA thread.</summary>
    public static T RunSta<T>(Func<T> func)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = func();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            ExceptionDispatchInfo.Throw(error);
        }

        return result;
    }

    public static void RunSta(Action action) => RunSta(() =>
    {
        action();
        return 0;
    });

    public static byte[] Pixels(BitmapSource source)
    {
        var bgra = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    public static GifBitmapDecoder DecodeGif(byte[] gif) =>
        new(new MemoryStream(gif), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

    public static string TempFile(string extension) =>
        Path.Combine(Path.GetTempPath(), "glance-tests", Guid.NewGuid().ToString("N") + extension);
}
