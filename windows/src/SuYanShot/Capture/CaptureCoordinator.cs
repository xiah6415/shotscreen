using SuYanShot.Annotations;
using SuYanShot.Storage;
using SuYanShot.UI;

namespace SuYanShot.Capture;

/// The workflow boundary, as on macOS: get an image from a capture path, copy it to the clipboard, show the preview card.
/// Capture code knows nothing about the clipboard, files or UI.
internal sealed class CaptureCoordinator
{
    private readonly ImageExporter exporter;
    private bool busy;

    public CaptureCoordinator(ImageExporter exporter)
    {
        this.exporter = exporter;
    }

    public Task CaptureRegion() => Run(async () =>
    {
        using var desktop = ScreenCapture.FreezeDesktop();
        var result = await SelectionSession.Run(desktop, SelectionMode.Region, Array.Empty<WindowInfo>());
        return result is SelectionResult.Region region ? desktop.Crop(region.ScreenRect) : null;
    });

    public Task CaptureWindow() => Run(async () =>
    {
        // Snapshot the window list before our overlays exist so they can never be picked.
        var windows = WindowList.Snapshot();
        using var desktop = ScreenCapture.FreezeDesktop();
        return await PickWindow(desktop, windows);
    });

    /// All-in-One: adjust a region, then capture it, or switch to window / full-screen capture on the same frozen desktop.
    public Task CaptureAllInOne() => Run(async () =>
    {
        var windows = WindowList.Snapshot();
        using var desktop = ScreenCapture.FreezeDesktop();
        return await AllInOneSession.Run(desktop) switch
        {
            AllInOneAction.Capture capture => desktop.Crop(capture.ScreenRect),
            AllInOneAction.Window => await PickWindow(desktop, windows),
            AllInOneAction.FullScreen full => desktop.Crop(full.Monitor),
            _ => null,
        };
    });

    private static async Task<Bitmap?> PickWindow(FrozenDesktop desktop, IReadOnlyList<WindowInfo> windows)
    {
        var result = await SelectionSession.Run(desktop, SelectionMode.Window, windows);
        if (result is not SelectionResult.Window window) return null;
        await Task.Delay(50); // let the overlays disappear before PrintWindow asks the window to repaint
        return ScreenCapture.CaptureWindow(window.Info.Handle, desktop);
    }

    /// The whole monitor under the pointer, no picker (same as macOS).
    public Task CaptureFullScreen() => Run(() => Task.FromResult<Bitmap?>(ScreenCapture.CaptureRect(ScreenCapture.ScreenUnderCursor().Bounds)));

    public Task CaptureDelayed(int seconds) => Run(async () =>
    {
        if (!await CountdownHud.Run(seconds)) return null;
        await Task.Delay(80); // the HUD is excluded from capture anyway; this just lets its fade finish
        return ScreenCapture.CaptureRect(ScreenCapture.ScreenUnderCursor().Bounds);
    });

    private async Task Run(Func<Task<Bitmap?>> capture)
    {
        if (busy) return;
        busy = true;
        Bitmap? image;
        try
        {
            image = await capture();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"截圖失敗：{ex.Message}", "拾光", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        finally
        {
            busy = false;
        }
        if (image == null) return;
        ClipboardManager.Copy(image);
        new FloatingThumbnail(image, exporter, ScreenCapture.ScreenUnderCursor(), OpenEditor).Show();
    }

    private void OpenEditor(Bitmap image, DateTime capturedAt)
    {
        var editor = new AnnotationEditor(image, capturedAt, exporter);
        editor.Show();
        editor.Activate();
    }
}
