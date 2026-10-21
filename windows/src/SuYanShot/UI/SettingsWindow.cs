using SuYanShot.App;
using SuYanShot.Shortcuts;

namespace SuYanShot.UI;

/// 設定: a recorder per capture action (changes apply immediately), 還原預設, and 登入時啟動.
internal sealed class SettingsWindow : Form
{
    private static readonly (ShortcutAction Action, string Title)[] Rows =
    {
        (ShortcutAction.Region, "框選截圖"),
        (ShortcutAction.Window, "視窗截圖"),
        (ShortcutAction.FullScreen, "全螢幕截圖"),
        (ShortcutAction.Delayed, "延遲截圖（3 秒）"),
    };

    private readonly ShortcutStore store;
    private readonly Func<IReadOnlyDictionary<ShortcutAction, KeyShortcut>, IReadOnlySet<ShortcutAction>> applyShortcuts;
    private readonly Dictionary<ShortcutAction, ShortcutRecorder> recorders = new();
    private readonly Dictionary<ShortcutAction, Label> warnings = new();
    private const string DefaultHint = "點一下按鈕，再按下新的組合鍵。要包含 Ctrl、Alt 或 Win（F1–F24 和 PrintScreen 可以單獨用）。按 Esc 取消，按 Backspace 清除。";

    private readonly Label hint = Note(DefaultHint);

    /// <param name="applyShortcuts">Registers the given shortcuts globally and returns the actions Windows refused.</param>
    public SettingsWindow(ShortcutStore store, Func<IReadOnlyDictionary<ShortcutAction, KeyShortcut>, IReadOnlySet<ShortcutAction>> applyShortcuts)
    {
        this.store = store;
        this.applyShortcuts = applyShortcuts;

        Text = "素顏 Shot 設定";
        Font = new Font("Microsoft JhengHei UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);
        KeyPreview = true;

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 3,
            Dock = DockStyle.Fill,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));

        layout.Controls.Add(Heading("快捷鍵"), 0, 0);
        layout.SetColumnSpan(layout.GetControlFromPosition(0, 0)!, 3);

        var row = 1;
        foreach (var (action, title) in Rows)
        {
            layout.Controls.Add(new Label { Text = title, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 6) }, 0, row);
            var recorder = new ShortcutRecorder { Dock = DockStyle.Fill, Height = 30, Margin = new Padding(0, 3, 0, 3) };
            recorder.Recorded += shortcut => Assign(action, shortcut);
            recorder.RecordingChanged += OnRecordingChanged;
            recorder.Rejected += message => hint.Text = message;
            recorders[action] = recorder;
            layout.Controls.Add(recorder, 1, row);
            var warning = new Label { AutoSize = true, ForeColor = Color.Firebrick, Anchor = AnchorStyles.Left, Margin = new Padding(8, 6, 0, 6) };
            warnings[action] = warning;
            layout.Controls.Add(warning, 2, row);
            row++;
        }

        AddSpanning(layout, hint, row++);
        AddSpanning(layout, Note("要用 PrintScreen 的話，先到 Windows「設定 → 協助工具 → 鍵盤」，把「使用 Print Screen 鍵開啟螢幕擷取」關掉。"), row++);

        var reset = new Button { Text = "還原預設", AutoSize = true, Margin = new Padding(0, 6, 0, 12) };
        reset.Click += (_, _) => Display(store.ResetToDefaults());
        AddSpanning(layout, reset, row++);

        AddSpanning(layout, Heading("一般"), row++);
        var startAtLogin = new CheckBox { Text = "登入 Windows 時自動啟動", AutoSize = true, Checked = StartupRegistration.IsEnabled, Margin = new Padding(0, 4, 0, 4) };
        startAtLogin.CheckedChanged += (_, _) => StartupRegistration.IsEnabled = startAtLogin.Checked;
        AddSpanning(layout, startAtLogin, row++);
        AddSpanning(layout, Note("截圖存檔在桌面，檔名和 Mac 版相同。"), row++);

        var close = new Button { Text = "關閉", AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(0, 12, 0, 0) };
        close.Click += (_, _) => Close();
        layout.Controls.Add(close, 2, row);

        Controls.Add(layout);
        Display(store.Load());
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Esc closes the window, except while a recorder is listening (there it only cancels recording).
        if (e.KeyCode == Keys.Escape && !recorders.Values.Any(r => r.IsRecording)) Close();
        base.OnKeyDown(e);
    }

    private void Assign(ShortcutAction action, KeyShortcut? shortcut) => Display(store.Set(action, shortcut));

    private void OnRecordingChanged(bool recording)
    {
        if (recording)
        {
            hint.Text = "請按下新的組合鍵…（Esc 取消，Backspace 清除）";
            applyShortcuts(new Dictionary<ShortcutAction, KeyShortcut>()); // let the keys reach the recorder
        }
        else if (!recorders.Values.Any(r => r.IsRecording))
        {
            hint.Text = DefaultHint;
            Display(store.Load());
        }
    }

    /// Displays and registers the shortcuts, marking any Windows refused.
    private void Display(Dictionary<ShortcutAction, KeyShortcut> shortcuts)
    {
        var failed = applyShortcuts(shortcuts);
        foreach (var (action, recorder) in recorders)
        {
            recorder.Shortcut = shortcuts.TryGetValue(action, out var shortcut) ? shortcut : null;
            warnings[action].Text = failed.Contains(action) ? "⚠ 已被其他程式使用" : "";
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Display(store.Load()); // re-register in case the window closed mid-recording
        base.OnFormClosed(e);
    }

    private static Label Heading(string text) =>
        new() { Text = text, AutoSize = true, Font = new Font("Microsoft JhengHei UI", 10.5f, FontStyle.Bold), Margin = new Padding(0, 4, 0, 8) };

    private static Label Note(string text) =>
        new() { Text = text, AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 6, 0, 0) };

    private static void AddSpanning(TableLayoutPanel layout, Control control, int row)
    {
        layout.Controls.Add(control, 0, row);
        layout.SetColumnSpan(control, 3);
    }
}
