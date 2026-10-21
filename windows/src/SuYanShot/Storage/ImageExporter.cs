using System.Drawing.Imaging;
using System.Globalization;

namespace SuYanShot.Storage;

/// Owns PNG naming and writing policy, matching the macOS app: "Screenshot 2026-10-21 at 14.03.07.png" on the Desktop.
internal sealed class ImageExporter
{
    public string DefaultDirectory { get; }

    public ImageExporter(string? defaultDirectory = null)
    {
        DefaultDirectory = defaultDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    }

    public static string DefaultFilename(DateTime date) =>
        "Screenshot " + date.ToString("yyyy-MM-dd 'at' HH.mm.ss", CultureInfo.InvariantCulture) + ".png";

    /// Writes the image and returns its path. Never overwrites: a second capture in the same second gets " (2)".
    public string ExportPng(Bitmap image, string? directory = null, DateTime? date = null)
    {
        var target = directory ?? DefaultDirectory;
        Directory.CreateDirectory(target);
        var path = UniquePath(target, DefaultFilename(date ?? DateTime.Now));
        image.Save(path, ImageFormat.Png);
        return path;
    }

    internal static string UniquePath(string directory, string filename)
    {
        var path = Path.Combine(directory, filename);
        var stem = Path.GetFileNameWithoutExtension(filename);
        var extension = Path.GetExtension(filename);
        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(directory, $"{stem} ({n}){extension}");
        return path;
    }
}
