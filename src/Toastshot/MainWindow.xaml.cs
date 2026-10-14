using System.ComponentModel;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Shapes;
using Toastshot.Services;

namespace Toastshot;

public partial class MainWindow : Window
{
    private readonly AppController _app;

    public MainWindow(AppController app)
    {
        InitializeComponent();
        _app = app;
        _app.SettingsChanged += (_, _) => Refresh();
        _app.UpdateFound += (_, _) => ShowUpdateDot();
        ShowUpdateDot();
        Refresh();
        Activated += (_, _) =>
        {
            // A hotkey another app held (e.g. Print Screen) may be free now.
            if (_app.RetryHotkeys())
            {
                Refresh();
            }
            else
            {
                _ = CheckOllamaAsync();
            }
        };
    }

    private void Refresh()
    {
        CaptureKeys.ItemsSource = KeycapsConverter.Split(_app.Settings.CaptureHotkey);
        GifKeys.ItemsSource = KeycapsConverter.Split(_app.Settings.GifHotkey);
        HotkeyWarning.Text = string.Join("\n", _app.HotkeyProblems);
        HotkeyWarning.Visibility = _app.HotkeyProblems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _ = CheckOllamaAsync();
    }

    private bool _checking;

    /// <summary>Shows whether Translate and the AI reader will work, and how much graphics memory the model holds now.</summary>
    private async Task CheckOllamaAsync()
    {
        if (_checking)
        {
            return;
        }

        _checking = true;
        var settings = _app.Settings;
        try
        {
            if (!settings.AiEnabled)
            {
                ShowStatus("InkMutedBrush", "Local AI off", " · turn it on in Settings to translate");
                return;
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var client = _app.CreateOllama();
            var models = await client.ListModelsAsync(timeout.Token);
            if (!models.Any(m => IsSameModel(m.Name, settings.TranslateModel)))
            {
                ShowStatus("StatusWarnBrush", "Local AI needs setting up", $" · {LocalAi.DisplayName(settings.TranslateModel)} isn't downloaded", setUpLink: true);
                return;
            }

            var loaded = await client.ListLoadedAsync(timeout.Token);
            var vram = loaded.Sum(m => m.VramBytes);
            ShowStatus("StatusOkBrush", "Local AI ready",
                $" · {LocalAi.DisplayName(settings.TranslateModel)}" + (vram > 0 ? $" · using {vram / 1073741824.0:0.0} GB of graphics memory" : ""));
        }
        catch (Exception ex) when (ex is OllamaException or OperationCanceledException)
        {
            if (LocalAi.IsInstalled)
            {
                ShowStatus("StatusWarnBrush", "Ollama isn't running", " · capture, drawing and Windows OCR still work");
            }
            else
            {
                ShowStatus("InkMutedBrush", "Local AI not set up", " · unlocks Translate", setUpLink: true);
            }
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>The state in ink and semibold, the detail in the hint colour; the dot repeats the state.</summary>
    private void ShowStatus(string dotBrush, string state, string detail, bool setUpLink = false)
    {
        OllamaDot.SetResourceReference(Shape.FillProperty, dotBrush);
        var headline = new Run(state) { FontWeight = FontWeights.SemiBold };
        headline.SetResourceReference(TextElement.ForegroundProperty, "InkBrush");
        OllamaStatus.Inlines.Clear();
        OllamaStatus.Inlines.Add(headline);
        OllamaStatus.Inlines.Add(new Run(detail));
        if (setUpLink)
        {
            var link = new Hyperlink(new Run("Set up…")) { TextDecorations = null, Cursor = System.Windows.Input.Cursors.Hand };
            link.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
            link.Click += (_, _) =>
            {
                new AiSetupWindow(_app) { Owner = this }.ShowDialog();
                _ = CheckOllamaAsync();
            };
            OllamaStatus.Inlines.Add(new Run("  "));
            OllamaStatus.Inlines.Add(link);
        }
    }

    /// <summary>"qwen2.5:3b" matches "qwen2.5:3b"; "llava" matches "llava:latest".</summary>
    internal static bool IsSameModel(string installed, string wanted) =>
        string.Equals(installed, wanted, StringComparison.OrdinalIgnoreCase) ||
        (!wanted.Contains(':') && string.Equals(installed, wanted + ":latest", StringComparison.OrdinalIgnoreCase));

    private void OnCapture(object sender, RoutedEventArgs e) => _ = _app.CaptureAsync(fromMainWindow: true);

    private void OnGif(object sender, RoutedEventArgs e) => _ = _app.RecordGifAsync(fromMainWindow: true);

    private void OnOpen(object sender, RoutedEventArgs e) => _app.OpenFile(null);

    private void OnSettings(object sender, RoutedEventArgs e) => _app.ShowSettings();

    private void ShowUpdateDot()
    {
        var latest = _app.AvailableUpdate?.Latest;
        UpdateDot.Visibility = latest is null ? Visibility.Collapsed : Visibility.Visible;
        SettingsButton.ToolTip = latest is null ? null : $"Toastshot {latest.Version} is available";
    }

    private void OnQuit(object sender, RoutedEventArgs e) => _app.Exit();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_app.IsExiting)
        {
            // Closing hides to the tray; Quit (or the tray menu) exits.
            e.Cancel = true;
            Hide();
            _app.MainWindowHidden();
        }

        base.OnClosing(e);
    }
}
