using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;

namespace Toastshot.Services;

/// <summary>
/// A low-level keyboard hook that sees Toastshot's hotkeys even while a game is in front. Many games (and anti-cheat
/// systems) tell Windows not to deliver app hotkeys while they have focus; a low-level hook still sees the keys.
/// Keys are passed on untouched. Must be created on a thread with a message loop (the UI thread).
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYUP = 0x0105;

    private readonly HookProc _callback; // kept alive: Windows calls it for as long as the hook exists
    private readonly Dispatcher _dispatcher;
    private readonly HashSet<int> _down = [];
    private IntPtr _hook;
    private (HotkeyGesture Gesture, Action Action)[] _gestures = [];

    public KeyboardHook()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _callback = OnKey;
        using var module = Process.GetCurrentProcess().MainModule;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _callback, GetModuleHandle(module?.ModuleName), 0);
    }

    public bool IsInstalled => _hook != IntPtr.Zero;

    /// <summary>The gestures to watch and what each one does (run on the UI thread).</summary>
    public void Watch(IEnumerable<(HotkeyGesture Gesture, Action Action)> gestures) => _gestures = gestures.ToArray();

    private IntPtr OnKey(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var vk = Marshal.ReadInt32(data);
            var msg = (int)message;
            if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
            {
                // Only the first press, not auto-repeat while the key is held.
                if (_down.Add(vk))
                {
                    var modifiers = CurrentModifiers();
                    foreach (var (gesture, action) in _gestures)
                    {
                        if (gesture.VirtualKey == vk && gesture.Modifiers == modifiers)
                        {
                            _dispatcher.BeginInvoke(action); // never do work inside the hook: Windows times it out
                        }
                    }
                }
            }
            else if (msg is WM_KEYUP or WM_SYSKEYUP)
            {
                _down.Remove(vk);
            }
        }

        return CallNextHookEx(_hook, code, message, data);
    }

    private static ModifierKeys CurrentModifiers()
    {
        static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
        var modifiers = ModifierKeys.None;
        if (Down(0x11)) modifiers |= ModifierKeys.Control; // VK_CONTROL
        if (Down(0x12)) modifiers |= ModifierKeys.Alt;     // VK_MENU
        if (Down(0x10)) modifiers |= ModifierKeys.Shift;   // VK_SHIFT
        if (Down(0x5B) || Down(0x5C)) modifiers |= ModifierKeys.Windows;
        return modifiers;
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
}
