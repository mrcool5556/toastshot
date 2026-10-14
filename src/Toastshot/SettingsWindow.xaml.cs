using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Toastshot.Services;

namespace Toastshot;

public partial class SettingsWindow : Window
{
    /// <summary>"Keep GIFs under" choices in MB; 0 is no limit.</summary>
    internal static readonly int[] GifLimits = [0, 5, 10, 20, 50, 100];

    private readonly AppController _app;

    public SettingsWindow(AppController app)
    {
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height * 0.9; // longer than that, the body scrolls
        _app = app;
        var s = app.Settings;

        CaptureHotkey.Text = s.CaptureHotkey;
        GifHotkey.Text = s.GifHotkey;
        OllamaUrl.Text = s.OllamaUrl;
        TranslateModel.Text = s.TranslateModel;
        VisionModel.Text = s.VisionModel;
        TargetLanguage.ItemsSource = AiTasks.CommonLanguages;
        TargetLanguage.Text = s.TargetLanguage;
        Fps.Text = s.GifFramesPerSecond.ToString();
        MaxSeconds.Text = s.GifMaxSeconds.ToString();
        ShowCursor.IsChecked = s.GifShowCursor;
        StartWithWindows.IsChecked = Startup.IsEnabled();
        GifLimit.ItemsSource = GifLimits.Select(m => m == 0 ? "No limit" : $"{m} MB").ToList();
        GifLimit.SelectedIndex = Math.Max(0, Array.IndexOf(GifLimits, s.GifMaxMegabytes));

        var languages = new List<OcrLanguageOption> { new("", "Windows display language") };
        try
        {
            languages.AddRange(OcrService.InstalledLanguages());
        }
        catch (Exception)
        {
            // OCR API missing; only the default entry is offered.
        }

        OcrLanguage.ItemsSource = languages;
        OcrLanguage.SelectedItem = languages.FirstOrDefault(l => string.Equals(l.Tag, s.OcrLanguage, StringComparison.OrdinalIgnoreCase)) ?? languages[0];

        AiEnabled.IsChecked = s.AiEnabled;
        VersionText.Text = $"Toastshot {Updater.Current}";
        CheckForUpdates.IsChecked = s.CheckForUpdates;
        ShowUpdateStatus(app.AvailableUpdate);
        if (!Updater.IsConfigured)
        {
            UpdateStatus.Text = "This copy doesn't check for updates";
            UpdateAction.Visibility = AutoUpdateRow.Visibility = Visibility.Collapsed;
        }

        SupportRow.Visibility = Project.DonationUrl.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SourceButton.Visibility = Project.RepositoryUrl is null ? Visibility.Collapsed : Visibility.Visible;
        MemoryMode.ItemsSource = LocalAi.MemoryModes.Select(m => m.Label).ToList();
        MemoryMode.SelectedIndex = Math.Max(0, LocalAi.MemoryModes.ToList().FindIndex(m => m.KeepAlive == s.AiKeepAlive));
        RecommendedNote.Text = $"Recommended: {LocalAi.RecommendedName} for both ({LocalAi.RecommendedModel})";

        Loaded += async (_, _) =>
        {
            await RefreshAiAsync();
            await LoadModelsAsync();
        };
    }

    // ---------- About ----------

    private void ShowUpdateStatus(UpdateCheck? check)
    {
        if (check?.Latest is { } latest)
        {
            UpdateStatus.Text = $"Toastshot {latest.Version} is available";
            UpdateAction.Content = "See what's new";
        }
        else
        {
            UpdateStatus.Text = _app.Settings.LastUpdateCheck is { } at ? $"Up to date · checked {at.LocalDateTime:d MMM, HH:mm}" : "Up to date";
            UpdateAction.Content = "Check now";
        }
    }

    private async void OnUpdateAction(object sender, RoutedEventArgs e)
    {
        if (_app.AvailableUpdate is not null)
        {
            _app.ShowUpdate();
            return;
        }

        UpdateAction.IsEnabled = false;
        UpdateStatus.Text = "Checking…";
        var check = await _app.CheckForUpdatesAsync(now: true);
        UpdateAction.IsEnabled = true;
        if (check is null)
        {
            UpdateStatus.Text = "Couldn't reach GitHub. Try again later.";
            return;
        }

        ShowUpdateStatus(check);
    }

