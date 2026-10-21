using System.Windows.Forms;
using UpSTrayMonitor.Providers;
using UpSTrayMonitor.Tray;

namespace UpSTrayMonitor;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "tray";

        // The exe is a WinExe (no console of its own), so the command-line modes
        // have to attach to the parent console before they can print anything.
        if (mode is "--once" or "--console" or "--dump" or "--help" or "-h")
        {
            ConsoleAttach.Attach();
        }

        switch (mode)
        {
            case "--once":
                return RunOnce();

            case "--console":
                return RunConsoleLoop();

            case "--dump":
                HidUpsProvider.Dump();
                return 0;

            case "--screenshot":
                return RunScreenshot(args.Length > 1 ? args[1] : null, args.Length > 2 ? args[2] : "online");

            case "--help":
            case "-h":
                PrintHelp();
                return 0;

            case "tray":
            default:
                RunTrayApp();
                return 0;
        }
    }

    // ------------------------------------------------------------------ tray

    private static void RunTrayApp()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Application.ThreadException += (_, e) => CrashReport(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashReport(e.ExceptionObject as Exception);

        using var mutex = new Mutex(true, @"Local\UpsTrayMonitor_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "UPS Monitor is already running - look for its icon in the system tray.",
                "UPS Monitor",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
            Application.Run(new TrayApplicationContext());
        }
        catch (Exception ex)
        {
            CrashReport(ex);
        }
    }

    /// <summary>Tray apps have no console - write unhandled errors to a log file instead.</summary>
    private static void CrashReport(Exception? ex)
    {
        if (ex == null)
        {
            return;
        }

        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "ups-tray-monitor");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n{ex}\n\n");
        }
        catch
        {
            // Nothing else we can do.
        }
    }

    // --------------------------------------------------------------- screenshot

    private static int RunScreenshot(string? outputPath, string scenario)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        string path = outputPath ?? Path.Combine(Path.GetTempPath(), "ups-tray-monitor-screenshot.png");

        var form = new DetailsForm();
        form.Update(SampleForScenario(scenario));

        // DrawToBitmap does not paint child controls of a top-level form, and a
        // window needs a message loop to render at all - so show the window,
        // pump the loop briefly, capture it from the screen, then exit.
        form.ShowNearTray();

        bool done = false;
        var timer = new System.Windows.Forms.Timer { Interval = 800 };
        timer.Tick += (_, _) =>
        {
            if (done)
            {
                return;
            }

            done = true;
            timer.Stop();

            try
            {
                using var image = new System.Drawing.Bitmap(form.Width, form.Height);
                using (var g = System.Drawing.Graphics.FromImage(image))
                {
                    g.CopyFromScreen(form.Left, form.Top, 0, 0, image.Size, System.Drawing.CopyPixelOperation.SourceCopy);
                }

                image.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                Console.WriteLine("Screenshot saved to: " + path);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Capture failed: " + ex.Message);
            }

            form.Dispose();
            Application.Exit();
        };
        timer.Start();

        Application.Run();
        return done ? 0 : 1;
    }

    private static UpsSample SampleForScenario(string scenario) => scenario.ToLowerInvariant() switch
    {
        "battery" => new UpsSample
        {
            Status = UpsStatus.OnBattery,
            BatteryPercent = 64,
            LoadPercent = 23,
            OutputWatts = 184,
            RuntimeMinutes = 96,
            BatteryVoltage = 25.4,
            Model = "Test UPS 1500VA",
            Provider = "HID (sample data)",
            Timestamp = DateTime.Now,
        },

        "critical" => new UpsSample
        {
            Status = UpsStatus.Critical,
            BatteryPercent = 8,
            LoadPercent = 41,
            OutputWatts = 322,
            RuntimeMinutes = 7,
            Model = "Test UPS 1500VA",
            Provider = "HID (sample data)",
            Timestamp = DateTime.Now,
        },

        "nodata" => new UpsSample
        {
            Status = UpsStatus.Unknown,
            Provider = "none",
            Timestamp = DateTime.Now,
        },

        _ => new UpsSample
        {
            Status = UpsStatus.Online,
            BatteryPercent = 87,
            InputVoltage = 231.4,
            LoadPercent = 23,
            OutputWatts = 184,
            RuntimeMinutes = 142,
            BatteryVoltage = 27.1,
            Model = "Test UPS 1500VA",
            Provider = "HID (sample data)",
            Timestamp = DateTime.Now,
        },
    };

    // ---------------------------------------------------------------- console

    private static int RunOnce()
    {
        var monitor = new UpsMonitor();
        UpsSample sample;
        try
        {
            sample = monitor.GetSample();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Error reading UPS state: " + ex.Message);
            return 1;
        }

        PrintSample(sample);
        return sample.HasData() ? 0 : 2;
    }

    private static int RunConsoleLoop()
    {
        Console.WriteLine("UPS Monitor - polling every 5 s. Press Ctrl+C to stop.");
        Console.WriteLine();

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var monitor = new UpsMonitor();
        while (true)
        {
            PrintSample(monitor.GetSample());
            Console.WriteLine();

            int waited = 0;
            while (waited < 5000 && !cts.IsCancellationRequested)
            {
                Thread.Sleep(Math.Min(100, 5000 - waited));
                waited += 100;
            }

            if (cts.IsCancellationRequested)
            {
                break;
            }
        }

        Console.WriteLine("Stopped.");
        return 0;
    }

    private static void PrintSample(UpsSample sample)
    {
        Console.WriteLine($"  Time    : {sample.Timestamp:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"  Status  : {(sample.HasData() ? UpsPresentation.StatusText(sample) : "no data detected")}");
        Console.WriteLine($"  Battery : {(sample.BatteryPercent is int b ? b + "%" : "n/a")}");
        Console.WriteLine($"  Input   : {(sample.InputVoltage is double v ? v.ToString("0.#") + " V" : "n/a")}");
        Console.WriteLine($"  Load    : {(sample.LoadPercent is int l ? l + "%" : "n/a")}");
        Console.WriteLine($"  Power   : {sample.PowerText() ?? "n/a"}");
        Console.WriteLine($"  Runtime : {(sample.RuntimeMinutes is int m ? "~" + m + " min" : "n/a")}");
        Console.WriteLine($"  Model   : {sample.Model ?? "n/a"}");
        foreach (var (label, value) in sample.Details)
        {
            Console.WriteLine($"  {label,-8}: {value}");
        }

        Console.WriteLine($"  Source  : {sample.Provider}");
    }

    private static void PrintHelp()
    {
        Console.WriteLine("ups-tray-monitor - Windows system tray UPS monitor");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  ups-tray-monitor              Run the tray application (default)");
        Console.WriteLine("  ups-tray-monitor --once       Read one sample, print it, exit");
        Console.WriteLine("  ups-tray-monitor --console    Print a sample every 5 s (Ctrl+C to stop)");
        Console.WriteLine("  ups-tray-monitor --dump       List all UPS HID usages found on this machine");
        Console.WriteLine("  ups-tray-monitor --screenshot [file] [scenario]  Save a PNG of the details window");
        Console.WriteLine("                                          scenario: online | battery | critical | nodata");
        Console.WriteLine("  ups-tray-monitor --help       Show this help");
    }
}
