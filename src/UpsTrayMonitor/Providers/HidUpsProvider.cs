using System.Runtime.InteropServices;
using System.Text;

namespace UpSTrayMonitor.Providers;

/// <summary>
/// Reads a UPS that is exposed as a USB HID device (the standard way most
/// vendors - APC, CyberPower, Eaton, Tripp Lite, ... - expose UPS telemetry).
///
/// UPS data lives on two standard HID usage pages:
///   0x84  Power Device   (voltage, load, power, ...)
///   0x85  Battery System (charge, runtime, charging/discharging flags, ...)
///
/// Most of it is published through *feature* reports, so the provider parses
/// the report descriptor (HidP_GetValueCaps / HidP_GetButtonCaps), fetches
/// each report with HidD_GetFeature and decodes the fields with HidP_*.
/// The device handle is kept open so polling every few seconds is cheap.
/// </summary>
public sealed class HidUpsProvider : IUpsProvider, IDisposable
{
    public string Name => "HID";

    private UpsHidDevice? _device;

    // ------------------------------------------------------------------ API

    public UpsState? TryGetSample()
    {
        try
        {
            _device ??= UpsHidDevice.OpenFirstUps();
            if (_device == null)
            {
                return null;
            }

            List<FieldReading>? readings = ReadStable(_device);
            if (readings == null)
            {
                // Device unplugged or stopped answering: re-enumerate next poll.
                Release();
                return null;
            }

            return Map(readings, _device.Model);
        }
        catch
        {
            Release();
            return null;
        }
    }

    public void Dispose() => Release();

    // CyberPower firmware intermittently answers the load query with a bogus 0
    // (about 1 read in 6), which also zeroes the derived power. Re-read a few
    // times, and if it still says 0 keep the previous value for a short while;
    // a load that really is 0 is accepted after a few consecutive zeros.
    private const int LoadRetries = 3;
    private const int MaxHeldZeroPolls = 2;
    private double _lastLoad;
    private int _zeroLoadPolls;

    private List<FieldReading>? ReadStable(UpsHidDevice device)
    {
        List<FieldReading>? readings = null;
        FieldReading? load = null;
        for (int attempt = 0; attempt < LoadRetries; attempt++)
        {
            readings = device.ReadAll();
            if (readings == null)
            {
                return null;
            }

            load = readings.FirstOrDefault(x => x.Field.Page == Usages.PowerPage && x.Field.Usage == Usages.PercentLoad);
            if (load == null || load.Raw != 0)
            {
                break;
            }

            Thread.Sleep(150);
        }

        if (load == null || readings == null)
        {
            return readings;
        }

        if (load.Raw != 0)
        {
            _lastLoad = load.Value;
            _zeroLoadPolls = 0;
        }
        else if (_lastLoad > 0 && ++_zeroLoadPolls <= MaxHeldZeroPolls)
        {
            readings[readings.IndexOf(load)] = load with { Raw = (long)_lastLoad, Value = _lastLoad };
        }
        else
        {
            _lastLoad = 0;
        }

        return readings;
    }

    private void Release()
    {
        _device?.Dispose();
        _device = null;
    }

    // -------------------------------------------------------------- mapping

    private static bool IsBatteryLink(HidField f) =>
        f.LinkUsage is Usages.PowerSummaryCollection or Usages.BatteryCollection;

    /// <summary>
    /// Some firmwares (cheap Cypress-based units) declare a wrong unit exponent in
    /// the report descriptor, so voltages/frequencies come out 10-1000x off. There
    /// is no flag for that, so every non-zero voltage/frequency is sanity-checked
    /// against the range it must be in (using the nominal value when the unit
    /// reports one) and, if implausible, re-scaled by a power of ten until it fits.
    /// </summary>
    private static void NormalizeScales(List<FieldReading> r)
    {
        double? Nominal(ushort usage, Func<HidField, bool> link) =>
            r.FirstOrDefault(x => x.Field.Page == Usages.PowerPage && x.Field.Usage == usage
                                  && link(x.Field) && x.Value > 0)?.Value;

        double? batNominal = Nominal(Usages.ConfigVoltage, IsBatteryLink);
        double? freqNominal = Nominal(Usages.ConfigFrequency, _ => true);

        for (int i = 0; i < r.Count; i++)
        {
            FieldReading x = r[i];
            if (x.Field.IsButton || x.Field.Page != Usages.PowerPage || x.Raw == 0)
            {
                continue;
            }

            (double Min, double Max)? range = x.Field.Usage switch
            {
                Usages.Voltage or Usages.LowVoltageTransfer or Usages.HighVoltageTransfer
                    when IsBatteryLink(x.Field) => batNominal is double n ? (n * 0.3, n * 1.8) : (1, 150),
                Usages.Voltage or Usages.LowVoltageTransfer or Usages.HighVoltageTransfer => (50, 330),
                Usages.Frequency => freqNominal is double f ? (f * 0.8, f * 1.2) : (40, 70),
                _ => null,
            };

            if (range is not var (min, max) || (x.Value >= min && x.Value <= max))
            {
                continue;
            }

            foreach (double divisor in new[] { 10.0, 1.0, 100.0, 1000.0 })
            {
                double candidate = x.Raw / divisor;
                if (candidate >= min && candidate <= max)
                {
                    r[i] = x with { Value = candidate };
                    break;
                }
            }
        }
    }

