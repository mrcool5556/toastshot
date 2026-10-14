using System.IO;
using System.Windows;
using System.Windows.Threading;
using Toastshot.Capture;
using Toastshot.Services;

namespace Toastshot.Gif;

/// <summary>One GIF recording: outline + control bar, then recording, then hand-off to the preview.</summary>
internal sealed class GifSession
{
    private readonly AppController _app;
    private readonly PixelRect _region;
    private readonly RegionFrame _frame;
    private readonly RecordingBar _bar;
    private readonly DispatcherTimer _clock;
    private GifRecorder? _recorder;
    private bool _ending;

    /// <summary>Raised once; the recording, or null if cancelled or failed.</summary>
    public event EventHandler<GifRecording?>? Ended;

    public GifSession(AppController app, PixelRect region)
    {
        _app = app;
        _region = region;
        _frame = new RegionFrame(region);
        var s = app.Settings;
        _bar = new RecordingBar($"{region.Width} × {region.Height} · {s.GifFramesPerSecond} fps · up to {s.GifMaxSeconds} s");
        _bar.RecordClicked += (_, _) => Start();
        _bar.StopClicked += async (_, _) => await StopAsync();
        _bar.CancelClicked += async (_, _) => await CancelAsync();
        _clock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _clock.Tick += (_, _) =>
        {
            if (_recorder is not null)
            {
                _bar.UpdateClock(_recorder.Elapsed, _recorder.FramesCaptured);
            }
        };
    }

    public void Show()
    {
        _frame.Show();
        _bar.Show();
        _bar.UpdateLayout();
        PlaceBar();
        _bar.Activate();
    }

    /// <summary>Below the region if it fits on that monitor, otherwise above, otherwise inside the bottom edge.</summary>
    private void PlaceBar()
    {
        var (scaleX, scaleY) = NativeWindows.DpiScale(_bar);
        var width = (int)Math.Ceiling(_bar.ActualWidth * scaleX);
        var height = (int)Math.Ceiling(_bar.ActualHeight * scaleY);
        var work = NativeWindows.WorkAreaFor(_region);
        const int gap = 8;

        var x = Math.Clamp(_region.X + (_region.Width - width) / 2, work.X, Math.Max(work.X, work.Right - width));
        int y;
        if (_region.Bottom + RegionFrame.BorderPixels + gap + height <= work.Bottom)
        {
            y = _region.Bottom + RegionFrame.BorderPixels + gap;
        }
        else if (_region.Y - RegionFrame.BorderPixels - gap - height >= work.Y)
        {
            y = _region.Y - RegionFrame.BorderPixels - gap - height;
        }
        else
        {
            y = Math.Min(_region.Bottom, work.Bottom) - height - gap; // inside; excluded from capture anyway
        }

        NativeWindows.SetPosition(_bar, x, y);
    }

    private void Start()
    {
        if (_recorder is not null)
        {
            return;
        }

        var s = _app.Settings;
        try
        {
            _recorder = new GifRecorder(new ScreenFrameSource(_region, s.GifShowCursor), TempFiles.NewPath(".gif"),
                s.GifFramesPerSecond, TimeSpan.FromSeconds(s.GifMaxSeconds));
            _recorder.MaxDurationReached += (_, _) => _bar.Dispatcher.BeginInvoke(async () => await StopAsync());
            _recorder.Start();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Couldn't start recording: {ex.Message}", "Toastshot", MessageBoxButton.OK, MessageBoxImage.Warning);
            End(null);
            return;
        }

        _bar.ShowRecording();
        _frame.ShowRecording();
        _clock.Start();
    }

    /// <summary>The GIF hotkey pressed again: starts recording if waiting, otherwise stops.</summary>
    public Task ToggleAsync()
    {
        if (_recorder is null && !_ending)
        {
            Start();
            return Task.CompletedTask;
        }

        return StopAsync();
    }

    /// <summary>Stops and finishes the GIF. Before recording has started this behaves like Cancel.</summary>
    public async Task StopAsync()
    {
        if (_ending)
        {
            return;
        }

        if (_recorder is null)
        {
            await CancelAsync();
            return;
        }

        _ending = true;
        _clock.Stop();
        _bar.ShowFinishing();
        GifRecording? result = null;
        try
        {
            result = await _recorder.StopAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"The recording couldn't be saved: {ex.Message}", "Toastshot", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            await _recorder.DisposeAsync();
        }

        End(result is { FrameCount: > 0 } ? result : null);
    }

    private async Task CancelAsync()
    {
        if (_ending)
        {
            return;
        }

        _ending = true;
        _clock.Stop();
        if (_recorder is not null)
        {
            try
            {
                var result = await _recorder.StopAsync();
                TempFiles.TryDelete(result.Path);
            }
            catch (Exception)
            {
                // Cancelled anyway; nothing to keep.
            }

            await _recorder.DisposeAsync();
        }

        End(null);
    }

    private void End(GifRecording? result)
    {
        _clock.Stop();
        _frame.Close();
        _bar.Close();
        Ended?.Invoke(this, result);
    }
}
