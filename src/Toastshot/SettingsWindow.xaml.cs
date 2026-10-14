using System.Diagnostics;
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

    private static readonly string[] Themes = ["System", "Dark", "Light"];
    private string _themeAtOpen = "System";
    private bool _saved;

    /// <summary>Shows the chosen theme straight away; Save keeps it, Cancel goes back.</summary>
    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && ThemeBox.SelectedItem is string theme)
        {
            ThemeManager.Use(Application.Current, theme);
        }
    }

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
        _themeAtOpen = s.Theme;
        ThemeBox.ItemsSource = Themes;
        ThemeBox.SelectedIndex = Math.Max(0, Array.IndexOf(Themes, s.Theme));
        EventHandler updateFound = (_, _) => ShowUpdateStatus(_app.AvailableUpdate);
        _app.UpdateFound += updateFound;
        Closed += (_, _) =>
        {
            if (!_saved)
            {
                ThemeManager.Use(Application.Current, _themeAtOpen); // Cancel undoes the preview
            }

            _app.UpdateFound -= updateFound;
            _updating?.Cancel();
            _updateHttp.Dispose();
        };
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

    private UpdateCheck? _update;
    private readonly HttpClient _updateHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    private CancellationTokenSource? _updating;

    /// <summary>
    /// Up to date: "Check now". Update found: the button becomes Update and what's new opens below this row, one
    /// card per release since this copy.
    /// </summary>
    internal void ShowUpdateStatus(UpdateCheck? check)
    {
        _update = check?.Latest is null ? null : check;
        Releases.Children.Clear();
        if (_update?.Latest is { } latest)
        {
            UpdateStatus.Text = _update.Newer.Count == 1
                ? $"Toastshot {latest.Version} is available"
                : $"Toastshot {latest.Version} is available · {_update.Newer.Count} updates since yours";
            // Only an installed copy can replace itself; any other copy gets the download page.
            UpdateAction.Content = Updater.IsInstalledCopy && latest.PackageUrl is not null ? "Update" : "Download";
            UpdateAction.Style = (Style)FindResource("PrimaryButton");
            foreach (var release in _update.Newer)
            {
                Releases.Children.Add(ReleaseCard(release, first: Releases.Children.Count == 0));
            }

            UpdatePanel.Visibility = Visibility.Visible;
        }
        else
        {
            UpdateStatus.Text = _app.Settings.LastUpdateCheck is { } at ? $"Up to date · checked {at.LocalDateTime:d MMM, HH:mm}" : "Up to date";
            UpdateAction.Content = "Check now";
            UpdateAction.Style = (Style)FindResource("ButtonBase");
            UpdatePanel.Visibility = Visibility.Collapsed;
        }
    }

    private UIElement ReleaseCard(ReleaseInfo release, bool first)
    {
        var title = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var date = new TextBlock { Text = release.Published == default ? "" : release.Published.LocalDateTime.ToString("d MMM yyyy"), Style = (Style)FindResource("Hint") };
        DockPanel.SetDock(date, Dock.Right);
        title.Children.Add(date);
        title.Children.Add(new TextBlock { Text = $"What's new in {release.Version}", Style = (Style)FindResource("SettingLabel"), FontWeight = FontWeights.SemiBold });

        var card = new StackPanel { Margin = new Thickness(0, first ? 0 : 14, 0, 0) };
        card.Children.Add(title);
        card.Children.Add(ReleaseNotes.Render(release.Notes));
        return card;
    }

    private async void OnUpdateAction(object sender, RoutedEventArgs e)
    {
        if (_update?.Latest is { } latest)
        {
            await InstallAsync(latest);
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

    /// <summary>An installed copy downloads, checks and installs the update, then restarts; any other copy opens the release page.</summary>
    private async Task InstallAsync(ReleaseInfo latest)
    {
        if ((string)UpdateAction.Content == "Download")
        {
            Process.Start(new ProcessStartInfo(latest.PageUrl) { UseShellExecute = true });
            return;
        }

        _updating = new CancellationTokenSource();
        UpdateAction.IsEnabled = false;
        Error.Visibility = Visibility.Collapsed;
        UpdateProgress.Visibility = Visibility.Visible;
        try
        {
            await Updater.InstallAsync(_updateHttp, latest, new Progress<double?>(fraction =>
            {
                Progress.IsIndeterminate = fraction is null;
                if (fraction is { } f)
                {
                    ProgressText.Text = $"Downloading Toastshot {latest.Version}…";
                    ProgressDetail.Text = $"{f:P0}";
                    Progress.Value = f;
                }
                else
                {
                    ProgressText.Text = "Installing… Toastshot restarts in a moment.";
                    ProgressDetail.Text = "";
                }
            }), _updating.Token);
            Log.Write($"Update to {latest.Version} started.");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.IO.IOException or System.IO.InvalidDataException or System.ComponentModel.Win32Exception)
        {
            UpdateProgress.Visibility = Visibility.Collapsed;
            ShowError($"Couldn't update: {ex.Message}");
            UpdateAction.IsEnabled = true;
            UpdateAction.Content = "Try again";
        }
        catch (OperationCanceledException)
        {
        }
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
                ShowAi("StatusWarnBrush", "Ollama isn't running", "", AiStep.Start, "Start");
            }
            else
            {
                ShowAi("InkMutedBrush", "Not set up", "Nothing is uploaded; it all stays on this PC", AiStep.SetUp, "Set up…");
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
                    ? "Not in memory now; it loads when you use Translate or the AI reader"
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
        AiDetail.Visibility = detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
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
            AiDetail.Visibility = Visibility.Visible;
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
                : $"{models.Count} models installed, {models.Count(m => m.HasVision)} can read images.";
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
            ShowError("Frames per second must be a number from 1 to 30.", Fps);
            return;
        }

        if (!int.TryParse(MaxSeconds.Text, out var seconds) || seconds is < 1 or > 600)
        {
            ShowError("Maximum length must be 1 to 600 seconds.", MaxSeconds);
            return;
        }

        if (!Uri.TryCreate(OllamaUrl.Text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            ShowError("The Ollama address must look like http://localhost:11434", OllamaUrl);
            return;
        }

        if (CaptureHotkey.Text == GifHotkey.Text)
        {
            ShowError("The two hotkeys must be different.", GifHotkey);
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
        settings.Theme = ThemeBox.SelectedItem as string ?? "System";
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

        _saved = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void ShowError(string message)
    {
        Error.Text = message;
        Error.Visibility = Visibility.Visible;
    }

    /// <summary>Shows the problem and takes you to the field, on whichever tab (or inside Advanced) it is.</summary>
    private void ShowError(string message, Control field)
    {
        ShowError(message);
        for (DependencyObject? node = field; node is not null; node = LogicalTreeHelper.GetParent(node))
        {
            if (node == AdvancedPanel && AdvancedToggle.IsChecked != true)
            {
                AdvancedToggle.IsChecked = true;
                AdvancedPanel.Visibility = Visibility.Visible;
            }
            else if (node is TabItem tab)
            {
                tab.IsSelected = true;
                break;
            }
        }

        // The tab's content is built when it's shown, so focus once layout has caught up.
        Dispatcher.BeginInvoke(() => field.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }
}
