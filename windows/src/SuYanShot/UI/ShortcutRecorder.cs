using System.Runtime.InteropServices;
using SuYanShot.Shortcuts;

namespace SuYanShot.UI;

/// A button that records a key combination: click it, press the keys. Esc cancels, Backspace / Delete clears.
internal sealed class ShortcutRecorder : Button
{
    private KeyShortcut? shortcut;
    private bool recording;

    /// The user pressed a valid combination (or null after clearing).
    public event Action<KeyShortcut?>? Recorded;

    /// Recording started or stopped; the window suspends global hotkeys meanwhile so they reach this button.
    public event Action<bool>? RecordingChanged;

    /// A key was pressed that cannot be a shortcut; the argument explains why.
    public event Action<string>? Rejected;

    public ShortcutRecorder()
    {
        UseVisualStyleBackColor = true;
        UpdateText();
    }

    public KeyShortcut? Shortcut
    {
        get => shortcut;
        set { shortcut = value; UpdateText(); }
    }

    public bool IsRecording => recording;

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        SetRecording(!recording);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        SetRecording(false);
    }

    public void SetRecording(bool value)
    {
        if (recording == value) return;
        recording = value;
        UpdateText();
        RecordingChanged?.Invoke(value);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!recording) return base.ProcessCmdKey(ref msg, keyData);
        HandleKey(keyData & Keys.KeyCode, keyData);
        return true;
    }

    // Windows delivers PrintScreen only as key-up.
    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (recording && e.KeyCode == Keys.PrintScreen) HandleKey(Keys.PrintScreen, e.KeyData);
        else base.OnKeyUp(e);
    }

    private void HandleKey(Keys key, Keys keyData)
    {
        if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin) return;
        var control = keyData.HasFlag(Keys.Control);
        var alt = keyData.HasFlag(Keys.Alt);
        var shift = keyData.HasFlag(Keys.Shift);
        var win = IsDown(VK_LWIN) || IsDown(VK_RWIN);
        var plain = !control && !alt && !shift && !win;

        if (plain && key == Keys.Escape)
        {
            SetRecording(false);
            return;
        }
        if (plain && key is Keys.Back or Keys.Delete)
        {
            Finish(null);
            return;
        }
        if (KeyShortcut.TryCreate(key, control, alt, shift, win, out var created))
            Finish(created);
        else
            Rejected?.Invoke("要包含 Ctrl、Alt 或 Win，再加一個字母、數字或 F 鍵（F1–F24 和 PrintScreen 可以單獨用）。");
    }

    private void Finish(KeyShortcut? value)
    {
        shortcut = value;
        SetRecording(false);
        Recorded?.Invoke(value);
    }

    private void UpdateText() => Text = recording ? "請按下組合鍵…" : shortcut?.ToString() ?? "未設定";

    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    private static bool IsDown(int virtualKey) => (GetKeyState(virtualKey) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);
}
