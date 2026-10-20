using System.Runtime.InteropServices;
using System.Text;

namespace UpSTrayMonitor.Providers;

/// <summary>
/// Reads a UPS that is exposed as a USB HID device (the standard way most
/// vendors - APC, CyberPower, Eaton, Tripp Lite, PowerExpert, ... - expose
/// UPS telemetry on Windows).
///
/// Uses the standard "Battery Status" HID usage page (0xFF):
///   0x08 Battery Pack Discharging (flag)
///   0x0B Battery Pack Charge (0-100)
///   0x0E Battery Pack Remaining Time
///   0x09 Battery Pack Discharge Time
///   0x14 Input Mains Voltage
///   0x17 Output Load (0-100)
///   0x18 Output Voltage
///   0x1B Output Active Power (W)
///   0x1A Output Apparent Power (VA)
///
/// The provider opens the first HID device whose report descriptor defines
/// one of those usages, reads one input report, and then queries the cached
/// values through HidP_GetUsageValue. The device handle is kept open so
/// polling every few seconds is cheap.
/// </summary>
public sealed class HidUpsProvider : IUpsProvider, IDisposable
{
    public string Name => "HID";

    // ---- standard UPS (Battery Status) usages, page 0xFF -----------------
    private const ushort UsDischarging    = 0x08;
    private const ushort UsDischargeTime  = 0x09;
    private const ushort UsBatteryCharge  = 0x0B;
    private const ushort UsRemainingTime  = 0x0E;
    private const ushort UsInputVoltage   = 0x14;
    private const ushort UsOutputLoad     = 0x17;
    private const ushort UsOutputVoltage  = 0x18;
    private const ushort UsOutputCurrent  = 0x19;
    private const ushort UsApparentPower  = 0x1A;
    private const ushort UsActivePower    = 0x1B;

    private static readonly uint[] UpsUsages =
        [UsDischarging, UsBatteryCharge, UsRemainingTime, UsInputVoltage, UsOutputLoad, UsActivePower];

    private IntPtr _device = IntPtr.Zero;
    private IntPtr _preparsed = IntPtr.Zero;
    private int _reportSize;
    private bool _gotReport;

    // ------------------------------------------------------------------ API

    public UpsState? TryGetSample()
    {
        try
        {
            if (!EnsureDevice())
            {
                return null;
            }

            byte[] buffer = new byte[Math.Max(1, _reportSize)];
            bool read = ReadReport(buffer, out _);
            if (read)
            {
                _gotReport = true;
            }

            if (!_gotReport)
            {
                return null;
            }

            var state = new UpsState();
            bool any = false;

            if (ReadValue(UsBatteryCharge, out int charge))
            {
                state.BatteryPercent = Math.Clamp(charge, 0, 100);
                any = true;
            }

            if (ReadValue(UsDischarging, out int discharging))
            {
                bool onBattery = discharging != 0;
                state.Status = onBattery
                    ? (state.BatteryPercent == 0 ? UpsStatus.Critical : UpsStatus.OnBattery)
                    : (state.BatteryPercent is > 0 ? UpsStatus.Online : UpsStatus.Unknown);
                any = true;
            }

            int? runtime = null;
            if (ReadValue(UsRemainingTime, out int rt1)) runtime = rt1;
            if (runtime == null && ReadValue(UsDischargeTime, out int rt2)) runtime = rt2;
            if (runtime != null)
            {
                // Spec says minutes; some vendors report seconds.
                state.RuntimeMinutes = runtime.Value > 900 ? runtime.Value / 60 : runtime.Value;
                any = true;
            }

            if (ReadValue(UsInputVoltage, out int vin)) { state.InputVoltage = vin; any = true; }
            if (ReadValue(UsOutputLoad, out int load)) { state.LoadPercent = Math.Clamp(load, 0, 100); any = true; }

            double? watts = null;
            if (ReadValue(UsActivePower, out int w)) watts = w;
            if (watts is null or 0 && ReadValue(UsApparentPower, out int va)) watts = va;
            if (watts != null) { state.OutputWatts = watts; any = true; }

            if (!any)
            {
                return null;
            }

            return state;
        }
        catch
        {
            // Device may have been unplugged; force re-enumeration next poll.
            ReleaseDevice();
            return null;
        }
    }

    // ------------------------------------------------------- device opening

