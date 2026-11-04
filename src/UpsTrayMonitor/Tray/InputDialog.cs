using System.Drawing;
using System.Media;

namespace UpSTrayMonitor.Tray;

/// <summary>
/// Minimal dark-themed prompt that asks for a non-negative whole number.
/// Matches the popup window's palette; Enter accepts, Esc cancels.
/// </summary>
internal sealed class InputDialog : Form
{
    private readonly TextBox _input;

    /// <summary>The number the user entered (valid whenever <see cref="DialogResult"/> is OK).</summary>
    public int Number { get; private set; }

    public InputDialog(string title, string prompt, int current, int max, Point location)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ControlBox = false;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Location = location;
        BackColor = UpsPresentation.WindowBackground;
        ForeColor = UpsPresentation.TextPrimary;
        Font = new Font("Segoe UI", 9.5f);
        ClientSize = new Size(360, 140);

        // Auto-sized so long prompts (e.g. a UPS-reported power sentence) never clip;
        // the rest of the layout is placed relative to the prompt's measured height.
        var promptLabel = new Label
        {
            Text = prompt,
            ForeColor = UpsPresentation.TextSecondary,
            AutoSize = true,
            MaximumSize = new Size(ClientSize.Width - 32, 0),
            Location = new Point(16, 14),
        };
        Controls.Add(promptLabel);

        int inputY = promptLabel.Bottom + 12;

        _input = new TextBox
        {
            Location = new Point(16, inputY),
            Size = new Size(110, 26),
            BackColor = UpsPresentation.CardBackground,
            ForeColor = UpsPresentation.TextPrimary,
        };
        _input.KeyPress += (_, e) =>
        {
            if (!char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar))
            {
                e.Handled = true;
            }
        };
        Controls.Add(_input);

        var unitLabel = new Label
        {
            Text = "W",
            ForeColor = UpsPresentation.TextSecondary,
            AutoSize = true,
            Location = new Point(132, inputY + 4),
        };
        Controls.Add(unitLabel);

        ClientSize = new Size(360, inputY + 26 + 14 + 36);

        var okButton = MakeDialogButton("OK", new Point(ClientSize.Width - 178, ClientSize.Height - 40));
        okButton.ForeColor = UpsPresentation.TextPrimary;
        okButton.Click += (_, _) => Accept(current, max);
        var cancelButton = MakeDialogButton("Cancel", new Point(ClientSize.Width - 90, ClientSize.Height - 40));
        cancelButton.Click += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };
        // Clears the override so the app falls back to whatever the UPS reports.
        var resetButton = MakeDialogButton("Reset", new Point(16, ClientSize.Height - 40));
        resetButton.Enabled = current > 0;
        resetButton.Click += (_, _) =>
        {
            Number = 0;
            DialogResult = DialogResult.OK;
            Close();
        };
        Controls.Add(okButton);
        Controls.Add(cancelButton);
        Controls.Add(resetButton);

        AcceptButton = okButton;
        CancelButton = cancelButton;

        if (current > 0)
        {
            _input.Text = current.ToString();
        }

        Shown += (_, _) =>
        {
            _input.Focus();
            _input.SelectAll();
        };
    }

    private void Accept(int current, int max)
    {
        // Empty input means "reset to 0"; anything else must parse.
        if (_input.Text.Trim().Length == 0)
        {
            Number = 0;
        }
        else if (!int.TryParse(_input.Text.Trim(), out int parsed) || parsed > max)
        {
            Number = current;
            SystemSounds.Beep.Play();
            return;
        }
        else
        {
            Number = parsed;
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    private static Button MakeDialogButton(string text, Point location)
    {
        var button = new Button
        {
            Text = text,
            Location = location,
            Size = new Size(80, 28),
            FlatStyle = FlatStyle.Flat,
            ForeColor = UpsPresentation.TextSecondary,
            BackColor = UpsPresentation.WindowBackground,
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = UpsPresentation.CardBackground;
        button.FlatAppearance.MouseDownBackColor = UpsPresentation.CardBackground;
        return button;
    }
}
