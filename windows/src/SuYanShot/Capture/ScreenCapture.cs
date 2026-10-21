using System.Drawing.Imaging;
using SuYanShot.Native;

namespace SuYanShot.Capture;

/// A still copy of the whole virtual desktop, taken before any overlay is shown.
/// Region and window selection draw on top of it and crop from it, so what the user sees is exactly what they get,
/// and our own overlays can never end up in the image.
internal sealed class FrozenDesktop : IDisposable
{
    public Bitmap Image { get; }

    /// Virtual-screen bounds in physical pixels; may start at negative coordinates when a monitor is left of / above the primary.
    public Rectangle Bounds { get; }

    public FrozenDesktop(Bitmap image, Rectangle bounds)
    {
        Image = image;
        Bounds = bounds;
    }

    /// Crops a rectangle given in screen pixels. Returns null if it does not overlap the desktop.
    public Bitmap? Crop(Rectangle screenRect)
    {
        var clipped = Rectangle.Intersect(screenRect, Bounds);
        if (clipped.Width <= 0 || clipped.Height <= 0) return null;
        clipped.Offset(-Bounds.X, -Bounds.Y);
        return Image.Clone(clipped, PixelFormat.Format32bppArgb);
    }

    public void Dispose() => Image.Dispose();
}

internal static class ScreenCapture
{
    public static Rectangle VirtualScreen => SystemInformation.VirtualScreen;

    public static FrozenDesktop FreezeDesktop()
    {
        var bounds = VirtualScreen;
        return new FrozenDesktop(CaptureRect(bounds), bounds);
    }

    /// Captures a rectangle of the live screen in physical pixels. Windows marked with
    /// <see cref="NativeMethods.ExcludeFromCapture"/> (our preview cards and HUDs) are left out by the system.
    public static Bitmap CaptureRect(Rectangle screenRect)
    {
        var bitmap = new Bitmap(screenRect.Width, screenRect.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(screenRect.Location, Point.Empty, screenRect.Size, CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    /// Captures a window's own pixels even where other windows cover it, trimmed to its visible frame.
    /// Falls back to the frozen desktop for apps that render black through PrintWindow.
    public static Bitmap? CaptureWindow(IntPtr hWnd, FrozenDesktop fallback)
    {
        var visible = NativeMethods.VisibleBounds(hWnd);
        if (visible.Width <= 0 || visible.Height <= 0) return null;
        if (NativeMethods.GetWindowRect(hWnd, out var rawRect))
        {
            var raw = rawRect.ToRectangle();
            using var full = new Bitmap(raw.Width, raw.Height, PixelFormat.Format32bppArgb);
            bool printed;
            using (var graphics = Graphics.FromImage(full))
            {
                var hdc = graphics.GetHdc();
                printed = NativeMethods.PrintWindow(hWnd, hdc, NativeMethods.PW_RENDERFULLCONTENT);
                graphics.ReleaseHdc(hdc);
            }
            var inner = Rectangle.Intersect(new Rectangle(visible.X - raw.X, visible.Y - raw.Y, visible.Width, visible.Height),
                                            new Rectangle(Point.Empty, raw.Size));
            if (printed && inner.Width > 0 && inner.Height > 0)
            {
                var cropped = full.Clone(inner, PixelFormat.Format32bppArgb);
                if (!LooksBlank(cropped)) return Opaque(cropped);
                cropped.Dispose();
            }
        }
        return fallback.Crop(visible);
    }

    /// PrintWindow leaves the alpha channel undefined for many apps; screenshots should be fully opaque.
    private static Bitmap Opaque(Bitmap source)
    {
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(result))
            graphics.DrawImageUnscaled(source, 0, 0);
        source.Dispose();
        return result;
    }

    /// True when every sampled pixel is black, which is how GPU-rendered apps often come back from PrintWindow.
    internal static bool LooksBlank(Bitmap bitmap)
    {
        var stepX = Math.Max(1, bitmap.Width / 24);
        var stepY = Math.Max(1, bitmap.Height / 24);
        for (var y = 0; y < bitmap.Height; y += stepY)
            for (var x = 0; x < bitmap.Width; x += stepX)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.R > 4 || pixel.G > 4 || pixel.B > 4) return false;
            }
        return true;
    }

    /// The monitor under the mouse pointer.
    public static Screen ScreenUnderCursor() => Screen.FromPoint(Cursor.Position);
}
