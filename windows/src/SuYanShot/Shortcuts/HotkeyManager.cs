using SuYanShot.Native;

namespace SuYanShot.Shortcuts;

/// Registers global hotkeys on a hidden message-only window and raises <see cref="Pressed"/> on the UI thread.
internal sealed class HotkeyManager : NativeWindow, IDisposable
{
    private readonly Dictionary<int, ShortcutAction> registered = new();

    public event Action<ShortcutAction>? Pressed;

    public HotkeyManager()
    {
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) }); // HWND_MESSAGE
    }

    /// Replaces all registrations and returns the shortcuts Windows refused (usually taken by another app).
    public List<(ShortcutAction Action, KeyShortcut Shortcut)> Apply(IReadOnlyDictionary<ShortcutAction, KeyShortcut> shortcuts)
    {
        UnregisterAll();
        var failed = new List<(ShortcutAction, KeyShortcut)>();
        foreach (var (action, shortcut) in shortcuts)
        {
            var id = (int)action + 1;
            if (NativeMethods.RegisterHotKey(Handle, id, shortcut.Modifiers | NativeMethods.MOD_NOREPEAT, (uint)shortcut.Key))
                registered[id] = action;
            else
                failed.Add((action, shortcut));
        }
        return failed;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_HOTKEY && registered.TryGetValue((int)m.WParam, out var action))
        {
            Pressed?.Invoke(action);
            return;
        }
        base.WndProc(ref m);
    }

    private void UnregisterAll()
    {
        foreach (var id in registered.Keys) NativeMethods.UnregisterHotKey(Handle, id);
        registered.Clear();
    }

    public void Dispose()
    {
        UnregisterAll();
        DestroyHandle();
    }
}
