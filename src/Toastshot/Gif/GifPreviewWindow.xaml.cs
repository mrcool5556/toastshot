using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Toastshot.Services;
using Microsoft.Win32;

namespace Toastshot.Gif;

/// <summary>Plays a recorded (or opened) GIF with Save and Copy. Recordings live in a temp file until saved.</summary>
public partial class GifPreviewWindow : Window
{
    private const string NotSaved = "Not saved yet. Save or copy it before closing.";

    /// <summary>A size choice: fit a file size, or scale the picture; neither means the original.</summary>
    private sealed record SizeOption(string Label, long? MaxBytes, double? Scale)
    {
        public override string ToString() => Label;
    }

    private readonly AppController _app;
    private readonly string _originalPath;
    private readonly bool _isRecording;
    private readonly List<string> _shrunkFiles = [];
    private readonly List<SizeOption> _sizes =
    [
        new("Original", null, null),
        new("Under 5 MB", 5L << 20, null),
        new("Under 10 MB", 10L << 20, null),
        new("Under 20 MB", 20L << 20, null),
        new("Under 50 MB", 50L << 20, null),
        new("75% size", null, 0.75),
        new("50% size", null, 0.5),
        new("25% size", null, 0.25)
    ];

    private string _path; // the version shown, saved and copied
    private GifPlayer _player;
    private string? _copiedPath;
    private CancellationTokenSource? _shrinking;

    /// <param name="recording">The fresh recording at <paramref name="path"/>, or null when viewing an existing GIF.</param>
    public GifPreviewWindow(AppController app, string path, GifRecording? recording)
    {
        InitializeComponent();
        _app = app;
        _originalPath = _path = path;
        _isRecording = recording is not null;
        _player = new GifPlayer(path);
        ShowPlayer();
        if (!_isRecording)
        {
            Title = $"Toastshot — {Path.GetFileName(path)}";
            Status.Text = path;
        }

        // New recordings start at the size limit from Settings; opened files start as they are.
        var limit = app.Settings.GifMaxMegabytes;
        var start = _sizes[0];
        if (_isRecording && limit > 0)
        {
            start = _sizes.FirstOrDefault(o => o.MaxBytes == (long)limit << 20) ?? new SizeOption($"Under {limit} MB", (long)limit << 20, null);
            if (!_sizes.Contains(start))
            {
                _sizes.Insert(1, start);
            }
        }

        SizeBox.ItemsSource = _sizes;
        SizeBox.SelectedItem = start;
        SizeBox.SelectionChanged += async (_, _) => await ApplySizeAsync();
        if (start != _sizes[0])
        {
            Loaded += async (_, _) => await ApplySizeAsync();
        }

        SourceInitialized += (_, _) =>
        {
            // Show at real pixel size on high-DPI screens.
            var (scaleX, scaleY) = NativeWindows.DpiScale(this);
            Player.LayoutTransform = new ScaleTransform(1 / scaleX, 1 / scaleY);
        };
        Loaded += (_, _) => _player.Play();
        PreviewKeyDown += OnKey;
        Closed += OnClosed;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var folder = _app.Settings.LastSaveFolder;
        var dialog = new SaveFileDialog
        {
            Filter = "GIF animation (*.gif)|*.gif",
            FileName = _isRecording ? ImageExport.SuggestedFileName(".gif") : Path.GetFileName(_originalPath),
            InitialDirectory = folder is not null && Directory.Exists(folder)
                ? folder
                : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            if (!string.Equals(Path.GetFullPath(dialog.FileName), Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(_path, dialog.FileName, overwrite: true);
            }

            _app.SavePreference(s => s.LastSaveFolder = Path.GetDirectoryName(dialog.FileName));
            Status.Text = $"Saved {Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status.Text = $"Couldn't save: {ex.Message}";
        }
    }

    /// <summary>Saves the recording as an MP4: full size and frame rate (made from the original, not a shrunk GIF).</summary>
    private async void OnSaveMp4(object sender, RoutedEventArgs e)
    {
        var folder = _app.Settings.LastSaveFolder;
        var dialog = new SaveFileDialog
        {
            Filter = "MP4 video (*.mp4)|*.mp4",
            FileName = _isRecording ? ImageExport.SuggestedFileName(".mp4") : Path.ChangeExtension(Path.GetFileName(_originalPath), ".mp4"),
            InitialDirectory = folder is not null && Directory.Exists(folder)
                ? folder
                : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        Mp4Button.IsEnabled = false;
        Status.Text = "Making the MP4…";
        try
        {
            var result = await Task.Run(() => Mp4Exporter.ExportAsync(_originalPath, dialog.FileName));
            _app.SavePreference(s => s.LastSaveFolder = Path.GetDirectoryName(dialog.FileName));
            Status.Text = $"Saved {Path.GetFileName(dialog.FileName)} ({FormatBytes(result.FileBytes)}, {result.Width} × {result.Height}).";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Status.Text = $"Couldn't make the MP4: {ex.Message}";
        }
        finally
        {
            Mp4Button.IsEnabled = true;
        }
    }

    private async void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            await ImageExport.CopyFileAsync(_path);
            // Paste reads the file later, so keep that temp file (cleaned up after a day).
            _copiedPath = _path;
            Status.Text = "GIF copied. Paste it into a chat, browser or folder.";
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            Status.Text = ImageExport.BusyMessage(ex);
        }
    }

