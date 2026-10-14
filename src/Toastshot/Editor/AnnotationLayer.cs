using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace Toastshot.Editor;

/// <summary>
/// The drawing tools, independent of the window: pointer positions in, shapes on the canvas out.
/// All coordinates are image pixels.
/// </summary>
public sealed class AnnotationLayer
{
    private readonly Canvas _ink;
    private readonly Brush _editBorder;
    private readonly FontFamily _font;
    private Point _start;
    private Shape? _drawing;

    public AnnotationLayer(Canvas ink, Brush editBorder, FontFamily font)
    {
        _ink = ink;
        _editBorder = editBorder;
        _font = font;
        History = new EditHistory(ink.Children);
    }

    public EditHistory History { get; }
    public Tool Tool { get; set; } = Tool.Pen;
    public Color Color { get; set; } = Colors.Red;
    public double Size { get; set; } = 4;

    /// <summary>The text box being typed into, if any.</summary>
    public TextBox? Typing { get; private set; }

    public bool HasAnnotations => _ink.Children.Count > 0;

    /// <summary>Returns true if the press was used (the window should capture the mouse for a drag).</summary>
    public bool PointerDown(Point point)
    {
        if (Typing is not null)
        {
            CommitText(); // clicking away finishes the text
            return false;
        }

        if (Tool == Tool.Text)
        {
            StartText(point);
            return false;
        }

        _start = point;
        _drawing = CreateShape(point);
        _ink.Children.Add(_drawing);
        return true;
    }

    public void PointerMove(Point point, bool constrain)
    {
        switch (_drawing)
        {
            case null:
                return;
            case Polyline line:
                line.Points.Add(point);
                break;
            case Path path:
                var end = constrain ? Annotations.SnapAngle(_start, point) : point;
                path.Data = Tool == Tool.Line ? new LineGeometry(_start, end) : Annotations.Arrow(_start, end, Size);
                break;
            default:
                var rect = Annotations.DragRect(_start, point, constrain);
                Canvas.SetLeft(_drawing, rect.X);
                Canvas.SetTop(_drawing, rect.Y);
                _drawing.Width = rect.Width;
                _drawing.Height = rect.Height;
                break;
        }
    }

    public void PointerUp()
    {
        if (_drawing is not { } shape)
        {
            return;
        }

        _drawing = null;
        switch (shape)
        {
            case Polyline { Points.Count: 1 } dot:
                // A click with the pen leaves a dot.
                dot.Points.Add(new Point(dot.Points[0].X + 0.01, dot.Points[0].Y));
                break;
            case Path path when path.Data.IsEmpty() || path.Data.Bounds.Width < 3 && path.Data.Bounds.Height < 3:
            case Shape when shape is not (Polyline or Path) && shape.Width < 3 && shape.Height < 3:
                _ink.Children.Remove(shape); // a click, not a drag
                return;
        }

        History.Added(shape);
    }

    public void StartText(Point at)
    {
        CommitText();
        var fontSize = Annotations.TextSize(Size);
        var box = new TextBox
        {
            Style = null,
            Background = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)),
            BorderBrush = _editBorder,
            BorderThickness = new Thickness(1),
            Foreground = new SolidColorBrush(Color),
            CaretBrush = new SolidColorBrush(Color),
            FontFamily = _font,
            FontSize = fontSize,
            FontWeight = FontWeights.SemiBold,
            MinWidth = 40,
            Padding = new Thickness(2, 0, 2, 0)
        };
        box.PreviewKeyDown += OnTypingKey;
        box.LostKeyboardFocus += (_, _) =>
        {
            if (Typing == box)
            {
                CommitText();
            }
        };
        Canvas.SetLeft(box, at.X);
        Canvas.SetTop(box, at.Y - fontSize * 0.7);
        _ink.Children.Add(box);
        Typing = box;
        box.Dispatcher.BeginInvoke(() => box.Focus());
    }

    /// <summary>Turns the text being typed into a text annotation (or drops it if empty).</summary>
    public void CommitText()
    {
        if (Typing is not { } box)
        {
            return;
        }

        Typing = null;
        _ink.Children.Remove(box);
        if (string.IsNullOrWhiteSpace(box.Text))
        {
            return;
        }

        var text = new TextBlock
        {
            Text = box.Text,
            Foreground = box.Foreground,
            FontFamily = box.FontFamily,
            FontSize = box.FontSize,
            FontWeight = box.FontWeight,
            Padding = new Thickness(3, 1, 3, 1),
            IsHitTestVisible = false,
            // A faint shadow keeps text readable on any background.
            Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Opacity = 0.6 }
        };
        Canvas.SetLeft(text, Canvas.GetLeft(box));
        Canvas.SetTop(text, Canvas.GetTop(box));
        _ink.Children.Add(text);
        History.Added(text);
    }

    private void OnTypingKey(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            var caret = box.CaretIndex;
            box.Text = box.Text.Insert(caret, Environment.NewLine);
            box.CaretIndex = caret + Environment.NewLine.Length;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            CommitText();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            box.Text = "";
            CommitText();
            e.Handled = true;
        }
    }

    private Shape CreateShape(Point start)
    {
        var brush = new SolidColorBrush(Color);
        brush.Freeze();
        Shape shape = Tool switch
        {
            Tool.Pen => new Polyline { Stroke = brush, StrokeThickness = Size, Points = { start } },
            Tool.Marker => new Polyline
            {
                Stroke = new SolidColorBrush(Color.FromArgb(0x66, Color.R, Color.G, Color.B)),
                StrokeThickness = Math.Max(10, Size * 4),
                Points = { start }
            },
            Tool.Line => new Path { Stroke = brush, StrokeThickness = Size, Data = Geometry.Empty },
            Tool.Arrow => new Path { Stroke = brush, Fill = brush, StrokeThickness = Size, Data = Geometry.Empty },
            Tool.Box => new Rectangle { Stroke = brush, StrokeThickness = Size },
            Tool.Oval => new Ellipse { Stroke = brush, StrokeThickness = Size },
            Tool.Redact => new Rectangle { Fill = Brushes.Black },
            _ => throw new InvalidOperationException($"{Tool} doesn't draw shapes.")
        };

        shape.StrokeLineJoin = PenLineJoin.Round;
        shape.StrokeStartLineCap = PenLineCap.Round;
        shape.StrokeEndLineCap = PenLineCap.Round;
        shape.IsHitTestVisible = false;
        if (shape is not (Polyline or Path))
        {
            Canvas.SetLeft(shape, start.X);
            Canvas.SetTop(shape, start.Y);
            shape.Width = 0;
            shape.Height = 0;
        }

        return shape;
    }
}
