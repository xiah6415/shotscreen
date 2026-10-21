using System.Drawing.Imaging;
using SuYanShot.Capture;
using SuYanShot.Shortcuts;
using SuYanShot.Storage;
using Xunit;

namespace SuYanShot.Tests;

public sealed class ImageExporterTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "SuYanShotTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void FilenameMatchesMacOSNaming()
    {
        Assert.Equal("Screenshot 2026-10-21 at 09.05.03.png", ImageExporter.DefaultFilename(new DateTime(2026, 10, 21, 9, 5, 3)));
    }

    [Fact]
    public void ExportNeverOverwrites()
    {
        var exporter = new ImageExporter(folder);
        using var image = new Bitmap(4, 3);
        var date = new DateTime(2026, 10, 21, 9, 5, 3);
        var first = exporter.ExportPng(image, date: date);
        var second = exporter.ExportPng(image, date: date);
        Assert.EndsWith("09.05.03.png", first);
        Assert.EndsWith("09.05.03 (2).png", second);
        using var loaded = new Bitmap(second);
        Assert.Equal(new Size(4, 3), loaded.Size);
    }

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }
}

public sealed class KeyShortcutTests
{
    [Theory]
    [InlineData("Ctrl+Shift+1", Keys.D1, true, false, true, false)]
    [InlineData("ctrl + alt + s", Keys.S, true, true, false, false)]
    [InlineData("Win+Shift+F5", Keys.F5, false, false, true, true)]
    [InlineData("F9", Keys.F9, false, false, false, false)]
    [InlineData("Alt+PrintScreen", Keys.PrintScreen, false, true, false, false)]
    public void Parses(string text, Keys key, bool control, bool alt, bool shift, bool win)
    {
        Assert.True(KeyShortcut.TryParse(text, out var shortcut));
        Assert.Equal(new KeyShortcut(key, control, alt, shift, win), shortcut);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("Shift+1")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+1+2")]
    [InlineData("Ctrl+Banana")]
    public void Rejects(string text) => Assert.False(KeyShortcut.TryParse(text, out _));

    [Fact]
    public void RoundTrips()
    {
        Assert.True(KeyShortcut.TryParse("Shift+Ctrl+2", out var shortcut));
        Assert.Equal("Ctrl+Shift+2", shortcut.ToString());
    }
}

public sealed class ShortcutStoreTests : IDisposable
{
    private readonly string file = Path.Combine(Path.GetTempPath(), "SuYanShotTests", Guid.NewGuid().ToString("N"), "shortcuts.json");

    [Fact]
    public void MissingFileGivesDefaults()
    {
        var shortcuts = new ShortcutStore(file).Load();
        Assert.Equal("Ctrl+Shift+1", shortcuts[ShortcutAction.Region].ToString());
        Assert.Equal(4, shortcuts.Count);
    }

    [Fact]
    public void ClearedInvalidAndDuplicateEntriesAreDropped()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, """{ "Region": "", "Window": "Ctrl+Alt+W", "FullScreen": "nonsense", "Delayed": "Ctrl+Alt+W" }""");
        var shortcuts = new ShortcutStore(file).Load();
        Assert.False(shortcuts.ContainsKey(ShortcutAction.Region));
        Assert.Equal("Ctrl+Alt+W", shortcuts[ShortcutAction.Window].ToString());
        Assert.False(shortcuts.ContainsKey(ShortcutAction.FullScreen));
        Assert.False(shortcuts.ContainsKey(ShortcutAction.Delayed));
    }

    [Fact]
    public void CorruptFileFallsBackToDefaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{ not json");
        Assert.Equal(4, new ShortcutStore(file).Load().Count);
    }

    public void Dispose()
    {
        var folder = Path.GetDirectoryName(file)!;
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }
}

public sealed class FrozenDesktopTests
{
    [Fact]
    public void CropUsesScreenCoordinatesIncludingNegativeOrigins()
    {
        // A monitor left of the primary puts the virtual screen at x = -100.
        var bitmap = new Bitmap(300, 100, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap)) g.Clear(Color.Black);
        bitmap.SetPixel(110, 20, Color.Red); // screen point (10, 20)
        using var desktop = new FrozenDesktop(bitmap, new Rectangle(-100, 0, 300, 100));

        using var crop = desktop.Crop(new Rectangle(10, 20, 5, 5))!;
        Assert.Equal(Color.Red.ToArgb(), crop.GetPixel(0, 0).ToArgb());

        using var clipped = desktop.Crop(new Rectangle(180, 90, 50, 50))!;
        Assert.Equal(new Size(20, 10), clipped.Size);

        Assert.Null(desktop.Crop(new Rectangle(500, 500, 10, 10)));
    }

    [Fact]
    public void BlankDetection()
    {
        using var black = new Bitmap(50, 50);
        using (var g = Graphics.FromImage(black)) g.Clear(Color.Black);
        Assert.True(ScreenCapture.LooksBlank(black));
        using var content = new Bitmap(50, 50);
        using (var g = Graphics.FromImage(content)) g.Clear(Color.SteelBlue);
        Assert.False(ScreenCapture.LooksBlank(content));
    }
}
