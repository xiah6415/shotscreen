using SuYanShot.Native;

namespace SuYanShot.Capture;

internal readonly record struct WindowInfo(IntPtr Handle, Rectangle Bounds);

internal static class WindowList
{
    /// Visible top-level windows, front to back, excluding this app's own windows.
    public static List<WindowInfo> Snapshot()
    {
        var ownProcess = (uint)Environment.ProcessId;
        var windows = new List<WindowInfo>();
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hWnd) || NativeMethods.IsIconic(hWnd) || NativeMethods.IsCloaked(hWnd))
                return true;
            NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == ownProcess) return true;
            var exStyle = NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            // Untitled tool windows are usually invisible helpers (shadows, IME, tray flyouts).
            if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0 && NativeMethods.GetWindowTextLengthW(hWnd) == 0)
                return true;
            var bounds = NativeMethods.VisibleBounds(hWnd);
            if (bounds.Width > 1 && bounds.Height > 1)
                windows.Add(new WindowInfo(hWnd, bounds));
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    /// The front-most window containing a screen point.
    public static WindowInfo? At(IReadOnlyList<WindowInfo> windows, Point screenPoint)
    {
        foreach (var window in windows)
            if (window.Bounds.Contains(screenPoint)) return window;
        return null;
    }
}
