using Microsoft.Win32;

namespace UpSTrayMonitor.Tray;

/// <summary>
/// Turns "start at Windows startup" on and off by adding or removing a value
/// under HKCU\Software\Microsoft\Windows\CurrentVersion\Run (per-user, so no
/// admin rights needed).
/// </summary>
internal static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "UpsTrayMonitor";

    /// <summary>True when our Run-key value is present.</summary>
    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is not null;
        }
        catch
        {
            // Unreadable registry - treat as "not registered".
            return false;
        }
    }

    /// <summary>
    /// Registers (or unregisters) the current executable in the Run key.
    /// Throws if the registry cannot be written; callers must revert the UI.
    /// </summary>
    public static void SetEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(
            RunKeyPath, RegistryKeyPermissionCheck.ReadWriteSubTree);

        if (enabled)
        {
            string exe = Environment.ProcessPath
                         ?? throw new InvalidOperationException("Cannot determine the executable path.");
            key.SetValue(ValueName, $"\"{exe}\"", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
