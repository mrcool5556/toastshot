using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Glance.Editor;
using Glance.Services;

namespace Glance.Tests;

public sealed class AnnotationTests
{
    [Fact]
    public void Arrow_Reaches_The_Tip() => TestUtil.RunSta(() =>
    {
        var geometry = Annotations.Arrow(new Point(10, 10), new Point(110, 10), 4);
        var bounds = geometry.Bounds;
        Assert.InRange(bounds.Right, 109.5, 110.5);
        Assert.InRange(bounds.Left, 9.5, 10.5);
        Assert.True(bounds.Height > 8, "Arrowhead should be wider than the shaft.");
        Assert.True(Annotations.Arrow(new Point(5, 5), new Point(5, 5), 4).IsEmpty());
    });

    [Fact]
    public void DragRect_And_Snap()
    {
        Assert.Equal(new Rect(10, 10, 30, 30), Annotations.DragRect(new Point(10, 10), new Point(40, 25), square: true));
        Assert.Equal(new Rect(0, 5, 10, 5), Annotations.DragRect(new Point(10, 10), new Point(0, 5), square: false));
        var snapped = Annotations.SnapAngle(new Point(0, 0), new Point(100, 8));
        Assert.Equal(0, snapped.Y, 6);
        var diagonal = Annotations.SnapAngle(new Point(0, 0), new Point(50, 46));
        Assert.Equal(diagonal.X, diagonal.Y, 6);
    }

    [Fact]
    public void Compose_Draws_Annotations_Over_Image() => TestUtil.RunSta(() =>
    {
        const int w = 60, h = 40;
        var blue = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null,
            Enumerable.Repeat(new byte[] { 255, 0, 0, 255 }, w * h).SelectMany(p => p).ToArray(), w * 4);
        var ink = new Canvas { Width = w, Height = h };
        var box = new Rectangle { Width = 20, Height = 10, Fill = Brushes.Red };
        Canvas.SetLeft(box, 30);
        Canvas.SetTop(box, 20);
        ink.Children.Add(box);
        ink.Measure(new Size(w, h));
        ink.Arrange(new Rect(0, 0, w, h));

        var result = Annotations.Compose(blue, ink, w, h);
        Assert.Equal(w, result.PixelWidth);
        Assert.Equal(h, result.PixelHeight);
        var px = TestUtil.Pixels(result);
        byte[] At(int x, int y) => px.Skip((y * w + x) * 4).Take(3).ToArray(); // B, G, R
        Assert.Equal(new byte[] { 255, 0, 0 }, At(5, 5));     // image
        Assert.Equal(new byte[] { 0, 0, 255 }, At(40, 25));   // red box
    });

    [Fact]
    public void History_Undoes_And_Redoes_Adds_And_Clear() => TestUtil.RunSta(() =>
    {
        var canvas = new Canvas();
        var history = new EditHistory(canvas.Children);
        var a = new Rectangle();
        var b = new Ellipse();
        canvas.Children.Add(a);
        history.Added(a);
        canvas.Children.Add(b);
        history.Added(b);

        history.ClearAll();
        Assert.Empty(canvas.Children);
        history.Undo();
        Assert.Equal(new UIElement[] { a, b }, canvas.Children.Cast<UIElement>());
        history.Undo();
        Assert.Equal(new UIElement[] { a }, canvas.Children.Cast<UIElement>());
        history.Redo();
        Assert.Equal(2, canvas.Children.Count);
        Assert.True(history.CanUndo);
        Assert.True(history.CanRedo); // the Clear is still redoable

        history.Undo();
        canvas.Children.Add(new Rectangle());
        history.Added((UIElement)canvas.Children[^1]);
        Assert.False(history.CanRedo); // a new edit discards redo
    });
}

public sealed class AnnotationLayerTests
{
    private static (Canvas Ink, AnnotationLayer Layer) NewLayer()
    {
        var ink = new Canvas { Width = 200, Height = 120, Background = Brushes.Transparent };
        return (ink, new AnnotationLayer(ink, Brushes.Cyan, new FontFamily("Segoe UI")));
    }

