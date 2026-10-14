using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Toastshot.Capture;
using Toastshot.Gif;

namespace Toastshot.Overlay;

/// <summary>
/// Full-desktop overlay for dragging out a region. With a frozen screenshot it shows that image
/// (what you select is exactly what was on screen); without one it is a live, see-through overlay.
/// </summary>
public partial class RegionSelectWindow : Window
{
    private readonly PixelRect _virtual = ScreenCapture.VirtualScreen;
    private readonly RectangleGeometry _brightClip = new(Rect.Empty);
    private Point? _start;
    private double _scaleX = 1;
    private double _scaleY = 1;

    /// <summary>The selected region in absolute device pixels (virtual-screen coordinates).</summary>
    public PixelRect? Selection { get; private set; }

    public RegionSelectWindow(BitmapSource? frozenScreen, string hint)
    {
        InitializeComponent();
        HintText.Text = hint;
        Frozen.Source = frozenScreen;
        Bright.Source = frozenScreen;
        Bright.Clip = _brightClip;
        if (frozenScreen is not null)
        {
            // The frozen picture covers everything, so the window can be opaque: see-through (layered) windows
            // re-send the whole screen to Windows on every mouse move, which makes dragging stutter.
            AllowsTransparency = false;
            Background = Brushes.Black;
        }

        SourceInitialized += (_, _) =>
        {
            CoverVirtualScreen();
            NativeWindows.SquareCorners(this);
        };
        Loaded += (_, _) =>
        {
            Activate();
            NativeWindows.ForceForeground(this); // over a game, Activate alone may be refused
            Keyboard.Focus(this);
            PlaceHint();
            UpdateDim(null);
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Finish(null);
            }
        };
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        MouseRightButtonDown += (_, _) => Finish(null);
    }

    private void CoverVirtualScreen()
    {
        // Size in device pixels so the window spans every monitor exactly, whatever their DPI.
        NativeWindows.SetBounds(this, _virtual);
        (_scaleX, _scaleY) = NativeWindows.DpiScale(this);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _scaleX = newDpi.DpiScaleX;
        _scaleY = newDpi.DpiScaleY;
        // WPF resizes the window for the new DPI; put it back over the whole desktop.
        NativeWindows.SetBounds(this, _virtual);
    }

    /// <summary>Puts the hint at the top centre of the primary monitor (or the first one).</summary>
    private void PlaceHint()
    {
        var monitors = NativeWindows.Monitors().ToList();
        var primary = monitors.FirstOrDefault(m => m.X == 0 && m.Y == 0, monitors.FirstOrDefault());
        HintBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var left = (primary.X - _virtual.X + primary.Width / 2.0) / _scaleX - HintBar.DesiredSize.Width / 2;
        var top = (primary.Y - _virtual.Y) / _scaleY + 24;
        HintBar.Margin = new Thickness(Math.Max(0, left), top, 0, 0);
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(Root);
        CaptureMouse();
        SelectionRect.Visibility = Visibility.Visible;
        SizeBadge.Visibility = Visibility.Visible;
        HintBar.Visibility = Visibility.Collapsed;
        Crosshair.Visibility = Visibility.Collapsed; // full-screen lines moving every frame would redraw everything
        UpdateSelection(_start.Value, _start.Value);
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(Root);
        CrossV.X1 = CrossV.X2 = pos.X;
        CrossV.Y1 = 0;
        CrossV.Y2 = Root.ActualHeight;
        CrossH.Y1 = CrossH.Y2 = pos.Y;
        CrossH.X1 = 0;
        CrossH.X2 = Root.ActualWidth;

        if (_start is { } start)
        {
            UpdateSelection(start, pos);
        }
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_start is not { } start)
        {
            return;
        }

        _start = null;
        ReleaseMouseCapture();
        var device = ToDevice(Normalize(start, e.GetPosition(Root)));
        Finish(device.Width >= 4 && device.Height >= 4 ? device : null);
    }

    private bool _finished;

    private void Finish(PixelRect? selection)
    {
        if (_finished)
        {
            return; // e.g. Esc pressed while the window is already closing
        }

        _finished = true;
        Selection = selection;
        DialogResult = selection is not null;
    }

    private void UpdateSelection(Point a, Point b)
    {
        var r = Normalize(a, b);
        Canvas.SetLeft(SelectionRect, r.X);
        Canvas.SetTop(SelectionRect, r.Y);
        SelectionRect.Width = r.Width;
        SelectionRect.Height = r.Height;
        UpdateDim(r);

        var device = ToDevice(r);
        SizeText.Text = $"{device.Width} × {device.Height}";
        Canvas.SetLeft(SizeBadge, r.X);
        Canvas.SetTop(SizeBadge, r.Y >= 26 ? r.Y - 26 : r.Bottom + 4);
    }

    /// <summary>Dims everything except the selection.</summary>
    private void UpdateDim(Rect? hole) =>
        _brightClip.Rect = hole is { Width: > 0, Height: > 0 } h ? h : Rect.Empty;

    private static Rect Normalize(Point a, Point b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private PixelRect ToDevice(Rect dip)
    {
        var left = (int)Math.Round(_virtual.X + dip.X * _scaleX);
        var top = (int)Math.Round(_virtual.Y + dip.Y * _scaleY);
        var right = (int)Math.Round(_virtual.X + dip.Right * _scaleX);
        var bottom = (int)Math.Round(_virtual.Y + dip.Bottom * _scaleY);
        left = Math.Clamp(left, _virtual.X, _virtual.Right);
        right = Math.Clamp(right, _virtual.X, _virtual.Right);
        top = Math.Clamp(top, _virtual.Y, _virtual.Bottom);
        bottom = Math.Clamp(bottom, _virtual.Y, _virtual.Bottom);
        return new PixelRect(left, top, right - left, bottom - top);
    }
}
