namespace UpSTrayMonitor.Providers;

/// <summary>
/// Polls all known data sources and merges them into a single sample.
/// Provider order defines priority (first provider wins per field; the first
/// provider that reports a definite status defines the status).
/// </summary>
public sealed class UpsMonitor
{
    private readonly List<IUpsProvider> _providers;

    public UpsMonitor()
    {
        _providers = new List<IUpsProvider>
        {
            new HidUpsProvider(),
            new WmiUpsProvider(),
            new SystemPowerProvider(),
        };
    }

    public IEnumerable<string> ProviderNames => _providers.Select(p => p.Name);

    /// <summary>Reads one merged sample. Never throws.</summary>
    public UpsSample GetSample()
    {
        UpsState? best = null;
        var usedProviders = new List<string>();

        foreach (IUpsProvider provider in _providers)
        {
            UpsState? sample;
            try
            {
                sample = provider.TryGetSample();
            }
            catch
            {
                sample = null;
            }

            if (sample == null)
            {
                continue;
            }

            usedProviders.Add(provider.Name);

            if (best == null)
            {
                best = sample;
                continue;
            }

            best = best with
            {
                Status = best.Status != UpsStatus.Unknown ? best.Status : sample.Status,
                BatteryPercent = best.BatteryPercent ?? sample.BatteryPercent,
                InputVoltage = best.InputVoltage ?? sample.InputVoltage,
                LoadPercent = best.LoadPercent ?? sample.LoadPercent,
                OutputWatts = best.OutputWatts ?? sample.OutputWatts,
                RuntimeMinutes = best.RuntimeMinutes ?? sample.RuntimeMinutes,
                Model = best.Model ?? sample.Model,
            };
        }

        best ??= new UpsState();

        return new UpsSample
        {
            Status = best.Status,
            BatteryPercent = best.BatteryPercent,
            InputVoltage = best.InputVoltage,
            LoadPercent = best.LoadPercent,
            OutputWatts = best.OutputWatts,
            RuntimeMinutes = best.RuntimeMinutes,
            Model = best.Model,
            Provider = usedProviders.Count > 0 ? string.Join(" + ", usedProviders) : "none",
            Timestamp = DateTime.Now,
        };
    }
}
