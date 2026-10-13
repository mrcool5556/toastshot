using System.IO;

namespace Glance.Services;

/// <summary>
/// A small diagnostic log at %LOCALAPPDATA%\Glance\glance.log: hotkeys received, captures, windows shown. Kept under
/// about 1 MB (the older half is dropped). Never throws.
/// </summary>
public static class Log
{
    private const long MaxBytes = 1 << 20;
    private static readonly object Gate = new();

    public static string Path { get; } = System.IO.Path.Combine(AppSettings.DataDirectory, "glance.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppSettings.DataDirectory);
                if (File.Exists(Path) && new FileInfo(Path).Length > MaxBytes)
                {
                    var lines = File.ReadAllLines(Path);
                    File.WriteAllLines(Path, lines[(lines.Length / 2)..]);
                }

                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never break the app.
        }
    }
}
