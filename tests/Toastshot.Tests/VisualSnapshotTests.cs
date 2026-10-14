using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Toastshot.Editor;
using Toastshot.Gif;
using Toastshot.Services;

namespace Toastshot.Tests;

/// <summary>
/// Renders Toastshot's windows off-screen (no window is shown, nothing takes focus) and drives the real
/// buttons, saving PNGs for review. Opt-in: set TOASTSHOT_SNAPSHOT_DIR and TOASTSHOT_SAMPLE_DIR
/// (containing sample.png and sample.gif).
/// </summary>
public sealed class VisualSnapshotTests
{
    [SkippableFact]
    public void Render_Windows_And_Run_Ocr_And_Translate()
    {
        var outDir = Environment.GetEnvironmentVariable("TOASTSHOT_SNAPSHOT_DIR");
        var samples = Environment.GetEnvironmentVariable("TOASTSHOT_SAMPLE_DIR");
        Skip.If(string.IsNullOrEmpty(outDir) || string.IsNullOrEmpty(samples), "Snapshots are opt-in.");
        Directory.CreateDirectory(outDir!);

        TestUtil.RunSta(() =>
        {
            // Like the real app: async UI handlers resume on this (dispatcher) thread.
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            // A plain Application with only the theme resources: App would run OnStartup (tray, hotkeys,
            // single-instance check) as soon as the dispatcher pumps.
            // TOASTSHOT_SNAPSHOT_THEME=light renders the light theme.
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var light = string.Equals(Environment.GetEnvironmentVariable("TOASTSHOT_SNAPSHOT_THEME"), "light", StringComparison.OrdinalIgnoreCase);
            ThemeManager.Apply(app, light ? AppTheme.Light : AppTheme.Dark);
            var controller = new AppController(new AppSettings());

            // Main window
            var main = new MainWindow(controller);
            PumpUntil(() => !main.OllamaStatus.Text.StartsWith("Checking"), TimeSpan.FromSeconds(5));
            Snapshot(main, 460, 0, Path.Combine(outDir!, "1-main.png"));

            // Editor with drawings
            var editor = new EditorWindow(controller, AppController.LoadImage(Path.Combine(samples!, "sample.png")));
            var layer = editor.Layer;
            layer.Tool = Tool.Box;
            layer.Color = Color.FromRgb(0xEF, 0x44, 0x44);
            Drag(layer, new Point(16, 60), new Point(560, 148));
            layer.Tool = Tool.Arrow;
            Drag(layer, new Point(300, 230), new Point(196, 196));
            layer.Tool = Tool.Marker;
            layer.Color = Color.FromRgb(0xFA, 0xCC, 0x15);
            Drag(layer, new Point(24, 128), new Point(330, 128));
            layer.Tool = Tool.Text;
            layer.Color = Color.FromRgb(0x3B, 0x82, 0xF6);
            layer.PointerDown(new Point(330, 200));
            layer.Typing!.Text = "Save button";
            layer.CommitText();
            Snapshot(editor, 1040, 600, Path.Combine(outDir!, "2-editor-drawn.png"));

            // Extract text (Windows OCR) through the real button
            Click(editor.ReadButton);
            PumpUntil(() => editor.Status.Text.StartsWith("Found") || editor.Status.Text.StartsWith("No text"), TimeSpan.FromSeconds(30));
            Snapshot(editor, 1040, 600, Path.Combine(outDir!, "3-editor-ocr.png"));
            File.WriteAllText(Path.Combine(outDir!, "ocr.txt"), editor.OcrText.Text);

            // Translate through the real button (needs Ollama)
            Click(editor.TranslateButton);
            PumpUntil(() => editor.Status.Text.StartsWith("Translated") || editor.StopButton.Visibility != Visibility.Visible && !editor.Status.Text.StartsWith("Translating"),
                TimeSpan.FromMinutes(3));
            Snapshot(editor, 1040, 600, Path.Combine(outDir!, "4-editor-translated.png"));
            File.WriteAllText(Path.Combine(outDir!, "translation.txt"), editor.TranslationText.Text + "\n---\n" + editor.Status.Text);

            // Exported image (what Copy/Save produce)
            Save(editor.ComposeImage(), Path.Combine(outDir!, "5-exported.png"));

            // A large capture shown scaled down must still export at full resolution with drawings in place.
            var large = BitmapSource.Create(2000, 1200, 96, 96, PixelFormats.Bgr32, null,
                Enumerable.Repeat((byte)255, 2000 * 1200 * 4).ToArray(), 2000 * 4);
            var big = new EditorWindow(controller, large);
            big.Layer.Tool = Tool.Redact;
            Drag(big.Layer, new Point(1500, 900), new Point(1900, 1100));
            Snapshot(big, 800, 500, Path.Combine(outDir!, "10-large-scaled.png"));
            var exported = big.ComposeImage();
            Assert.Equal((2000, 1200), (exported.PixelWidth, exported.PixelHeight));
            var px = TestUtil.Pixels(exported);
            Assert.Equal(0, px[(1000 * 2000 + 1700) * 4]);        // inside the redaction: black
            Assert.Equal(255, px[(100 * 2000 + 100) * 4]);        // outside: untouched white
            Assert.Equal(255, px[(1000 * 2000 + 1450) * 4]);      // just left of it: white

            // Settings
            var settings = new SettingsWindow(controller);
            PumpUntil(() => !settings.ModelStatus.Text.StartsWith("Loading"), TimeSpan.FromSeconds(5));
            PumpUntil(() => !settings.AiStatus.Text.StartsWith("Checking"), TimeSpan.FromSeconds(5));
            settings.AdvancedToggle.IsChecked = true;
            settings.AdvancedPanel.Visibility = Visibility.Visible;
            for (var tab = 0; tab < settings.Tabs.Items.Count; tab++)
            {
                settings.Tabs.SelectedIndex = tab;
                Snapshot(settings, 560, 640, Path.Combine(outDir!, $"6-settings-{tab + 1}.png"));
            }
            Snapshot(new AiSetupWindow(controller), 480, 0, Path.Combine(outDir!, "12-ai-setup.png"));

            // Update window for someone two releases behind.
            var releases = Updater.Parse("""
                [{"tag_name":"v0.3.0","draft":false,"prerelease":false,"published_at":"2026-10-20T10:00:00Z","html_url":"https://x/3",
                  "body":"### New\n- **Save MP4** at full size and frame rate\n- GIF size menu: fit 5–50 MB or scale\n### Fixed\n- Copying no longer freezes when another app holds the clipboard","assets":[]},
                 {"tag_name":"v0.2.0","draft":false,"prerelease":false,"published_at":"2026-10-12T10:00:00Z","html_url":"https://x/2",
                  "body":"- Print Screen works inside games that block hotkeys\n- Local AI setup with Gemma 3 4B","assets":[]}]
                """, new Version(0, 1, 0));
            Snapshot(new UpdateWindow(new UpdateCheck(new Version(0, 1, 0), releases)), 520, 0, Path.Combine(outDir!, "13-update.png"));

            // GIF preview and recording bar
            var preview = new GifPreviewWindow(controller, Path.Combine(samples!, "sample.gif"), null);
            Snapshot(preview, 600, 340, Path.Combine(outDir!, "7-gif-preview.png"));
            var bar = new RecordingBar("640 × 360", 15, 60);
            Snapshot(bar, 0, 0, Path.Combine(outDir!, "8-recording-bar.png"));

            // Region selection overlay over a "frozen screen", mid-drag
            var overlay = new Overlay.RegionSelectWindow(AppController.LoadImage(Path.Combine(samples!, "sample.png")), "Drag to capture a region");
            Snapshot(overlay, 620, 260, Path.Combine(outDir!, "9-overlay.png"));
            typeof(Overlay.RegionSelectWindow).GetMethod("UpdateSelection", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(overlay, [new Point(20, 60), new Point(330, 140)]);
            Snapshot(overlay, 620, 260, Path.Combine(outDir!, "9-overlay.png"));

            // Capture overlay after choosing a region: tools beside it, actions under it, drawings and the text card.
            var desk = new DrawingVisual();
            using (var dc = desk.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x5B, 0x71, 0x86)), null, new Rect(0, 0, 1100, 640));
                dc.DrawImage(AppController.LoadImage(Path.Combine(samples!, "sample.png")), new Rect(40, 40, 600, 260));
            }

            var screen = new RenderTargetBitmap(1100, 640, 96, 96, PixelFormats.Pbgra32);
            screen.Render(desk);
            var capture = new Overlay.CaptureWindow(controller, screen, new PixelRect(0, 0, 1100, 640));
            var captureRoot = (FrameworkElement)capture.Content;
            captureRoot.Measure(new Size(1100, 640));
            captureRoot.Arrange(new Rect(0, 0, 1100, 640));
            capture.Select(new Rect(56, 80, 440, 150));
            capture.SelectTool(Tool.Box);
            Drag(capture.Layer, new Point(60, 132), new Point(250, 172));
            capture.SelectTool(Tool.Arrow);
            Drag(capture.Layer, new Point(420, 210), new Point(270, 160));
            capture.SelectTool(Tool.Redact);
            Drag(capture.Layer, new Point(300, 196), new Point(470, 222));
            capture.SelectTool(null);
            var reading = capture.ExtractTextAsync();
            PumpUntil(() => reading.IsCompleted, TimeSpan.FromSeconds(30));
            Snapshot(capture, 1100, 640, Path.Combine(outDir!, "11-capture-edit.png"));

            // Drawings stay at their place on screen when the selection moves.
            var before = TestUtil.Pixels(capture.ComposeImage());
            Assert.Equal(0, before[((210 - 80) * 440 + (400 - 56)) * 4]);  // screen (400, 210): redacted
            capture.SetSelection(new Rect(76, 80, 440, 150));
            var after = TestUtil.Pixels(capture.ComposeImage());
            Assert.Equal(0, after[((210 - 80) * 440 + (400 - 76)) * 4]);   // same screen point, 20px further left in the image
            Assert.NotEqual(0, after[((210 - 80) * 440 + (480 - 76)) * 4]); // right of the redaction: not black

            preview.Close();
            app.Shutdown();
        });
    }

    private static void Drag(AnnotationLayer layer, Point from, Point to)
    {
        layer.PointerDown(from);
        for (var i = 1; i <= 8; i++)
        {
            layer.PointerMove(from + (to - from) * (i / 8.0), false);
        }

        layer.PointerUp();
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    /// <summary>Runs the dispatcher (async work, bindings) until the condition holds.</summary>
    private static void PumpUntil(Func<bool> done, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!done() && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
            Dispatcher.PushFrame(frame);
            timer.Stop();
        }
    }

    /// <summary>Lays out the window's content at the given size (0 = size to content) and renders it.</summary>
    private static void Snapshot(Window window, double width, double height, string path)
    {
        var root = (FrameworkElement)window.Content;
        var available = new Size(width > 0 ? width : double.PositiveInfinity, height > 0 ? height : double.PositiveInfinity);
        root.Measure(available);
        var size = new Size(width > 0 ? width : root.DesiredSize.Width, height > 0 ? height : root.DesiredSize.Height);
        root.Arrange(new Rect(size));
        root.UpdateLayout();
        PumpUntil(() => false, TimeSpan.FromMilliseconds(100)); // let pending renders/bindings settle
        root.Measure(available);
        root.Arrange(new Rect(size));
        root.UpdateLayout();

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(window.Background ?? Brushes.Transparent, null, new Rect(size));
            dc.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(size));
        }

        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        Save(bitmap, path);
    }

    private static void Save(BitmapSource image, string path) => ImageExport.Save(image, path);
}