    private bool EnsureDevice()
    {
        if (_device != IntPtr.Zero)
        {
            return true;
        }

        foreach (string path in EnumerateHidDevicePaths())
        {
            IntPtr handle = Native.CreateFile(
                path,
                0x80000000 /* GENERIC_READ */,
                0x00000003 /* FILE_SHARE_READ | FILE_SHARE_WRITE */,
                IntPtr.Zero,
                3 /* OPEN_EXISTING */,
                0x80 /* FILE_ATTRIBUTE_NORMAL */,
                IntPtr.Zero);

            if (handle == Native.InvalidHandle)
            {
                continue;
            }

            try
            {
                if (Native.HidD_GetAttributes(handle, out Native.HID_ATTRIBUTES attrs) != 0)
                {
                    continue;
                }

                if (Native.HidD_GetPreparsedData(handle, out IntPtr preparsed) != 0)
                {
                    continue;
                }

                if (!DefinesUpsUsages(preparsed))
                {
                    Native.HidD_FreePreparsedData(preparsed);
                    continue;
                }

                if (Native.HidP_GetCaps(preparsed, out Native.HIDP_CAPS caps) != 0)
                {
                    Native.HidD_FreePreparsedData(preparsed);
                    continue;
                }

                int size = Math.Max(caps.InputReportByteLength, caps.FeatureReportByteLength);
                _device = handle;
                _preparsed = preparsed;
                _reportSize = Math.Max(1, size);
                _gotReport = false;
                return true;
            }
            catch
            {
                Native.CloseHandle(handle);
            }
        }

        return false;
    }

