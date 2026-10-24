using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using SuYanShot.Native;

namespace SuYanShot.ClipboardHistory;

/// Listens for clipboard changes (Windows notifies, so unlike macOS there is no polling) and reports new copies.
/// Skips content that apps mark as private, and anything copied from password managers.
internal sealed class ClipboardMonitor : NativeWindow, IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    /// Larger text is skipped rather than bloating the history.
    private const int MaxTextLength = 1_000_000;

    /// Formats that password managers and privacy-aware apps add to say "do not record this".
    private static readonly string[] PrivateFormats =
    {
        "ExcludeClipboardContentFromMonitorProcessing",
        "Clipboard Viewer Ignore",
    };

    private static readonly HashSet<string> SkippedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "KeePass", "KeePassXC", "1Password", "Bitwarden", "Dashlane", "NordPass", "RoboForm", "Enpass", "LastPass",
    };

    private bool listening;
    private uint ignoredSequence;

    public event Action<ClipboardSnapshot>? Captured;

    public ClipboardMonitor()
    {
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) }); // HWND_MESSAGE
    }

    public void Start()
    {
        if (listening) return;
        listening = AddClipboardFormatListener(Handle);
    }

    public void Stop()
    {
        if (!listening) return;
        RemoveClipboardFormatListener(Handle);
        listening = false;
    }

    /// Our own write when pasting from history must not be recorded as a new copy.
    public void Ignore(uint sequenceNumber) => ignoredSequence = sequenceNumber;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_CLIPBOARDUPDATE)
        {
            if (GetClipboardSequenceNumber() != ignoredSequence) Read();
            return;
        }
        base.WndProc(ref m);
    }

    private void Read()
    {
        IDataObject? data;
        try
        {
            data = System.Windows.Forms.Clipboard.GetDataObject();
        }
        catch (ExternalException)
        {
            return; // another app still holds the clipboard open; its next change will arrive anyway
        }
        if (data == null || IsPrivate(data)) return;
        var source = SourceApp();
        if (source.ProcessName is string process && SkippedProcesses.Contains(process)) return;
        var sourceName = source.DisplayName;

        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            Captured?.Invoke(new ClipboardSnapshot(ClipboardKind.File) { FilePaths = files.ToList(), SourceAppName = sourceName });
            return;
        }

        if (data.GetDataPresent("PNG") || data.GetDataPresent(DataFormats.Bitmap) || data.GetDataPresent(DataFormats.Dib))
        {
            ReadImage(data, sourceName);
            return;
        }

        if (data.GetData(DataFormats.UnicodeText) is not string text || string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength) return;
        var rtf = data.GetData(DataFormats.Rtf) as string;
        var html = data.GetData(DataFormats.Html) as string;
        Captured?.Invoke(new ClipboardSnapshot(rtf != null || html != null ? ClipboardKind.RichText : ClipboardKind.Text)
        {
            Text = text,
            Rtf = rtf == null ? null : Encoding.UTF8.GetBytes(rtf),
            Html = html == null ? null : Encoding.UTF8.GetBytes(html),
            SourceAppName = sourceName,
        });
    }

    /// Screenshots can be tens of MB; PNG encoding runs off the UI thread and the result is reported back on it.
    private void ReadImage(IDataObject data, string? sourceName)
    {
        byte[]? png = (data.GetData("PNG") as MemoryStream)?.ToArray();
        Bitmap? bitmap = png == null ? (data.GetData(DataFormats.Bitmap) as Bitmap) ?? System.Windows.Forms.Clipboard.GetImage() as Bitmap : null;
        if (png == null && bitmap == null) return;
        var ui = SynchronizationContext.Current;
        Task.Run(() =>
        {
            try
            {
                if (png != null)
                {
                    using var decoded = new Bitmap(new MemoryStream(png));
                    return (png, decoded.Size);
                }
                using (bitmap)
                {
                    var stream = new MemoryStream();
                    bitmap!.Save(stream, ImageFormat.Png);
                    return (stream.ToArray(), bitmap.Size);
                }
            }
            catch (ArgumentException)
            {
                return ((byte[]?)null, Size.Empty);
            }
        }).ContinueWith(task =>
        {
            var (bytes, size) = task.Result;
            if (bytes == null) return;
            ui?.Post(_ => Captured?.Invoke(new ClipboardSnapshot(ClipboardKind.Image) { Png = bytes, ImageSize = size, SourceAppName = sourceName }), null);
        }, TaskScheduler.Default);
    }

    private static bool IsPrivate(IDataObject data)
    {
        if (PrivateFormats.Any(data.GetDataPresent)) return true;
        // Windows' own convention: CanIncludeInClipboardHistory = 0 means "keep out of history".
        if (data.GetData("CanIncludeInClipboardHistory") is MemoryStream stream && stream.Length >= 4)
        {
            var buffer = new byte[4];
            stream.Read(buffer, 0, 4);
            return BitConverter.ToInt32(buffer, 0) == 0;
        }
        return false;
    }

    private readonly record struct Source(string? ProcessName, string? DisplayName);

    /// The app that put the data on the clipboard (or, if it did not say, the foreground app).
    private static Source SourceApp()
    {
        var owner = GetClipboardOwner();
        if (owner == IntPtr.Zero) owner = GetForegroundWindow();
        if (owner == IntPtr.Zero) return default;
        NativeMethods.GetWindowThreadProcessId(owner, out var pid);
        if (pid == Environment.ProcessId) return new Source("Glean", "拾光");
        try
        {
            using var process = Process.GetProcessById((int)pid);
            string? description = null;
            try { description = process.MainModule?.FileVersionInfo.FileDescription; }
            catch (System.ComponentModel.Win32Exception) { } // elevated or protected process
            return new Source(process.ProcessName, string.IsNullOrWhiteSpace(description) ? process.ProcessName : description);
        }
        catch (ArgumentException)
        {
            return default;
        }
    }

    public void Dispose()
    {
        Stop();
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    internal static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardOwner();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
