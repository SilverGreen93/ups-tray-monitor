namespace UpSTrayMonitor;

/// <summary>Overall UPS state as seen by the monitor.</summary>
public enum UpsStatus
{
    Unknown = 0,
    Online = 1,      // Mains power present, UPS healthy
    OnBattery = 2,   // Mains lost, UPS running on battery
    Critical = 3     // Battery critically low / failing
}

/// <summary>
/// A raw sample produced by a single data source.
/// Fields left as <c>null</c> mean "this source does not report it".
/// </summary>
public sealed record UpsState
{
    public UpsStatus Status { get; set; } = UpsStatus.Unknown;

    /// <summary>Battery charge percentage (0-100).</summary>
    public int? BatteryPercent { get; set; }

    /// <summary>Wall (input/mains) voltage in volts, if reported.</summary>
    public double? InputVoltage { get; set; }

    /// <summary>Output load as a percentage of rated capacity (0-100).</summary>
    public int? LoadPercent { get; set; }

    /// <summary>Current output (active) power in watts, if reported.</summary>
    public double? OutputWatts { get; set; }

    /// <summary>Output apparent power in VA, only set when the UPS gives no real watts.</summary>
    public double? OutputVa { get; set; }

    /// <summary>Rated/maximum active power in watts declared by the UPS, if any.</summary>
    public double? RatedWatts { get; set; }

    /// <summary>Rated/maximum apparent power in VA declared by the UPS, if any.</summary>
    public double? RatedVa { get; set; }

    /// <summary>Estimated remaining runtime in minutes, if reported.</summary>
    public int? RuntimeMinutes { get; set; }

    /// <summary>Battery pack voltage in volts, if reported.</summary>
    public double? BatteryVoltage { get; set; }

    /// <summary>UPS model / vendor name, if known.</summary>
    public string? Model { get; set; }

    /// <summary>Extra label/value pairs for the console output (rated power, frequency, ...).</summary>
    public List<(string Label, string Value)> Details { get; } = new();
}

/// <summary>A merged sample with bookkeeping info for the UI.</summary>
public sealed record UpsSample
{
    public required UpsStatus Status { get; init; }
    public int? BatteryPercent { get; init; }
    public double? InputVoltage { get; init; }
    public int? LoadPercent { get; init; }
    public double? OutputWatts { get; init; }
    public double? OutputVa { get; init; }
    public double? RatedWatts { get; init; }
    public double? RatedVa { get; init; }
    public int? RuntimeMinutes { get; init; }
    public double? BatteryVoltage { get; init; }
    public string? Model { get; init; }

    /// <summary>Extra label/value pairs reported by the sources, in display order.</summary>
    public IReadOnlyList<(string Label, string Value)> Details { get; init; } = Array.Empty<(string, string)>();

    /// <summary>Names of the data sources that contributed (e.g. "HID + WMI").</summary>
    public string Provider { get; init; } = "none";

    public DateTime Timestamp { get; init; } = DateTime.MinValue;
}
