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

    /// <summary>Power value color while the user's manual max-power override is in use.</summary>
    public static readonly Color PowerOverride = Color.FromArgb(0xFF, 0x9F, 0x43); // orange

    public static readonly Color Link = Color.FromArgb(0x6C, 0xB6, 0xFF); // light blue, for link labels

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

        if (sample.PowerText() is string power)
        {
            lines.Add($"Power: {power}");
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
    /// <summary>
    /// Output power, always in watts, using this precedence:
    /// UPS-measured active power; then load % x rated power, where the user's
    /// manually set maximum power (popup: double-click the Power value) replaces
    /// the UPS's rated value; then measured apparent power (VA) converted at a
    /// 0.6 power factor (typical for desktop/UPS loads); then load % x rated VA
    /// with the same conversion.
    /// </summary>
    public static string? PowerText(this UpsSample sample)
    {
        if (sample.OutputWatts is > 0)
        {
            return $"{sample.OutputWatts:0} W";
        }

        if (EffectiveRatedWatts(sample) is > 0 and double rated && sample.LoadPercent is int load)
        {
            return $"{rated * load / 100.0:0} W";
        }

        if (sample.OutputVa is > 0)
        {
            return $"{sample.OutputVa * 0.6:0} W";
        }

        if (sample.RatedVa is > 0 && sample.LoadPercent is int loadVa)
        {
            return $"{sample.RatedVa * 0.6 * loadVa / 100.0:0} W";
        }

        return sample.OutputWatts is null ? null : $"{sample.OutputWatts:0} W";
    }

    /// <summary>True while the displayed Power is derived from the user's manual max-power override.</summary>
    public static bool PowerUsesOverride(this UpsSample sample) =>
        AppSettings.Current.MaxPowerWatts > 0
        && sample.OutputWatts is not > 0
        && sample.LoadPercent != null;

    /// <summary>The rated (maximum) watts used for the load % estimate; the user's override replaces the UPS value.</summary>
    private static double? EffectiveRatedWatts(UpsSample sample) =>
        AppSettings.Current.MaxPowerWatts > 0 ? AppSettings.Current.MaxPowerWatts : sample.RatedWatts;

    public static bool HasData(this UpsSample sample) =>
        sample.Status != UpsStatus.Unknown
        || sample.BatteryPercent != null
        || sample.InputVoltage != null
        || sample.LoadPercent != null
        || sample.OutputWatts != null
        || sample.OutputVa != null
        || sample.RuntimeMinutes != null;
}