    private void OnPause(object sender, RoutedEventArgs e)
    {
        if ((string)PauseButton.Content == "Pause")
        {
            _player.Pause();
            PauseButton.Content = "Play";
        }
        else
        {
            _player.Play();
            PauseButton.Content = "Pause";
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            return;
        }

        if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            OnSaveMp4(this, e);
            e.Handled = true;
        }
        else if (e.Key == Key.S)
        {
            OnSave(this, e);
            e.Handled = true;
        }
        else if (e.Key == Key.C)
        {
            OnCopy(this, e);
            e.Handled = true;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _shrinking?.Cancel();
        _player.Dispose();
        var temp = _isRecording ? _shrunkFiles.Append(_originalPath) : _shrunkFiles;
        foreach (var file in temp.Where(f => f != _copiedPath))
        {
            TempFiles.TryDelete(file);
        }
    }

    // ---------- Size ----------

    /// <summary>Makes the GIF fit the chosen size, in the background, then shows the result.</summary>
    private async Task ApplySizeAsync()
    {
        _shrinking?.Cancel();
        if (SizeBox.SelectedItem is not SizeOption option)
        {
            return;
        }

        if (option.MaxBytes is null && option.Scale is null)
        {
            Show(_originalPath);
            Status.Text = _isRecording ? NotSaved : _originalPath;
            return;
        }

        var cancel = new CancellationTokenSource();
        _shrinking = cancel;
        var output = TempFiles.NewPath(".gif");
        _shrunkFiles.Add(output);
        var originalBytes = new FileInfo(_originalPath).Length;
        Status.Text = option.MaxBytes is { } max ? $"Shrinking to under {FormatBytes(max)}…" : $"Resizing to {option.Label}…";
        try
        {
            var result = await Task.Run(() => option.MaxBytes is { } m
                ? GifShrinker.FitToSize(_originalPath, m, output, cancel.Token)
                : GifShrinker.Resize(_originalPath, option.Scale!.Value, output, cancel.Token), cancel.Token);
            if (cancel.IsCancellationRequested)
            {
                return;
            }

            if (result is null)
            {
                Show(_originalPath);
                Status.Text = $"Already under {FormatBytes(option.MaxBytes!.Value)}. " + (_isRecording ? NotSaved : "");
                return;
            }

            Show(result.Path);
            var missed = option.MaxBytes is { } limit && result.FileBytes > limit ? $" It couldn't get under {FormatBytes(limit)}." : "";
            Status.Text = $"Shrunk from {FormatBytes(originalBytes)}.{missed} " + (_isRecording ? NotSaved : "");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or FileFormatException)
        {
            Status.Text = $"Couldn't shrink the GIF: {ex.Message}";
        }
    }

    /// <summary>Switches the player (and Save/Copy) to another version of the GIF.</summary>
    private void Show(string path)
    {
        if (path == _path)
        {
            return;
        }

        var playing = (string)PauseButton.Content == "Pause";
        _player.Dispose();
        _player = new GifPlayer(path);
        _path = path;
        ShowPlayer();
        if (playing)
        {
            _player.Play();
        }
    }

    private void ShowPlayer()
    {
        Player.Source = _player.Source;
        Player.Width = _player.Width;
        Player.Height = _player.Height;
        var fps = _player.Duration.TotalSeconds > 0 ? _player.FrameCount / _player.Duration.TotalSeconds : 0;
        Details.Text = $"{_player.Width} × {_player.Height} · {fps:0} fps · {_player.Duration.TotalSeconds:0.0} s · {FormatBytes(new FileInfo(_path).Length)}";
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:0.0} MB" : $"{Math.Max(1, bytes / 1024)} KB";
}