    private static UpsState? Map(List<FieldReading> r, string? model)
    {
        var state = new UpsState { Model = model };
        bool any = false;

        FieldReading? Find(ushort page, ushort usage, ushort? linkUsage = null) =>
            r.FirstOrDefault(x => x.Field.Page == page && x.Field.Usage == usage
                                  && (linkUsage == null || x.Field.LinkUsage == linkUsage));

        // ---- battery
        var charge = Find(Usages.BatteryPage, Usages.RemainingCapacity) ?? Find(Usages.BatteryPage, Usages.RelativeStateOfCharge);
        if (charge != null)
        {
            state.BatteryPercent = (int)Math.Clamp(Math.Round(charge.Value), 0, 100);
            any = true;
        }

        var runtime = Find(Usages.BatteryPage, Usages.RunTimeToEmpty) ?? Find(Usages.BatteryPage, Usages.AverageTimeToEmpty);
        if (runtime != null && runtime.Raw is > 0 and < 0xFFFF)
        {
            state.RuntimeMinutes = (int)Math.Round(runtime.Value / 60.0); // spec: seconds
            any = true;
        }

        // ---- status
        bool? discharging = Find(Usages.BatteryPage, Usages.Discharging)?.Flag;
        bool? charging = Find(Usages.BatteryPage, Usages.Charging)?.Flag;
        bool? acPresent = Find(Usages.BatteryPage, Usages.ACPresent)?.Flag;
        bool lowBattery = Find(Usages.BatteryPage, Usages.BelowRemainingCapacityLimit)?.Flag == true
                          || Find(Usages.PowerPage, Usages.ShutdownImminent)?.Flag == true;

        bool? onBattery = discharging ?? (acPresent.HasValue ? !acPresent.Value : null);
        if (onBattery == true)
        {
            state.Status = lowBattery || state.BatteryPercent == 0 ? UpsStatus.Critical : UpsStatus.OnBattery;
            any = true;
        }
        else if (onBattery == false || charging == true)
        {
            state.Status = UpsStatus.Online;
            any = true;
        }
        else if (state.BatteryPercent is > 0)
        {
            state.Status = UpsStatus.Online;
        }

        // ---- electrical
        var vin = Find(Usages.PowerPage, Usages.Voltage, Usages.InputCollection)
                  ?? r.FirstOrDefault(x => x.Field.Page == Usages.PowerPage && x.Field.Usage == Usages.Voltage
                                           && x.Field.LinkUsage != Usages.OutputCollection && !IsBatteryLink(x.Field));
        if (vin != null)
        {
            state.InputVoltage = vin.Value;
            any = true;
        }

        var load = Find(Usages.PowerPage, Usages.PercentLoad);
        if (load != null)
        {
            state.LoadPercent = (int)Math.Clamp(Math.Round(load.Value), 0, 100);
            any = true;
        }

        // Measured power only. The UPS's rated power is reported as-is in
        // RatedWatts/RatedVa; the load % x rated estimate happens in the UI
        // (UpsPresentation), where the user's manual max-power override applies.
        double? watts = Find(Usages.PowerPage, Usages.ActivePower)?.Value;
        double? va = watts is null or 0 ? Find(Usages.PowerPage, Usages.ApparentPower)?.Value : null;

        if (watts is > 0 || (watts != null && va is not > 0))
        {
            state.OutputWatts = watts;
            any = true;
        }
        else if (va != null)
        {
            state.OutputVa = va;
            any = true;
        }

        state.RatedWatts = Find(Usages.PowerPage, Usages.ConfigActivePower)?.Value;
        state.RatedVa = Find(Usages.PowerPage, Usages.ConfigApparentPower)?.Value;
        if (state.RatedWatts is > 0 || state.RatedVa is > 0)
        {
            any = true;
        }

        AddDetails(state, r, charging, acPresent);

        return any ? state : null;
    }

