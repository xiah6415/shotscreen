using System.Diagnostics;
using SuYanShot.Capture;
using SuYanShot.ClipboardHistory;
using SuYanShot.Shortcuts;
using SuYanShot.Storage;
using SuYanShot.UI;
using SuYanShot.Update;

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
    private readonly UpdateController updates;
    private readonly ToolStripMenuItem phoneSwitch;
    private SettingsWindow? settingsWindow;
    private bool trayShowsSharing;

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
        actionItems[ShortcutAction.Scroll] = Item("捲動截圖", () => _ = coordinator.CaptureScrolling());
        menu.Items.AddRange(new ToolStripItem[] { actionItems[ShortcutAction.Region], actionItems[ShortcutAction.Window], actionItems[ShortcutAction.FullScreen], actionItems[ShortcutAction.Scroll] });

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
        phoneSwitch = Item("手機分享", () => clipboard!.TogglePhoneShare());
        menu.Items.Add(phoneSwitch);
        menu.Items.Add(Item("手機分享設定…", () => clipboard!.ShowPhoneShare()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("打開截圖資料夾", () => Process.Start(new ProcessStartInfo(exporter.DefaultDirectory) { UseShellExecute = true })));
        menu.Items.Add(Item("設定…", OpenSettings));
        menu.Items.Add(Item("檢查更新…", () => updates!.CheckNow()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("結束拾光", ExitThread));

        // Created after the menu, so a WinForms synchronization context exists for the clipboard and phone server.
        var settings = AppSettings.Load();
        clipboard = new ClipboardFeature(settings);
        updates = new UpdateController(settings) { CanInstallNow = () => !coordinator.IsCapturing, Exit = ExitThread };

        trayIcon = new NotifyIcon
        {
            Icon = AppIcon.Tray(sharing: false),
            Text = "拾光",
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

        clipboard.PhoneShare.Changed += UpdatePhoneShare;
        UpdatePhoneShare();
        StartupRegistration.RefreshPath();
        updates.Start();

        var failed = ApplyShortcuts(shortcutStore.Load());
        if (failed.Count > 0)
            trayIcon.ShowBalloonTip(5000, "拾光", "有快捷鍵已被其他程式使用，可以在「設定…」換一組。", ToolTipIcon.Warning);
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
            ShortcutAction.Scroll => coordinator.CaptureScrolling(),
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
        settingsWindow = new SettingsWindow(shortcutStore, ApplyShortcuts, clipboard, updates);
        settingsWindow.FormClosed += (_, _) => settingsWindow = null;
        settingsWindow.Show();
        settingsWindow.Activate();
    }

    /// Check mark and status on 手機分享, and a dot on the tray icon while phones can connect (as on macOS).
    private void UpdatePhoneShare()
    {
        var share = clipboard.PhoneShare;
        string? note = share.Status switch
        {
            PhoneShareStatus.Running => "分享中",
            PhoneShareStatus.OtherNetwork => "暫停：不在允許的網路",
            PhoneShareStatus.NeedsPassword => "要先設定密碼",
            PhoneShareStatus.NoNetwork => "等待網路",
            PhoneShareStatus.Failed => "無法啟動",
            _ => null,
        };
        phoneSwitch.Checked = share.IsEnabled;
        phoneSwitch.Text = share.IsEnabled && note != null ? $"手機分享（{note}）" : "手機分享";
        if (trayShowsSharing == share.IsServing) return;
        trayShowsSharing = share.IsServing;
        var old = trayIcon.Icon;
        trayIcon.Icon = AppIcon.Tray(trayShowsSharing);
        old?.Dispose();
    }

    protected override void ExitThreadCore()
    {
        settingsWindow?.Close();
        clipboard.Dispose();
        updates.Dispose();
        hotkeys.Dispose();
        trayIcon.Visible = false;
        trayIcon.Dispose();
        base.ExitThreadCore();
    }
}
