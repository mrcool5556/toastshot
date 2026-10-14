using System.Windows;
using System.Windows.Media;

namespace Toastshot.Gif;

/// <summary>Record / Stop / Cancel controls shown next to the GIF area. Kept out of the recording.</summary>
public partial class RecordingBar : Window
{
    public RecordingBar(string info)
    {
        InitializeComponent();
        Info.Text = info;
        SourceInitialized += (_, _) => NativeWindows.ExcludeFromCapture(this);
    }

    public event EventHandler? RecordClicked;
    public event EventHandler? StopClicked;
    public event EventHandler? CancelClicked;

    public void ShowRecording()
    {
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