    private static void Drag(AnnotationLayer layer, Point from, Point to, bool constrain = false)
    {
        Assert.True(layer.PointerDown(from));
        for (var i = 1; i <= 5; i++)
        {
            layer.PointerMove(from + (to - from) * (i / 5.0), constrain);
        }

        layer.PointerUp();
    }

    [Fact]
    public void Line_Draws_A_Straight_Line_And_Shift_Snaps_It() => TestUtil.RunSta(() =>
    {
        var (ink, layer) = NewLayer();
        layer.Tool = Tool.Line;
        Drag(layer, new Point(10, 10), new Point(90, 14), constrain: true);
        var line = (LineGeometry)((Path)Assert.Single(ink.Children)).Data;
        Assert.Equal(new Point(10, 10), line.StartPoint);
        Assert.Equal(10, line.EndPoint.Y, 3); // nearly horizontal snaps to horizontal
    });

    [Fact]
    public void Compose_Region_Takes_Drawings_From_Their_Place_On_The_Canvas() => TestUtil.RunSta(() =>
    {
        // The capture overlay keeps drawings in screen pixels and crops a region out of them.
        var ink = new Canvas { Width = 400, Height = 300, Background = Brushes.Transparent };
        var layer = new AnnotationLayer(ink, Brushes.Cyan, new FontFamily("Segoe UI")) { Tool = Tool.Redact };
        Drag(layer, new Point(150, 120), new Point(190, 140));
        var region = new Rect(100, 100, 200, 100);
        ink.Clip = new RectangleGeometry(region);
        ink.Measure(new Size(400, 300));
        ink.Arrange(new Rect(0, 0, 400, 300));
        var white = BitmapSource.Create(200, 100, 96, 96, PixelFormats.Bgr32, null, Enumerable.Repeat((byte)255, 200 * 100 * 4).ToArray(), 200 * 4);

        var result = Annotations.Compose(white, ink, region);

        Assert.Equal((200, 100), (result.PixelWidth, result.PixelHeight));
        var px = TestUtil.Pixels(result);
        Assert.Equal(0, px[(30 * 200 + 70) * 4]);    // canvas (170, 130) is region (70, 30): inside the redaction
        Assert.Equal(255, px[(5 * 200 + 5) * 4]);    // region corner: untouched
        Assert.Equal(255, px[(30 * 200 + 120) * 4]); // right of the redaction: untouched
    });

    [Fact]
    public void Every_Shape_Tool_Draws_One_Undoable_Shape() => TestUtil.RunSta(() =>
    {
        var (ink, layer) = NewLayer();
        foreach (var tool in new[] { Tool.Pen, Tool.Marker, Tool.Arrow, Tool.Box, Tool.Oval, Tool.Redact })
        {
            layer.Tool = tool;
            Drag(layer, new Point(10, 10), new Point(90, 60));
        }

        Assert.Equal(6, ink.Children.Count);
        Assert.IsType<Polyline>(ink.Children[0]);
        Assert.Equal(6, ((Polyline)ink.Children[0]).Points.Count);
        var box = (Rectangle)ink.Children[3];
        Assert.Equal((10.0, 10.0, 80.0, 50.0), (Canvas.GetLeft(box), Canvas.GetTop(box), box.Width, box.Height));
        Assert.Equal(Brushes.Black, ((Rectangle)ink.Children[5]).Fill);

        for (var i = 0; i < 6; i++)
        {
            layer.History.Undo();
        }

        Assert.Empty(ink.Children);
    });

    [Fact]
    public void Click_Without_Drag_Leaves_A_Dot_For_Pen_But_Nothing_For_Box() => TestUtil.RunSta(() =>
    {
        var (ink, layer) = NewLayer();
        layer.Tool = Tool.Box;
        layer.PointerDown(new Point(50, 50));
        layer.PointerUp();
        Assert.Empty(ink.Children);
        Assert.False(layer.History.CanUndo);

        layer.Tool = Tool.Pen;
        layer.PointerDown(new Point(50, 50));
        layer.PointerUp();
        Assert.Single(ink.Children);
        Assert.True(layer.History.CanUndo);
    });

