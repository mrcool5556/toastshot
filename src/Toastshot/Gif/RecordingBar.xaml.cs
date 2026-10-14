using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Toastshot.Gif;

/// <summary>
/// Record / Stop / Cancel controls shown next to the GIF area, with the frame rate and length to pick before
/// recording. Kept out of the recording.
/// </summary>
public partial class RecordingBar : Window
{
    private static readonly int[] FrameRates = [5, 10, 12, 15, 20, 24, 30];
    private static readonly int[] Lengths = [5, 10, 15, 30, 60, 120, 300, 600];

    /// <param name="size">The area, e.g. "640 × 360".</param>
    /// <param name="framesPerSecond">The frame rate to start with (from Settings).</param>
    /// <param name="maxSeconds">The length to start with (from Settings).</param>
    public RecordingBar(string size, int framesPerSecond, int maxSeconds)
    {
        InitializeComponent();
        Info.Text = size;
        Fill(FpsBox, FrameRates, framesPerSecond, fps => $"{fps} fps");
        Fill(LengthBox, Lengths, maxSeconds, LengthLabel);
        SourceInitialized += (_, _) => NativeWindows.ExcludeFromCapture(this);
    }

    public int FramesPerSecond => (int)((ComboBoxItem)FpsBox.SelectedItem).Tag;

    public int MaxSeconds => (int)((ComboBoxItem)LengthBox.SelectedItem).Tag;

    /// <summary>"30 s", "1 min", "90 s", "10 min".</summary>
    internal static string LengthLabel(int seconds) => seconds >= 60 && seconds % 60 == 0 ? $"{seconds / 60} min" : $"{seconds} s";

    /// <summary>The usual choices, plus the current value if it isn't one of them (set in Settings).</summary>
    private static void Fill(ComboBox box, int[] choices, int current, Func<int, string> label)
    {
        foreach (var value in choices.Append(current).Distinct().Order())
        {
            var item = new ComboBoxItem { Content = label(value), Tag = value };
            box.Items.Add(item);
            if (value == current)
            {
                box.SelectedItem = item;
            }
        }
    }

    public event EventHandler? RecordClicked;
    public event EventHandler? StopClicked;
    public event EventHandler? CancelClicked;

    public void ShowRecording()
    {
        Choices.Visibility = Visibility.Collapsed;
        RecordButton.Visibility = Visibility.Collapsed;
        StopButton.Visibility = Visibility.Visible;
        Dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "StatusRecordBrush");
    }

    public void ShowFinishing()
    {
        StopButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        StopButton.Content = "Finishing…";
    }

    public void UpdateClock(TimeSpan elapsed, int frames)
    {
        Clock.Text = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}.{elapsed.Milliseconds / 100}";
        Info.Text = $"{frames} frames";
    }

    private void OnRecord(object sender, RoutedEventArgs e) => RecordClicked?.Invoke(this, EventArgs.Empty);
    private void OnStop(object sender, RoutedEventArgs e) => StopClicked?.Invoke(this, EventArgs.Empty);
    private void OnCancel(object sender, RoutedEventArgs e) => CancelClicked?.Invoke(this, EventArgs.Empty);
}

/// <summary>A click-through dashed outline drawn just outside the recorded area.</summary>
internal sealed class RegionFrame : Window
{
    public const int BorderPixels = 3;
    private readonly System.Windows.Shapes.Rectangle _outline;
    private readonly PixelRect _bounds;

    public RegionFrame(PixelRect region)
    {
        _bounds = new PixelRect(region.X - BorderPixels, region.Y - BorderPixels, region.Width + BorderPixels * 2, region.Height + BorderPixels * 2);
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        _outline = new System.Windows.Shapes.Rectangle
        {
            Stroke = (Brush)Application.Current.FindResource("OverlaySelectionBrush"),
            StrokeDashArray = [4, 2],
            IsHitTestVisible = false
        };
        Content = _outline;
        SourceInitialized += (_, _) =>
        {
            NativeWindows.MakeClickThrough(this);
            NativeWindows.ExcludeFromCapture(this);
            NativeWindows.SetBounds(this, _bounds);
            // The stroke sits entirely in the margin around the region, never over recorded pixels.
            var (scaleX, _) = NativeWindows.DpiScale(this);
            _outline.StrokeThickness = BorderPixels / scaleX;
        };
    }

    public void ShowRecording() => _outline.Stroke = (Brush)Application.Current.FindResource("StatusRecordBrush");
}