    /// <summary>Fills the extra console-only values (rated power, frequency, thresholds, ...).</summary>
    private static void AddDetails(UpsState state, List<FieldReading> r, bool? charging, bool? acPresent)
    {
        FieldReading? Find(ushort page, ushort usage, ushort? link = null) =>
            r.FirstOrDefault(x => x.Field.Page == page && x.Field.Usage == usage && (link == null || x.Field.LinkUsage == link));

        void Add(string label, double? value, string unit, string format = "0.#")
        {
            if (value != null)
            {
                state.Details.Add((label, value.Value.ToString(format) + " " + unit));
            }
        }

        // Output side
        double? outV = Find(Usages.PowerPage, Usages.Voltage, Usages.OutputCollection)?.Value;
        Add("Out volt", outV, "V");
        Add("Out freq", Find(Usages.PowerPage, Usages.Frequency, Usages.OutputCollection)?.Value, "Hz");

        double? outA = Find(Usages.PowerPage, Usages.Current, Usages.OutputCollection)?.Value;
        if (outA != null)
        {
            Add("Current", outA, "A", "0.00");
        }
        else if ((state.OutputWatts ?? state.OutputVa) is > 0 and double power && outV is > 0)
        {
            // The UPS does not report current; derive it from power and voltage.
            state.Details.Add(("Current", $"~{power / outV.Value:0.00} A (estimated)"));
        }

        Add("Rated W", Find(Usages.PowerPage, Usages.ConfigActivePower)?.Value, "W", "0");
        Add("Rated VA", Find(Usages.PowerPage, Usages.ConfigApparentPower)?.Value, "VA", "0");
        Add("App.pwr", Find(Usages.PowerPage, Usages.ApparentPower)?.Value, "VA", "0");

        // Input side
        Add("In freq", Find(Usages.PowerPage, Usages.Frequency, Usages.InputCollection)?.Value, "Hz");
        double? nomIn = Find(Usages.PowerPage, Usages.ConfigVoltage, Usages.InputCollection)?.Value;
        if (nomIn is >= 50 and <= 330) // some firmwares put the battery nominal here
        {
            Add("Nom. in", nomIn, "V", "0");
        }

        double? low = Find(Usages.PowerPage, Usages.LowVoltageTransfer)?.Value;
        double? high = Find(Usages.PowerPage, Usages.HighVoltageTransfer)?.Value;
        if (low != null && high != null)
        {
            state.Details.Add(("Transfer", $"{low:0} V - {high:0} V (switches to battery outside this range)"));
        }

        // Battery side: values in the PowerSummary collection.
        double? batV = r.FirstOrDefault(x => x.Field.Page == Usages.PowerPage && x.Field.Usage == Usages.Voltage && IsBatteryLink(x.Field))?.Value;
        double? batNom = r.FirstOrDefault(x => x.Field.Page == Usages.PowerPage && x.Field.Usage == Usages.ConfigVoltage && IsBatteryLink(x.Field))?.Value;
        if (batV != null)
        {
            state.BatteryVoltage = batV;
            state.Details.Add(("Bat volt", batNom != null ? $"{batV:0.#} V (nominal {batNom:0} V)" : $"{batV:0.#} V"));
        }

        if (charging != null || acPresent != null)
        {
            string text = charging == true ? "charging" : "not charging";
            if (acPresent != null)
            {
                text += acPresent == true ? ", AC present" : ", AC lost";
            }

            state.Details.Add(("Charger", text));
        }

        if (Find(Usages.BatteryPage, Usages.NeedReplacement)?.Flag == true)
        {
            state.Details.Add(("Warning", "battery needs replacement"));
        }
    }

    // ------------------------------------------------------------ diagnostics

