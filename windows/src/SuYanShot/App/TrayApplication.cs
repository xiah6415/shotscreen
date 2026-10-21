using System.Diagnostics;
using System.Drawing.Drawing2D;
using Microsoft.Win32;
using SuYanShot.Capture;
using SuYanShot.Shortcuts;
using SuYanShot.Storage;

namespace SuYanShot.App;

/// The Windows counterpart of the macOS menu-bar app: a notification-area icon with the same Chinese menu.
internal sealed class TrayApplication : ApplicationContext
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "SuYanShot";

    private readonly ImageExporter exporter = new();
    private readonly CaptureCoordinator coordinator;
    private readonly ShortcutStore shortcutStore = new();
    private readonly HotkeyManager hotkeys = new();
    private readonly NotifyIcon trayIcon;
    private readonly Dictionary<ShortcutAction, ToolStripMenuItem> actionItems = new();
    private readonly FileSystemWatcher? shortcutWatcher;
    private readonly ToolStripMenuItem startAtLogin;

    public TrayApplication()
    {
        coordinator = new CaptureCoordinator(exporter);
        hotkeys.Pressed += OnHotkey;

        var menu = new ContextMenuStrip();
        actionItems[ShortcutAction.Region] = Item("框選截圖", () => coordinator.CaptureRegion());
        actionItems[ShortcutAction.Window] = Item("視窗截圖", () => coordinator.CaptureWindow());
        actionItems[ShortcutAction.FullScreen] = Item("全螢幕截圖", () => coordinator.CaptureFullScreen());
        menu.Items.AddRange(new ToolStripItem[] { actionItems[ShortcutAction.Region], actionItems[ShortcutAction.Window], actionItems[ShortcutAction.FullScreen] });

        var delayed = new ToolStripMenuItem("延遲截圖");
        foreach (var seconds in new[] { 3, 5, 10 })
        {
            var item = Item($"{seconds} 秒", () => coordinator.CaptureDelayed(seconds));
            if (seconds == 3) actionItems[ShortcutAction.Delayed] = item;
            delayed.DropDownItems.Add(item);
        }
        menu.Items.Add(delayed);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("打開截圖資料夾", () => Open(exporter.DefaultDirectory)));
        menu.Items.Add(Item("編輯快捷鍵…", EditShortcuts));
        startAtLogin = new ToolStripMenuItem("登入時啟動", null, (_, _) => ToggleStartAtLogin()) { Checked = StartsAtLogin() };
        menu.Items.Add(startAtLogin);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("結束 素顏 Shot", ExitThread));

        trayIcon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "素顏 Shot",
            ContextMenuStrip = menu,
            Visible = true,
        };
        // Left-click opens the same menu, like clicking the macOS menu-bar item.
        trayIcon.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            typeof(NotifyIcon).GetMethod("ShowContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.Invoke(trayIcon, null);
        };

        shortcutStore.EnsureFileExists();
        ApplyShortcuts(showWarnings: true);
        shortcutWatcher = WatchShortcutFile();
    }

    private static ToolStripMenuItem Item(string title, Action action) => new(title, null, (_, _) => action());

    private void OnHotkey(ShortcutAction action)
    {
        _ = action switch
        {
            ShortcutAction.Region => coordinator.CaptureRegion(),
            ShortcutAction.Window => coordinator.CaptureWindow(),
            ShortcutAction.FullScreen => coordinator.CaptureFullScreen(),
            ShortcutAction.Delayed => coordinator.CaptureDelayed(3),
            _ => Task.CompletedTask,
        };
    }

    private void ApplyShortcuts(bool showWarnings)
    {
        var shortcuts = shortcutStore.Load();
        var failed = hotkeys.Apply(shortcuts);
        foreach (var (action, item) in actionItems)
            item.ShortcutKeyDisplayString = shortcuts.TryGetValue(action, out var shortcut) ? shortcut.ToString() : null;
        if (showWarnings && failed.Count > 0)
        {
            var list = string.Join("、", failed.Select(f => f.Shortcut.ToString()));
            trayIcon.ShowBalloonTip(5000, "素顏 Shot", $"這些快捷鍵已被其他程式使用：{list}。可以在「編輯快捷鍵…」換一組。", ToolTipIcon.Warning);
        }
    }

    private FileSystemWatcher? WatchShortcutFile()
    {
        var folder = Path.GetDirectoryName(shortcutStore.FilePath);
        if (folder == null || !Directory.Exists(folder)) return null;
        var watcher = new FileSystemWatcher(folder, Path.GetFileName(shortcutStore.FilePath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        var ui = SynchronizationContext.Current;
        // Editors often write twice; debounce so the hotkeys are re-registered once.
        System.Threading.Timer? debounce = null;
        watcher.Changed += (_, _) =>
        {
            debounce?.Dispose();
            debounce = new System.Threading.Timer(_ => ui?.Post(_ => ApplyShortcuts(showWarnings: true), null), null, 400, Timeout.Infinite);
        };
        return watcher;
    }

    private void EditShortcuts() => Process.Start(new ProcessStartInfo("notepad.exe", $"\"{shortcutStore.FilePath}\""));

    private static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    private static bool StartsAtLogin()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string;
    }

    private void ToggleStartAtLogin()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (StartsAtLogin()) key.DeleteValue(RunValue, throwOnMissingValue: false);
        else key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
        startAtLogin.Checked = StartsAtLogin();
    }

    /// A viewfinder glyph drawn at runtime, so the project needs no binary icon file yet.
    private static Icon CreateIcon()
    {
        var size = SystemInformation.SmallIconSize.Width;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var stroke = Math.Max(1.5f, size / 10f);
            using var pen = new Pen(Color.White, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            var inset = stroke;
            var far = size - inset;
            var arm = size * 0.3f;
            g.DrawLines(pen, new[] { new PointF(inset, inset + arm), new PointF(inset, inset), new PointF(inset + arm, inset) });
            g.DrawLines(pen, new[] { new PointF(far - arm, inset), new PointF(far, inset), new PointF(far, inset + arm) });
            g.DrawLines(pen, new[] { new PointF(far, far - arm), new PointF(far, far), new PointF(far - arm, far) });
            g.DrawLines(pen, new[] { new PointF(inset + arm, far), new PointF(inset, far), new PointF(inset, far - arm) });
            var dot = size * 0.22f;
            g.FillEllipse(Brushes.White, (size - dot) / 2, (size - dot) / 2, dot, dot);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }

    protected override void ExitThreadCore()
    {
        shortcutWatcher?.Dispose();
        hotkeys.Dispose();
        trayIcon.Visible = false;
        trayIcon.Dispose();
        base.ExitThreadCore();
    }
}
