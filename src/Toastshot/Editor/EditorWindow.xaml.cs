using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Toastshot.Services;
using Microsoft.Win32;

namespace Toastshot.Editor;

public partial class EditorWindow : Window
{
    internal static readonly Color[] Palette =
    [
        Color.FromRgb(0xEF, 0x44, 0x44), // red
        Color.FromRgb(0xF9, 0x73, 0x16), // orange
        Color.FromRgb(0xFA, 0xCC, 0x15), // yellow
        Color.FromRgb(0x22, 0xC5, 0x5E), // green
        Color.FromRgb(0x3B, 0x82, 0xF6), // blue
        Color.FromRgb(0xA8, 0x55, 0xF7), // purple
        Colors.White,
        Colors.Black
    ];

    private readonly AppController _app;
    private readonly BitmapSource _image;
    private readonly AnnotationLayer _layer;

    private CancellationTokenSource? _work;

    internal AnnotationLayer Layer => _layer;

    public EditorWindow(AppController app, BitmapSource image)
    {
        InitializeComponent();
        _app = app;
        _image = image;
        _layer = new AnnotationLayer(Ink, (Brush)FindResource("AccentBrush"), (FontFamily)FindResource("UiFont"))
        {
            Color = Palette[0],
            Size = SizeSlider.Value
        };
        _layer.History.Changed += (_, _) => UpdateHistoryButtons();

        BaseImage.Source = image;
        Surface.Width = image.PixelWidth;
        Surface.Height = image.PixelHeight;
        SizeInfo.Text = $"{image.PixelWidth} × {image.PixelHeight}";

        BuildSwatches();
        BuildReaderOptions();
        LanguageBox.ItemsSource = AiTasks.CommonLanguages;
        LanguageBox.Text = app.Settings.TargetLanguage;

        if (!app.Settings.AiEnabled)
        {
            // Local AI is off: no Translate anywhere (Windows OCR still reads text).
            foreach (var element in new UIElement[] { TranslateButton, TranslationHeader, TranslationControls, TranslationText, TranslationCopy })
            {
                element.Visibility = Visibility.Collapsed;
            }
        }

        SourceInitialized += (_, _) => FitToImage();
        PreviewKeyDown += OnPreviewKeyDown;
        Closed += (_, _) => _work?.Cancel();
    }

    // ---------- Layout ----------

    /// <summary>Shows the image at its real pixel size (not DPI-scaled) and sizes the window around it.</summary>
    private void FitToImage()
    {
        var (scaleX, scaleY) = NativeWindows.DpiScale(this);
        Surface.LayoutTransform = new ScaleTransform(1 / scaleX, 1 / scaleY);

        var work = SystemParameters.WorkArea;
        var imageWidth = _image.PixelWidth / scaleX;
        var imageHeight = _image.PixelHeight / scaleY;
        Width = Math.Clamp(imageWidth + 60, MinWidth, work.Width * 0.9);
        Height = Math.Clamp(imageHeight + 150, MinHeight, work.Height * 0.9);
        Left = work.Left + (work.Width - Width) / 2;
        Top = work.Top + (work.Height - Height) / 2;
    }

    private void BuildSwatches()
    {
        foreach (var color in Palette)
        {
            var swatch = new RadioButton
            {
                Style = (Style)FindResource("Swatch"),
                GroupName = "Color",
                Background = new SolidColorBrush(color),
                Tag = color,
                IsChecked = color == Palette[0],
                ToolTip = "Colour"
            };
            swatch.Checked += (_, _) => _layer.Color = (Color)swatch.Tag;
            Swatches.Children.Add(swatch);
        }
    }

    private sealed record ReaderOption(string Label, string? OcrLanguage, bool UseAi)
    {
        public override string ToString() => Label;
    }

    private void BuildReaderOptions()
    {
        var options = new List<ReaderOption> { new("Windows OCR (display language)", "", false) };
        try
        {
            options.AddRange(OcrService.InstalledLanguages().Select(l => new ReaderOption($"Windows OCR · {l.DisplayName}", l.Tag, false)));
        }
        catch (Exception)
        {
            // OCR API unavailable (very old Windows): AI reading still works.
        }

        if (_app.Settings.AiEnabled)
        {
            options.Add(new ReaderOption($"AI vision · {LocalAi.DisplayName(_app.Settings.VisionModel)}", null, true));
        }
        ReaderBox.ItemsSource = options;
        ReaderBox.SelectedItem = options.FirstOrDefault(o => !o.UseAi && string.Equals(o.OcrLanguage, _app.Settings.OcrLanguage, StringComparison.OrdinalIgnoreCase))
                                 ?? options[0];
    }

    // ---------- Tools ----------

