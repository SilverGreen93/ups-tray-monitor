using System.Windows.Forms;
using UpSTrayMonitor.Providers;

namespace UpSTrayMonitor.Tray;

/// <summary>
/// The tray application: polls the UPS in the background (every 5 s) and
/// keeps the tooltip, tray icon and popup window up to date.
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private const int PollIntervalMs = 5000;

    private readonly UpsMonitor _monitor;
    private readonly NotifyIcon _notifyIcon;
    private readonly DetailsForm _detailsForm;
    private readonly Thread _pollThread;
    private readonly object _uiLock = new();

    private SynchronizationContext? _sync;
    private volatile bool _exitRequested;
    private volatile bool _refreshRequested;
    private volatile UpsSample? _lastSample;

    public TrayApplicationContext()
    {
        _monitor = new UpsMonitor();

        _detailsForm = new DetailsForm();

        _notifyIcon = new NotifyIcon
        {
            Text = "UPS Monitor: starting…",
            Icon = StatusIcon.Create(null),
            Visible = true,
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Show details", null, (_, _) => _detailsForm.HandleTrayIconClick(_lastSample));
        menu.Items.Add("Refresh now", null, (_, _) => _refreshRequested = true);

        bool startupEnabled = StartupManager.IsEnabled();
        if (!AppSettings.Current.FirstRunCompleted)
        {
            // First run (no settings yet): opt in to auto-start unless the user
            // already registered the app some other way. Whatever happens, the
            // flag below makes this a once-only default.
            if (!startupEnabled)
            {
                try
                {
                    StartupManager.SetEnabled(true);
                    startupEnabled = true;
                }
                catch
                {
                    // Registry not writable - leave the option off.
                }
            }

            AppSettings.Current.FirstRunCompleted = true;
            AppSettings.Current.Save();
        }

        var startupItem = new ToolStripMenuItem("Start at Windows startup")
        {
            Checked = startupEnabled,
            CheckOnClick = true,
        };
        startupItem.Click += (_, _) => ToggleStartup(startupItem);
        menu.Items.Add(startupItem);

        // Re-read the registry every time the menu opens so changes made
        // outside the app (Task Manager, Settings) are reflected.
        menu.Opening += (_, _) => startupItem.Checked = StartupManager.IsEnabled();

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());
        _notifyIcon.ContextMenuStrip = menu;

        // Single left-click toggles the popup: visible → hide, hidden → show.
        // DetailsForm does NOT auto-hide on losing focus, so this is a simple, race-free toggle.
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                _detailsForm.HandleTrayIconClick(_lastSample);
            }
        };

        _pollThread = new Thread(PollLoop)
        {
            Name = "UPS poller",
            IsBackground = true,
        };

        // Make sure we can post UI updates from the poller thread.
        // (Created on the UI thread, so it targets the UI message loop.)
        _sync = SynchronizationContext.Current as WindowsFormsSynchronizationContext
               ?? new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(_sync);

        Application.ApplicationExit += (_, _) => Cleanup();

        _pollThread.Start();
    }

    private void PollLoop()
    {
        while (!_exitRequested)
        {
            UpsSample? sample;
            try
            {
                sample = _monitor.GetSample();
            }
            catch
            {
                sample = null;
            }

            _lastSample = sample;
            PostUiUpdate();

            if (SleepInterruptible(PollIntervalMs))
            {
                break;
            }
        }
    }

    /// <summary>Sleeps up to <paramref name="ms"/>, waking early on exit/refresh. Returns true if we should stop.</summary>
    private bool SleepInterruptible(int ms)
    {
        int elapsed = 0;
        while (elapsed < ms)
        {
            if (_exitRequested)
            {
                return true;
            }

            if (_refreshRequested)
            {
                _refreshRequested = false;
                return false;
            }

            int step = Math.Min(150, ms - elapsed);
            Thread.Sleep(step);
            elapsed += step;
        }

        return _exitRequested;
    }

    private void PostUiUpdate()
    {
        SynchronizationContext? sync = _sync;
        if (sync == null)
        {
            return;
        }

        try
        {
            sync.Post(static state => ((TrayApplicationContext)state!).ApplyUiUpdate(), this);
        }
        catch (InvalidOperationException)
        {
            // Message loop already shutting down.
        }
    }

    private void ApplyUiUpdate()
    {
        UpsSample? sample = _lastSample;

        lock (_uiLock)
        {
            _notifyIcon.Text = UpsPresentation.BuildTooltip(sample);

            var newIcon = StatusIcon.Create(sample);
            Icon? oldIcon = _notifyIcon.Icon;
            _notifyIcon.Icon = newIcon;
            oldIcon?.Dispose();

            if (_detailsForm.Visible)
            {
                _detailsForm.Update(sample);
            }
        }
    }

    /// <summary>
    /// Applies the "Start at Windows startup" tick to the registry. CheckOnClick
    /// has already flipped the tick before this runs, so on failure we flip it back.
    /// </summary>
    private void ToggleStartup(ToolStripMenuItem item)
    {
        bool wanted = item.Checked;
        try
        {
            StartupManager.SetEnabled(wanted);
        }
        catch
        {
            item.Checked = !wanted;
        }
    }

    private void ExitApplication()
    {
        _exitRequested = true;
        _pollThread.Join(3000);
        Application.Exit();
    }

    private void Cleanup()
    {
        _exitRequested = true;

        try
        {
            _detailsForm.Dispose();
        }
        catch
        {
            // Already disposed.
        }

        try
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
        catch
        {
            // Already disposed.
        }
    }
}

