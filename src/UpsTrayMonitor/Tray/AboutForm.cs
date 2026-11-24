using System.Diagnostics;
using System.Reflection;

namespace UpSTrayMonitor.Tray;

public sealed class AboutForm : Form
{
    private const string GitHubUrl = "https://github.com/SilverGreen93/ups-tray-monitor";

    public AboutForm()
    {
        Text = "About UPS Tray Monitor";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(340, 190);
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ShowIcon = false;
        Font = new Font("Segoe UI", 9.5f);

        var appNameLabel = new Label
        {
            Text = "UPS Tray Monitor",
            Font = new Font("Segoe UI", 13f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(24, 20),
        };
        Controls.Add(appNameLabel);

        Version? version = Assembly.GetExecutingAssembly().GetName().Version;
        var versionLabel = new Label
        {
            Text = $"Version {version?.ToString(2) ?? "Unknown"}",
            AutoSize = true,
            Location = new Point(24, 56),
        };
        Controls.Add(versionLabel);

        string copyright = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright
            ?? "© 2026 SilverGreen93";
        var copyrightLabel = new Label
        {
            Text = copyright,
            AutoSize = true,
            Location = new Point(24, 82),
        };
        Controls.Add(copyrightLabel);

        var gitHubLink = new LinkLabel
        {
            Text = "GitHub",
            AutoSize = true,
            Location = new Point(24, 116),
            LinkColor = UpsPresentation.Link,
        };
        gitHubLink.LinkClicked += (_, _) =>
            Process.Start(new ProcessStartInfo(GitHubUrl) { UseShellExecute = true });
        Controls.Add(gitHubLink);

        var closeButton = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Size = new Size(84, 28),
            Location = new Point(ClientSize.Width - 24 - 84, ClientSize.Height - 18 - 28),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
        };
        Controls.Add(closeButton);
        AcceptButton = closeButton;
    }
}
