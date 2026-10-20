using System.Management;

namespace UpSTrayMonitor.Providers;

/// <summary>
/// Fallback source: the WMI <c>UPS</c> and <c>UPSBattery</c> classes in
/// <c>root\CIMV2\Power</c>. Populated when the UPS (or vendor software)
/// installs a WMI-capable UPS driver. Provides battery %, runtime, load %
/// and model information.
/// </summary>
public sealed class WmiUpsProvider : IUpsProvider
{
    public string Name => "WMI";

    public UpsState? TryGetSample()
    {
        var state = new UpsState();
        bool any = false;

        ManagementScope? scope = null;
        try
        {
            scope = new ManagementScope(new ManagementPath(@"root\CIMV2\Power"));
            scope.Connect();
        }
        catch
        {
            return null;
        }

        try
        {
            using var upsQuery = new ManagementObjectSearcher(scope, new SelectQuery("SELECT * FROM UPS"));
            foreach (ManagementObject obj in upsQuery.Get())
            {
                string? mfr = GetString(obj, "MFR") ?? GetString(obj, "Manufacturer");
                string? model = GetString(obj, "Model");
                if (!string.IsNullOrWhiteSpace(model))
                {
                    state.Model = string.IsNullOrWhiteSpace(mfr) ? model!.Trim() : $"{mfr} {model}".Trim();
                }

                if (GetInt(obj, "LoadPercentage") is int load)
                {
                    state.LoadPercent = Math.Clamp(load, 0, 100);
                    any = true;
                }
            }
        }
        catch
        {
            // No UPS class or access denied - keep going.
        }

        try
        {
            using var batteryQuery = new ManagementObjectSearcher(scope, new SelectQuery("SELECT * FROM UPSBattery"));
            foreach (ManagementObject obj in batteryQuery.Get())
            {
                if (GetInt(obj, "EstimatedChargeRemaining") is int percent)
                {
                    state.BatteryPercent = Math.Clamp(percent, 0, 100);
                    any = true;
                }

                if (GetInt(obj, "EstimatedRunTime") is int seconds && seconds > 0)
                {
                    state.RuntimeMinutes = (int)Math.Round(seconds / 60.0);
                    any = true;
                }
            }
        }
        catch
        {
            // Ignore.
        }

        if (!any)
        {
            return null;
        }

        // WMI does not tell us directly whether we are on battery; the HID
        // provider (preferred) or system power provider will set the status.
        if (state.Status == UpsStatus.Unknown && state.BatteryPercent is > 0)
        {
            state.Status = UpsStatus.Online;
        }

        return state;
    }

    private static object? GetValue(ManagementObject obj, string property)
    {
        try
        {
            return obj[property];
        }
        catch
        {
            // Property missing or unreadable.
            return null;
        }
    }

    private static string? GetString(ManagementObject obj, string property) =>
        GetValue(obj, property) as string;

    private static int? GetInt(ManagementObject obj, string property) =>
        GetValue(obj, property) is IConvertible convertible
            ? Convert.ToInt32(convertible, System.Globalization.CultureInfo.InvariantCulture)
            : null;
}
