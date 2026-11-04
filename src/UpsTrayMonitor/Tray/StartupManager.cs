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
    private const string ApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "UpsTrayMonitor";

    /// <summary>
    /// True when our Run-key value is present AND not disabled externally
    /// (Task Manager / Settings move entries to a "StartupApproved" marker
    /// with a leading 0x03 byte instead of deleting the Run value).
    /// </summary>
    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? run = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            if (run?.GetValue(ValueName) is null)
            {
                return false;
            }

            using RegistryKey? approved = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath);
            if (approved?.GetValue(ValueName) is byte[] flag && flag.Length > 0 && flag[0] == 0x03)
            {
                return false; // Disabled in Task Manager / Settings.
            }

            return true; // No marker means enabled.
        }
        catch
        {
            // Unreadable registry - treat as "not registered".
            return false;
        }
    }

    /// <summary>
    /// Registers (or unregisters) the current executable in the Run key.
    /// Enabling also clears any external "disabled" marker, otherwise the
    /// entry would stay blocked by Task Manager. Throws if the registry
    /// cannot be written; callers must revert the UI.
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

            // Absence of a StartupApproved marker means "enabled"; removing our
            // disabled marker is enough. If the key doesn't exist, nothing blocks us.
            using RegistryKey? approved = Registry.CurrentUser.OpenSubKey(
                ApprovedKeyPath, RegistryKeyPermissionCheck.ReadWriteSubTree);
            approved?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
