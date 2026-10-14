using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Toastshot.Services;

namespace Toastshot;

/// <summary>
/// Shows what changed since this copy (each skipped release with its notes) and updates in one click: an installed
/// copy downloads and installs the new version; any other copy opens the release page to download it.
/// </summary>
public partial class UpdateWindow : Window
{
    private readonly UpdateCheck _check;
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private CancellationTokenSource? _work;

    public UpdateWindow(UpdateCheck check)
    {
        InitializeComponent();
        _check = check;
        var latest = check.Latest!;
        Heading.Text = $"Toastshot {latest.Version} is available";
        Subheading.Text = check.Newer.Count == 1
            ? $"You have {check.Current}. Here's what's new:"
            : $"You have {check.Current}. {check.Newer.Count} updates since then, newest first:";

        foreach (var release in check.Newer)
        {
            var title = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var date = new TextBlock { Text = release.Published == default ? "" : release.Published.LocalDateTime.ToString("d MMM yyyy"), Style = (Style)FindResource("Hint") };
            DockPanel.SetDock(date, Dock.Right);
            title.Children.Add(date);
            title.Children.Add(new TextBlock { Text = $"Toastshot {release.Version}", Style = (Style)FindResource("CardTitle") });

            var body = new StackPanel();
            body.Children.Add(title);
            body.Children.Add(ReleaseNotes.Render(release.Notes));
            Releases.Children.Add(new Border
            {
                Style = (Style)FindResource("SettingsCard"),
                Padding = new Thickness(16, 12, 16, 10),
                Margin = new Thickness(0, 0, 0, 8),
                Child = body
            });
        }

        // Only an installed copy can replace itself; a copy run from elsewhere gets the download page.
        if (!Updater.IsInstalledCopy || latest.ZipUrl is null)
        {
            UpdateButton.Content = "Download";
        }

        Closed += (_, _) =>
        {
            _work?.Cancel();
            _http.Dispose();
        };
    }

    private async void OnUpdate(object sender, RoutedEventArgs e)
    {
        var latest = _check.Latest!;
        if ((string)UpdateButton.Content == "Download")
        {
            Process.Start(new ProcessStartInfo(latest.PageUrl) { UseShellExecute = true });
            Close();
            return;
        }

        _work = new CancellationTokenSource();
        UpdateButton.IsEnabled = false;
        ErrorText.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        try
        {
            await Updater.InstallAsync(_http, latest, new Progress<double?>(fraction =>
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
            }), _work.Token);
            Log.Write($"Update to {latest.Version} started.");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.IO.IOException or System.IO.InvalidDataException or System.ComponentModel.Win32Exception)
        {
            ProgressPanel.Visibility = Visibility.Collapsed;
            ErrorText.Text = $"Couldn't update: {ex.Message}";
            ErrorText.Visibility = Visibility.Visible;
            UpdateButton.IsEnabled = true;
            UpdateButton.Content = "Try again";
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnLater(object sender, RoutedEventArgs e) => Close();
}
