using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;

namespace UpSTrayMonitor.Tray;

/// <summary>
/// Borderless popup shown when the tray icon is clicked.
/// Displays the same data as the hover tooltip, in more detail.
/// Toggled by a single click on the tray icon; closed via the tray icon or the X button.
/// </summary>
public sealed class DetailsForm : Form
{
    private const int FormWidth = 340;
    private const int TopBar = 28; // strip above the title row holding the close button
    private const int FormHeight = 336 + TopBar;

    private readonly Label _statusPill;
    private readonly Label _modelLabel;
    private readonly Label _batteryValue;
    private readonly ProgressBar _batteryBar;
    private readonly Label _inputValue;
    private readonly Label _batteryVoltValue;
    private readonly Label _loadValue;
    private readonly Label _powerValue;
    private readonly Label _runtimeValue;
    private readonly Label _footerSource;
    private readonly Label _footerTime;

    private bool _isOpen;

    /// <summary>True while the popup is actually on screen (i.e. <see cref="Form.Visible"/> is true).</summary>
    public bool IsOpen => _isOpen;

    /// <summary>
    /// Toggles the popup in response to a tray-icon click (or the "Show details" menu item).
    /// Visible → hide; hidden → show.
    ///
    /// We intentionally do NOT auto-hide on OnDeactivate. Clicking the tray icon deactivates
    /// the popup (firing OnDeactivate) BEFORE NotifyIcon.MouseClick fires, so a hide-on-
    /// deactivation would race with the toggle and make single-click unreliable (the click
    /// that should hide would instead re-show). Keeping the popup open until it's explicitly
    /// closed (tray click or X) makes the toggle a simple, correct visibility check.
    /// </summary>
    public void HandleTrayIconClick(UpsSample? sample)
    {
        if (IsOpen)
        {
            Hide();
        }
        else
        {
            Update(sample);
            ShowNearTray();
        }
    }

    public DetailsForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(FormWidth, FormHeight);
        BackColor = UpsPresentation.WindowBackground;
        ForeColor = UpsPresentation.TextPrimary;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        Font = new Font("Segoe UI", 9.5f);
        Region = new Region(RoundedRectangle(12));

        var closeButton = new Button
        {
            Location = new Point(FormWidth - 34, 2),
            Size = new Size(24, 24),
            FlatStyle = FlatStyle.Flat,
            ForeColor = UpsPresentation.TextSecondary,
            BackColor = UpsPresentation.WindowBackground,
        };
        closeButton.TabStop = false;
        closeButton.FlatAppearance.BorderSize = 0;
        closeButton.FlatAppearance.MouseOverBackColor = UpsPresentation.CardBackground;
        closeButton.FlatAppearance.MouseDownBackColor = UpsPresentation.CardBackground;
        closeButton.Click += (_, _) => Hide();

        // Drawn as two lines rather than a "×" glyph, which sits low in the
        // button box in most fonts.
        closeButton.Paint += (sender, e) =>
        {
            var b = (Button)sender!;
            const int arm = 5;
            float cx = (b.Width - 1) / 2f, cy = (b.Height - 1) / 2f;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(b.ForeColor, 1.5f);
            e.Graphics.DrawLine(pen, cx - arm, cy - arm, cx + arm, cy + arm);
            e.Graphics.DrawLine(pen, cx - arm, cy + arm, cx + arm, cy - arm);
        };
        Controls.Add(closeButton);

        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "";
        var titleLabel = new Label
        {
            Text = string.IsNullOrEmpty(version) ? "UPS Tray Monitor" : $"UPS Tray Monitor v{version}",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = UpsPresentation.TextSecondary,
            AutoSize = true,
            Location = new Point(14, 7),
        };
        Controls.Add(titleLabel);

        _modelLabel = new Label
        {
            Text = "USB UPS",
            Font = new Font("Segoe UI", 13.5f, FontStyle.Bold),
            ForeColor = UpsPresentation.TextPrimary,
            AutoSize = true,
            Location = new Point(18, TopBar + 14),
            MaximumSize = new Size(FormWidth - 90, 0),
        };
        Controls.Add(_modelLabel);

        _statusPill = new Label
        {
            Text = "—",
            AutoSize = true,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = Color.FromArgb(0x10, 0x12, 0x18),
            BackColor = UpsPresentation.Unknown,
            Padding = new Padding(10, 4, 10, 4),
        };
        _statusPill.Location = new Point(FormWidth - 18 - _statusPill.PreferredWidth, TopBar + 16);
        Controls.Add(_statusPill);

        AddDivider(TopBar + 60);

        _batteryValue = AddRow(TopBar + 82, "Battery level");

        _batteryBar = new ProgressBar
        {
            Location = new Point(20, TopBar + 106),
            Size = new Size(FormWidth - 40, 10),
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Style = ProgressBarStyle.Continuous,
            ForeColor = UpsPresentation.Unknown,
            BackColor = UpsPresentation.CardBackground,
        };
        Controls.Add(_batteryBar);

