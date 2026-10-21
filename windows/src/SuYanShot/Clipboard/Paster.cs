using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Text;

namespace SuYanShot.ClipboardHistory;

/// Puts a history entry back on the clipboard and presses Ctrl+V in the app that was in front before the panel opened.
/// Windows needs no permission for this (macOS needs Accessibility); apps running as administrator ignore it.
internal static class Paster
{
    /// Returns the clipboard sequence number of the write, so the monitor can ignore it.
    public static uint Write(ClipboardItem item, ClipboardStore store, bool plainText = false)
    {
        var data = new DataObject();
        switch (item.Kind)
        {
            case ClipboardKind.Text:
                data.SetText(item.Text ?? "", TextDataFormat.UnicodeText);
                break;
            case ClipboardKind.RichText:
                data.SetText(item.Text ?? "", TextDataFormat.UnicodeText);
                if (!plainText)
                {
                    if (item.HasRtf && store.Payload(item.Id, "rtf") is byte[] rtf) data.SetData(DataFormats.Rtf, Encoding.UTF8.GetString(rtf));
                    if (item.HasHtml && store.Payload(item.Id, "html") is byte[] html) data.SetData(DataFormats.Html, Encoding.UTF8.GetString(html));
                }
                break;
            case ClipboardKind.Image:
                if (store.Payload(item.Id, "png") is byte[] png)
                {
                    data.SetData("PNG", false, new MemoryStream(png));
                    // Most Windows apps only read the bitmap formats.
                    data.SetImage(new Bitmap(new MemoryStream(png)));
                }
                break;
            case ClipboardKind.File:
                var files = new StringCollection();
                files.AddRange((item.FilePaths ?? new()).ToArray());
                data.SetFileDropList(files);
                break;
        }
        System.Windows.Forms.Clipboard.SetDataObject(data, copy: true, retryTimes: 5, retryDelay: 100);
        return ClipboardMonitor.GetClipboardSequenceNumber();
    }

    /// Brings back the previous window and synthesizes Ctrl+V, releasing any modifier still held from the panel's shortcut.
    public static void PasteInto(IntPtr window)
    {
        if (window != IntPtr.Zero) SetForegroundWindow(window);
        var inputs = new List<INPUT>();
        foreach (var held in new ushort[] { VK_SHIFT, VK_MENU, VK_LWIN, VK_RWIN })
            if ((GetAsyncKeyState(held) & 0x8000) != 0) inputs.Add(Key(held, up: true));
        inputs.Add(Key(VK_CONTROL, up: false));
        inputs.Add(Key(VK_V, up: false));
        inputs.Add(Key(VK_V, up: true));
        inputs.Add(Key(VK_CONTROL, up: true));
        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }

    private const ushort VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_V = 0x56;
    private const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x0002;

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 } },
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    // The union must be as large as its largest member (MOUSEINPUT) or SendInput rejects the size.
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
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

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
