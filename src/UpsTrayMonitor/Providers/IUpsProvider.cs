namespace UpSTrayMonitor.Providers;

/// <summary>
/// A source of UPS telemetry. Implementations must never throw:
/// return <c>null</c> when no usable data is available.
/// </summary>
public interface IUpsProvider
{
    /// <summary>Short name used in UI / diagnostics (e.g. "HID").</summary>
    string Name { get; }

    /// <summary>
    /// Try to read the current UPS state.
    /// Returns <c>null</c> if this source has nothing useful to report.
    /// </summary>
    UpsState? TryGetSample();
}