    /// <summary>
    /// Lists every HID interface on the machine and, for the ones on the UPS
    /// usage pages, dumps every field with its current value
    /// (run: ups-tray-monitor --dump).
    /// </summary>
    public static void Dump()
    {
        Console.WriteLine("Enumerating HID devices...");
        List<string> paths = Native.EnumerateHidPaths(out string? enumError);
        if (enumError != null)
        {
            Console.WriteLine("  Enumeration error: " + enumError);
        }

        Console.WriteLine($"  {paths.Count} HID interface(s) found.");

        int upsCount = 0;
        foreach (string path in paths)
        {
            UpsHidDevice? dev = UpsHidDevice.TryOpen(path, out string? error);
            if (dev == null)
            {
                Console.WriteLine();
                Console.WriteLine($"  {path}");
                Console.WriteLine($"    (cannot open: {error})");
                continue;
            }

            using (dev)
            {
                Console.WriteLine();
                Console.WriteLine($"  {path}");
                Console.WriteLine($"    VID 0x{dev.VendorId:X4}  PID 0x{dev.ProductId:X4}  \"{dev.Model}\"");
                Console.WriteLine($"    Top-level usage page 0x{dev.TopUsagePage:X2}, usage 0x{dev.TopUsage:X2}"
                                  + $"  | reports: in {dev.InputLength} / feature {dev.FeatureLength} bytes"
                                  + (dev.IsUps ? "  <-- UPS" : ""));

                if (!dev.IsUps)
                {
                    continue;
                }

                upsCount++;
                List<FieldReading>? readings = dev.ReadAll();
                if (readings == null)
                {
                    Console.WriteLine("    Could not read any report from this device.");
                    foreach (HidField f in dev.Fields)
                    {
                        Console.WriteLine($"      {Describe(f)}");
                    }

                    continue;
                }

                foreach (HidField f in dev.Fields)
                {
                    FieldReading? rd = readings.FirstOrDefault(x => x.Field == f);
                    string val = rd == null ? "(not available)"
                        : f.IsButton ? (rd.Flag == true ? "ON" : "off")
                        : rd.Raw == rd.Value ? rd.Raw.ToString() : $"{rd.Raw} -> {rd.Value:0.##}";
                    Console.WriteLine($"      {Describe(f),-62} = {val}");
                }
            }
        }

        Console.WriteLine();
        if (upsCount == 0)
        {
            Console.WriteLine("No HID device on the Power Device (0x84) / Battery System (0x85) usage pages found.");
        }
    }

    private static string Describe(HidField f)
    {
        string name = Usages.Name(f.Page, f.Usage) ?? "?";
        string link = Usages.Name(f.LinkUsagePage, f.LinkUsage) ?? $"0x{f.LinkUsage:X2}";
        string type = f.ReportType == Native.HidpFeature ? "feat" : "in";
        return $"[{type} id {f.ReportId}] 0x{f.Page:X2}/0x{f.Usage:X2} {name} (in {link})";
    }

    // ------------------------------------------------------------------ model

    private sealed record HidField
    {
        public ushort Page { get; init; }
        public ushort Usage { get; init; }
        public byte ReportId { get; init; }
        public int ReportType { get; init; }
        public ushort Link { get; init; }
        public ushort LinkUsage { get; init; }
        public ushort LinkUsagePage { get; init; }
        public bool IsButton { get; init; }
        public int LogicalMin { get; init; }
        public ushort BitSize { get; init; }
        public uint Units { get; init; }
        public int UnitsExp { get; init; }
    }

    private sealed record FieldReading(HidField Field, long Raw, double Value, bool? Flag);

    private static class Usages
    {
        public const ushort PowerPage = 0x84;
        public const ushort BatteryPage = 0x85;

        // Power Device page (0x84)
        public const ushort InputCollection = 0x1A;
        public const ushort OutputCollection = 0x1C;
        public const ushort PowerSummaryCollection = 0x24; // battery-side values
        public const ushort BatteryCollection = 0x12;      // battery-side values (other firmwares)
        public const ushort Voltage = 0x30;
        public const ushort Current = 0x31;
        public const ushort Frequency = 0x32;
        public const ushort ApparentPower = 0x33;
        public const ushort ActivePower = 0x34;
        public const ushort PercentLoad = 0x35;
        public const ushort ConfigVoltage = 0x40;
        public const ushort ConfigFrequency = 0x42;
        public const ushort ConfigApparentPower = 0x43;
        public const ushort ConfigActivePower = 0x44;
        public const ushort LowVoltageTransfer = 0x53;
        public const ushort HighVoltageTransfer = 0x54;
        public const ushort ShutdownImminent = 0x69;

        // Battery System page (0x85)
        public const ushort BelowRemainingCapacityLimit = 0x42;
        public const ushort Charging = 0x44;
        public const ushort Discharging = 0x45;
        public const ushort NeedReplacement = 0x4B;
        public const ushort RelativeStateOfCharge = 0x64;
        public const ushort RemainingCapacity = 0x66;
        public const ushort RunTimeToEmpty = 0x68;
        public const ushort AverageTimeToEmpty = 0x69;
        public const ushort ACPresent = 0xD0;
        public const ushort BatteryPresent = 0xD1;