    private void OnSupport(object sender, RoutedEventArgs e) => Open(Project.DonationUrl);

    private void OnSource(object sender, RoutedEventArgs e) => Open(Project.RepositoryUrl);

    private static void Open(string? url)
    {
        if (!string.IsNullOrEmpty(url))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
    }

    // ---------- Local AI ----------

    private enum AiStep
    {
        None,
        SetUp,
        Start,
        Unload
    }

    private AiStep _aiStep;

    /// <summary>Shows where the local AI stands and the one action that moves it forward.</summary>
    private async Task RefreshAiAsync()
    {
        var client = _app.CreateOllama();
        var model = _app.Settings.TranslateModel;
        if (!await LocalAi.IsRunningAsync(client, CancellationToken.None))
        {
            if (LocalAi.IsInstalled)
            {
                ShowAi("StatusWarnBrush", "Ollama isn't running", "Translate and AI vision need it", AiStep.Start, "Start");
            }
            else
            {
                ShowAi("InkMutedBrush", "Not set up", $"Unlocks Translate and AI vision. Runs on this PC; nothing is uploaded.", AiStep.SetUp, "Set up…");
            }

            return;
        }

        try
        {
            var models = await client.ListModelsAsync(CancellationToken.None);
            if (!models.Any(m => MainWindow.IsSameModel(m.Name, model)))
            {
                ShowAi("StatusWarnBrush", $"{LocalAi.DisplayName(model)} isn't downloaded", "Set up downloads it", AiStep.SetUp, "Set up…");
                return;
            }

            var loaded = await client.ListLoadedAsync(CancellationToken.None);
            var vram = loaded.Sum(m => m.VramBytes);
            ShowAi("StatusOkBrush", $"Ready · {LocalAi.DisplayName(model)}",
                loaded.Count == 0
                    ? "Not in memory now; it loads when you use Translate or AI vision"
                    : $"Using {vram / 1073741824.0:0.0} GB of graphics memory now",
                loaded.Count == 0 ? AiStep.None : AiStep.Unload, "Unload now");
        }
        catch (OllamaException ex)
        {
            ShowAi("StatusErrorBrush", "Ollama didn't answer", ex.Message, AiStep.Start, "Start");
        }
    }

    private void ShowAi(string dot, string status, string detail, AiStep step, string action)
    {
        AiDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, dot);
        AiStatus.Text = status;
        AiDetail.Text = detail;
        _aiStep = step;
        AiAction.Content = action;
        AiAction.Visibility = step == AiStep.None ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnAiAction(object sender, RoutedEventArgs e)
    {
        AiAction.IsEnabled = false;
        try
        {
            var client = _app.CreateOllama();
            switch (_aiStep)
            {
                case AiStep.SetUp:
                    new AiSetupWindow(_app) { Owner = this }.ShowDialog();
                    TranslateModel.Text = _app.Settings.TranslateModel;
                    VisionModel.Text = _app.Settings.VisionModel;
                    break;
                case AiStep.Start:
                    AiStatus.Text = "Starting Ollama…";
                    await LocalAi.StartAsync(client, CancellationToken.None);
                    break;
                case AiStep.Unload:
                    foreach (var loaded in await client.ListLoadedAsync(CancellationToken.None))
                    {
                        await client.UnloadAsync(loaded.Name, CancellationToken.None);
                    }

                    break;
            }
        }
        catch (Exception ex) when (ex is OllamaException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            AiDetail.Text = ex.Message;
        }
        finally
        {
            AiAction.IsEnabled = true;
            await RefreshAiAsync();
            await LoadModelsAsync();
        }
    }

    private void OnAdvanced(object sender, RoutedEventArgs e) =>
        AdvancedPanel.Visibility = AdvancedToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private void OnUseRecommended(object sender, RoutedEventArgs e)
    {
        TranslateModel.Text = LocalAi.RecommendedModel;
        VisionModel.Text = LocalAi.RecommendedModel;
    }

    private async void OnRefreshModels(object sender, RoutedEventArgs e) => await LoadModelsAsync();

