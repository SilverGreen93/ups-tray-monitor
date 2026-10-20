using System.Runtime.InteropServices;

namespace UpSTrayMonitor.Providers;

/// <summary>
/// Last-resort source: the OS power state (GetSystemPowerStatus).
/// Only trusted when the OS actually reports a battery/UPS power source
/// (a plain desktop reports "no battery" and is ignored).
/// </summary>
public sealed class SystemPowerProvider : IUpsProvider
{
    public string Name => "System Power";

    public UpsState? TryGetSample()
    {
        if (!Native.GetSystemPowerStatus(out var power))
        {
            return null;
        }

        // 3 = no battery, 128 = status unknown -> plain PC, not a UPS.
        if (power.BatteryFlag is 3 or 128)
        {
            return null;
        }

        var state = new UpsState();

        // 0 = offline (mains lost), 1 = online, 255 = unknown.
        state.Status = power.ACLineStatus == 0
            ? UpsStatus.OnBattery
            : (power.BatteryFlag == 2 /* critical */ ? UpsStatus.Critical : UpsStatus.Online);

        if (power.BatteryLifePercent != 255)
        {
            state.BatteryPercent = power.BatteryLifePercent;
        }

        return state;
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte Reserved1;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll")]
        public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);
    }
}
