using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Toastshot.Capture;
using Toastshot.Editor;
using Toastshot.Gif;
using Toastshot.Overlay;
using Toastshot.Services;
using Microsoft.Win32;

namespace Toastshot;

/// <summary>Owns app-wide state (settings, hotkeys, tray) and runs the capture and GIF flows.</summary>
public sealed class AppController : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private HotkeyService? _hotkeys;
    private KeyboardHook? _keyboardHook;
    private readonly Dictionary<string, DateTime> _lastFired = [];
    private TrayIcon? _tray;
    private MainWindow? _main;
    private GifSession? _gif;
    private bool _selecting;
    private bool _exiting;

    public AppController(AppSettings settings)
    {
        Settings = settings;
    }

    public AppSettings Settings { get; private set; }

    /// <summary>Hotkeys that could not be registered (usually taken by another app).</summary>
    public IReadOnlyList<string> HotkeyProblems { get; private set; } = [];

    public event EventHandler? SettingsChanged;

    /// <summary>Newer releases, once a check has found some.</summary>
    public UpdateCheck? AvailableUpdate { get; private set; }

    public event EventHandler? UpdateFound;

    /// <summary>
    /// Asks GitHub for newer releases: at most about once a day unless <paramref name="now"/> (the Check now button).
    /// Returns the result, or null when updates aren't set up, are turned off, or the check failed.
    /// </summary>
    public async Task<UpdateCheck?> CheckForUpdatesAsync(bool now)
    {
        if (!Updater.IsConfigured || !now && (!Settings.CheckForUpdates || Settings.LastUpdateCheck > DateTimeOffset.Now.AddHours(-20)))
        {
            return null;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var check = await Updater.CheckAsync(_http, timeout.Token);
            SavePreference(s => s.LastUpdateCheck = DateTimeOffset.Now);
            AvailableUpdate = check.Latest is null ? null : check;
            if (AvailableUpdate is not null)
            {
                Log.Write($"Update found: {check.Latest!.Version} ({check.Newer.Count} newer releases).");
                UpdateFound?.Invoke(this, EventArgs.Empty);
            }

            return check;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            Log.Write($"Update check failed: {ex.Message}");
            return null;
        }
    }

    public void ShowUpdate()
    {
        if (AvailableUpdate is { } update)
        {
            new UpdateWindow(update).Show();
        }
    }

    public OllamaClient CreateOllama() => new(_http, Settings.OllamaUrl, LocalAi.KeepAliveValue(Settings.AiKeepAlive));

    public void Start(string[] args)
    {
        _hotkeys = new HotkeyService();
        // Games often block app hotkeys while they are in front; a low-level hook still sees the keys.
        _keyboardHook = new KeyboardHook();
        Log.Write($"Started. Keyboard hook {(_keyboardHook.IsInstalled ? "installed" : "NOT installed")}.");
        _tray = new TrayIcon(this);
        RegisterHotkeys();

        var file = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal) && File.Exists(a));
        if (file is not null)
        {
            OpenFile(file);
        }
        else if (!args.Contains("--tray", StringComparer.OrdinalIgnoreCase))
        {
            ShowMain();
        }

        // Look for updates shortly after starting, then every few hours (the check itself runs about once a day).
        var updates = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        updates.Tick += async (_, _) =>
        {
            updates.Interval = TimeSpan.FromHours(4);
            await CheckForUpdatesAsync(now: false);
        };
        updates.Start();
    }

    public void ShowMain()
    {
        _main ??= new MainWindow(this);
        _main.Show();
        if (_main.WindowState == WindowState.Minimized)
        {
            _main.WindowState = WindowState.Normal;
        }

        _main.Activate();
    }

    /// <summary>Called when the main window is closed by the user (it hides instead).</summary>
    internal void MainWindowHidden() => _tray?.ShowStillRunningHint();

    internal bool IsExiting => _exiting;

    public async Task CaptureAsync(bool fromMainWindow)
    {
        if (_selecting || _gif is not null)
        {
            return;
        }

        _selecting = true;
        try
        {
            await HideMainForCaptureAsync(fromMainWindow);

            // Freeze the screen first so menus and tooltips open at hotkey time are kept.
            var screen = ScreenCapture.VirtualScreen;
            var frozen = ScreenCapture.CaptureRegion(screen);
            // The overlay marks up, copies, saves and reads the selection in place; only the full editor opens after it.
            var overlay = new CaptureWindow(this, frozen, screen);
            overlay.ShowDialog();
            if (overlay.OpenInEditor is { } image)
            {
                new EditorWindow(this, image).Show();
            }
            else if (fromMainWindow)
            {
                ShowMain();
            }
        }
        finally
        {
            _selecting = false;
        }
    }

    /// <summary>
    /// Starts a GIF region selection. If an area is already chosen, starts recording; if recording, stops.
    /// </summary>
    public async Task RecordGifAsync(bool fromMainWindow)
    {
        if (_gif is not null)
        {
            await _gif.ToggleAsync();
            return;
        }

        if (_selecting)
        {
            return;
        }

        _selecting = true;
        PixelRect? selection;
        try
        {
            await HideMainForCaptureAsync(fromMainWindow);
            // Choose on a frozen picture (smooth to drag over); recording then follows the live screen.
            var overlay = new RegionSelectWindow(ScreenCapture.CaptureRegion(ScreenCapture.VirtualScreen), "Drag to choose the GIF area");
            selection = overlay.ShowDialog() == true ? overlay.Selection : null;
        }
        finally
        {
            _selecting = false;
        }

        if (selection is not { } region)
        {
            if (fromMainWindow)
            {
                ShowMain();
            }

            return;
        }

        _gif = new GifSession(this, region);
        _gif.Ended += (_, result) =>
        {
            _gif = null;
            if (result is not null)
            {
                new GifPreviewWindow(this, result.Path, result).Show();
            }
            else if (fromMainWindow)
            {
                ShowMain();
            }
        };
        _gif.Show();
    }

    /// <summary>Opens an image in the editor, or a GIF in the preview. Shows a file dialog when <paramref name="path"/> is null.</summary>
    public void OpenFile(string? path)
    {
        if (path is null)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp|All files (*.*)|*.*"
            };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            path = dialog.FileName;
        }

        try
        {
            if (Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase) && GifPlayer.CountFrames(path) > 1)
            {
                new GifPreviewWindow(this, path, null).Show();
                return;
            }

            new EditorWindow(this, LoadImage(path)).Show();
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or FileFormatException)
        {
            MessageBox.Show($"Couldn't open {Path.GetFileName(path)}: {ex.Message}", "Toastshot", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Loads an image at 96 DPI so one image pixel is one editor unit.</summary>
    internal static BitmapSource LoadImage(string path)
    {
        BitmapSource decoded;
        using (var stream = File.OpenRead(path))
        {
            decoded = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        }

        var bgra = new FormatConvertedBitmap(decoded, PixelFormats.Bgra32, null, 0);
        var stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        var result = BitmapSource.Create(bgra.PixelWidth, bgra.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    public void ShowSettings()
    {
        var window = new SettingsWindow(this);
        // Free the hotkeys while Settings is open so pressing the current combination can be recorded.
        window.Loaded += (_, _) => _hotkeys?.UnregisterAll();
        window.Closed += (_, _) => RegisterHotkeys();
        if (_main is { IsVisible: true })
        {
            window.Owner = _main;
        }

        window.Show();
        window.Activate();
    }

    /// <summary>Saves new settings and re-registers hotkeys. Returns hotkey problems, if any.</summary>
    public IReadOnlyList<string> ApplySettings(AppSettings settings)
    {
        settings.Save();
        Settings = settings;
        RegisterHotkeys();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        return HotkeyProblems;
    }

    /// <summary>Asks where to save the image and saves it. Returns the file name, or null if the user cancelled.</summary>
    /// <exception cref="IOException">The file couldn't be written (also <see cref="UnauthorizedAccessException"/>).</exception>
    public string? SaveImageAs(Window owner, BitmapSource image)
    {
        var folder = Settings.LastSaveFolder;
        var dialog = new SaveFileDialog
        {
            Filter = ImageExport.SaveImageFilter,
            FileName = ImageExport.SuggestedFileName(".png"),
            InitialDirectory = folder is not null && Directory.Exists(folder)
                ? folder
                : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };
        if (dialog.ShowDialog(owner) != true)
        {
            return null;
        }

        ImageExport.Save(image, dialog.FileName);
        SavePreference(s => s.LastSaveFolder = Path.GetDirectoryName(dialog.FileName));
        return Path.GetFileName(dialog.FileName);
    }

    /// <summary>Prints the image centred on the page, at screen size or shrunk to fit. False if the user cancelled.</summary>
    public bool PrintImage(BitmapSource image)
    {
        var dialog = new System.Windows.Controls.PrintDialog();
        if (dialog.ShowDialog() != true)
        {
            return false;
        }

        // One screen pixel is 1/96 inch: small captures print at their size, large ones shrink to the page.
        var page = new Size(dialog.PrintableAreaWidth, dialog.PrintableAreaHeight);
        var scale = Math.Min(1, Math.Min(page.Width / image.PixelWidth, page.Height / image.PixelHeight));
        var size = new Size(image.PixelWidth * scale, image.PixelHeight * scale);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(image, new Rect(new Point((page.Width - size.Width) / 2, 0), size));
        }

        dialog.PrintVisual(visual, "Toastshot capture");
        return true;
    }

    /// <summary>Persists a small preference change (e.g. last save folder) without touching hotkeys.</summary>
    public void SavePreference(Action<AppSettings> change)
    {
        change(Settings);
        try
        {
            Settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Preferences are a convenience; never fail the user's action over them.
        }
    }

    public void Exit()
    {
        _exiting = true;
        foreach (var window in Application.Current.Windows.OfType<Window>().ToList())
        {
            window.Close();
        }

        Application.Current.Shutdown();
    }

    public void Dispose()
    {
        _hotkeys?.Dispose();
        _keyboardHook?.Dispose();
        _tray?.Dispose();
        _http.Dispose();
    }

    private void RegisterHotkeys()
    {
        if (_hotkeys is null)
        {
            return;
        }

        _hotkeys.UnregisterAll();
        var problems = new List<string>();
        var watched = new List<(HotkeyGesture, Action)>();
        Register(Settings.CaptureHotkey, "Capture", () => _ = CaptureAsync(fromMainWindow: false), problems, watched);
        Register(Settings.GifHotkey, "GIF", () => _ = RecordGifAsync(fromMainWindow: false), problems, watched);
        _keyboardHook?.Watch(watched);
        HotkeyProblems = problems;
        _tray?.UpdateTooltip(Settings.CaptureHotkey, Settings.GifHotkey);
    }

    private void Register(string text, string name, Action action, List<string> problems, List<(HotkeyGesture, Action)> watched)
    {
        if (!HotkeyGesture.TryParse(text, out var gesture))
        {
            problems.Add($"{name} hotkey \"{text}\" isn't valid.");
            return;
        }

        // The hotkey and the keyboard hook can both see one press: whichever comes first acts, the other is ignored.
        void Fire(string source)
        {
            var now = DateTime.UtcNow;
            if (_lastFired.TryGetValue(name, out var last) && now - last < TimeSpan.FromMilliseconds(500))
            {
                return;
            }

            _lastFired[name] = now;
            Log.Write($"{name} hotkey {gesture} via {source}; in front: {NativeWindows.ForegroundProcessName()}");
            action();
        }

        if (!_hotkeys!.TryRegister(gesture, () => Fire("hotkey"), out _))
        {
            problems.Add(gesture is { Key: Key.PrintScreen, Modifiers: ModifierKeys.None }
                ? $"{name} hotkey Print Screen is taken by another app (Lightshot, Snipping Tool, ShareX…). Quit it or turn off its Print Screen key; Toastshot takes the key next time you open this window."
                : $"{name} hotkey {gesture} is already used by another app. Pick another in Settings.");
        }
        else
        {
            // Only keys Toastshot owns: if another app holds the key, the hook must not fire Toastshot as well.
            watched.Add((gesture, () => Fire("keyboard hook")));
            if (gesture is { Key: Key.PrintScreen, Modifiers: ModifierKeys.None } && WindowsUsesPrintScreen())
            {
                problems.Add("Windows also opens Snipping Tool on Print Screen. Turn off \"Use the Print screen key to open screen capture\" in Windows Settings (search for \"Print screen\").");
            }
        }
    }

    /// <summary>Windows 11's "Use the Print screen key to open screen capture" setting.</summary>
    private static bool WindowsUsesPrintScreen()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Keyboard");
        return key?.GetValue("PrintScreenKeyForSnippingEnabled") is int enabled && enabled == 1;
    }

    /// <summary>Tries the hotkeys again if some couldn't be registered (the app holding one may have quit). True if anything changed.</summary>
    public bool RetryHotkeys()
    {
        if (HotkeyProblems.Count == 0)
        {
            return false;
        }

        var before = string.Join("\n", HotkeyProblems);
        RegisterHotkeys();
        return before != string.Join("\n", HotkeyProblems);
    }

    private async Task HideMainForCaptureAsync(bool fromMainWindow)
    {
        if (fromMainWindow && _main is { IsVisible: true })
        {
            _main.Hide();
            // Give Windows a moment to repaint what was under the window.
            await Task.Delay(200);
        }
    }
}
