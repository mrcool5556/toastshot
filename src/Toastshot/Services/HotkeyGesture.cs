using System.Windows.Input;

namespace Toastshot.Services;

/// <summary>A global hotkey such as "Ctrl+Alt+S", convertible to RegisterHotKey arguments.</summary>
public readonly record struct HotkeyGesture(ModifierKeys Modifiers, Key Key)
{
    public const uint MOD_ALT = 0x1;
    public const uint MOD_CONTROL = 0x2;
    public const uint MOD_SHIFT = 0x4;
    public const uint MOD_WIN = 0x8;
    public const uint MOD_NOREPEAT = 0x4000;

    public uint NativeModifiers =>
        (Modifiers.HasFlag(ModifierKeys.Alt) ? MOD_ALT : 0) |
        (Modifiers.HasFlag(ModifierKeys.Control) ? MOD_CONTROL : 0) |
        (Modifiers.HasFlag(ModifierKeys.Shift) ? MOD_SHIFT : 0) |
        (Modifiers.HasFlag(ModifierKeys.Windows) ? MOD_WIN : 0) |
        MOD_NOREPEAT;

    public int VirtualKey => KeyInterop.VirtualKeyFromKey(Key);

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    /// <summary>Parses "Ctrl+Shift+F9" style text. Requires at least one modifier, except for function keys.</summary>
    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var modifiers = ModifierKeys.None;
        Key? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= ModifierKeys.Control;
                    break;
                case "alt":
                    modifiers |= ModifierKeys.Alt;
                    break;
                case "shift":
                    modifiers |= ModifierKeys.Shift;
                    break;
                case "win" or "windows":
                    modifiers |= ModifierKeys.Windows;
                    break;
                default:
                    if (key is not null || ParseKey(raw) is not { } parsed)
                    {
                        return false;
                    }

                    key = parsed;
                    break;
            }
        }

        if (key is not { } k)
        {
            return false;
        }

        var isFunctionKey = k is >= Key.F1 and <= Key.F24;
        if (modifiers == ModifierKeys.None && !isFunctionKey && k != Key.PrintScreen)
        {
            return false;
        }

        gesture = new HotkeyGesture(modifiers, k);
        return true;
    }

    private static Key? ParseKey(string text)
    {
        if (text.Length == 1 && char.IsDigit(text[0]))
        {
            return Key.D0 + (text[0] - '0');
        }

        if (text.Equals("PrintScreen", StringComparison.OrdinalIgnoreCase) || text.Equals("PrtSc", StringComparison.OrdinalIgnoreCase))
        {
            return Key.PrintScreen;
        }

        return Enum.TryParse<Key>(text, ignoreCase: true, out var key) && key is not (Key.None or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            ? key
            : null;
    }

    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        Key.PrintScreen => "PrintScreen",
        _ => key.ToString()
    };
}
