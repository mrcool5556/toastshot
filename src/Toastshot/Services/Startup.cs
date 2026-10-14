using Microsoft.Win32;

namespace Toastshot.Services;

/// <summary>
/// "Start with Windows": a value under the user's Run key that opens Toastshot in the notification area at sign-in.
/// The registry is the source of truth, so turning it off in Task Manager's Startup apps also shows here.
/// </summary>
public static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Toastshot";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string command && command.Length > 0;
    }

    /// <summary>Turns it on (pointing at this copy of Toastshot) or off.</summary>
    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled && Environment.ProcessPath is { } exe)
        {
            key.SetValue(ValueName, Command(exe));
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>The command Windows runs at sign-in: quietly into the tray, no window.</summary>
    public static string Command(string exe) => $"\"{exe}\" --tray";
}
