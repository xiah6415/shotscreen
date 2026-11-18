using SuYanShot.Annotations;
using SuYanShot.App;
using SuYanShot.Capture;
using SuYanShot.ClipboardHistory;
using SuYanShot.Shortcuts;
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
        hud.Update(new ScrollCaptureProgress(400, new FrameResult.Unchanged(), null, AutoScrollStatus.Paused, ScrollPauseReason.NoMovement));
    });

    [Fact]
    public void WelcomeWindowSavesCompletion() => OnStaThread(() =>
    {
        var settings = new AppSettings { FilePath = Path.Combine(folder, "settings.json") };
        var shortcuts = new ShortcutStore(Path.Combine(folder, "shortcuts.json")).Load();
        var form = new WelcomeWindow(settings, shortcuts, new HashSet<ShortcutAction> { ShortcutAction.Region }, true, _ => { }, () => { });
        form.Show();
        form.Close();
        Assert.True(AppSettings.Load(settings.FilePath).OnboardingCompleted);
    });

    [Fact]
    public void WindowGridShowsThumbnailsAndSearches() => OnStaThread(() =>
    {
        using var target = new Form { Text = "拾光測試視窗", Bounds = new Rectangle(100, 100, 480, 320), BackColor = Color.Orange };
        target.Show();
        Application.DoEvents();
        var info = new WindowInfo(target.Handle, target.Bounds);
        Assert.Equal("拾光測試視窗", WindowGrid.Describe(info).Title);
        Assert.True(WindowGrid.Describe(info).Matches("測試"));
        Assert.False(WindowGrid.Describe(info).Matches("找不到"));

        using var desktop = new FrozenDesktop(new Bitmap(800, 600), new Rectangle(0, 0, 800, 600));
        WindowInfo? picked = null;
        var grid = new WindowGrid(new[] { info }, desktop, Screen.PrimaryScreen!.Bounds, w => picked = w, () => { });
        Build(grid);
        using var shrunk = WindowGrid.Shrink(new Bitmap(1200, 300), new Size(300, 200));
        Assert.Equal(new Size(300, 75), shrunk.Size);
        Assert.Null(picked);
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
