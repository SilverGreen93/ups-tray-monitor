using System.Drawing;

namespace UpSTrayMonitor.Tray;

/// <summary>Shared formatting/colors for the tray tooltip and popup window.</summary>
public static class UpsPresentation
{
    // Palette (dark theme)
    public static readonly Color WindowBackground = Color.FromArgb(0x14, 0x17, 0x1E);
    public static readonly Color CardBackground = Color.FromArgb(0x1D, 0x22, 0x2D);
    public static readonly Color TextPrimary = Color.FromArgb(0xEA, 0xEE, 0xF5);
    public static readonly Color TextSecondary = Color.FromArgb(0x98, 0xA2, 0xB5);

    public static readonly Color Online = Color.FromArgb(0x34, 0xC7, 0x59);    // green
    public static readonly Color OnBattery = Color.FromArgb(0xF5, 0x9E, 0x0B); // amber
    public static readonly Color Critical = Color.FromArgb(0xE5, 0x48, 0x4D); // red
    public static readonly Color Unknown = Color.FromArgb(0x7D, 0x87, 0x99); // gray

    public static string StatusText(UpsSample? sample)
    {
        if (sample == null)
        {
            return "No data";
        }

        return sample.Status switch
        {
            UpsStatus.Online => "On line",
            UpsStatus.OnBattery => "On battery",
            UpsStatus.Critical => "Critical",
            _ => sample.HasData() ? "Online" : "No data",
        };
    }

    public static Color StatusColor(UpsSample? sample)
    {
        if (sample == null)
        {
            return Unknown;
        }

        return sample.Status switch
        {
            UpsStatus.Online => Online,
            UpsStatus.OnBattery => OnBattery,
            UpsStatus.Critical => Critical,
            _ => sample.HasData() ? Online : Unknown,
        };
    }

    /// <summary>Tooltip text shown on hover (NotifyIcon limit: 128 chars).</summary>
    public static string BuildTooltip(UpsSample? sample)
    {
        if (sample == null || !sample.HasData())
        {
            return "UPS Monitor: no UPS data detected";
        }

        var lines = new List<string>(6)
        {
            "UPS: " + StatusText(sample),
        };

        if (sample.BatteryPercent != null)
        {
            lines.Add($"Battery: {sample.BatteryPercent}%");
        }

        if (sample.InputVoltage != null)
        {
            lines.Add($"Input: {sample.InputVoltage:0.#} V");
        }

        if (sample.BatteryVoltage != null)
        {
            lines.Add($"Battery: {sample.BatteryVoltage:0.#} V");
        }

        if (sample.LoadPercent != null)
        {
            lines.Add($"Load: {sample.LoadPercent}%");
        }

        if (sample.OutputWatts != null)
        {
            lines.Add($"Power: {sample.OutputWatts:0} W");
        }

        if (sample.RuntimeMinutes != null)
        {
            lines.Add($"Runtime: ~{sample.RuntimeMinutes} min");
        }

        string text = string.Join("\n", lines);
        return text.Length <= 128 ? text : text[..127] + "…";
    }
}

/// <summary>Extension helpers for samples.</summary>
public static class SampleExtensions
{
    public static bool HasData(this UpsSample sample) =>
        sample.Status != UpsStatus.Unknown
        || sample.BatteryPercent != null
        || sample.InputVoltage != null
        || sample.LoadPercent != null
        || sample.OutputWatts != null
        || sample.RuntimeMinutes != null;
}
