using System.Globalization;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace UpSTrayMonitor.Providers;

/// <summary>
/// Reads UPSes that tunnel the Megatec / Voltronic "Q1" serial protocol over a
/// vendor-defined HID interface (cheap Cypress-based units: Ippon, Mustek,
/// Powercom, ...). Those units also expose a standard UPS interface, but their
/// firmware only fills in the battery %, runtime and status flags there; the
/// voltages, frequency and load are only available through Q1.
///
/// Protocol detection is by behaviour, not by VID/PID. A vendor-page (0xFF00)
/// interface is only probed when it has 8-byte in/out reports AND a sibling
/// interface of the same USB device is on the UPS usage pages, so unrelated
/// devices (keyboards, headsets, ...) are never written to. The interface is
/// used only if it answers "Q1\r" with a well-formed "(...)" line.
/// </summary>
public sealed class MegatecHidProvider : IUpsProvider, IDisposable
{
    public string Name => "Q1";

    private const ushort VendorPage = 0xFF00;
    private const int ChunkSize = 8;

    // Q1 is the usual status command; some units (Voltronic "QS" firmwares) only echo it and answer QS.
    private static readonly string[] Commands = { "Q1", "QS" };
    private const int ReplyTimeoutMs = 2000;

    private Channel? _channel;
    private readonly HashSet<string> _rejected = new(StringComparer.OrdinalIgnoreCase);

    public UpsState? TryGetSample()
    {
        try
        {
            _channel ??= FindChannel(_rejected, out _);
            if (_channel == null)
            {
                return null;
            }

            string? reply = _channel.Query(_channel.Command);
            if (reply == null || !TryParse(reply, out UpsState? state))
            {
                // Unplugged or stopped answering: re-detect next poll.
                Release();
                return null;
            }

            return state;
        }
        catch
        {
            Release();
            return null;
        }
    }

    public void Dispose() => Release();

    private void Release()
    {
        _channel?.Dispose();
        _channel = null;
    }

    // ----------------------------------------------------------------- parse

    /// <summary>Parses "(MMM.M NNN.N PPP.P QQQ RR.R S.SS TT.T b7b6b5b4b3b2b1b0".</summary>
    internal static bool TryParse(string reply, out UpsState? state)
    {
        state = null;
        string[] f = reply.Trim().TrimStart('(').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!reply.TrimStart().StartsWith('(') || f.Length < 8 || f[7].Length < 8 || f[7].Any(c => c is not ('0' or '1')))
        {
            return false;
        }

        bool Num(string s, out double v) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        if (!Num(f[0], out double vin) || !Num(f[2], out double vout) || !Num(f[3], out double load)
            || !Num(f[4], out double freq) || !Num(f[5], out double vbat)
            || vin is < 0 or > 400 || vout is < 0 or > 400 || load is < 0 or > 255)
        {
            return false;
        }

        string bits = f[7];
        bool utilityFail = bits[0] == '1';
        bool batteryLow = bits[1] == '1';

        state = new UpsState
        {
            InputVoltage = vin,
            LoadPercent = (int)Math.Clamp(Math.Round(load), 0, 100),
            Status = utilityFail ? (batteryLow ? UpsStatus.Critical : UpsStatus.OnBattery) : UpsStatus.Online,
        };

        // Some models send the voltage per cell (about 2 V) instead of the pack voltage.
        if (vbat > 5)
        {
            state.BatteryVoltage = vbat;
        }

        state.Details.Add(("Out volt", vout.ToString("0.#") + " V"));
        if (freq > 0)
        {
            state.Details.Add(("Out freq", freq.ToString("0.#") + " Hz"));
        }

