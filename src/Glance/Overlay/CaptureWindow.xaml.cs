using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Glance.Capture;
using Glance.Editor;
using Glance.Gif;
using Glance.Services;

namespace Glance.Overlay;

/// <summary>
/// The capture overlay. Drag a region on the frozen screen; it stays on screen with a tool column beside it and an
/// action bar under it, so it can be marked up, moved, resized, copied, saved, read or translated in place.
/// Drawings are kept in screen pixels (they stay put when the selection moves) and clipped to the selection.
/// </summary>
public partial class CaptureWindow : Window
{
    private const double Gap = 8;          // DIPs between the selection and its bars
    private const double HandleReach = 6;  // DIPs around a handle that still grab it
    private const int MinSize = 4;         // device pixels

    private enum Drag
    {
        None,
        Select,
        Move,
        Resize,
        Draw
    }

    private readonly AppController _app;
    private readonly BitmapSource _frozen;
    private readonly PixelRect _virtual;
    private readonly AnnotationLayer _layer;
    private readonly Dictionary<ToggleButton, Tool> _tools;
    private readonly DispatcherTimer _noticeTimer;
    private readonly RectangleGeometry _brightClip = new(Rect.Empty);
    private readonly RectangleGeometry _inkClip = new(Rect.Empty);
    private Point? _pendingDrag; // the latest pointer position, applied once per frame
    private bool _frameHooked;

    private double _scaleX = 1;
    private double _scaleY = 1;
    private Rect? _selection; // device pixels from the virtual screen's top-left
    private Drag _drag;
    private Point _dragStart;
    private Rect _dragOrigin;
    private string? _handle;
    private Tool? _tool;
    private bool _textCardOpen;
    private CancellationTokenSource? _work;

    /// <summary>Set when the full editor was asked for; the caller opens it once the overlay has closed.</summary>
    public BitmapSource? OpenInEditor { get; private set; }

    /// <summary>True once the capture was copied, saved or handed to the editor.</summary>
    public bool Completed { get; private set; }

    internal AnnotationLayer Layer => _layer;

    internal Rect? Selection => _selection;

    public CaptureWindow(AppController app, BitmapSource frozenScreen, PixelRect virtualScreen)
    {
        InitializeComponent();
        _app = app;
        _frozen = frozenScreen;
        _virtual = virtualScreen;
        Frozen.Source = frozenScreen;
        Bright.Source = frozenScreen;
        Bright.Clip = _brightClip;
        Ink.Clip = _inkClip;
        Ink.Width = virtualScreen.Width;
        Ink.Height = virtualScreen.Height;
        _layer = new AnnotationLayer(Ink, (Brush)FindResource("AccentBrush"), (FontFamily)FindResource("UiFont"))
        {
            Color = EditorWindow.Palette[0],
            Size = SizeSlider.Value
        };
        _layer.History.Changed += (_, _) => UndoButton.IsEnabled = _layer.History.CanUndo;
        _tools = new Dictionary<ToggleButton, Tool>
        {
            [PenTool] = Tool.Pen,
            [MarkerTool] = Tool.Marker,
            [LineTool] = Tool.Line,
            [ArrowTool] = Tool.Arrow,
            [BoxTool] = Tool.Box,
            [OvalTool] = Tool.Oval,
            [TextTool] = Tool.Text,
            [RedactTool] = Tool.Redact
        };
        BuildSwatches();
        if (!app.Settings.AiEnabled)
        {
            // Local AI is off: reading text still works (Windows OCR); translating doesn't show.
            BarTranslate.Visibility = LanguageBox.Visibility = TranslateButton.Visibility = Visibility.Collapsed;
        }

        LanguageBox.ItemsSource = AiTasks.CommonLanguages;
        LanguageBox.Text = app.Settings.TargetLanguage;
        _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            HintBar.Visibility = Visibility.Collapsed;
        };
        ApplyScale(1, 1);