        _inputValue = AddRow(TopBar + 134, "Input voltage");
        _batteryVoltValue = AddRow(TopBar + 168, "Battery voltage");
        _loadValue = AddRow(TopBar + 202, "Output load");
        _powerValue = AddRow(TopBar + 236, "Power");
        _runtimeValue = AddRow(TopBar + 270, "Runtime");

        AddDivider(TopBar + 302);

        _footerSource = new Label
        {
            Text = "Source: —",
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = UpsPresentation.TextSecondary,
            AutoSize = true,
            Location = new Point(20, TopBar + 312),
        };
        Controls.Add(_footerSource);

        _footerTime = new Label
        {
            Text = "",
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = UpsPresentation.TextSecondary,
            AutoSize = true,
        };
        _footerTime.Location = new Point(FormWidth - 20 - _footerTime.PreferredWidth, TopBar + 312);
        Controls.Add(_footerTime);
    }

    /// <summary>Positions the popup near the bottom-right of the work area (near the tray) and shows it.</summary>
    public void ShowNearTray()
    {
        Rectangle workArea = Screen.PrimaryScreen?.WorkingArea
                             ?? Rectangle.FromLTRB(0, 0, 1920, 1080);
        Location = new Point(workArea.Right - FormWidth - 14, workArea.Bottom - FormHeight - 14);
        Show();
        Activate();
    }

    public void Update(UpsSample? sample)
    {
        bool hasData = sample != null && sample.HasData();
        Color color = UpsPresentation.StatusColor(sample);

        _statusPill.Text = hasData ? UpsPresentation.StatusText(sample) : "No data";
        _statusPill.BackColor = color;

        _modelLabel.Text = sample?.Model ?? "USB UPS";


        if (sample?.BatteryPercent is int percent)
        {
            _batteryValue.Text = percent + "%";
            _batteryBar.Visible = true;
            _batteryBar.Value = Math.Clamp(percent, 0, 100);
            _batteryBar.ForeColor = color;
        }
        else
        {
            _batteryValue.Text = "—";
            _batteryBar.Visible = false;
        }

        _inputValue.Text = sample?.InputVoltage is double v ? $"{v:0.#} V" : "—";
        _batteryVoltValue.Text = sample?.BatteryVoltage is double bv ? $"{bv:0.#} V" : "—";
        _loadValue.Text = sample?.LoadPercent is int load ? $"{load}%" : "—";
        _powerValue.Text = sample?.OutputWatts is double w ? $"{w:0} W" : "—";
        _runtimeValue.Text = sample?.RuntimeMinutes is int minutes ? $"~{minutes} min" : "—";

        _footerSource.Text = "Source: " + (sample?.Provider ?? "none");
        string time = sample?.Timestamp is DateTime t && t != default
            ? t.ToString("HH:mm:ss")
            : "Waiting for data…";
        _footerTime.Text = time;

        // Re-anchor the right-aligned values AFTER their text changed.
        int[] rowYs = [TopBar + 82, TopBar + 134, TopBar + 168, TopBar + 202, TopBar + 236, TopBar + 270];
        Label[] rowValues = [_batteryValue, _inputValue, _batteryVoltValue, _loadValue, _powerValue, _runtimeValue];
        for (int i = 0; i < rowValues.Length; i++)
        {
            rowValues[i].Location = new Point(FormWidth - 20 - rowValues[i].PreferredWidth, rowYs[i]);
        }

        _statusPill.Location = new Point(FormWidth - 18 - _statusPill.PreferredWidth, TopBar + 16);
        _footerTime.Location = new Point(FormWidth - 20 - _footerTime.PreferredWidth, TopBar + 312);
    }

    // Note: we deliberately do NOT auto-hide on OnDeactivate. The popup stays open until
    // the user explicitly closes it (tray-icon click or the X button). This avoids the
    // WinForms race where clicking the tray icon deactivates the popup *before* the
    // MouseClick event fires, which made single-click toggle impossible.

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        _isOpen = Visible;
    }

    // ------------------------------------------------------------------ layout helpers

    /// <summary>Adds a caption/value row and returns the (right-aligned) value label.</summary>
    private Label AddRow(int y, string caption)
    {
        var label = new Label
        {
            Text = caption,
            ForeColor = UpsPresentation.TextSecondary,
            AutoSize = true,
            Location = new Point(20, y),
        };
        Controls.Add(label);

        var value = new Label
        {
            Text = "—",
            ForeColor = UpsPresentation.TextPrimary,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            AutoSize = true,
        };
        value.Location = new Point(FormWidth - 20 - value.PreferredWidth, y);
        Controls.Add(value);
        return value;
    }

    private void AddDivider(int y)
    {
        Controls.Add(new Panel
        {
            Location = new Point(20, y),
            Size = new Size(FormWidth - 40, 1),
            BackColor = UpsPresentation.CardBackground,
        });
    }

    private static GraphicsPath RoundedRectangle(int radius)
    {
        var bounds = new Rectangle(0, 0, FormWidth, FormHeight);
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
