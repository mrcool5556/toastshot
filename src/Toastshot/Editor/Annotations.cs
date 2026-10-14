using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Toastshot.Editor;

public enum Tool
{
    Pen,
    Marker,
    Line,
    Arrow,
    Box,
    Oval,
    Text,
    Redact
}

/// <summary>Pure geometry for the drawing tools.</summary>
public static class Annotations
{
    /// <summary>A line from <paramref name="from"/> to <paramref name="to"/> ending in a filled arrowhead.</summary>
    public static Geometry Arrow(Point from, Point to, double thickness)
    {
        var vector = to - from;
        var length = vector.Length;
        if (length < 0.5)
        {
            return Geometry.Empty;
        }

        vector /= length;
        var headLength = Math.Min(length, Math.Max(12, thickness * 4));
        var headHalfWidth = headLength * 0.45;
        var normal = new Vector(-vector.Y, vector.X);
        var baseCenter = to - vector * headLength;

        var head = new StreamGeometry();
        using (var ctx = head.Open())
        {
            ctx.BeginFigure(to, isFilled: true, isClosed: true);
            ctx.LineTo(baseCenter + normal * headHalfWidth, isStroked: true, isSmoothJoin: false);
            ctx.LineTo(baseCenter - normal * headHalfWidth, isStroked: true, isSmoothJoin: false);
        }

        var group = new GeometryGroup();
        // Stop the shaft inside the head so its round cap doesn't poke past the tip.
        group.Children.Add(new LineGeometry(from, baseCenter + vector * Math.Min(headLength / 2, thickness)));
        group.Children.Add(head);
        group.Freeze();
        return group;
    }

    /// <summary>The rectangle spanned by a drag; with <paramref name="square"/> both sides take the longer length.</summary>
    public static Rect DragRect(Point start, Point end, bool square)
    {
        if (square)
        {
            var side = Math.Max(Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y));
            end = new Point(start.X + Math.Sign(end.X - start.X) * side, start.Y + Math.Sign(end.Y - start.Y) * side);
        }

        return new Rect(start, end);
    }

    /// <summary>Snaps the end point to the nearest 45° direction from the start.</summary>
    public static Point SnapAngle(Point start, Point end)
    {
        var v = end - start;
        if (v.Length < 0.5)
        {
            return end;
        }

        var angle = Math.Round(Math.Atan2(v.Y, v.X) / (Math.PI / 4)) * (Math.PI / 4);
        return start + new Vector(Math.Cos(angle), Math.Sin(angle)) * v.Length;
    }

    public static double TextSize(double strokeSize) => 12 + strokeSize * 2;

    /// <summary>Flattens the base image and the annotation canvas into one image of <paramref name="width"/> × <paramref name="height"/> pixels.</summary>
    public static System.Windows.Media.Imaging.BitmapSource Compose(ImageSource image, Visual annotations, int width, int height) =>
        Compose(image, annotations, new Rect(0, 0, width, height));

    /// <summary>
    /// Flattens the base image with the part of the annotation canvas inside <paramref name="region"/> (canvas
    /// coordinates); the image covers exactly that region and the result is region-sized.
    /// </summary>
    public static System.Windows.Media.Imaging.BitmapSource Compose(ImageSource image, Visual annotations, Rect region)
    {
        var width = (int)Math.Round(region.Width);
        var height = (int)Math.Round(region.Height);
        var bounds = new Rect(0, 0, width, height);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(image, bounds);
            dc.DrawRectangle(new VisualBrush(annotations)
            {
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = region,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = bounds
            }, null, bounds);
        }

        var target = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }
}

/// <summary>Undo/redo over the children of the annotation canvas.</summary>
public sealed class EditHistory(UIElementCollection target)
{
    private readonly Stack<(UIElement[] Added, UIElement[] Removed)> _undo = new();
    private readonly Stack<(UIElement[] Added, UIElement[] Removed)> _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public event EventHandler? Changed;

    /// <summary>Records an element that the caller has already added to the canvas.</summary>
    public void Added(UIElement element) => Push(([element], []));

    public void ClearAll()
    {
        if (target.Count == 0)
        {
            return;
        }

        var removed = target.Cast<UIElement>().ToArray();
        target.Clear();
        Push(([], removed));
    }

    public void Undo() => Move(_undo, _redo, reverse: true);

    public void Redo() => Move(_redo, _undo, reverse: false);

    private void Push((UIElement[] Added, UIElement[] Removed) edit)
    {
        _undo.Push(edit);
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Move(Stack<(UIElement[] Added, UIElement[] Removed)> from, Stack<(UIElement[] Added, UIElement[] Removed)> to, bool reverse)
    {
        if (from.Count == 0)
        {
            return;
        }

        var edit = from.Pop();
        var (remove, add) = reverse ? (edit.Added, edit.Removed) : (edit.Removed, edit.Added);
        foreach (var element in remove)
        {
            target.Remove(element);
        }

        foreach (var element in add)
        {
            target.Add(element);
        }

        to.Push(edit);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