        SourceInitialized += (_, _) =>
        {
            // Size in device pixels so the window spans every monitor exactly, whatever their DPI.
            NativeWindows.SetBounds(this, _virtual);
            NativeWindows.SquareCorners(this);
            var (x, y) = NativeWindows.DpiScale(this);
            ApplyScale(x, y);
        };
        Loaded += (_, _) =>
        {
            Activate();
            var inFront = NativeWindows.ForceForeground(this); // over a game, Activate alone may be refused
            Log.Write($"Capture overlay shown; in front: {inFront}.");
            Keyboard.Focus(this);
            PlaceHint();
            UpdateDim();
        };
        PreviewKeyDown += OnKey;
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        MouseRightButtonDown += (_, e) =>
        {
            if (_selection is null)
            {
                Close(); // right-click before choosing a region cancels, as Esc does
            }
        };
        Closed += (_, _) =>
        {
            _work?.Cancel();
            CompositionTarget.Rendering -= OnFrame;
        };
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ApplyScale(newDpi.DpiScaleX, newDpi.DpiScaleY);
        // WPF resizes the window for the new DPI; put it back over the whole desktop.
        NativeWindows.SetBounds(this, _virtual);
    }

    private void ApplyScale(double x, double y)
    {
        _scaleX = x;
        _scaleY = y;
        Ink.LayoutTransform = new ScaleTransform(1 / x, 1 / y);
        if (_selection is { } s)
        {
            SetSelection(s);
            if (ToolsBar.Visibility == Visibility.Visible)
            {
                PlaceBars();
            }
        }
        else
        {
            UpdateDim();
        }
    }

    // ---------- Selecting, moving, resizing, drawing ----------

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (IsOnBars(e.OriginalSource))
        {
            return;
        }

        StylePanel.Visibility = Visibility.Collapsed;
        StyleButton.IsChecked = false;
        var p = e.GetPosition(Ink);

        if (e.ClickCount == 2 && _tool is null && _selection is { } current && current.Contains(p))
        {
            Copy(); // double-click the selection: copy and close
            e.Handled = true;
            return;
        }

        _dragStart = p;
        if (_selection is not { } selection)
        {
            BeginSelect(p);
        }
        else if (HandleAt(p) is { } handle)
        {
            _drag = Drag.Resize;
            _handle = handle;
            _dragOrigin = selection;
            HideBars();
        }
        else if (selection.Contains(p))
        {
            if (_tool is null)
            {
                _drag = Drag.Move;
                _dragOrigin = selection;
                HideBars();
            }
            else if (_layer.PointerDown(p))
            {
                _drag = Drag.Draw;
            }
        }
        else if (_tool is null)
        {
            BeginSelect(p); // a fresh selection; drawings stay where they are on screen
        }
        else
        {
            _layer.CommitText();
        }

        if (_drag != Drag.None)
        {
            CaptureMouse();
        }

        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(Ink);
        switch (_drag)
        {
            case Drag.Select or Drag.Move or Drag.Resize:
                // A gaming mouse reports up to 1000 times a second; apply only the latest position, once per frame.
                _pendingDrag = p;
                if (!_frameHooked)
                {
                    _frameHooked = true;
                    CompositionTarget.Rendering += OnFrame;
                }

                break;
            case Drag.Draw:
                _layer.PointerMove(p, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                break;
            default:
                UpdateCursor(p);
                break;
        }

        if (_selection is null)
        {
            var at = e.GetPosition(Root);
            CrossV.X1 = CrossV.X2 = at.X;
            CrossV.Y1 = 0;
            CrossV.Y2 = Root.ActualHeight;
            CrossH.Y1 = CrossH.Y2 = at.Y;
            CrossH.X1 = 0;
            CrossH.X2 = Root.ActualWidth;
        }
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (_pendingDrag is { } p)
        {
            _pendingDrag = null;
            ApplyDrag(p);
        }
    }

    private void ApplyDrag(Point p)
    {
        switch (_drag)
        {
            case Drag.Select:
                SetSelection(Normalize(_dragStart, p));
                break;
            case Drag.Move:
                var moved = _dragOrigin;
                moved.X = Math.Clamp(moved.X + p.X - _dragStart.X, 0, Math.Max(0, _virtual.Width - moved.Width));
                moved.Y = Math.Clamp(moved.Y + p.Y - _dragStart.Y, 0, Math.Max(0, _virtual.Height - moved.Height));
                SetSelection(moved);
                break;
            case Drag.Resize:
                SetSelection(Resize(_dragOrigin, _handle!, p));
                break;
        }
    }

    /// <summary>Applies the last pointer position and stops the per-frame updates.</summary>
    private void FlushDrag()
    {
        OnFrame(null, EventArgs.Empty);
        if (_frameHooked)
        {
            _frameHooked = false;
            CompositionTarget.Rendering -= OnFrame;
        }
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag == Drag.None)
        {
            return;
        }

        _pendingDrag = e.GetPosition(Ink);
        FlushDrag();
        var drag = _drag;
        _drag = Drag.None;
        ReleaseMouseCapture();
        if (drag == Drag.Draw)
        {
            _layer.PointerUp();
            return;
        }

        if (_selection is not { } selection || selection.Width < MinSize || selection.Height < MinSize)
        {
            ClearSelection(); // a click, not a drag: choose again
            return;
        }

        if (drag == Drag.Select && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && !_layer.HasAnnotations)
        {
            Copy(); // Ctrl held while selecting: copy at once
            return;
        }

        ShowEditing();
    }

    private void BeginSelect(Point p)
    {
        _drag = Drag.Select;
        HideBars();
        Crosshair.Visibility = Visibility.Collapsed; // two full-screen lines moving every frame would redraw everything
        HintBar.Visibility = Visibility.Collapsed;
        Handles.Visibility = Visibility.Collapsed;
        SelectionRect.Visibility = Visibility.Visible;
        SizeBadge.Visibility = Visibility.Visible;
        SetSelection(new Rect(p, p));
    }

    /// <summary>Selects a region (device pixels) as a finished drag would.</summary>
    internal void Select(Rect region)
    {
        HintBar.Visibility = Visibility.Collapsed;
        SelectionRect.Visibility = Visibility.Visible;
        SizeBadge.Visibility = Visibility.Visible;
        SetSelection(region);
        ShowEditing();
    }

    private void ClearSelection()
    {
        _selection = null;
        SelectionRect.Visibility = Visibility.Collapsed;
        SizeBadge.Visibility = Visibility.Collapsed;
        Handles.Visibility = Visibility.Collapsed;
        Crosshair.Visibility = Visibility.Visible;
        HideBars();
        _inkClip.Rect = Rect.Empty;
        UpdateDim();
    }

    /// <summary>Puts the selection (device pixels) on screen: outline, clip, dimming, size badge and handles.</summary>
    internal void SetSelection(Rect r)
    {
        var left = Math.Round(Math.Max(0, r.Left));
        var top = Math.Round(Math.Max(0, r.Top));
        var right = Math.Round(Math.Min(_virtual.Width, r.Right));
        var bottom = Math.Round(Math.Min(_virtual.Height, r.Bottom));
        var s = new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
        _selection = s;
        if (_drag == Drag.None || _layer.HasAnnotations)
        {
            _inkClip.Rect = s; // skipped while choosing with nothing drawn: the drawing layer covers the whole desktop
        }

        var d = ToDip(s);
        Canvas.SetLeft(SelectionRect, d.X);
        Canvas.SetTop(SelectionRect, d.Y);
        SelectionRect.Width = d.Width;
        SelectionRect.Height = d.Height;
        UpdateDim(d);

        SizeText.Text = $"{(int)s.Width} × {(int)s.Height}";
        Canvas.SetLeft(SizeBadge, d.X);
        Canvas.SetTop(SizeBadge, d.Y >= 28 ? d.Y - 28 : d.Y + 4);

        foreach (var handle in Handles.Children.OfType<FrameworkElement>())
        {
            var c = HandleCentre(d, (string)handle.Tag);
            Canvas.SetLeft(handle, c.X - handle.Width / 2);
            Canvas.SetTop(handle, c.Y - handle.Height / 2);
        }
    }

    private static Point HandleCentre(Rect r, string handle) => new(
        handle.Contains('W') ? r.Left : handle.Contains('E') ? r.Right : r.Left + r.Width / 2,
        handle.Contains('N') ? r.Top : handle.Contains('S') ? r.Bottom : r.Top + r.Height / 2);

    private string? HandleAt(Point device)
    {
        if (Handles.Visibility != Visibility.Visible || _selection is not { } s)
        {
            return null;
        }

        var d = ToDip(s);
        var p = new Point(device.X / _scaleX, device.Y / _scaleY);
        foreach (var name in new[] { "NW", "NE", "SE", "SW", "N", "E", "S", "W" })
        {
            var c = HandleCentre(d, name);
            if (Math.Abs(p.X - c.X) <= HandleReach && Math.Abs(p.Y - c.Y) <= HandleReach)
            {
                return name;
            }
        }

        return null;
    }

    private Rect Resize(Rect origin, string handle, Point p)
    {
        var left = handle.Contains('W') ? p.X : origin.Left;
        var right = handle.Contains('E') ? p.X : origin.Right;
        var top = handle.Contains('N') ? p.Y : origin.Top;
        var bottom = handle.Contains('S') ? p.Y : origin.Bottom;
        var r = Normalize(new Point(left, top), new Point(right, bottom));
        r.Width = Math.Max(MinSize, r.Width);
        r.Height = Math.Max(MinSize, r.Height);
        return r;
    }

    private void UpdateCursor(Point p)
    {
        Cursor = HandleAt(p) switch
        {
            "NW" or "SE" => Cursors.SizeNWSE,
            "NE" or "SW" => Cursors.SizeNESW,
            "N" or "S" => Cursors.SizeNS,
            "E" or "W" => Cursors.SizeWE,
            _ when _selection is { } s && s.Contains(p) => _tool switch
            {
                null => Cursors.SizeAll,
                Tool.Text => Cursors.IBeam,
                _ => Cursors.Cross
            },
            _ => _tool is null ? Cursors.Cross : Cursors.Arrow
        };
    }

    /// <summary>Dims everything except the selection (DIPs).</summary>
    private void UpdateDim(Rect? hole = null) =>
        _brightClip.Rect = hole is { Width: > 0, Height: > 0 } h ? h : Rect.Empty;

    // ---------- Bars ----------

    private void ShowEditing()
    {
        Crosshair.Visibility = Visibility.Collapsed;
        Handles.Visibility = Visibility.Visible;
        _inkClip.Rect = _selection ?? Rect.Empty;
        PlaceBars();
    }

    private void HideBars()
    {
        ToolsBar.Visibility = Visibility.Collapsed;
        ActionsBar.Visibility = Visibility.Collapsed;
        StylePanel.Visibility = Visibility.Collapsed;
        StyleButton.IsChecked = false;
        TextCard.Visibility = Visibility.Collapsed;
    }

    /// <summary>Tools beside the selection, actions under it, the text card on the free side; all kept on its monitor.</summary>
    internal void PlaceBars()
    {
        if (_selection is not { } s)
        {
            return;
        }

        var sel = ToDip(s);
        var area = MonitorArea(s);
        ToolsBar.Visibility = Visibility.Visible;
        ActionsBar.Visibility = Visibility.Visible;
        var tools = Measure(ToolsBar);
        var actions = Measure(ActionsBar);

        // Tools: right of the selection, else left of it, else inside its right edge.
        var tx = sel.Right + Gap;
        if (tx + tools.Width > area.Right)
        {
            tx = sel.Left - Gap - tools.Width;
        }

        if (tx < area.Left)
        {
            tx = sel.Right - Gap - tools.Width;
        }

        var ty = Math.Clamp(sel.Top, area.Top, Math.Max(area.Top, area.Bottom - tools.Height));

        // Actions: under the selection, right-aligned with it; else above it; else inside its bottom edge.
        var ax = Math.Clamp(sel.Right - actions.Width, area.Left, Math.Max(area.Left, area.Right - actions.Width));
        var ay = sel.Bottom + Gap;
        if (ay + actions.Height > area.Bottom)
        {
            ay = sel.Top - Gap - actions.Height;
        }

        if (ay < area.Top)
        {
            ay = sel.Bottom - Gap - actions.Height;
        }

        var toolsRect = new Rect(tx, ty, tools.Width, tools.Height);
        var actionsRect = new Rect(ax, ay, actions.Width, actions.Height);
        if (toolsRect.IntersectsWith(actionsRect))
        {
            // The actions are wider than the selection and run into the tools. Lift the tools to end at the selection's
            // bottom; else put the actions above the selection; else below the tools.
            var lifted = toolsRect with { Y = Math.Max(area.Top, sel.Bottom - tools.Height) };
            var above = actionsRect with { Y = sel.Top - Gap - actions.Height };
            if (!lifted.IntersectsWith(actionsRect))
            {
                toolsRect = lifted;
            }
            else if (above.Top >= area.Top && !above.IntersectsWith(toolsRect))
            {
                actionsRect = above;
            }
            else
            {
                actionsRect.Y = Math.Min(toolsRect.Bottom + Gap, Math.Max(area.Top, area.Bottom - actions.Height));
            }
        }

        Place(ToolsBar, toolsRect.X, toolsRect.Y);
        Place(ActionsBar, actionsRect.X, actionsRect.Y);
        if (actionsRect.Bottom <= sel.Top && actionsRect.Bottom > sel.Top - 32)
        {
            Canvas.SetTop(SizeBadge, sel.Top + 4); // the actions took the badge's place above the selection
        }

        if (_textCardOpen)
        {
            TextCard.Visibility = Visibility.Visible;
            var card = Measure(TextCard);
            // Left of the selection, else right of the tools, else under the actions; always on the monitor.
            var candidates = new[]
            {
                new Point(sel.Left - Gap - card.Width, sel.Top),
                new Point(Math.Max(sel.Right, toolsRect.Right) + Gap, sel.Top),
                new Point(sel.Left, actionsRect.Bottom + Gap)
            };
            var spot = candidates.FirstOrDefault(c => area.Contains(new Rect(c, card)), candidates[0]);
            Place(TextCard,
                Math.Clamp(spot.X, area.Left, Math.Max(area.Left, area.Right - card.Width)),
                Math.Clamp(spot.Y, area.Top, Math.Max(area.Top, area.Bottom - card.Height)));
        }

        if (StyleButton.IsChecked == true)
        {
            PlaceStylePanel(toolsRect, sel, area);
        }
    }

    private void PlaceStylePanel(Rect tools, Rect sel, Rect area)
    {
        StylePanel.Visibility = Visibility.Visible;
        var panel = Measure(StylePanel);
        ToolsBar.UpdateLayout();
        var buttonTop = StyleButton.TranslatePoint(new Point(0, 0), ToolsBar).Y + tools.Top;
        var x = tools.Left >= sel.Right ? tools.Right + Gap : tools.Left - Gap - panel.Width;
        if (x < area.Left || x + panel.Width > area.Right)
        {
            x = tools.Left - Gap - panel.Width;
        }

        Place(StylePanel, Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - panel.Width)),
            Math.Clamp(buttonTop, area.Top, Math.Max(area.Top, area.Bottom - panel.Height)));
    }

    private static Size Measure(FrameworkElement element)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return element.DesiredSize;
    }

    private static void Place(UIElement element, double x, double y)
    {
        Canvas.SetLeft(element, Math.Round(x));
        Canvas.SetTop(element, Math.Round(y));
    }

    /// <summary>The monitor holding most of the selection, in window DIPs (the whole window if none matches).</summary>
    private Rect MonitorArea(Rect s)
    {
        var window = new Rect(0, 0, _virtual.Width / _scaleX, _virtual.Height / _scaleY);
        var centre = new Point(_virtual.X + s.X + s.Width / 2, _virtual.Y + s.Y + s.Height / 2);
        foreach (var m in NativeWindows.Monitors())
        {
            if (centre.X >= m.X && centre.X < m.Right && centre.Y >= m.Y && centre.Y < m.Bottom)
            {
                var area = new Rect((m.X - _virtual.X) / _scaleX, (m.Y - _virtual.Y) / _scaleY, m.Width / _scaleX, m.Height / _scaleY);
                area.Intersect(window);
                return area.IsEmpty ? window : area;
            }
        }

        return window;
    }

    private void PlaceHint()
    {
        var area = _selection is { } s ? MonitorArea(s) : MonitorArea(new Rect(-_virtual.X, -_virtual.Y, 1, 1));
        HintBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        HintBar.Margin = new Thickness(Math.Max(0, area.Left + (area.Width - HintBar.DesiredSize.Width) / 2), area.Top + 24, 0, 0);
    }

    /// <summary>A short message in the hint chip (for failures), gone after a few seconds.</summary>
    private void ShowNotice(string text)
    {
        HintText.Text = text;
        HintTail.Visibility = Visibility.Collapsed;
        HintBar.Visibility = Visibility.Visible;
        PlaceHint();
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    private bool IsOnBars(object source)
    {
        for (var d = source as DependencyObject; d is not null;
             d = d is Visual ? VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d == Bars)
            {
                return true;
            }
        }

        return false;
    }

    // ---------- Tools ----------

    private void BuildSwatches()
    {
        foreach (var color in EditorWindow.Palette)
        {
            var swatch = new RadioButton
            {
                Style = (Style)FindResource("Swatch"),
                GroupName = "CaptureColour",
                Background = new SolidColorBrush(color),
                Tag = color,
                Focusable = false,
                IsChecked = color == EditorWindow.Palette[0]
            };
            swatch.Checked += (_, _) =>
            {
                _layer.Color = (Color)swatch.Tag;
                StyleButton.Background = swatch.Background;
            };
            Swatches.Children.Add(swatch);
        }

        StyleButton.Background = new SolidColorBrush(EditorWindow.Palette[0]);
    }

    private void OnToolClick(object sender, RoutedEventArgs e)
    {
        var button = (ToggleButton)sender;
        SelectTool(button.IsChecked == true ? _tools[button] : null);
    }

    /// <summary>Picks a drawing tool, or none (then dragging moves and resizes the selection).</summary>
    internal void SelectTool(Tool? tool)
    {
        _layer.CommitText();
        _tool = tool;
        foreach (var (button, t) in _tools)
        {
            button.IsChecked = t == tool;
        }

        if (tool is { } chosen)
        {
            _layer.Tool = chosen;
        }
    }

    private void OnStyleClick(object sender, RoutedEventArgs e)
    {
        if (StyleButton.IsChecked == true)
        {
            PlaceBars();
        }
        else
        {
            StylePanel.Visibility = Visibility.Collapsed;
        }
    }

    private void OnSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_layer is not null)
        {
            _layer.Size = e.NewValue;
            SizeValue.Text = ((int)e.NewValue).ToString();
        }
    }

    private void OnUndo(object sender, RoutedEventArgs e)
    {
        _layer.CommitText();
        _layer.History.Undo();
    }

    // ---------- Outputs ----------

    /// <summary>The selection as it looks: frozen screen plus drawings, at full resolution.</summary>
    internal BitmapSource ComposeImage()
    {
        _layer.CommitText();
        var s = _selection ?? throw new InvalidOperationException("Nothing is selected.");
        var crop = ScreenCapture.Crop(_frozen, new PixelRect((int)s.X, (int)s.Y, (int)s.Width, (int)s.Height));
        if (!_layer.HasAnnotations)
        {
            return crop;
        }

        Ink.UpdateLayout();
        return Annotations.Compose(crop, Ink, s);
    }

    private void OnCopy(object sender, RoutedEventArgs e) => Copy();

    private bool _copying;

    /// <summary>Copies and closes. The copy never freezes the overlay; if the clipboard is busy for long, it says so and stays.</summary>
    private async void Copy()
    {
        if (_selection is null || _copying)
        {
            return;
        }

        _copying = true;
        var image = ComposeImage();
        var copy = ImageExport.CopyImageAsync(image);
        if (await Task.WhenAny(copy, Task.Delay(200)) != copy)
        {
            ShowNotice("Copying… another app is using the clipboard.");
        }

        try
        {
            await copy;
            Completed = true;
            Close();
        }
        catch (ExternalException ex)
        {
            ShowNotice(ImageExport.BusyMessage(ex));
        }
        finally
        {
            _copying = false;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e) => Save();

    private void Save()
    {
        if (_selection is null)
        {
            return;
        }

        try
        {
            if (_app.SaveImageAs(this, ComposeImage()) is not null)
            {
                Completed = true;
                Close();
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            ShowNotice($"Couldn't save: {ex.Message}");
        }
    }

    private void OnOpenEditor(object sender, RoutedEventArgs e)
    {
        if (_selection is null)
        {
            return;
        }

        OpenInEditor = ComposeImage();
        Completed = true;
        Close();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnSearchGoogle(object sender, RoutedEventArgs e) => SearchGoogle();

    /// <summary>Opens Google Lens results for the selection in the browser, and closes so the browser is in front.</summary>
    private void SearchGoogle()
    {
        if (_selection is null)
        {
            return;
        }

        try
        {
            ImageSearch.SearchGoogle(ComposeImage());
            Completed = true;
            Close();
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.ComponentModel.Win32Exception)
        {
            ShowNotice($"Couldn't open the browser: {ex.Message}");
        }
    }

    private void OnPrint(object sender, RoutedEventArgs e) => Print();

    private void Print()
    {
        if (_selection is null)
        {
            return;
        }

        try
        {
            if (_app.PrintImage(ComposeImage()))
            {
                Completed = true;
                Close();
            }
        }
        catch (System.Printing.PrintSystemException ex)
        {
            ShowNotice($"Couldn't print: {ex.Message}");
        }
    }

    /// <summary>Ends a drag in progress as if the button were released, keeping the shape or selection so far.</summary>
    private void FinishDrag()
    {
        if (_drag == Drag.None)
        {
            return;
        }

        FlushDrag();
        var drag = _drag;
        _drag = Drag.None;
        ReleaseMouseCapture();
        if (drag == Drag.Draw)
        {
            _layer.PointerUp();
        }
        else if (_selection is { } s && (s.Width < MinSize || s.Height < MinSize))
        {
            ClearSelection();
        }
    }

    /// <summary>Selects the whole monitor the pointer is on (Ctrl+C before choosing a region).</summary>
    private void SelectScreenUnderPointer()
    {
        var p = Mouse.GetPosition(Ink);
        var at = new Point(_virtual.X + p.X, _virtual.Y + p.Y);
        var monitor = NativeWindows.Monitors().FirstOrDefault(m => at.X >= m.X && at.X < m.Right && at.Y >= m.Y && at.Y < m.Bottom, _virtual);
        SetSelection(new Rect(monitor.X - _virtual.X, monitor.Y - _virtual.Y, monitor.Width, monitor.Height));
    }

    // ---------- Text ----------

    private void ShowTextCard()
    {
        _textCardOpen = true;
        PlaceBars();
    }

    private void OnCloseText(object sender, RoutedEventArgs e)
    {
        _work?.Cancel();
        _textCardOpen = false;
        TextCard.Visibility = Visibility.Collapsed;
    }

    private async void OnExtractText(object sender, RoutedEventArgs e) => await ExtractTextAsync();

    private async void OnTranslate(object sender, RoutedEventArgs e) => await TranslateAsync();

    private void OnCopyText(object sender, RoutedEventArgs e) => CopyText(OcrText.Text, "Text copied.");

    private void OnCopyTranslation(object sender, RoutedEventArgs e) => CopyText(TranslationText.Text, "Translation copied.");

    private async void CopyText(string text, string done)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            TextStatus.Text = "Nothing to copy yet.";
            return;
        }

        try
        {
            await ImageExport.CopyTextAsync(text);
            TextStatus.Text = done;
        }
        catch (ExternalException ex)
        {
            TextStatus.Text = ImageExport.BusyMessage(ex);
        }
    }

    /// <summary>Reads the selection with Windows OCR into the text card.</summary>
    internal async Task<bool> ExtractTextAsync()
    {
        if (_selection is null || _work is not null)
        {
            return false;
        }

        ShowTextCard();
        var image = ComposeImage();
        using var work = BeginWork("Reading text…");
        try
        {
            var text = await OcrService.RecognizeAsync(image, _app.Settings.OcrLanguage);
            OcrText.Text = text;
            var lines = text.Length == 0 ? 0 : text.Split('\n').Length;
            TextStatus.Text = lines == 0
                ? "No text found. For handwriting, try AI vision in the editor."
                : $"Found {lines} line{(lines == 1 ? "" : "s")} of text.";
            return lines > 0;
        }
        catch (Exception ex) // OCR engine missing or failed: report it, keep the overlay usable
        {
            TextStatus.Text = ex.Message;
            return false;
        }
        finally
        {
            EndWork();
        }
    }

    /// <summary>Translates the text card's text (reading it first if needed) with the Ollama translation model.</summary>
    internal async Task TranslateAsync()
    {
        if (_selection is null || _work is not null || !_app.Settings.AiEnabled)
        {
            return;
        }

        ShowTextCard();
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
        TranslationPanel.Visibility = Visibility.Visible;
        PlaceBars();
        using var work = BeginWork($"Translating to {target} with {model}…");
        try
        {
            TranslationText.Text = "";
            TranslationText.Text = await AiTasks.CollectAsync(
                AiTasks.TranslateAsync(_app.CreateOllama(), model, OcrText.Text, target, work.Token),
                partial => TranslationText.Text = partial, work.Token);
            TextStatus.Text = $"Translated to {target}.";
        }
        catch (OperationCanceledException)
        {
            TextStatus.Text = "Stopped.";
        }
        catch (Exception ex) // Ollama or network: report it, keep the overlay usable
        {
            TextStatus.Text = ex.Message;
        }
        finally
        {
            EndWork();
        }
    }

    private CancellationTokenSource BeginWork(string status)
    {
        _work = new CancellationTokenSource();
        TextStatus.Text = status;
        TranslateButton.IsEnabled = false;
        return _work;
    }

    private void EndWork()
    {
        _work = null;
        TranslateButton.IsEnabled = true;
    }

    // ---------- Keyboard ----------

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_layer.Typing is not null)
            {
                return; // the text being typed handles Esc (it discards the text)
            }

            Close();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && Keyboard.FocusedElement is not TextBox { SelectionLength: > 0 })
        {
            // Ctrl+C copies from any state: mid-drag, while typing, even before choosing (then the screen under the
            // pointer). Only a text box with selected text keeps its own Ctrl+C.
            FinishDrag();
            if (_selection is null)
            {
                SelectScreenUnderPointer();
            }

            Copy();
            e.Handled = true;
            return;
        }

        if (Keyboard.FocusedElement is TextBox || _selection is null || _drag != Drag.None)
        {
            return; // typing gets every other key
        }

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.P:
                    Print();
                    break;
                case Key.G:
                    SearchGoogle();
                    break;
                case Key.S:
                    Save();
                    break;
                case Key.E:
                    _ = ExtractTextAsync();
                    break;
                case Key.T:
                    _ = TranslateAsync();
                    break;
                case Key.Z:
                    _layer.History.Undo();
                    break;
                case Key.Y:
                    _layer.History.Redo();
                    break;
                default:
                    return;
            }

            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.None && ToolForKey(e.Key) is { } tool)
        {
            SelectTool(_tool == tool ? null : tool); // pressing a tool's key again goes back to moving and resizing
            e.Handled = true;
        }
    }

    private static Tool? ToolForKey(Key key) => key switch
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

    // ---------- Geometry ----------

    private Rect ToDip(Rect device) => new(device.X / _scaleX, device.Y / _scaleY, device.Width / _scaleX, device.Height / _scaleY);

    private static Rect Normalize(Point a, Point b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}
