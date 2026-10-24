using System.Drawing.Drawing2D;
using System.Reflection;

namespace SuYanShot.UI;

/// The 拾光 icon shared with macOS (Resources/AppIcon.png, embedded), at whatever size a window, the tray or a phone needs.
internal static class AppIcon
{
    private static readonly Lazy<Bitmap> Source = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SuYanShot.AppIcon.png")
                           ?? throw new InvalidOperationException("AppIcon.png is not embedded.");
        return new Bitmap(stream);
    });

    public static Bitmap Render(int size)
    {
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(Source.Value, 0, 0, size, size);
        return bitmap;
    }

    /// PNG bytes, for the phone page's apple-touch-icon.
    public static byte[] Png(int size)
    {
        using var bitmap = Render(size);
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        return stream.ToArray();
    }

    /// The tray icon; with <paramref name="sharing"/> a green dot shows that phones can connect (macOS adds a dot too).
    public static Icon Tray(bool sharing)
    {
        var size = SystemInformation.SmallIconSize.Width;
        using var bitmap = Render(size);
        if (sharing)
        {
            using var g = Graphics.FromImage(bitmap);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var d = Math.Max(5f, size * 0.42f);
            var dot = new RectangleF(size - d, size - d, d, d);
            using var ring = new SolidBrush(Color.White);
            g.FillEllipse(ring, RectangleF.Inflate(dot, 1, 1));
            using var green = new SolidBrush(Color.FromArgb(52, 199, 89));
            g.FillEllipse(green, dot);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }

    /// Title-bar icon for the app's windows.
    public static Icon Window => WindowIcon.Value;

    private static readonly Lazy<Icon> WindowIcon = new(() => Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application);
}
