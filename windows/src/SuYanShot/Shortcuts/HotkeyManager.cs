using System.Runtime.InteropServices;
using SuYanShot.Native;

namespace SuYanShot.Shortcuts;

/// Global hotkeys, raised as <see cref="Pressed"/> on the UI thread.
///
/// Two layers. `RegisterHotKey` claims each combination system-wide and tells us when another program already owns it.
/// A low-level keyboard hook (`WH_KEYBOARD_LL`) then catches those same combinations first and swallows them, because
/// many full-screen games register raw input with RIDEV_NOHOTKEYS, which silently disables `RegisterHotKey` while the game
/// is in front. Swallowing the key in the hook also stops the hotkey message, so an action never fires twice.
/// Windows does not send other programs' input to the hook while an app running as administrator is in front; then only
/// `RegisterHotKey` applies.
internal sealed class HotkeyManager : NativeWindow, IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    private readonly Dictionary<int, ShortcutAction> registered = new();
    /// Successfully registered shortcuts, for the hook to match.
    private readonly Dictionary<KeyShortcut, ShortcutAction> active = new();
    /// Keys whose key-down the hook swallowed; their key-up is swallowed too, and repeats are ignored (like MOD_NOREPEAT).
    private readonly HashSet<int> held = new();
    private readonly HookProc hookProc; // kept alive for as long as the hook is installed
    private IntPtr hook;

    public event Action<ShortcutAction>? Pressed;

    /// Whether the low-level hook is installed (it can fail in restricted sessions; hotkeys still work then).
    public bool HookInstalled => hook != IntPtr.Zero;

    /// <param name="useHook">False only in tests, to show what RegisterHotKey alone does.</param>
    public HotkeyManager(bool useHook = true)
    {
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) }); // HWND_MESSAGE
        hookProc = OnKeyboard;
        if (useHook) hook = SetWindowsHookEx(WH_KEYBOARD_LL, hookProc, GetModuleHandle(null), 0);
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
            {
                registered[id] = action;
                active[shortcut] = action;
            }
            else
            {
                // Another program owns it; leave the combination to that program in the hook too.
                failed.Add((action, shortcut));
            }
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

    /// Runs on the UI thread for every key event in the session; must return quickly, so actions are posted.
    private IntPtr OnKeyboard(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && active.Count > 0)
        {
            var vk = Marshal.ReadInt32(data); // KBDLLHOOKSTRUCT.vkCode
            var kind = (int)message;
            if (kind is WM_KEYUP or WM_SYSKEYUP)
            {
                if (held.Remove(vk)) return 1;
            }
            else if (kind is WM_KEYDOWN or WM_SYSKEYDOWN)
            {
                if (held.Contains(vk)) return 1; // auto-repeat of a shortcut we already handled
                if (Match(vk) is ShortcutAction action)
                {
                    held.Add(vk);
                    BeginInvoke(action);
                    return 1;
                }
            }
        }
        return CallNextHookEx(hook, code, message, data);
    }

    private ShortcutAction? Match(int vk)
    {
        var pressed = new KeyShortcut((Keys)vk, IsDown(VK_CONTROL), IsDown(VK_MENU), IsDown(VK_SHIFT), IsDown(VK_LWIN) || IsDown(VK_RWIN));
        return active.TryGetValue(pressed, out var action) ? action : null;
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private void BeginInvoke(ShortcutAction action)
    {
        // Post to our own message-only window so the hook returns before the action (which may open windows) runs.
        PostMessage(Handle, NativeMethods.WM_HOTKEY, (IntPtr)((int)action + 1), IntPtr.Zero);
    }

    private void UnregisterAll()
    {
        foreach (var id in registered.Keys) NativeMethods.UnregisterHotKey(Handle, id);
        registered.Clear();
        active.Clear();
        held.Clear();
    }

    public void Dispose()
    {
        UnregisterAll();
        if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
        hook = IntPtr.Zero;
        DestroyHandle();
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
