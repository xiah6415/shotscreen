using SuYanShot.App;
using SuYanShot.Shortcuts;

namespace SuYanShot.UI;

/// 初次設定, as on macOS: shown on first launch (and from the tray menu). Windows needs no permissions, so it covers the
/// shortcuts (and any Windows refused), whether to keep clipboard history, and starting at sign-in.
internal sealed class WelcomeWindow : Form
{
    private static readonly (ShortcutAction Action, string Title)[] Listed =
    {
        (ShortcutAction.AllInOne, "All-in-One"),
        (ShortcutAction.Region, "框選截圖"),
        (ShortcutAction.Window, "視窗截圖"),
        (ShortcutAction.FullScreen, "全螢幕截圖"),
        (ShortcutAction.Scroll, "捲動截圖"),
        (ShortcutAction.AnnotateClipboard, "標註剪貼簿裡的圖片"),
        (ShortcutAction.Clipboard, "剪貼簿紀錄"),
    };

    private readonly AppSettings settings;

    /// <param name="refused">Actions whose shortcut Windows would not register (taken by another program).</param>
    public WelcomeWindow(AppSettings settings, IReadOnlyDictionary<ShortcutAction, KeyShortcut> shortcuts,
                         IReadOnlySet<ShortcutAction> refused, bool clipboardEnabled, Action<bool> setClipboardEnabled, Action openSettings)
    {
        this.settings = settings;
        Text = "歡迎使用拾光";
        Icon = AppIcon.Window;
        Font = new Font("Microsoft JhengHei UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(20);
        TopMost = true; // first launch happens behind whatever the user is doing

        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
        var row = 0;

        var header = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 0, 0, 10) };
        header.Controls.Add(new PictureBox { Image = AppIcon.Render(56), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(56, 56), Margin = new Padding(0, 0, 12, 0) });
        var intro = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        intro.Controls.Add(new Label { Text = "歡迎使用拾光", AutoSize = true, Font = new Font("Microsoft JhengHei UI", 14f, FontStyle.Bold) });
        intro.Controls.Add(Note("截圖、捲動截圖、標註和剪貼簿紀錄，都在通知區域（右下角）的拾光圖示裡。花一分鐘看一下下面幾項。", 380));
        header.Controls.Add(intro);
        Span(layout, header, row++);

        Span(layout, Heading("快捷鍵"), row++);
        foreach (var (action, title) in Listed)
        {
            layout.Controls.Add(new Label { Text = title, AutoSize = true, Margin = new Padding(0, 3, 12, 3) }, 0, row);
            var taken = refused.Contains(action);
            var keys = shortcuts.TryGetValue(action, out var shortcut) ? shortcut.ToString() : "（未設定）";
            layout.Controls.Add(new Label
            {
                Text = taken ? $"{keys}　已被其他程式使用" : keys,
                AutoSize = true,
                ForeColor = taken ? Color.Firebrick : SystemColors.ControlText,
                Margin = new Padding(0, 3, 0, 3),
            }, 1, row++);
        }
        Span(layout, Note(refused.Count > 0
            ? "紅色的快捷鍵 Windows 不讓拾光使用，到「設定…」換一組就好。"
            : "要換的話到「設定…」。PrintScreen 也可以用，先在 Windows「設定 → 協助工具 → 鍵盤」關掉「使用 Print Screen 鍵開啟螢幕擷取」。"), row++);
        var settingsButton = new Button { Text = "打開設定…", AutoSize = true, Margin = new Padding(0, 6, 0, 4) };
        settingsButton.Click += (_, _) => openSettings();
        Span(layout, settingsButton, row++);

        Span(layout, Heading("剪貼簿紀錄"), row++);
        var recordClipboard = new CheckBox { Text = "記錄複製過的文字、圖片和檔案", AutoSize = true, Checked = clipboardEnabled, Margin = new Padding(0, 2, 0, 2) };
        recordClipboard.CheckedChanged += (_, _) => setClipboardEnabled(recordClipboard.Checked);
        Span(layout, recordClipboard, row++);
        var clipboardKeys = shortcuts.TryGetValue(ShortcutAction.Clipboard, out var clip) ? $"按 {clip} 叫出來。" : "";
        Span(layout, Note($"{clipboardKeys}只存在這台電腦。密碼管理工具標記為機密的內容不會被記錄。"), row++);

        Span(layout, Heading("一般"), row++);
        var startAtLogin = new CheckBox { Text = "登入 Windows 時自動啟動（讓快捷鍵一開機就能用）", AutoSize = true, Checked = StartupRegistration.IsEnabled, Margin = new Padding(0, 2, 0, 2) };
        startAtLogin.CheckedChanged += (_, _) => StartupRegistration.IsEnabled = startAtLogin.Checked;
        Span(layout, startAtLogin, row++);

        var footer = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill, Margin = new Padding(0, 16, 0, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(Note("之後可以從拾光圖示 → 初次設定… 再打開這裡。", 330), 0, 0);
        var done = new Button { Text = "開始使用", AutoSize = true };
        done.Click += (_, _) => Close();
        footer.Controls.Add(done, 1, 0);
        Span(layout, footer, row++);

        AcceptButton = done;
        Controls.Add(layout);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // Closing counts as done too; the menu's 初次設定… brings it back.
        settings.OnboardingCompleted = true;
        try { settings.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        base.OnFormClosed(e);
    }

    private static Label Heading(string text) =>
        new() { Text = text, AutoSize = true, Font = new Font("Microsoft JhengHei UI", 10.5f, FontStyle.Bold), Margin = new Padding(0, 12, 0, 6) };

    private static Label Note(string text, int width = 460) =>
        new() { Text = text, AutoSize = true, MaximumSize = new Size(width, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 4, 0, 0) };

    private static void Span(TableLayoutPanel layout, Control control, int row)
    {
        layout.Controls.Add(control, 0, row);
        layout.SetColumnSpan(control, 2);
    }
}
