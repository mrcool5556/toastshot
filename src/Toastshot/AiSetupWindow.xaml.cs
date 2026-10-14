using System.Net.Http;
using System.Windows;
using System.Windows.Shapes;
using Toastshot.Services;

namespace Toastshot;

/// <summary>
/// Installs the local AI: Ollama from ollama.com (if missing), then the recommended model, with progress. Before
/// anything is downloaded it says what it unlocks, whether this PC's graphics card can run it, and how big it is.
/// </summary>
public partial class AiSetupWindow : Window
{
    private readonly AppController _app;
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private CancellationTokenSource? _work;
    private bool _done;

    public AiSetupWindow(AppController app)
    {
        InitializeComponent();
        _app = app;

        var card = LocalAi.DetectGraphicsCard();
        var fit = LocalAi.FitFor(card, LocalAi.RecommendedVramGb);
        CardText.Text = card is null ? "Graphics card: not detected" : $"{card.Name} · {card.MemoryGb:0} GB";
        var model = $"{LocalAi.RecommendedName} uses about {LocalAi.RecommendedVramGb:0} GB of graphics memory while it works";
        (FitText.Text, var dot) = fit switch
        {
            Fit.Comfortable => ($"{model}, so it fits comfortably. It's freed again after use (see Settings).", "StatusOkBrush"),
            Fit.Tight => ($"{model}. That fits, but games may run slower while it's loaded.", "StatusWarnBrush"),
            Fit.TooSmall => ($"{model}, more than this card has. It will run on the processor instead: it works, but each request takes much longer.", "StatusErrorBrush"),
            _ => ($"{model}. Without a suitable graphics card it runs on the processor, which is slower.", "InkMutedBrush")
        };
        FitDot.SetResourceReference(Shape.FillProperty, dot);

        DownloadText.Text = LocalAi.IsInstalled
            ? $"{LocalAi.RecommendedName}, about {LocalAi.RecommendedDownloadGb:0.0} GB. Ollama is already installed."
            : $"Ollama, the free app that runs the model (from ollama.com, about 1 GB), and {LocalAi.RecommendedName}, about {LocalAi.RecommendedDownloadGb:0.0} GB.";

        Closed += (_, _) =>
        {
            _work?.Cancel();
            _http.Dispose();
        };
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        if (_done)
        {
            Close();
            return;
        }

        _work = new CancellationTokenSource();
        var ct = _work.Token;
        InstallButton.IsEnabled = false;
        ErrorText.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        var client = _app.CreateOllama();
        try
        {
            if (!LocalAi.IsInstalled && !await LocalAi.IsRunningAsync(client, ct))
            {
                Step("Downloading Ollama…", null);
                await LocalAi.InstallOllamaAsync(_http, new Progress<double?>(f =>
                {
                    if (f is { } fraction)
                    {
                        Step("Downloading Ollama…", fraction, $"{fraction:P0}");
                    }
                    else
                    {
                        Step("Installing Ollama…", null);
                    }
                }), ct);
            }

            Step("Starting Ollama…", null);
            if (!await LocalAi.StartAsync(client, ct))
            {
                throw new OllamaException("Ollama didn't start. Try starting the Ollama app, then run setup again.");
            }

            var model = LocalAi.RecommendedModel;
            var installed = await client.ListModelsAsync(ct);
            if (!installed.Any(m => MainWindow.IsSameModel(m.Name, model)))
            {
                Step($"Downloading {LocalAi.RecommendedName}…", null);
                await client.PullAsync(model, new Progress<PullProgress>(p =>
                {
                    if (p.Fraction is { } fraction && p.Total > 50_000_000)
                    {
                        Step($"Downloading {LocalAi.RecommendedName}…", fraction, $"{p.Completed / 1073741824.0:0.0} of {p.Total / 1073741824.0:0.0} GB");
                    }
                    else if (p.Status.StartsWith("verifying", StringComparison.OrdinalIgnoreCase) || p.Status.StartsWith("writing", StringComparison.OrdinalIgnoreCase))
                    {
                        Step("Checking the download…", null);
                    }
                }), ct);
            }

            _app.SavePreference(s =>
            {
                s.TranslateModel = model;
                s.VisionModel = model;
                s.AiEnabled = true;
            });

            Step("Local AI is ready.", 1, "");
            ProgressText.Text = $"Local AI is ready. Translate and the AI reader now use {LocalAi.RecommendedName}.";
            _done = true;
            InstallButton.Content = "Done";
            CancelButton.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
            Step("Stopped.", 0, "");
        }
        catch (Exception ex) when (ex is OllamaException or HttpRequestException or InvalidOperationException or System.ComponentModel.Win32Exception or System.IO.IOException)
        {
            ProgressPanel.Visibility = Visibility.Collapsed;
            ErrorText.Text = $"Setup stopped: {ex.Message}";
            ErrorText.Visibility = Visibility.Visible;
            InstallButton.Content = "Try again";
        }
        finally
        {
            InstallButton.IsEnabled = true;
            _work = null;
        }
    }

    private void Step(string text, double? fraction, string detail = "")
    {
        ProgressText.Text = text;
        ProgressDetail.Text = detail;
        Progress.IsIndeterminate = fraction is null;
        if (fraction is { } f)
        {
            Progress.Value = f;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_work is not null)
        {
            _work.Cancel(); // stops the download; a running installer finishes on its own
            return;
        }

        Close();
    }
}
