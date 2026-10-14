using System.IO;
using System.Text.Json;

namespace Toastshot.Services;

public sealed class AppSettings
{
    public string OllamaUrl { get; set; } = "http://localhost:11434";

    /// <summary>Small text model used for translation.</summary>
    public string TranslateModel { get; set; } = LocalAi.RecommendedModel;

    /// <summary>Vision model used by "Read with AI" (an alternative to Windows OCR).</summary>
    public string VisionModel { get; set; } = LocalAi.RecommendedModel;

    /// <summary>Translate and the AI reader are shown and use Ollama. Off hides them and never contacts Ollama.</summary>
    public bool AiEnabled { get; set; } = true;

    /// <summary>"System" (follow the Windows app mode), "Dark" or "Light".</summary>
    public string Theme { get; set; } = "System";

    /// <summary>How long the model stays in graphics memory after use (Ollama keep_alive): "30s", "2m", "5m", or "-1" for always.</summary>
    public string AiKeepAlive { get; set; } = "2m";

    /// <summary>Ask GitHub about once a day whether a newer Toastshot is out.</summary>
    public bool CheckForUpdates { get; set; } = true;

    public DateTimeOffset? LastUpdateCheck { get; set; }

    public string TargetLanguage { get; set; } = "English";

    /// <summary>Windows OCR language tag (e.g. "ja"); empty means the Windows display languages.</summary>
    public string OcrLanguage { get; set; } = "";

    public string CaptureHotkey { get; set; } = "PrintScreen";
    public string GifHotkey { get; set; } = "Shift+PrintScreen";

    public int GifFramesPerSecond { get; set; } = 15;
    public int GifMaxSeconds { get; set; } = 60;
    public bool GifShowCursor { get; set; } = true;

    /// <summary>New recordings are shrunk to fit this many MB (0 = no limit).</summary>
    public int GifMaxMegabytes { get; set; } = 20;

    /// <summary>Format of the saved file; older files are upgraded when loaded.</summary>
    public int SettingsVersion { get; set; }

    private const int CurrentVersion = 3;

    /// <summary>Folder the Save dialog opens in; remembered from the last save.</summary>
    public string? LastSaveFolder { get; set; }

    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Toastshot");

    public static string DefaultPath => Path.Combine(DataDirectory, "settings.json");

    /// <summary>
    /// Toastshot was called Glance before 0.2.0: the first time it starts, it takes the settings Glance saved.
    /// </summary>
    public static void MoveFromGlance()
    {
        var old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glance", "settings.json");
        try
        {
            if (File.Exists(old) && !File.Exists(DefaultPath))
            {
                Directory.CreateDirectory(DataDirectory);
                File.Copy(old, DefaultPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Defaults are fine if the old settings can't be read.
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions);
                if (settings is not null)
                {
                    settings.Upgrade();
                    settings.Normalize();
                    return settings;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A broken settings file falls back to defaults rather than stopping the app.
        }

        return new AppSettings();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Normalize();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    private void Upgrade()
    {
        if (SettingsVersion < 2 && CaptureHotkey == "Ctrl+Alt+S")
        {
            CaptureHotkey = "PrintScreen"; // version 2: Print Screen became the default capture key
        }

        if (SettingsVersion < 3 && GifHotkey == "Ctrl+Alt+G")
        {
            GifHotkey = "Shift+PrintScreen"; // version 3: often taken by game overlays; pairs with Print Screen
        }
    }

    private void Normalize()
    {
        SettingsVersion = CurrentVersion;
        GifFramesPerSecond = Math.Clamp(GifFramesPerSecond, 1, 30);
        GifMaxSeconds = Math.Clamp(GifMaxSeconds, 1, 600);
        GifMaxMegabytes = Math.Clamp(GifMaxMegabytes, 0, 1000);
        Theme = Theme is "Dark" or "Light" ? Theme : "System";
        AiKeepAlive = LocalAi.MemoryModes.Any(m => m.KeepAlive == AiKeepAlive) ? AiKeepAlive : "2m";
        OllamaUrl = string.IsNullOrWhiteSpace(OllamaUrl) ? "http://localhost:11434" : OllamaUrl.Trim().TrimEnd('/');
        TargetLanguage = string.IsNullOrWhiteSpace(TargetLanguage) ? "English" : TargetLanguage.Trim();
        TranslateModel = TranslateModel?.Trim() ?? "";
        VisionModel = VisionModel?.Trim() ?? "";
        OcrLanguage = OcrLanguage?.Trim() ?? "";
    }
}
