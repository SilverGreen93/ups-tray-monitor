using System.Text.Json;

namespace UpSTrayMonitor;

/// <summary>
/// User preferences persisted across runs in %APPDATA%\ups-tray-monitor\settings.json.
/// Loading/saving is best-effort: on any problem we fall back to defaults
/// rather than crashing a tray app that has no console to report errors to.
/// </summary>
internal sealed class AppSettings
{
    public const int MaxPowerLimitWatts = 100_000;

    /// <summary>
    /// Manually set maximum (rated) UPS output power in watts. When the UPS
    /// reports neither watts nor VA, Power is then estimated as
    /// MaxPowerWatts x Load% / 100. 0 means "no override" - show whatever the
    /// UPS itself reports.
    /// </summary>
    public int MaxPowerWatts { get; set; }

    /// <summary>The settings in effect for this run, loaded once at startup.</summary>
    public static AppSettings Current { get; } = Load();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ups-tray-monitor",
        "settings.json");

    private static AppSettings Load()
    {
        try
        {
            var settings = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath))
                : null;
            settings ??= new AppSettings();
            if (settings.MaxPowerWatts is < 0 or > MaxPowerLimitWatts)
            {
                settings.MaxPowerWatts = 0;
            }

            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Settings are best-effort; the app works fine with defaults.
        }
    }
}