    /// <summary>
    /// True if the report descriptor of this device declares at least one
    /// well-known UPS usage on page 0xFF. Uses HidP_GetUsages, which inspects
    /// the descriptor only - no report read required.
    /// </summary>
    private static bool DefinesUpsUsages(IntPtr preparsed)
    {
        if (Native.HidP_GetUsages(0xFF, IntPtr.Zero, out uint count, preparsed) != 0 || count == 0)
        {
            return false;
        }

        int structSize = Marshal.SizeOf<Native.HIDP_USAGE_LIST>();
        IntPtr list = Marshal.AllocHGlobal((IntPtr)((long)structSize * count));
        try
        {
            if (Native.HidP_GetUsages(0xFF, list, out count, preparsed) != 0)
            {
                return false;
            }

            for (int i = 0; i < count; i++)
            {
                var usage = Marshal.PtrToStructure<Native.HIDP_USAGE_LIST>(list + i * structSize);
                if (UpsUsages.Contains(usage.Usage))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(list);
        }
    }

    // ---------------------------------------------------------------- reading

    /// <summary>Reads one input report using overlapped I/O with a timeout.</summary>
    private bool ReadReport(byte[] buffer, out int bytesRead)
    {
        bytesRead = 0;
        IntPtr eventHandle = Native.CreateEvent(IntPtr.Zero, true, true, null);
        var ov = new Native.OVERLAPPED { hEvent = eventHandle };
        try
        {
            bool completed = Native.ReadFile(_device, buffer, (uint)buffer.Length, out uint read, ref ov);
            if (!completed)
            {
                if (Native.GetLastError() != 255 /* ERROR_IO_PENDING */)
                {
                    return false;
                }

                int wait = Native.WaitForSingleObject(eventHandle, 2500);
                if (wait != 0 /* WAIT_OBJECT_0 */)
                {
                    return false; // timed out waiting for the UPS report
                }

                if (!Native.GetOverlappedResult(_device, ref ov, out read, out bool failed) || failed)
                {
                    return false;
                }
            }

            bytesRead = (int)read;
            return bytesRead > 0;
        }
        finally
        {
            Native.CloseHandle(eventHandle);
        }
    }

    private bool ReadValue(ushort usage, out int value)
    {
        // Try report IDs 0 and 1 - different vendors pick different ones.
        for (ushort reportId = 0; reportId <= 1; reportId++)
        {
            if (Native.HidP_GetUsageValue(0xFF, usage, _preparsed, reportId, out value) == 0)
            {
                return true;
            }
        }

        value = 0;
        return false;
    }

    // ------------------------------------------------------- enumeration

    private static IEnumerable<string> EnumerateHidDevicePaths()
    {
        var result = new List<string>();

        if (Native.HidD_GetHidGuid(out Guid hidGuid) != 0)
        {
            return result;
        }

        IntPtr devInfo = Native.SetupDiGetClassDevtList(
            ref hidGuid, IntPtr.Zero, IntPtr.Zero, 0 /* present devices */);
        if (devInfo == Native.InvalidHandle)
        {
            return result;
        }

        try
        {
            var interfaceData = new Native.SP_DEVICE_INTERFACE_DATA
            {
                cbSize = Marshal.SizeOf<Native.SP_DEVICE_INTERFACE_DATA>(),
            };
            var devInfoData = new Native.SP_DEVINFO_DATA
            {
                cbSize = Marshal.SizeOf<Native.SP_DEVINFO_DATA>(),
            };

            for (uint index = 0; ; index++)
            {
                if (!Native.SetupDiEnumDeviceInterfaces(devInfo, ref devInfoData, ref hidGuid, index, ref interfaceData))
                {
                    break;
                }

                if (!Native.SetupDiGetDeviceInterfaceDetail(devInfo, ref interfaceData, IntPtr.Zero, 0, out int required, IntPtr.Zero)
                    || required == 0)
                {
                    continue;
                }

                IntPtr detail = Marshal.AllocHGlobal(required);
                try
                {
                    Marshal.WriteInt32(detail, 4); // cbSize = sizeof(DWORD) for the W struct
                    if (Native.SetupDiGetDeviceInterfaceDetail(devInfo, ref interfaceData, detail, required, out _, IntPtr.Zero))
                    {
                        string? path = Marshal.PtrToStringUni(detail + 4);
                        if (!string.IsNullOrWhiteSpace(path))
                        {
                            result.Add(path!);
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
            Native.SetupDiDestroyDeviceInfoList(devInfo);
        }

        return result;
    }

    // ------------------------------------------------------------ cleanup

    private void ReleaseDevice()
    {
        if (_preparsed != IntPtr.Zero)
        {
            Native.HidD_FreePreparsedData(_preparsed);
            _preparsed = IntPtr.Zero;
        }

        if (_device != IntPtr.Zero)
        {
            Native.CloseHandle(_device);
            _device = IntPtr.Zero;
        }

        _reportSize = 0;
        _gotReport = false;
    }

    public void Dispose()
    {
        ReleaseDevice();
    }

    // ------------------------------------------------------------ diagnostics

    /// <summary>
    /// Dumps every usage on page 0xFF of every HID device that defines UPS
    /// usages. Useful to figure out what a given UPS actually reports
    /// (run: ups-tray-monitor --dump).
    /// </summary>
    public static void Dump()
    {
        Console.WriteLine("Scanning HID devices for UPS usages (page 0xFF)...");
        bool found = false;

        foreach (string path in EnumerateHidDevicePaths())
        {
            IntPtr handle = Native.CreateFile(path, 0x80000000, 0x00000003, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
            if (handle == Native.InvalidHandle)
            {
                continue;
            }

            try
            {
                if (Native.HidD_GetAttributes(handle, out Native.HID_ATTRIBUTES attrs) != 0
                    || Native.HidD_GetPreparsedData(handle, out IntPtr preparsed) != 0)
                {
                    continue;
                }

                try
                {
                    if (Native.HidP_GetUsages(0xFF, IntPtr.Zero, out uint count, preparsed) != 0 || count == 0)
                    {
                        continue;
                    }

                    int structSize = Marshal.SizeOf<Native.HIDP_USAGE_LIST>();
                    IntPtr list = Marshal.AllocHGlobal((IntPtr)((long)structSize * count));
                    try
                    {
                        Native.HidP_GetUsages(0xFF, list, out count, preparsed);

                        int reportLength = 0;
                        if (Native.HidP_GetCaps(preparsed, out Native.HIDP_CAPS caps) == 0)
                        {
                            reportLength = caps.InputReportByteLength;
                        }

                        Console.WriteLine();
                        Console.WriteLine($"  {path}");
                        Console.WriteLine($"    Vendor 0x{attrs.VendorID:X4}  Product 0x{attrs.ProductID:X4}  Version 0x{attrs.VersionNumber:X4}");
                        Console.WriteLine($"    Input report length: {reportLength} bytes");

                        // Nudge the device to produce a report so values are cached.
                        int size = Math.Max(1, reportLength);
                        byte[] buffer = new byte[size];
                        IntPtr evt = Native.CreateEvent(IntPtr.Zero, true, true, null);
                        var ov = new Native.OVERLAPPED { hEvent = evt };
                        bool done = Native.ReadFile(handle, buffer, (uint)buffer.Length, out _, ref ov);
                        if (!done && Native.GetLastError() == 255)
                        {
                            Native.WaitForSingleObject(evt, 2000);
                        }
                        Native.CloseHandle(evt);

                        for (int i = 0; i < count; i++)
                        {
                            var usage = Marshal.PtrToStructure<Native.HIDP_USAGE_LIST>(list + i * structSize);
                            ushort usageId = (ushort)usage.Usage;
                            string name = KnownUsageName(usageId) ?? "?";
                            for (ushort rid = 0; rid <= 1; rid++)
                            {
                                if (Native.HidP_GetUsageValue(0xFF, usageId, preparsed, rid, out int value) == 0)
                                {
                                    Console.WriteLine($"      0x{usageId:X2}  {name,-28} = {value}   (report id {rid})");
                                    break;
                                }
                            }
                        }

                        found = true;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(list);
                    }
                }
                finally
                {
                    Native.HidD_FreePreparsedData(preparsed);
                }
            }
            catch
            {
                // Ignore individual device failures.
            }
            finally
            {
                Native.CloseHandle(handle);
            }
        }

        Console.WriteLine();
        if (!found)
        {
            Console.WriteLine("No HID device with standard UPS usages found.");
        }
    }

    private static string? KnownUsageName(ushort usage) => usage switch
    {
        UsDischarging   => "Battery Pack Discharging",
        UsDischargeTime => "Battery Pack Discharge Time",
        UsBatteryCharge => "Battery Pack Charge",
        UsRemainingTime => "Battery Pack Remaining Time",
        UsInputVoltage  => "Input Mains Voltage",
        UsOutputLoad    => "Output Load",
        UsOutputVoltage => "Output Voltage",
        UsOutputCurrent => "Output Current",
        UsApparentPower => "Output Apparent Power",
        UsActivePower   => "Output Active Power",
        _ => null,
    };

    // ------------------------------------------------------------------ P/Invoke

    private static class Native
    {
        public static readonly IntPtr InvalidHandle = new(-1);

        [StructLayout(LayoutKind.Sequential)]
        public struct HID_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_USAGE_LIST
        {
            public ushort Reserved;
            public uint Usage;
            public ushort ReportID;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_CAPS
        {
            public uint Usage;
            public uint UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;

            public ushort NumberLinkCollections;
            public ushort NumberButtonCaps;
            public ushort NumberRangeUsages;
            public ushort NumberRangeButtonCaps;
            public ushort NumberControlButtonCaps;
            public ushort NumberRangeStringIndices;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public byte Flags;
            public int Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public uint Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct OVERLAPPED
        {
            public uint Internal;
            public uint InternalHigh;
            public uint Offset;
            public uint OffsetHigh;
            public IntPtr hEvent;
        }

        // HID_STATUS: 0 = success.

        [DllImport("hid.dll")]
        public static extern int HidD_GetHidGuid(out Guid guid);

        [DllImport("hid.dll")]
        public static extern int HidD_GetAttributes(IntPtr handle, out HID_ATTRIBUTES attributes);

        [DllImport("hid.dll")]
        public static extern int HidD_GetPreparsedData(IntPtr handle, out IntPtr preparsedData);

        [DllImport("hid.dll")]
        public static extern int HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll")]
        public static extern int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS caps);

        [DllImport("hid.dll")]
        public static extern int HidP_GetUsages(ushort usagePage, IntPtr usageList, out uint numberOfUsages, IntPtr preparsedData);

        [DllImport("hid.dll")]
        public static extern int HidP_GetUsageValue(ushort usagePage, ushort usage, IntPtr preparsedData, ushort reportId, out int usageValue);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateFile(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        public static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, ref OVERLAPPED lpOverlapped);

        [DllImport("kernel32.dll")]
        public static extern bool GetOverlappedResult(IntPtr hFile, ref OVERLAPPED lpOverlapped, out uint lpNumberOfBytesTransferred, out bool bFailed);

        [DllImport("kernel32.dll")]
        public static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

        [DllImport("kernel32.dll")]
        public static extern int WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll")]
        public static extern int GetLastError();

        [DllImport("setupapi.dll")]
        public static extern IntPtr SetupDiGetClassDevtList(ref Guid classGuid, IntPtr hwndParent, IntPtr machineName, uint flags);

        [DllImport("setupapi.dll")]
        public static extern bool SetupDiEnumDeviceInterfaces(
            IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
            ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW")]
        public static extern bool SetupDiGetDeviceInterfaceDetail(
            IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
            IntPtr deviceInterfaceDetailData, int deviceInterfaceDetailDataLen,
            out int requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll")]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
    }
}
