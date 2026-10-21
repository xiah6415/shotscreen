using System.Diagnostics;
using System.Drawing.Drawing2D;
using SuYanShot.Capture;
using SuYanShot.ClipboardHistory;
using SuYanShot.Shortcuts;
using SuYanShot.Storage;
using SuYanShot.UI;

namespace SuYanShot.App;

/// The Windows counterpart of the macOS menu-bar app: a notification-area icon with the same Chinese menu.
internal sealed class TrayApplication : ApplicationContext
{
    private readonly ImageExporter exporter = new();
    private readonly CaptureCoordinator coordinator;
    private readonly ShortcutStore shortcutStore = new();
    private readonly HotkeyManager hotkeys = new();
    private readonly NotifyIcon trayIcon;
    private readonly Dictionary<ShortcutAction, ToolStripMenuItem> actionItems = new();
    private readonly ClipboardFeature clipboard;
    private SettingsWindow? settingsWindow;

    public TrayApplication()
    {
        coordinator = new CaptureCoordinator(exporter);
        hotkeys.Pressed += OnHotkey;

        var menu = new ContextMenuStrip();
        actionItems[ShortcutAction.AllInOne] = Item("All-in-One", () => coordinator.CaptureAllInOne());
        menu.Items.Add(actionItems[ShortcutAction.AllInOne]);
        menu.Items.Add(new ToolStripSeparator());
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
        actionItems[ShortcutAction.Clipboard] = Item("剪貼簿紀錄", () => clipboard!.TogglePanel());
        menu.Items.Add(actionItems[ShortcutAction.Clipboard]);
        var phoneSwitch = new ToolStripMenuItem("手機連線", null, (_, _) => clipboard!.PhoneShare.IsEnabled = !clipboard.PhoneShare.IsEnabled);
        menu.Items.Add(phoneSwitch);
        menu.Items.Add(Item("在手機上開啟…", () => clipboard!.ShowPhoneShare()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Opening += (_, _) => phoneSwitch.Checked = clipboard!.PhoneShare.IsRunning;
        menu.Items.Add(Item("打開截圖資料夾", () => Process.Start(new ProcessStartInfo(exporter.DefaultDirectory) { UseShellExecute = true })));
        menu.Items.Add(Item("設定…", OpenSettings));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("結束 素顏 Shot", ExitThread));

        // Created after the menu, so a WinForms synchronization context exists for the clipboard and phone server.
        clipboard = new ClipboardFeature(AppSettings.Load());

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

        var failed = ApplyShortcuts(shortcutStore.Load());
        if (failed.Count > 0)
            trayIcon.ShowBalloonTip(5000, "素顏 Shot", "有快捷鍵已被其他程式使用，可以在「設定…」換一組。", ToolTipIcon.Warning);
    }

    private static ToolStripMenuItem Item(string title, Action action) => new(title, null, (_, _) => action());

    private void OnHotkey(ShortcutAction action)
    {
        _ = action switch
        {
            ShortcutAction.AllInOne => coordinator.CaptureAllInOne(),
            ShortcutAction.Region => coordinator.CaptureRegion(),
            ShortcutAction.Window => coordinator.CaptureWindow(),
            ShortcutAction.FullScreen => coordinator.CaptureFullScreen(),
            ShortcutAction.Delayed => coordinator.CaptureDelayed(3),
            ShortcutAction.Clipboard => ToggleClipboard(),
            _ => Task.CompletedTask,
        };
    }

    private Task ToggleClipboard()
    {
        clipboard.TogglePanel();
        return Task.CompletedTask;
    }

    /// Registers the shortcuts, shows them in the menu, and returns the actions Windows refused.
    private IReadOnlySet<ShortcutAction> ApplyShortcuts(IReadOnlyDictionary<ShortcutAction, KeyShortcut> shortcuts)
    {
        var failed = hotkeys.Apply(shortcuts).Select(f => f.Action).ToHashSet();
        foreach (var (action, item) in actionItems)
            item.ShortcutKeyDisplayString = shortcuts.TryGetValue(action, out var shortcut) ? shortcut.ToString() : null;
        return failed;
    }

    private void OpenSettings()
    {
        if (settingsWindow is { IsDisposed: false })
        {
            settingsWindow.Activate();
            return;
        }
        settingsWindow = new SettingsWindow(shortcutStore, ApplyShortcuts, clipboard);
        settingsWindow.FormClosed += (_, _) => settingsWindow = null;
        settingsWindow.Show();
        settingsWindow.Activate();
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
        settingsWindow?.Close();
        clipboard.Dispose();
        hotkeys.Dispose();
        trayIcon.Visible = false;
        trayIcon.Dispose();
        base.ExitThreadCore();
    }
}