        return true;
    }

    // ------------------------------------------------------------- detection

    /// <summary>Finds and verifies the first interface that answers Q1.</summary>
    private static Channel? FindChannel(HashSet<string> rejected, out List<string> log)
    {
        log = new List<string>();
        List<string> paths = HidUpsProvider.Native.EnumerateHidPaths(out _);
        var info = paths.ToDictionary(p => p, ReadInfo);

        foreach (string path in paths)
        {
            Info i = info[path];
            if (i.UsagePage != VendorPage || i.InputLength != ChunkSize + 1 || i.OutputLength != ChunkSize + 1
                || rejected.Contains(path))
            {
                continue;
            }

            string? device = DeviceKey(path);
            bool hasUpsSibling = device != null && paths.Any(p => p != path && DeviceKey(p) == device
                                                                   && info[p].UsagePage is 0x84 or 0x85);
            if (!hasUpsSibling)
            {
                continue;
            }

            Channel? channel = Channel.Open(path, i);
            foreach (string command in Commands)
            {
                string? reply = channel?.Query(command);
                log.Add($"{path} [{command}]: " + (reply == null ? "no reply" : $"\"{reply}\""));
                if (channel != null && reply != null && TryParse(reply, out _))
                {
                    channel.Command = command;
                    return channel;
                }
            }

            channel?.Dispose();
            rejected.Add(path);
        }

        return null;
    }

    /// <summary>"vid_0665&amp;pid_5161" part of a HID path: identifies the USB device.</summary>
    private static string? DeviceKey(string path)
    {
        int v = path.IndexOf("vid_", StringComparison.OrdinalIgnoreCase);
        int p = path.IndexOf("&pid_", StringComparison.OrdinalIgnoreCase);
        return v >= 0 && p > v && path.Length >= p + 9 ? path.Substring(v, p + 9 - v).ToLowerInvariant() : null;
    }

    private readonly record struct Info(ushort UsagePage, int InputLength, int OutputLength);

    private static Info ReadInfo(string path)
    {
        IntPtr handle = HidUpsProvider.Native.CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle == HidUpsProvider.Native.InvalidHandle)
        {
            return default;
        }

        try
        {
            if (!HidUpsProvider.Native.HidD_GetPreparsedData(handle, out IntPtr preparsed))
            {
                return default;
            }

            try
            {
                return HidUpsProvider.Native.HidP_GetCaps(preparsed, out var caps) == HidUpsProvider.Native.HidpSuccess
                    ? new Info(caps.UsagePage, caps.InputReportByteLength, caps.OutputReportByteLength)
                    : default;
            }
            finally
            {
                HidUpsProvider.Native.HidD_FreePreparsedData(preparsed);
            }
        }
        finally
        {
            HidUpsProvider.Native.CloseHandle(handle);
        }
    }

    // --------------------------------------------------------------- channel

    /// <summary>An open vendor HID interface used as a serial line (8 bytes per report).</summary>
    private sealed class Channel : IDisposable
    {
        private readonly FileStream _stream;
        private readonly int _reportLength;

        /// <summary>The status command this unit answers.</summary>
        public string Command { get; set; } = "Q1";

        private Channel(FileStream stream, int reportLength)
        {
            _stream = stream;
            _reportLength = reportLength;
        }

        public static Channel? Open(string path, Info info)
        {
            const uint FILE_FLAG_OVERLAPPED = 0x40000000;
            IntPtr handle = HidUpsProvider.Native.CreateFile(
                path, HidUpsProvider.Native.GenericRead | HidUpsProvider.Native.GenericWrite, 3,
                IntPtr.Zero, 3, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (handle == HidUpsProvider.Native.InvalidHandle)
            {
                return null;
            }

            var stream = new FileStream(new SafeFileHandle(handle, ownsHandle: true), FileAccess.ReadWrite, 1, isAsync: true);
            return new Channel(stream, info.InputLength);
        }

        /// <summary>Sends a command and returns the reply line (without CR), or null.</summary>
        public string? Query(string command)
        {
            // Drop anything left over from an earlier, timed-out query.
            while (ReadReport(50) != null)
            {
            }

            byte[] bytes = Encoding.ASCII.GetBytes(command + "\r");
            for (int offset = 0; offset < bytes.Length; offset += ChunkSize)
            {
                var report = new byte[_reportLength]; // [0] = report id (none)
                Array.Copy(bytes, offset, report, 1, Math.Min(ChunkSize, bytes.Length - offset));
                _stream.Write(report, 0, report.Length);
            }

            var reply = new StringBuilder();
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(ReplyTimeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                byte[]? report = ReadReport(Math.Max(1, (int)(deadline - DateTime.UtcNow).TotalMilliseconds));
                if (report == null)
                {
                    break;
                }

                foreach (byte b in report.Skip(1))
                {
                    if (b == '\r')
                    {
                        // Some firmwares echo the command before the real reply.
                        if (reply.Length == 0 || reply.ToString() == command)
                        {
                            reply.Clear();
                            continue;
                        }

                        return reply.ToString();
                    }

                    if (b != 0)
                    {
                        reply.Append((char)b);
                    }
                }
            }

            return null;
        }

        /// <summary>Sends a command and appends every report received within 1.5 s as hex and ASCII.</summary>
        public void Raw(string command, StringBuilder output)
        {
            while (ReadReport(50) != null)
            {
            }

            byte[] bytes = Encoding.ASCII.GetBytes(command + "\r");
            var report = new byte[_reportLength];
            Array.Copy(bytes, 0, report, 1, bytes.Length);
            _stream.Write(report, 0, report.Length);

            DateTime deadline = DateTime.UtcNow.AddMilliseconds(1500);
            while (DateTime.UtcNow < deadline)
            {
                byte[]? r = ReadReport(Math.Max(1, (int)(deadline - DateTime.UtcNow).TotalMilliseconds));
                if (r == null)
                {
                    break;
                }

                string ascii = new(r.Skip(1).Select(b => b is >= 32 and < 127 ? (char)b : '.').ToArray());
                output.Append($"[{BitConverter.ToString(r.Skip(1).ToArray())} \"{ascii}\"] ");
            }
        }

        private byte[]? ReadReport(int timeoutMs)
        {
            var buffer = new byte[_reportLength];
            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                int n = _stream.ReadAsync(buffer, 0, buffer.Length, cts.Token).GetAwaiter().GetResult();
                return n > 0 ? buffer : null;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        public void Dispose() => _stream.Dispose();
    }

    // ------------------------------------------------------------ diagnostics

    /// <summary>Sends several commands to each candidate interface and prints every raw report (hex + ASCII).</summary>
    private static void DumpRaw()
    {
        Console.WriteLine("  Raw probe of candidate interfaces:");
        List<string> paths = HidUpsProvider.Native.EnumerateHidPaths(out _);
        foreach (string path in paths)
        {
            Info i = ReadInfo(path);
            if (i.UsagePage != VendorPage || i.InputLength != ChunkSize + 1 || i.OutputLength != ChunkSize + 1)
            {
                continue;
            }

            Console.WriteLine("  " + path);
            using Channel? channel = Channel.Open(path, i);
            if (channel == null)
            {
                Console.WriteLine("    cannot open");
                continue;
            }

            foreach (string cmd in new[] { "Q1", "QS", "QGS", "QPI", "QMOD", "QVFW", "I", "F", "Q" })
            {
                var sb = new StringBuilder();
                channel.Raw(cmd, sb);
                Console.WriteLine($"    {cmd,-5} -> {(sb.Length == 0 ? "(nothing)" : sb.ToString().TrimEnd())}");
            }
        }
    }

    /// <summary>Probes the Q1 channel and prints the raw reply (run: ups-tray-monitor --dump).</summary>
    public static void Dump()
    {
        Console.WriteLine();
        Console.WriteLine("Probing for a Megatec Q1 serial-over-HID channel...");
        var rejected = new HashSet<string>();
        Channel? channel;
        List<string> log;
        try
        {
            channel = FindChannel(rejected, out log);
        }
        catch (Exception ex)
        {
            Console.WriteLine("  Probe failed: " + ex.Message);
            return;
        }

        if (channel == null)
        {
            DumpRaw();
        }

        using (channel)
        {
            foreach (string line in log)
            {
                Console.WriteLine("  " + line);
            }

            if (log.Count == 0)
            {
                Console.WriteLine("  No candidate interface (vendor page 0xFF00, 8-byte reports, next to a UPS interface).");
            }
            else if (channel != null && TryParse(channel.Query(channel.Command) ?? "", out UpsState? s) && s != null)
            {
                Console.WriteLine($"  OK: in {s.InputVoltage:0.#} V, load {s.LoadPercent}%, battery {s.BatteryVoltage:0.#} V, status {s.Status}");
            }
        }
    }
}
