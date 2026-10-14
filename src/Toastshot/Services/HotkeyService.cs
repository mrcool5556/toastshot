using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Toastshot.Services;

/// <summary>Registers system-wide hotkeys on a hidden message-only window.</summary>
public sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _handlers = new();
    private int _nextId = 0xB100;

    public HotkeyService()
    {
        _source = new HwndSource(new HwndSourceParameters("Toastshot hotkeys") { ParentWindow = HWND_MESSAGE, Width = 0, Height = 0 });
        _source.AddHook(WndProc);
    }

    /// <summary>Returns false if another app already owns the combination.</summary>
    public bool TryRegister(HotkeyGesture gesture, Action onPressed, out int id)
    {
        id = _nextId++;
        if (!RegisterHotKey(_source.Handle, id, gesture.NativeModifiers, (uint)gesture.VirtualKey))
        {
            return false;
        }

        _handlers[id] = onPressed;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _handlers.Keys)
        {
            UnregisterHotKey(_source.Handle, id);
        }

        _handlers.Clear();
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _handlers.TryGetValue(wParam.ToInt32(), out var handler))
        {
            handled = true;
            // Run after the message is handled: actions open modal overlays, which must not nest inside WndProc.
            _source.Dispatcher.BeginInvoke(handler);
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
