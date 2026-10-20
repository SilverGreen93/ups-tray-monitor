using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace UpSTrayMonitor.Tray;

/// <summary>
/// Draws the tray icon at runtime (no .ico file needed): a battery glyph whose
/// fill level follows the reported charge and whose color follows the status.
/// The rendered bitmap is wrapped in a minimal ICO container (PNG entry -
/// System.Drawing on .NET cannot save .ico files directly).
/// </summary>
public static class StatusIcon
{
    private const int IconSize = 32;

    public static Icon Create(UpsSample? sample)
    {
        using var bitmap = new Bitmap(IconSize, IconSize, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            bool hasData = sample != null && sample.HasData();
            Color color = UpsPresentation.StatusColor(sample);

            // Battery body + terminal nub.
            var body = new Rectangle(2, 9, 22, 15);
            var nub = new Rectangle(25, 13, 3, 7);

            using (var bodyBrush = new SolidBrush(Color.FromArgb(0x24, 0x29, 0x33)))
            using (var outline = new Pen(Color.FromArgb(hasData ? 0xD7 : 0x66, color), 1.4f))
            using (var path = RoundedRect(body, 3))
            {
                g.FillPath(bodyBrush, path);
                g.FillRectangle(bodyBrush, nub);
                g.DrawPath(outline, path);
                g.DrawRectangle(outline, nub);
            }

            if (hasData)
            {
                // Charge fill inside the body.
                int percent = sample!.BatteryPercent ?? 100; // no charge data -> show "powered"
                if (percent > 0)
                {
                    int innerLeft = 4;
                    int innerWidth = body.Width - 4;
                    int fillWidth = Math.Max(2, (int)Math.Round(innerWidth * percent / 100.0));
                    using var fill = new SolidBrush(color);
                    g.FillRectangle(fill, innerLeft, 11, fillWidth, 11);
                }

                // Lightning bolt while on battery.
                if (sample!.Status is UpsStatus.OnBattery or UpsStatus.Critical)
                {
                    using var bolt = new SolidBrush(Color.FromArgb(0x12, 0x15, 0x1C));
                    Point[] points =
                    {
                        new(17, 10), new(11, 17), new(14, 17),
                        new(12, 23), new(20, 15), new(16, 15),
                    };
                    g.FillPolygon(bolt, points);
                }
            }
        }

        using var stream = new MemoryStream();
        WriteIcoContainer(stream, bitmap);
        stream.Position = 0;
        return new Icon(stream); // stream-based icons own their handle - no leak
    }

    // ---------------------------------------------------------------- ICO writer

    /// <summary>
    /// Writes a minimal single-image ICO file (32x32) around a PNG-encoded bitmap:
    /// ICONDIR + ICONDIRENTRY + PNG data.
    /// </summary>
    private static void WriteIcoContainer(Stream stream, Bitmap bitmap)
    {
        using var png = new MemoryStream();
        bitmap.Save(png, ImageFormat.Png);
        byte[] pngBytes = png.ToArray();

        // ICONDIR
        WriteU16(stream, 0);   // reserved
        WriteU16(stream, 1);   // type: icon
        WriteU16(stream, 1);   // image count

        // ICONDIRENTRY
        stream.WriteByte((byte)IconSize);  // width (256 would be 0)
        stream.WriteByte((byte)IconSize);  // height
        stream.WriteByte(0);               // palette colors
        stream.WriteByte(0);               // reserved
        WriteU16(stream, 1);               // color planes
        WriteU16(stream, 32);              // bits per pixel
        WriteU32(stream, (uint)pngBytes.Length);
        WriteU32(stream, 22);              // image data offset

        stream.Write(pngBytes, 0, pngBytes.Length);
    }

    private static void WriteU16(Stream stream, ushort value)
    {
        stream.WriteByte((byte)(value & 0xFF));
        stream.WriteByte((byte)(value >> 8));
    }

    private static void WriteU32(Stream stream, uint value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        stream.Write(bytes, 0, 4);
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
