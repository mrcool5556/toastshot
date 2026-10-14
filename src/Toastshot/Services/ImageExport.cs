using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Toastshot.Services;

public static class ImageExport
{
    public const string SaveImageFilter = "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg|Bitmap (*.bmp)|*.bmp";

    public static string SuggestedFileName(string extension) =>
        $"Toastshot {DateTime.Now:yyyy-MM-dd HHmmss}{extension}";

    public static byte[] EncodePng(BitmapSource image)
    {
        using var stream = new MemoryStream();
        Encode(image, new PngBitmapEncoder(), stream);
        return stream.ToArray();
    }

    /// <summary>Saves in the format implied by the file extension (PNG if unknown).</summary>
    public static void Save(BitmapSource image, string path)
    {
        BitmapEncoder encoder = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = 92 },
            ".bmp" => new BmpBitmapEncoder(),
            _ => new PngBitmapEncoder()
        };

        var temp = path + ".part";
        using (var stream = File.Create(temp))
        {
            Encode(image, encoder, stream);
        }

        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Puts the image on the clipboard as both PNG (keeps quality in modern apps) and a bitmap (for older apps).</summary>
    public static Task CopyImageAsync(BitmapSource image)
    {
        var data = new DataObject();
        data.SetImage(image);
        data.SetData("PNG", new MemoryStream(EncodePng(image)), autoConvert: false);
        return SetClipboardAsync(data);
    }

    /// <summary>Copies a file (e.g. a GIF) so it can be pasted into chat apps, browsers and Explorer.</summary>
    public static Task CopyFileAsync(string path)
    {
        var data = new DataObject();
        data.SetFileDropList(new StringCollection { path });
        return SetClipboardAsync(data);
    }

    public static Task CopyTextAsync(string text) => SetClipboardAsync(new DataObject(DataFormats.UnicodeText, text));

    private static void Encode(BitmapSource image, BitmapEncoder encoder, Stream stream)
    {
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(stream);
    }

    private const int ClipboardBusy = unchecked((int)0x800401D0); // CLIPBRD_E_CANT_OPEN

    /// <summary>
    /// Only one app can have the clipboard open, and clipboard watchers (clipboard managers, chat apps, browsers) open it
    /// for a moment every time it changes. Each try here returns at once (WPF's own Clipboard call waits up to a second
    /// inside, freezing the app), and the waits between tries are asynchronous, so Toastshot never freezes. Gives up after
    /// about 2 seconds. Must be called on the UI thread.
    /// </summary>
    private static async Task SetClipboardAsync(DataObject data)
    {
        EnsureOle();
        var com = (System.Runtime.InteropServices.ComTypes.IDataObject)data;
        for (var attempt = 0; ; attempt++)
        {
            var hr = OleSetClipboard(com);
            if (hr == 0)
            {
                await FlushAsync(com);
                return;
            }

            if (hr != ClipboardBusy)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            if (attempt >= 40)
            {
                var holder = ClipboardHolder();
                Log.Write($"Clipboard: still busy after 2 s, held by {holder}.");
                throw new ClipboardBusyException(holder, Marshal.GetExceptionForHR(hr)!);
            }

            await Task.Delay(50);
        }
    }

    /// <summary>
    /// Makes the copy outlive Toastshot (renders every format now). The data is already on the clipboard and pastes
    /// either way, so if a watcher keeps the clipboard busy this just stops trying.
    /// </summary>
    private static async Task FlushAsync(System.Runtime.InteropServices.ComTypes.IDataObject data)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (OleIsCurrentClipboard(data) != 0 || OleFlushClipboard() == 0)
            {
                return; // flushed, or something else has been copied since
            }

            await Task.Delay(50);
        }

        Log.Write($"Clipboard: copied, but {ClipboardHolder()} kept it busy, so it stays only while Toastshot runs.");
    }

    /// <summary>The app that has the clipboard open right now, or "another app".</summary>
    private static string ClipboardHolder()
    {
        try
        {
            var window = GetOpenClipboardWindow();
            if (window != IntPtr.Zero && GetWindowThreadProcessId(window, out var pid) != 0)
            {
                using var process = System.Diagnostics.Process.GetProcessById((int)pid);
                return process.ProcessName;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
        }

        return "another app";
    }

    /// <summary>The user-facing sentence for a clipboard that stayed busy.</summary>
    public static string BusyMessage(Exception ex) =>
        $"Couldn't copy: {(ex as ClipboardBusyException)?.Holder ?? "another app"} is using the clipboard. Try again.";

    [ThreadStatic]
    private static bool _oleReady;

    /// <summary>The clipboard API needs OLE on this thread (WPF usually sets it up, but not always before the first copy).</summary>
    private static void EnsureOle()
    {
        if (!_oleReady)
        {
            _ = OleInitialize(IntPtr.Zero); // S_FALSE when already initialised: fine
            _oleReady = true;
        }
    }

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr reserved);

    [DllImport("ole32.dll")]
    private static extern int OleSetClipboard(System.Runtime.InteropServices.ComTypes.IDataObject? data);

    [DllImport("ole32.dll")]
    private static extern int OleFlushClipboard();

    /// <summary>S_OK (0) if <paramref name="data"/> is still what is on the clipboard.</summary>
    [DllImport("ole32.dll")]
    private static extern int OleIsCurrentClipboard(System.Runtime.InteropServices.ComTypes.IDataObject data);

    [DllImport("user32.dll")]
    private static extern IntPtr GetOpenClipboardWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}

/// <summary>The clipboard stayed open in another app (named by <see cref="Holder"/>) for every try.</summary>
public sealed class ClipboardBusyException(string holder, Exception inner)
    : ExternalException($"{holder} is using the clipboard.", inner)
{
    public string Holder { get; } = holder;
}

/// <summary>Scratch files (GIF recordings waiting to be saved). Nothing here is the user's saved work.</summary>
public static class TempFiles
{
    public static string Directory { get; } = Path.Combine(Path.GetTempPath(), "Toastshot");

    public static string NewPath(string extension)
    {
        System.IO.Directory.CreateDirectory(Directory);
        return Path.Combine(Directory, $"toastshot-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}{extension}");
    }

    /// <summary>Removes scratch files older than <paramref name="maxAge"/>. Recent ones may still be on the clipboard.</summary>
    public static void CleanUp(TimeSpan maxAge)
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return;
        }

        foreach (var file in System.IO.Directory.EnumerateFiles(Directory))
        {
            try
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > maxAge)
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (Path.GetDirectoryName(Path.GetFullPath(path)) == Path.GetFullPath(Directory).TrimEnd(Path.DirectorySeparatorChar))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
