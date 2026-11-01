using System.Drawing.Imaging;

namespace SuYanShot.Storage;

/// Reads an image off the system clipboard for 標註剪貼簿裡的圖片: copied image data (PNG first, which keeps transparency,
/// then the bitmap formats), or a copied image file from Explorer.
internal static class ClipboardImage
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic",
    };

    /// A copy the caller owns, or null when the clipboard holds no image.
    public static Bitmap? Read()
    {
        IDataObject? data;
        try
        {
            data = System.Windows.Forms.Clipboard.GetDataObject();
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return null; // another app is holding the clipboard open
        }
        if (data == null) return null;

        if (data.GetData("PNG") is MemoryStream png && TryLoad(png) is Bitmap fromPng) return fromPng;
        if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is Image bitmap) return Copy(bitmap);
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
        {
            foreach (var file in files.Where(f => ImageExtensions.Contains(Path.GetExtension(f))))
            {
                try
                {
                    using var stream = File.OpenRead(file);
                    if (TryLoad(stream) is Bitmap fromFile) return fromFile;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        return null;
    }

    private static Bitmap? TryLoad(Stream stream)
    {
        try
        {
            using var image = Image.FromStream(stream);
            return Copy(image);
        }
        catch (ArgumentException)
        {
            return null; // not an image GDI+ can read (for example HEIC without the codec)
        }
    }

    /// Detaches the image from its stream / clipboard handle, as a 32-bit bitmap the editor can draw on.
    private static Bitmap Copy(Image source)
    {
        var copy = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(copy);
        g.DrawImage(source, new Rectangle(0, 0, source.Width, source.Height));
        return copy;
    }
}
