using System.Runtime.InteropServices;
using SuYanShot.Shortcuts;
using Xunit;

namespace SuYanShot.Tests;

/// Drives the real hotkey manager with injected key presses (an unusual combination, so nothing else reacts).
public sealed class HotkeyManagerTests
{
    private const ushort VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_F12 = 0x7B;

    [Fact]
    public void ShortcutFiresOncePerPressEvenWhenHeld()
    {
        var count = 0;
        var hookInstalled = false;
        RunOnMessageLoop(done =>
        {
            var hotkeys = new HotkeyManager();
            hookInstalled = hotkeys.HookInstalled;
            hotkeys.Pressed += action => { if (action == ShortcutAction.Region) count++; };
            KeyShortcut.TryParse("Ctrl+Alt+Shift+F12", out var shortcut);
            hotkeys.Apply(new Dictionary<ShortcutAction, KeyShortcut> { [ShortcutAction.Region] = shortcut });

            Send(Down(VK_CONTROL), Down(VK_MENU), Down(VK_SHIFT), Down(VK_F12), Down(VK_F12), Down(VK_F12), Up(VK_F12),
                 Down(VK_F12), Up(VK_F12), Up(VK_SHIFT), Up(VK_MENU), Up(VK_CONTROL));
            // A different combination must pass through untouched.
            Send(Down(VK_CONTROL), Down(VK_F12), Up(VK_F12), Up(VK_CONTROL));

            var timer = new System.Windows.Forms.Timer { Interval = 600 };
            timer.Tick += (_, _) =>
            {
                timer.Dispose();
                hotkeys.Dispose();
                done();
            };
            timer.Start();
        });
        Assert.True(hookInstalled);
        Assert.Equal(2, count); // two presses: the held repeats count once
    }

    [Fact]
    public void ClearedShortcutsAreLeftAlone()
    {
        var count = 0;
        RunOnMessageLoop(done =>
        {
            var hotkeys = new HotkeyManager();
            hotkeys.Pressed += _ => count++;
            hotkeys.Apply(new Dictionary<ShortcutAction, KeyShortcut>()); // e.g. while the settings window records a new combination
            Send(Down(VK_CONTROL), Down(VK_MENU), Down(VK_SHIFT), Down(VK_F12), Up(VK_F12), Up(VK_SHIFT), Up(VK_MENU), Up(VK_CONTROL));
            var timer = new System.Windows.Forms.Timer { Interval = 400 };
            timer.Tick += (_, _) => { timer.Dispose(); hotkeys.Dispose(); done(); };
            timer.Start();
        });
        Assert.Equal(0, count);
    }

    /// A stand-in for a full-screen game: a separate process whose foreground window registers raw keyboard input with
    /// RIDEV_NOHOTKEYS (as many games do), which stops Windows from delivering RegisterHotKey hotkeys while it is in front.
    /// Uses windows/tests/fake-game.ps1.
    [Theory]
    [InlineData(false, 0)] // RegisterHotKey alone: the "game" swallows the shortcut
    [InlineData(true, 1)]  // with the low-level hook it still works
    public void ShortcutWorksInFrontOfAGameThatDisablesHotkeys(bool useHook, int expected)
    {
        var script = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "fake-game.ps1");
        using var game = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{Path.GetFullPath(script)}\" -Flags 512 -Seconds 6") { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            IntPtr gameWindow = IntPtr.Zero;
            for (var i = 0; i < 100 && gameWindow == IntPtr.Zero; i++)
            {
                Thread.Sleep(100);
                gameWindow = FindWindow(null, "fake game process");
            }
            Assert.NotEqual(IntPtr.Zero, gameWindow);
            Thread.Sleep(300);

            var count = 0;
            var foreground = false;
            RunOnMessageLoop(done =>
            {
                // Clicking the window is the reliable way for a background test process to bring it to the front.
                Cursor.Position = new Point(250, 200);
                Send(Mouse(0x0002), Mouse(0x0004)); // left down, up
                var settle = new System.Windows.Forms.Timer { Interval = 300 };
                settle.Tick += (_, _) =>
                {
                    settle.Dispose();
                    foreground = GetForegroundWindow() == gameWindow;
                    var hotkeys = new HotkeyManager(useHook);
                    hotkeys.Pressed += _ => count++;
                    KeyShortcut.TryParse("Ctrl+Alt+Shift+F12", out var shortcut);
                    hotkeys.Apply(new Dictionary<ShortcutAction, KeyShortcut> { [ShortcutAction.Region] = shortcut });
                    Send(Down(VK_CONTROL), Down(VK_MENU), Down(VK_SHIFT), Down(VK_F12), Up(VK_F12), Up(VK_SHIFT), Up(VK_MENU), Up(VK_CONTROL));
                    var finish = new System.Windows.Forms.Timer { Interval = 600 };
                    finish.Tick += (_, _) => { finish.Dispose(); hotkeys.Dispose(); done(); };
                    finish.Start();
                };
                settle.Start();
            });
            Assert.True(foreground, "the fake game window did not come to the front");
            Assert.Equal(expected, count);
        }
        finally
        {
            if (!game.HasExited) game.Kill();
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string windowName);

    private static INPUT Mouse(uint flags) => new() { type = 0, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags } } };

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private static void RunOnMessageLoop(Action<Action> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var context = new ApplicationContext();
                // Start once the loop runs, so the hook is called from a pumping thread.
                var start = new System.Windows.Forms.Timer { Interval = 50 };
                start.Tick += (_, _) => { start.Dispose(); body(context.ExitThread); };
                start.Start();
                Application.Run(context);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "message loop did not finish");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static INPUT Down(ushort vk) => Key(vk, 0);
    private static INPUT Up(ushort vk) => Key(vk, 0x0002);

    private static INPUT Key(ushort vk, uint flags) =>
        new() { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = flags } } };

    private static void Send(params INPUT[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
