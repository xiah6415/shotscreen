using SuYanShot.Annotations;
using SuYanShot.App;
using SuYanShot.Capture;
using SuYanShot.ClipboardHistory;
using SuYanShot.Storage;
using SuYanShot.UI;
using Xunit;

namespace SuYanShot.Tests;

/// Builds and lays out every window once (without showing it), so property mistakes that only throw at runtime — like a
/// translucent BackColor on a form — fail here instead of in front of the user.
public sealed class WindowSmokeTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "SuYanShotTests", Guid.NewGuid().ToString("N"));

    private static void OnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static void Build(Form form)
    {
        using (form)
        {
            form.CreateControl();
            _ = form.Handle;
            form.PerformLayout();
            using var bitmap = new Bitmap(Math.Max(1, form.Width), Math.Max(1, form.Height));
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); // runs OnPaint
        }
    }

    [Fact]
    public void ClipboardPanel() => OnStaThread(() =>
    {
        var store = new ClipboardStore(folder);
        store.Add(new ClipboardSnapshot(ClipboardKind.Text) { Text = "hello" });
        store.CreateTag("工作", store.Items[0].Id);
        Build(new ClipboardPanel(store, (_, _, _) => { }));
    });

    [Fact]
    public void PreviewCardAnnotationEditorAndScrollHud() => OnStaThread(() =>
    {
        var exporter = new ImageExporter(folder);
        Build(new FloatingThumbnail(new Bitmap(400, 300), exporter, Screen.PrimaryScreen!, (_, _) => { }));
        Build(new AnnotationEditor(new Bitmap(800, 500), DateTime.Now, exporter));
        using var hud = new ScrollCaptureHud(new Rectangle(200, 200, 600, 400), () => { }, () => { }, _ => { });
        hud.Update(new ScrollCaptureProgress(400, new FrameResult.First(), new Bitmap(200, 133), AutoScrollStatus.Off));
    });

    [Fact]
    public void PhoneShareWindow() => OnStaThread(() =>
    {
        var store = new ClipboardStore(folder);
        using var share = new PhoneShare(store, _ => { }, new AppSettings { FilePath = Path.Combine(folder, "settings.json") },
            new PhoneShare.Options { Port = 0, FindNetwork = () => null, Advertise = false, AuthPath = Path.Combine(folder, "auth.json") });
        Build(new PhoneShareWindow(share));
    });

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }
}