    private void OnToolChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string name } && Enum.TryParse<Tool>(name, out var tool) && _layer is not null)
        {
            _layer.CommitText();
            _layer.Tool = tool;
            Ink.Cursor = tool == Tool.Text ? Cursors.IBeam : Cursors.Cross;
        }
    }

    private void OnSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_layer is not null)
        {
            _layer.Size = e.NewValue;
            SizeText.Text = ((int)e.NewValue).ToString();
        }
    }

    private void SelectTool(Tool tool)
    {
        foreach (var radio in ((WrapPanel)PenTool.Parent).Children.OfType<RadioButton>())
        {
            if (radio.GroupName == "Tool" && (string)radio.Tag == tool.ToString())
            {
                radio.IsChecked = true;
            }
        }
    }

    private void OnInkDown(object sender, MouseButtonEventArgs e)
    {
        if (_layer.PointerDown(e.GetPosition(Ink)))
        {
            Ink.CaptureMouse();
        }

        e.Handled = true;
    }

    private void OnInkMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            _layer.PointerMove(e.GetPosition(Ink), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        }
    }

    private void OnInkUp(object sender, MouseButtonEventArgs e)
    {
        Ink.ReleaseMouseCapture();
        _layer.PointerUp();
    }

    private void OnUndo(object sender, RoutedEventArgs e)
    {
        _layer.CommitText();
        _layer.History.Undo();
    }

    private void OnRedo(object sender, RoutedEventArgs e) => _layer.History.Redo();

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _layer.CommitText();
        _layer.History.ClearAll();
    }

    private void UpdateHistoryButtons()
    {
        UndoButton.IsEnabled = _layer.History.CanUndo;
        RedoButton.IsEnabled = _layer.History.CanRedo;
        ClearButton.IsEnabled = _layer.HasAnnotations;
    }

    // ---------- Output ----------

    /// <summary>The image as shown: screenshot plus drawings.</summary>
    public BitmapSource ComposeImage()
    {
        _layer.CommitText();
        Ink.UpdateLayout();
        return _layer.HasAnnotations
            ? Annotations.Compose(_image, Ink, _image.PixelWidth, _image.PixelHeight)
            : _image;
    }

    private async void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            await ImageExport.CopyImageAsync(ComposeImage());
            SetStatus("Image copied to the clipboard.");
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            SetStatus(ImageExport.BusyMessage(ex));
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_app.SaveImageAs(this, ComposeImage()) is { } name)
            {
                SetStatus($"Saved {name}.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Couldn't save: {ex.Message}");
        }
    }

    private void OnPrint(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_app.PrintImage(ComposeImage()))
            {
                SetStatus("Sent to the printer.");
            }
        }
        catch (System.Printing.PrintSystemException ex)
        {
            SetStatus($"Couldn't print: {ex.Message}");
        }
    }

    private void OnSearchGoogle(object sender, RoutedEventArgs e)
    {
        try
        {
            ImageSearch.SearchGoogle(ComposeImage());
            SetStatus("Opened Google Lens in your browser.");
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            SetStatus($"Couldn't open the browser: {ex.Message}");
        }
    }

    private void OnCopyOcrText(object sender, RoutedEventArgs e) => CopyText(OcrText.Text, "Text copied.");

    private void OnCopyTranslation(object sender, RoutedEventArgs e) => CopyText(TranslationText.Text, "Translation copied.");

    private async void CopyText(string text, string done)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            SetStatus("Nothing to copy yet.");
            return;
        }

        try
        {
            await ImageExport.CopyTextAsync(text);
            SetStatus(done);
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            SetStatus(ImageExport.BusyMessage(ex));
        }
    }

    // ---------- Text: OCR and translation ----------

    private async void OnExtractText(object sender, RoutedEventArgs e) => await ExtractTextAsync();

    private async void OnTranslate(object sender, RoutedEventArgs e) => await TranslateAsync();

    private void OnStop(object sender, RoutedEventArgs e) => _work?.Cancel();

    private void OnCloseSide(object sender, RoutedEventArgs e)
    {
        SidePanel.Visibility = Visibility.Collapsed;
        SideColumn.Width = new GridLength(0);
    }

    private void ShowSidePanel()
    {
        if (SidePanel.Visibility != Visibility.Visible)
        {
            SidePanel.Visibility = Visibility.Visible;
            SideColumn.Width = new GridLength(300);
        }
    }

    private async Task<bool> ExtractTextAsync()
    {
        if (_work is not null || ReaderBox.SelectedItem is not ReaderOption reader)
        {
            return false;
        }

        ShowSidePanel();
        var image = ComposeImage();
        using var work = BeginWork(reader.UseAi ? $"Reading text with {_app.Settings.VisionModel}…" : "Reading text…");
        try
        {
            string text;
            if (reader.UseAi)
            {
                OcrText.Text = "";
                text = await AiTasks.CollectAsync(
                    AiTasks.ReadTextAsync(_app.CreateOllama(), _app.Settings.VisionModel, ImageExport.EncodePng(image), work.Token),
                    partial => OcrText.Text = partial, work.Token);
            }
            else
            {
                text = await OcrService.RecognizeAsync(image, reader.OcrLanguage);
                if (!string.Equals(reader.OcrLanguage, _app.Settings.OcrLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    _app.SavePreference(s => s.OcrLanguage = reader.OcrLanguage ?? "");
                }
            }

            OcrText.Text = text;
            var lines = text.Length == 0 ? 0 : text.Split('\n').Length;
            SetStatus(lines == 0
                ? "No text found. Try another reader language, or AI vision."
                : $"Found {lines} line{(lines == 1 ? "" : "s")} of text.");
            return lines > 0;
        }
        catch (OperationCanceledException)
        {
            SetStatus("Stopped.");
            return false;
        }
        catch (Exception ex) // OCR engine, Ollama or network: report it, keep the window usable
        {
            SetStatus(ex.Message);
            return false;
        }
        finally
        {
            EndWork();
        }
    }

    private async Task TranslateAsync()
    {
        if (_work is not null || !_app.Settings.AiEnabled)
        {
            return;
        }

        ShowSidePanel();
        if (string.IsNullOrWhiteSpace(OcrText.Text) && !await ExtractTextAsync())
        {
            return;
        }

        var target = string.IsNullOrWhiteSpace(LanguageBox.Text) ? _app.Settings.TargetLanguage : LanguageBox.Text.Trim();
        if (target != _app.Settings.TargetLanguage)
        {
            _app.SavePreference(s => s.TargetLanguage = target);
        }

        var model = _app.Settings.TranslateModel;
        using var work = BeginWork($"Translating to {target} with {model}…");
        try
        {
            TranslationText.Text = "";
            TranslationText.Text = await AiTasks.CollectAsync(
                AiTasks.TranslateAsync(_app.CreateOllama(), model, OcrText.Text, target, work.Token),
                partial => TranslationText.Text = partial, work.Token);
            SetStatus($"Translated to {target}.");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Stopped.");
        }
        catch (Exception ex) // Ollama or network: report it, keep the window usable
        {
            SetStatus(ex.Message);
        }
        finally
        {
            EndWork();
        }
    }

    private CancellationTokenSource BeginWork(string status)
    {
        _work = new CancellationTokenSource();
        SetStatus(status);
        StopButton.Visibility = Visibility.Visible;
        foreach (var button in new[] { ReadButton, TranslateButton, RereadButton, SideTranslateButton })
        {
            button.IsEnabled = false;
        }

        return _work;
    }

    private void EndWork()
    {
        _work = null;
        StopButton.Visibility = Visibility.Collapsed;
        foreach (var button in new[] { ReadButton, TranslateButton, RereadButton, SideTranslateButton })
        {
            button.IsEnabled = true;
        }
    }

    private void SetStatus(string text) => Status.Text = text;

    // ---------- Keyboard ----------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var inTextBox = Keyboard.FocusedElement is TextBox;

        if (ctrl && e.Key == Key.S)
        {
            OnSave(this, e);
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.C && Keyboard.FocusedElement is not TextBox { SelectionLength: > 0 })
        {
            OnCopy(this, e); // the image, from anywhere; only a text box with selected text keeps its own Ctrl+C
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.P)
        {
            OnPrint(this, e);
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.G)
        {
            OnSearchGoogle(this, e);
            e.Handled = true;
            return;
        }

        if (inTextBox)
        {
            return; // let text boxes handle copy, undo and typing
        }

        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.C:
                    OnCopy(this, e);
                    e.Handled = true;
                    break;
                case Key.Z when Keyboard.Modifiers.HasFlag(ModifierKeys.Shift):
                case Key.Y:
                    _layer.History.Redo();
                    e.Handled = true;
                    break;
                case Key.Z:
                    _layer.History.Undo();
                    e.Handled = true;
                    break;
                case Key.E:
                    _ = ExtractTextAsync();
                    e.Handled = true;
                    break;
                case Key.T:
                    _ = TranslateAsync();
                    e.Handled = true;
                    break;
            }

            return;
        }

        if (Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        Tool? tool = e.Key switch
        {
            Key.P => Tool.Pen,
            Key.M => Tool.Marker,
            Key.L => Tool.Line,
            Key.A => Tool.Arrow,
            Key.B => Tool.Box,
            Key.O => Tool.Oval,
            Key.T => Tool.Text,
            Key.R => Tool.Redact,
            _ => null
        };
        if (tool is { } t)
        {
            SelectTool(t);
            e.Handled = true;
        }
    }

}