        public static string? Name(ushort page, ushort usage) => (page, usage) switch
        {
            (PowerPage, 0x04) => "UPS",
            (PowerPage, PowerSummaryCollection) => "PowerSummary",
            (PowerPage, 0x02) => "PresentStatus",
            (PowerPage, InputCollection) => "Input",
            (PowerPage, OutputCollection) => "Output",
            (PowerPage, 0x1E) => "Flow",
            (PowerPage, 0x16) => "PowerConverter",
            (PowerPage, BatteryCollection) => "Battery",
            (PowerPage, Voltage) => "Voltage",
            (PowerPage, Current) => "Current",
            (PowerPage, Frequency) => "Frequency",
            (PowerPage, ApparentPower) => "ApparentPower",
            (PowerPage, ActivePower) => "ActivePower",
            (PowerPage, PercentLoad) => "PercentLoad",
            (PowerPage, 0x36) => "Temperature",
            (PowerPage, ConfigVoltage) => "ConfigVoltage",
            (PowerPage, ConfigFrequency) => "ConfigFrequency",
            (PowerPage, ConfigApparentPower) => "ConfigApparentPower",
            (PowerPage, LowVoltageTransfer) => "LowVoltageTransfer",
            (PowerPage, HighVoltageTransfer) => "HighVoltageTransfer",
            (PowerPage, ConfigActivePower) => "ConfigActivePower",
            (PowerPage, ShutdownImminent) => "ShutdownImminent",
            (BatteryPage, BelowRemainingCapacityLimit) => "BelowRemainingCapacityLimit",
            (BatteryPage, Charging) => "Charging",
            (BatteryPage, Discharging) => "Discharging",
            (BatteryPage, NeedReplacement) => "NeedReplacement",
            (BatteryPage, RelativeStateOfCharge) => "RelativeStateOfCharge",
            (BatteryPage, RemainingCapacity) => "RemainingCapacity",
            (BatteryPage, 0x67) => "FullChargeCapacity",
            (BatteryPage, RunTimeToEmpty) => "RunTimeToEmpty",
            (BatteryPage, AverageTimeToEmpty) => "AverageTimeToEmpty",
            (BatteryPage, ACPresent) => "ACPresent",
            (BatteryPage, BatteryPresent) => "BatteryPresent",
            _ => null,
        };
    }

    // ---------------------------------------------------------------- device

    /// <summary>An open HID interface plus its parsed report descriptor.</summary>
    private sealed class UpsHidDevice : IDisposable
    {
        private IntPtr _handle;
        private IntPtr _preparsed;
        private readonly List<HidField> _fields = new();

        public ushort VendorId { get; private set; }
        public ushort ProductId { get; private set; }
        public string? Model { get; private set; }
        public ushort TopUsagePage { get; private set; }
        public ushort TopUsage { get; private set; }
        public int InputLength { get; private set; }
        public int FeatureLength { get; private set; }

        public IReadOnlyList<HidField> Fields => _fields;

        public bool IsUps => _fields.Any(f => f.Page is Usages.PowerPage or Usages.BatteryPage);

        public static UpsHidDevice? OpenFirstUps()
        {
            foreach (string path in Native.EnumerateHidPaths(out _))
            {
                UpsHidDevice? dev = TryOpen(path, out _);
                if (dev == null)
                {
                    continue;
                }

                if (dev.IsUps)
                {
                    return dev;
                }

                dev.Dispose();
            }

            return null;
        }

        public static UpsHidDevice? TryOpen(string path, out string? error)
        {
            error = null;

            // Read/write is needed for feature reports on some stacks; fall back
            // to a zero-access handle (still allows HidD_* queries) when denied.
            IntPtr handle = Native.CreateFile(path, Native.GenericRead | Native.GenericWrite, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle == Native.InvalidHandle)
            {
                handle = Native.CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            }

            if (handle == Native.InvalidHandle)
            {
                error = "CreateFile failed, Win32 error " + Marshal.GetLastWin32Error();
                return null;
            }

            var dev = new UpsHidDevice { _handle = handle };
            try
            {
                if (!dev.Initialize(out error))
                {
                    dev.Dispose();
                    return null;
                }

                return dev;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                dev.Dispose();
                return null;
            }
        }