    [Fact]
    public void Shift_Makes_Squares() => TestUtil.RunSta(() =>
    {
        var (ink, layer) = NewLayer();
        layer.Tool = Tool.Oval;
        Drag(layer, new Point(20, 20), new Point(80, 40), constrain: true);
        var oval = (Ellipse)ink.Children[0];
        Assert.Equal(oval.Width, oval.Height);
    });

    [Fact]
    public void Text_Is_Committed_As_A_Label_And_Empty_Text_Is_Dropped() => TestUtil.RunSta(() =>
    {
        var (ink, layer) = NewLayer();
        layer.Tool = Tool.Text;
        layer.Color = Colors.Yellow;
        Assert.False(layer.PointerDown(new Point(30, 40)));
        Assert.NotNull(layer.Typing);
        layer.Typing!.Text = "Look here";
        layer.CommitText();

        var label = Assert.IsType<TextBlock>(Assert.Single(ink.Children));
        Assert.Equal("Look here", label.Text);
        Assert.Equal(Colors.Yellow, ((SolidColorBrush)label.Foreground).Color);

        layer.PointerDown(new Point(60, 60));
        layer.CommitText(); // nothing typed
        Assert.Single(ink.Children);
    });

    [Fact]
    public void Redaction_Is_Solid_Black_In_The_Exported_Image() => TestUtil.RunSta(() =>
    {
        var (ink, layer) = NewLayer();
        layer.Tool = Tool.Redact;
        Drag(layer, new Point(20, 20), new Point(60, 50));
        ink.Measure(new Size(200, 120));
        ink.Arrange(new Rect(0, 0, 200, 120));

        var white = BitmapSource.Create(200, 120, 96, 96, PixelFormats.Bgr32, null,
            Enumerable.Repeat((byte)255, 200 * 120 * 4).ToArray(), 200 * 4);
        var px = TestUtil.Pixels(Annotations.Compose(white, ink, 200, 120));
        int At(int x, int y) => px[(y * 200 + x) * 4 + 2]; // red channel
        Assert.Equal(0, At(40, 35));
        Assert.Equal(255, At(100, 100));
    });
}

public sealed class OcrIntegrationTests
{
    private static BitmapSource RenderText(string text, double size) => TestUtil.RunSta(() =>
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, Brushes.Black, 1.0);
        var w = (int)Math.Ceiling(formatted.Width) + 40;
        var h = (int)Math.Ceiling(formatted.Height) + 30;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            dc.DrawText(formatted, new Point(20, 15));
        }

        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return (BitmapSource)bitmap;
    });

    [SkippableFact]
    public async Task Windows_Ocr_Reads_Rendered_Text()
    {
        Skip.If(OcrService.InstalledLanguages().Count == 0, "No Windows OCR language installed.");
        // Small UI-sized text checks that upscaling for OCR works.
        var text = await OcrService.RecognizeAsync(RenderText("Glance reads 2048 words", 13), null);
        Assert.Contains("Glance", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2048", text);
    }
}

/// <summary>Talks to a real local Ollama; skipped when it isn't running or the default model is missing.</summary>
public sealed class OllamaIntegrationTests
{
    [SkippableFact]
    public async Task Translates_With_Default_Model()
    {
        var settings = new AppSettings();
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        var client = new OllamaClient(http, settings.OllamaUrl);
        IReadOnlyList<OllamaModel> models;
        try
        {
            using var quick = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            models = await client.ListModelsAsync(quick.Token);
        }
        catch (Exception ex) when (ex is OllamaException or OperationCanceledException)
        {
            throw new SkipException("Ollama not reachable.");
        }

        Skip.IfNot(models.Any(m => m.Name == settings.TranslateModel), $"{settings.TranslateModel} not installed.");
        foreach (var (source, expected) in new[]
                 {
                     ("Bonjour le monde. Merci beaucoup !", new[] { "world", "thank" }),
                     ("ありがとうございます。\n設定を保存しました。", new[] { "thank", "sav" })
                 })
        {
            var result = await AiTasks.CollectAsync(
                AiTasks.TranslateAsync(client, settings.TranslateModel, source, "English", CancellationToken.None),
                null, CancellationToken.None);
            Assert.True(expected.All(word => result.Contains(word, StringComparison.OrdinalIgnoreCase)),
                $"Unexpected translation of \"{source}\": {result}");
        }
    }
}