    private async Task LoadModelsAsync()
    {
        ModelStatus.Text = "Loading models…";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var models = await new OllamaClient(http, OllamaUrl.Text.Trim()).ListModelsAsync(CancellationToken.None);
            SetItems(TranslateModel, models.Select(m => m.Name));
            SetItems(VisionModel, models.Where(m => m.HasVision).Select(m => m.Name));
            ModelStatus.Text = models.Count == 0
                ? $"Ollama has no models yet. Set up downloads {LocalAi.RecommendedName}."
                : $"{models.Count} models installed, {models.Count(m => m.HasVision)} with vision.";
        }
        catch (OllamaException ex)
        {
            ModelStatus.Text = ex.Message;
        }
    }

    /// <summary>Replaces the list without losing what the user typed.</summary>
    private static void SetItems(ComboBox box, IEnumerable<string> items)
    {
        var text = box.Text;
        box.ItemsSource = items.ToList();
        box.Text = text;
    }

    /// <summary>Windows sends Print Screen only as a key release, so it is recorded here.</summary>
    private void OnHotkeyKeyUp(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.PrintScreen && HotkeyGesture.TryParse(new HotkeyGesture(Keyboard.Modifiers, key).ToString(), out var gesture))
        {
            ((TextBox)sender).Text = gesture.ToString();
            Error.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
    }

    /// <summary>Records the pressed combination instead of typing characters.</summary>
    private void OnHotkeyKey(object sender, KeyEventArgs e)
    {
        var box = (TextBox)sender;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Tab)
        {
            return; // keep keyboard navigation
        }

        e.Handled = true;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return;
        }

        var text = new HotkeyGesture(Keyboard.Modifiers, key).ToString();
        if (HotkeyGesture.TryParse(text, out var gesture))
        {
            box.Text = gesture.ToString();
            Error.Visibility = Visibility.Collapsed;
        }
        else
        {
            ShowError("Use at least one of Ctrl, Alt, Shift or Win with a key (function keys and PrintScreen can be used alone).");
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(Fps.Text, out var fps) || fps is < 1 or > 30)
        {
            ShowError("Frames per second must be a number from 1 to 30.");
            return;
        }

        if (!int.TryParse(MaxSeconds.Text, out var seconds) || seconds is < 1 or > 600)
        {
            ShowError("Maximum length must be 1 to 600 seconds.");
            return;
        }

        if (!Uri.TryCreate(OllamaUrl.Text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            ShowError("The Ollama address must look like http://localhost:11434");
            return;
        }

        if (CaptureHotkey.Text == GifHotkey.Text)
        {
            ShowError("The two hotkeys must be different.");
            return;
        }

        var settings = _app.Settings.Clone();
        settings.CaptureHotkey = CaptureHotkey.Text;
        settings.GifHotkey = GifHotkey.Text;
        settings.OllamaUrl = OllamaUrl.Text;
        settings.TranslateModel = TranslateModel.Text;
        settings.VisionModel = VisionModel.Text;
        settings.TargetLanguage = TargetLanguage.Text;
        settings.OcrLanguage = (OcrLanguage.SelectedItem as OcrLanguageOption)?.Tag ?? "";
        settings.GifFramesPerSecond = fps;
        settings.GifMaxSeconds = seconds;
        settings.GifShowCursor = ShowCursor.IsChecked == true;
        settings.AiEnabled = AiEnabled.IsChecked == true;
        settings.CheckForUpdates = CheckForUpdates.IsChecked == true;
        settings.AiKeepAlive = LocalAi.MemoryModes[Math.Max(0, MemoryMode.SelectedIndex)].KeepAlive;
        settings.GifMaxMegabytes = GifLimits[Math.Max(0, GifLimit.SelectedIndex)];

        try
        {
            Startup.Set(StartWithWindows.IsChecked == true);
            var problems = _app.ApplySettings(settings);
            if (problems.Count > 0)
            {
                ShowError(string.Join("\n", problems));
                return;
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            ShowError($"Couldn't save settings: {ex.Message}");
            return;
        }

        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void ShowError(string message)
    {
        Error.Text = message;
        Error.Visibility = Visibility.Visible;
    }
}