        private bool Initialize(out string? error)
        {
            error = null;

            if (!Native.HidD_GetPreparsedData(_handle, out _preparsed))
            {
                error = "HidD_GetPreparsedData failed";
                return false;
            }

            if (Native.HidP_GetCaps(_preparsed, out Native.HIDP_CAPS caps) != Native.HidpSuccess)
            {
                error = "HidP_GetCaps failed";
                return false;
            }

            if (Native.HidD_GetAttributes(_handle, out Native.HID_ATTRIBUTES attrs))
            {
                VendorId = attrs.VendorID;
                ProductId = attrs.ProductID;
            }

            TopUsagePage = caps.UsagePage;
            TopUsage = caps.Usage;
            InputLength = caps.InputReportByteLength;
            FeatureLength = caps.FeatureReportByteLength;

            // Some units (CyberPower) report a junk manufacturer string such as "1".
            string mfr = VendorId == 0x0764 ? "CyberPower" : ReadString(Native.HidD_GetManufacturerString);
            string product = ReadString(Native.HidD_GetProductString);
            Model = $"{mfr} {product}".Trim();
            if (Model.Length == 0)
            {
                Model = null;
            }

            // Only the UPS pages are interesting; skip parsing for everything else.
            if (caps.UsagePage is not (Usages.PowerPage or Usages.BatteryPage))
            {
                return true;
            }

            LoadCaps(Native.HidpInput, caps.NumberInputValueCaps, isButton: false);
            LoadCaps(Native.HidpInput, caps.NumberInputButtonCaps, isButton: true);
            LoadCaps(Native.HidpFeature, caps.NumberFeatureValueCaps, isButton: false);
            LoadCaps(Native.HidpFeature, caps.NumberFeatureButtonCaps, isButton: true);
            return true;
        }

        private string ReadString(Func<IntPtr, StringBuilder, uint, bool> getter)
        {
            var sb = new StringBuilder(256);
            return getter(_handle, sb, 512) ? sb.ToString().Trim() : "";
        }

