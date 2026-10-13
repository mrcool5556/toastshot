using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Glance.Services;

namespace Glance.Tests;

/// <summary>Needs a real Windows desktop session (clipboard, screen, Media Foundation); CI runs without these.</summary>
[Trait("Needs", "Desktop")]
public sealed class ClipboardTests
{
    [Fact]
    public void Copy_Waits_For_An_App_Holding_The_Clipboard() => TestUtil.RunSta(() =>
    {
        // Another "app" (a thread) opens the clipboard and keeps it for 400 ms, as a clipboard watcher does.
        using var opened = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            Assert.True(OpenClipboard(IntPtr.Zero));
            opened.Set();
            Thread.Sleep(400);
            CloseClipboard();
        });
        holder.Start();
        opened.Wait();

        // Like the app: async copy on a dispatcher thread, which must stay responsive meanwhile.
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var copy = ImageExport.CopyTextAsync("glance clipboard test");
        var pumps = 0;
        while (!copy.IsCompleted)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            pumps++;
        }

        copy.GetAwaiter().GetResult();
        Assert.True(pumps > 10, "the UI thread kept running while the clipboard was busy");
        holder.Join();
        Assert.Equal("glance clipboard test", Clipboard.GetText());
    });

    [Fact]
    public void Busy_Message_Names_The_App() =>
        Assert.Equal("Couldn't copy: Discord is using the clipboard. Try again.",
            ImageExport.BusyMessage(new ClipboardBusyException("Discord", new COMException())));

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();
}
