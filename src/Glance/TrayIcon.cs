using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace Glance;

/// <summary>Notification-area icon: keeps Glance reachable while its window is closed.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Drawing.Icon _image;
    private bool _hintShown;

    public TrayIcon(AppController app)
    {
        _image = CreateIcon();
        // Menu clicks are queued so the menu has closed (and is out of the screenshot) before anything runs.
        static EventHandler Later(Action action) =>
            (_, _) => System.Windows.Application.Current.Dispatcher.BeginInvoke(action);

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Capture region", null, Later(async () =>
        {
            await Task.Delay(200); // let the menu's fade-out finish so it isn't in the frozen screenshot
            await app.CaptureAsync(fromMainWindow: false);
        }));
        menu.Items.Add("Record GIF", null, Later(() => _ = app.RecordGifAsync(fromMainWindow: false)));
        menu.Items.Add("Open image…", null, Later(() => app.OpenFile(null)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Show Glance", null, Later(app.ShowMain));
        menu.Items.Add("Settings", null, Later(app.ShowSettings));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, Later(app.Exit));

        _icon = new Forms.NotifyIcon
        {
            Icon = _image,
            Text = "Glance",
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.MouseDoubleClick += (_, _) => app.ShowMain();
    }

    public void UpdateTooltip(string captureHotkey, string gifHotkey)
    {
        var text = $"Glance — {captureHotkey} capture, {gifHotkey} GIF";
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    /// <summary>Shown once per session the first time the window is closed to the tray.</summary>
    public void ShowStillRunningHint()
    {
        if (_hintShown)
        {
            return;
        }

        _hintShown = true;
        _icon.ShowBalloonTip(3000, "Glance is still running", "Use the hotkeys or this tray icon. Right-click it to exit.", Forms.ToolTipIcon.None);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _image.Dispose();
    }

    /// <summary>The lens from Glance.ico at the tray's size (16px at 100%, larger on high-DPI screens).</summary>
    private static Drawing.Icon CreateIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Glance;component/Glance.ico"));
        if (resource is not null)
        {
            using var stream = resource.Stream;
            return new Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
        }

        return DrawIcon();
    }

    /// <summary>Fallback: the same lens drawn in code.</summary>
    private static Drawing.Icon DrawIcon()
    {
        using var bitmap = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            using var fill = new Drawing.SolidBrush(Drawing.Color.FromArgb(0x1A, 0x1D, 0x26));
            using var ring = new Drawing.Pen(Drawing.Color.FromArgb(0x4F, 0xC3, 0xF7), 3.5f);
            g.FillEllipse(fill, 2, 2, 22, 22);
            g.DrawEllipse(ring, 3, 3, 20, 20);
            using var handle = new Drawing.Pen(Drawing.Color.FromArgb(0x4F, 0xC3, 0xF7), 5f)
            {
                StartCap = Drawing.Drawing2D.LineCap.Round,
                EndCap = Drawing.Drawing2D.LineCap.Round
            };
            g.DrawLine(handle, 21, 21, 28, 28);
        }

        var hIcon = bitmap.GetHicon();
        try
        {
            // Clone so the icon owns its data and the HICON can be freed now.
            using var temp = Drawing.Icon.FromHandle(hIcon);
            return (Drawing.Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