        private void LoadCaps(int reportType, ushort count, bool isButton)
        {
            if (count == 0)
            {
                return;
            }

            const int capsSize = 72; // sizeof(HIDP_VALUE_CAPS) == sizeof(HIDP_BUTTON_CAPS)
            IntPtr buffer = Marshal.AllocHGlobal(capsSize * count);
            try
            {
                ushort length = count;
                int status = isButton
                    ? Native.HidP_GetButtonCaps(reportType, buffer, ref length, _preparsed)
                    : Native.HidP_GetValueCaps(reportType, buffer, ref length, _preparsed);
                if (status != Native.HidpSuccess)
                {
                    return;
                }

                for (int i = 0; i < length; i++)
                {
                    IntPtr p = buffer + i * capsSize;
                    ushort page = (ushort)Marshal.ReadInt16(p, 0);
                    byte reportId = Marshal.ReadByte(p, 2);
                    ushort link = (ushort)Marshal.ReadInt16(p, 6);
                    ushort linkUsage = (ushort)Marshal.ReadInt16(p, 8);
                    ushort linkPage = (ushort)Marshal.ReadInt16(p, 10);
                    bool isRange = Marshal.ReadByte(p, 12) != 0;
                    ushort usageMin = (ushort)Marshal.ReadInt16(p, 56);
                    ushort usageMax = isRange ? (ushort)Marshal.ReadInt16(p, 58) : usageMin;

                    ushort bitSize = 1;
                    uint units = 0;
                    int exp = 0, logicalMin = 0;
                    if (!isButton)
                    {
                        bitSize = (ushort)Marshal.ReadInt16(p, 18);
                        exp = Marshal.ReadInt32(p, 32) & 0xF;
                        if (exp > 7)
                        {
                            exp -= 16; // signed nibble
                        }

                        units = (uint)Marshal.ReadInt32(p, 36);
                        logicalMin = Marshal.ReadInt32(p, 40);
                    }

                    for (ushort usage = usageMin; usage <= usageMax; usage++)
                    {
                        _fields.Add(new HidField
                        {
                            Page = page,
                            Usage = usage,
                            ReportId = reportId,
                            ReportType = reportType,
                            Link = link,
                            LinkUsage = linkUsage,
                            LinkUsagePage = linkPage,
                            IsButton = isButton,
                            LogicalMin = logicalMin,
                            BitSize = bitSize,
                            Units = units,
                            UnitsExp = exp,
                        });

                        if (usage == ushort.MaxValue)
                        {
                            break;
                        }
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>
        /// Fetches every report that carries a UPS field and decodes it.
        /// Returns null if no report could be read at all.
        /// </summary>
        public List<FieldReading>? ReadAll()
        {
            var reports = new Dictionary<(int Type, byte Id), byte[]?>();
            var result = new List<FieldReading>();

            foreach (HidField f in _fields)
            {
                var key = (f.ReportType, f.ReportId);
                if (!reports.TryGetValue(key, out byte[]? report))
                {
                    report = FetchReport(f.ReportType, f.ReportId);
                    reports[key] = report;
                }

                if (report == null)
                {
                    continue;
                }

                if (f.IsButton)
                {
                    var list = new ushort[256];
                    uint length = (uint)list.Length;
                    int status = Native.HidP_GetUsages(f.ReportType, f.Page, f.Link, list, ref length, _preparsed, report, (uint)report.Length);
                    if (status == Native.HidpSuccess)
                    {
                        bool on = list.Take((int)length).Contains(f.Usage);
                        result.Add(new FieldReading(f, on ? 1 : 0, on ? 1 : 0, on));
                    }
                }
                else
                {
                    int status = Native.HidP_GetUsageValue(f.ReportType, f.Page, f.Link, f.Usage, out uint raw, _preparsed, report, (uint)report.Length);
                    if (status == Native.HidpSuccess && !IsPlaceholder(f, raw))
                    {
                        long value = raw;
                        if (f.LogicalMin < 0 && f.BitSize is > 0 and < 32 && (raw & (1u << (f.BitSize - 1))) != 0)
                        {
                            value = (long)raw - (1L << f.BitSize); // sign-extend
                        }

                        result.Add(new FieldReading(f, value, Scale(f, value), null));
                    }
                }
            }

            if (result.Count == 0)
            {
                return null;
            }

            NormalizeScales(result);
            return result;
        }

        /// <summary>
        /// Some firmwares declare far more fields than they implement and answer the
        /// rest with filler instead of an error: all bits set ("not available"), or
        /// 0xFF followed by the report id echoed back (e.g. id 0x20 reads 0x20FF).
        /// Such values must not be shown as measurements.
        /// </summary>
        private static bool IsPlaceholder(HidField f, uint raw)
        {
            if (f.BitSize is > 1 and <= 16 && f.LogicalMin >= 0 && raw == (1u << f.BitSize) - 1)
            {
                return true;
            }

            // 0xFF in a wider field is the same filler (id byte missing or zero).
            return f.ReportType == Native.HidpFeature && f.BitSize >= 8
                   && ((raw & 0xFF) == 0xFF && (raw >> 8) is var hi && (hi == f.ReportId || hi == 0));
        }

        private byte[]? FetchReport(int reportType, byte reportId)
        {
            int length = reportType == Native.HidpFeature ? FeatureLength : InputLength;
            if (length <= 0)
            {
                return null;
            }

            var buffer = new byte[length];
            buffer[0] = reportId;
            bool ok = reportType == Native.HidpFeature
                ? Native.HidD_GetFeature(_handle, buffer, (uint)buffer.Length)
                : Native.HidD_GetInputReport(_handle, buffer, (uint)buffer.Length);
            return ok ? buffer : null;
        }

        /// <summary>
        /// Applies the HID unit exponent for volts / watts. Those units are
        /// expressed in g*cm^2/s^3[/A], so an exponent of 7 means "SI units".
        /// </summary>
        private static double Scale(HidField f, long raw)
        {
            bool isVoltsOrWatts = (f.Units & 0xFFFFF) is 0xD121 or 0xF0D121 or 0x0D121;
            if (isVoltsOrWatts && f.UnitsExp is >= 1 and <= 7)
            {
                return raw * Math.Pow(10, f.UnitsExp - 7);
            }

            if ((f.Units & 0xFFFF) == 0xF001)
            {
                return raw * Math.Pow(10, f.UnitsExp); // Hertz
            }

            return raw;
        }

        public void Dispose()
        {
            if (_preparsed != IntPtr.Zero)
            {
                Native.HidD_FreePreparsedData(_preparsed);
                _preparsed = IntPtr.Zero;
            }

            if (_handle != IntPtr.Zero)
            {
                Native.CloseHandle(_handle);
                _handle = IntPtr.Zero;
            }
        }
    }

    // ------------------------------------------------------------------ P/Invoke

    internal static class Native
    {
        public static readonly IntPtr InvalidHandle = new(-1);

        public const uint GenericRead = 0x80000000;
        public const uint GenericWrite = 0x40000000;

        // HIDP_REPORT_TYPE
        public const int HidpInput = 0;
        public const int HidpFeature = 2;

        // HIDP_STATUS_SUCCESS is 0x00110000 - NOT zero.
        public const int HidpSuccess = 0x00110000;

        [StructLayout(LayoutKind.Sequential)]
        public struct HID_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;

            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public uint Flags;
            public UIntPtr Reserved;
        }

        // hid.dll: the HidD_* functions return BOOLEAN (true = success);
        // the HidP_* functions return an NTSTATUS (HidpSuccess = success).

        [DllImport("hid.dll")]
        public static extern void HidD_GetHidGuid(out Guid guid);

        [DllImport("hid.dll")]
        public static extern bool HidD_GetAttributes(IntPtr handle, out HID_ATTRIBUTES attributes);

        [DllImport("hid.dll")]
        public static extern bool HidD_GetPreparsedData(IntPtr handle, out IntPtr preparsedData);

        [DllImport("hid.dll")]
        public static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll", CharSet = CharSet.Unicode)]
        public static extern bool HidD_GetProductString(IntPtr handle, StringBuilder buffer, uint bufferLength);

        [DllImport("hid.dll", CharSet = CharSet.Unicode)]
        public static extern bool HidD_GetManufacturerString(IntPtr handle, StringBuilder buffer, uint bufferLength);

        [DllImport("hid.dll")]
        public static extern bool HidD_GetFeature(IntPtr handle, byte[] reportBuffer, uint reportBufferLength);

        [DllImport("hid.dll")]
        public static extern bool HidD_GetInputReport(IntPtr handle, byte[] reportBuffer, uint reportBufferLength);

        [DllImport("hid.dll")]
        public static extern int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS caps);

        [DllImport("hid.dll")]
        public static extern int HidP_GetValueCaps(int reportType, IntPtr valueCaps, ref ushort valueCapsLength, IntPtr preparsedData);

        [DllImport("hid.dll")]
        public static extern int HidP_GetButtonCaps(int reportType, IntPtr buttonCaps, ref ushort buttonCapsLength, IntPtr preparsedData);

        [DllImport("hid.dll")]
        public static extern int HidP_GetUsages(
            int reportType, ushort usagePage, ushort linkCollection,
            [Out] ushort[] usageList, ref uint usageLength,
            IntPtr preparsedData, byte[] report, uint reportLength);

        [DllImport("hid.dll")]
        public static extern int HidP_GetUsageValue(
            int reportType, ushort usagePage, ushort linkCollection, ushort usage,
            out uint usageValue, IntPtr preparsedData, byte[] report, uint reportLength);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateFile(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(
            IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid,
            uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(
            IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
            IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize,
            out uint requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll")]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        /// <summary>Device paths of all present HID interfaces.</summary>
        public static List<string> EnumerateHidPaths(out string? error)
        {
            error = null;
            var result = new List<string>();

            HidD_GetHidGuid(out Guid hidGuid);

            const uint DIGCF_PRESENT = 0x02, DIGCF_DEVICEINTERFACE = 0x10;
            IntPtr devInfo = SetupDiGetClassDevs(ref hidGuid, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (devInfo == InvalidHandle)
            {
                error = "SetupDiGetClassDevs failed, Win32 error " + Marshal.GetLastWin32Error();
                return result;
            }

            try
            {
                for (uint index = 0; ; index++)
                {
                    var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                    if (!SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref hidGuid, index, ref data))
                    {
                        break; // ERROR_NO_MORE_ITEMS
                    }

                    // First call is expected to "fail" with ERROR_INSUFFICIENT_BUFFER
                    // and report the required size.
                    SetupDiGetDeviceInterfaceDetail(devInfo, ref data, IntPtr.Zero, 0, out uint required, IntPtr.Zero);
                    if (required == 0)
                    {
                        continue;
                    }

                    IntPtr detail = Marshal.AllocHGlobal((int)required);
                    try
                    {
                        // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W: 8 on x64, 6 on x86.
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (SetupDiGetDeviceInterfaceDetail(devInfo, ref data, detail, required, out _, IntPtr.Zero))
                        {
                            string? path = Marshal.PtrToStringUni(detail + 4);
                            if (!string.IsNullOrWhiteSpace(path))
                            {
                                result.Add(path);
                            }
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detail);
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(devInfo);
            }

            return result;
        }
    }
}
