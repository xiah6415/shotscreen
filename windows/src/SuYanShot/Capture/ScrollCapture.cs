using System.Runtime.InteropServices;

namespace SuYanShot.Capture;

internal enum AutoScrollStatus
{
    Off,
    Running,
    /// Stopped after a stretch of auto scrolling; waiting for the user to continue or finish.
    Checkpoint,
    /// Auto scrolling stopped (see <see cref="ScrollCaptureProgress.Pause"/>); the user can retry, scroll by hand, or finish.
    Paused,
}

internal sealed record ScrollCaptureProgress(int Height, FrameResult LastResult, Bitmap? Preview, AutoScrollStatus Auto,
                                             ScrollPauseReason? Pause = null);

/// Scroll capture, as on macOS: repeatedly captures a fixed screen region and stitches what scrolled into view.
/// The user scrolls by hand, or turns on auto scroll, which pauses at checkpoints to ask before going further.
/// Our HUD windows are excluded from capture, so they never end up in the frames.
internal sealed class ScrollCaptureSession
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan AutoInterval = TimeSpan.FromMilliseconds(200);

    private sealed class AutoScrollState
    {
        /// Wanted movement per frame, in pixels.
        public int StepPixels;
        public int SinceCheckpoint;
        /// Wheel notches move a different number of pixels in every app; learned from what the stitcher reports.
        public double PixelsPerNotch = 100;
        public int LastNotches;
        public int Direction = -1; // negative wheel delta scrolls toward the end
        public bool TriedOtherDirection;
    }

    private readonly Rectangle region;
    private readonly Action<ScrollCaptureProgress> onProgress;
    private readonly ScrollStitcher stitcher = new();
    private readonly CancellationTokenSource cancel = new();
    private Task? loop;
    private AutoScrollState? auto;
    private AutoScrollStatus autoStatus = AutoScrollStatus.Off;
    private ScrollPauseReason? pauseReason;
    private readonly ScrollProgressWatchdog watchdog = new();
    /// After repeated capture failures, stop trying until the user presses 重試.
    private volatile bool captureSuspended;

    /// <param name="region">Screen pixels.</param>
    /// <param name="onProgress">Called on the UI thread after every frame.</param>
    public ScrollCaptureSession(Rectangle region, Action<ScrollCaptureProgress> onProgress)
    {
        this.region = region;
        this.onProgress = onProgress;
    }

    public Rectangle Region => region;

    /// Call on the UI thread.
    public void Start() => loop = Run(cancel.Token);

    private async Task Run(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (captureSuspended)
            {
                await Delay(Interval, token);
                continue;
            }
            var began = DateTime.UtcNow;
            Bitmap frame;
            try
            {
                frame = ScreenCapture.CaptureRect(region);
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or ExternalException or ArgumentException)
            {
                // The desktop can be briefly unavailable (secure desktop, lock screen); try again next tick,
                // but after a few failures in a row pause and tell the user instead of spinning silently.
                if (token.IsCancellationRequested) break;
                if (watchdog.CaptureFailed() is ScrollPauseReason reason)
                {
                    Pause(reason);
                    captureSuspended = true;
                    onProgress(new ScrollCaptureProgress(stitcher.Height, new FrameResult.Rejected(), null, autoStatus, pauseReason));
                }
                await Delay(Interval, token);
                continue;
            }
            // Matching is CPU work; keep it off the UI thread.
            var (result, preview) = await Task.Run(() =>
            {
                var r = stitcher.Add(frame);
                if (r is not (FrameResult.First or FrameResult.Appended)) frame.Dispose();
                return (r, r is FrameResult.First or FrameResult.Appended ? stitcher.MakePreview() : null);
            });
            if (token.IsCancellationRequested) { preview?.Dispose(); break; }
            watchdog.CaptureSucceeded();
            if (result is FrameResult.LimitReached) StopAuto(AutoScrollStatus.Off);
            AdvanceAutoScroll(result);
            onProgress(new ScrollCaptureProgress(stitcher.Height, result, preview, autoStatus, pauseReason));
            if (result is FrameResult.LimitReached) break;
            var remaining = (autoStatus == AutoScrollStatus.Running ? AutoInterval : Interval) - (DateTime.UtcNow - began);
            if (remaining > TimeSpan.Zero) await Delay(remaining, token);
        }
    }

    private static async Task Delay(TimeSpan time, CancellationToken token)
    {
        try { await Task.Delay(time, token); }
        catch (OperationCanceledException) { }
    }

    /// Turns auto scroll on or off (also resumes after a checkpoint or a pause; off after a capture failure retries capturing).
    public void SetAutoScroll(bool enabled)
    {
        captureSuspended = false;
        watchdog.Reset();
        if (!enabled)
        {
            StopAuto(AutoScrollStatus.Off);
            return;
        }
        auto ??= new AutoScrollState { StepPixels = Math.Max(60, (int)(region.Height * 0.35)) };
        auto.SinceCheckpoint = 0;
        autoStatus = AutoScrollStatus.Running;
        pauseReason = null;
        // Wheel input goes to the window under the pointer, so park it over the captured region.
        SetCursorPos(region.X + region.Width / 2, region.Y + region.Height / 2);
    }

    /// One auto-scroll decision per captured frame: scroll on, back off, pause at a checkpoint, or stop at the end.
    private void AdvanceAutoScroll(FrameResult result)
    {
        if (autoStatus != AutoScrollStatus.Running || auto is not AutoScrollState state) return;
        ScrollProgressWatchdog.Frame frame;
        switch (result)
        {
            case FrameResult.Appended appended:
                state.SinceCheckpoint += appended.Rows;
                if (state.LastNotches > 0)
                    state.PixelsPerNotch = state.PixelsPerNotch * 0.5 + appended.Rows / (double)state.LastNotches * 0.5;
                frame = ScrollProgressWatchdog.Frame.Appended;
                break;
            case FrameResult.Unchanged:
                frame = ScrollProgressWatchdog.Frame.Unchanged;
                break;
            case FrameResult.Rejected:
                // Scrolled further than the stitcher can follow; take smaller steps.
                state.StepPixels = Math.Max(30, state.StepPixels * 2 / 3);
                frame = ScrollProgressWatchdog.Frame.Rejected;
                break;
            case FrameResult.First:
                frame = ScrollProgressWatchdog.Frame.First;
                break;
            default:
                return;
        }

        if (watchdog.Observe(frame) is ScrollPauseReason reason)
        {
            // Nothing moved and nothing was ever stitched: the wheel direction may be inverted; try the other way once.
            if (reason == ScrollPauseReason.NoMovement && !state.TriedOtherDirection && stitcher.Height == region.Height)
            {
                state.TriedOtherDirection = true;
                state.Direction = -state.Direction;
                watchdog.Reset();
            }
            else
            {
                Pause(reason);
                return;
            }
        }

        if (state.SinceCheckpoint >= region.Height * 3)
        {
            state.SinceCheckpoint = 0;
            state.LastNotches = 0;
            autoStatus = AutoScrollStatus.Checkpoint;
            return;
        }
        var notches = Math.Clamp((int)Math.Round(state.StepPixels / Math.Max(1, state.PixelsPerNotch)), 1, 10);
        state.LastNotches = notches;
        ScrollWheel(state.Direction * notches * 120);
    }

    private void StopAuto(AutoScrollStatus status)
    {
        autoStatus = status;
        pauseReason = null;
        if (status == AutoScrollStatus.Off) auto = null;
    }

    private void Pause(ScrollPauseReason reason)
    {
        autoStatus = AutoScrollStatus.Paused;
        pauseReason = reason;
    }

    /// Stops capturing and returns the stitched image (null if nothing was captured).
    public async Task<Bitmap?> Finish()
    {
        cancel.Cancel();
        if (loop != null) await loop;
        var image = await Task.Run(stitcher.MakeImage);
        stitcher.Dispose();
        return image;
    }

    public void Cancel()
    {
        cancel.Cancel();
        _ = (loop ?? Task.CompletedTask).ContinueWith(_ => stitcher.Dispose(), TaskScheduler.Default);
    }

    // MARK: Input

    private static void ScrollWheel(int delta)
    {
        var input = new INPUT { type = 0 /* INPUT_MOUSE */, mi = new MOUSEINPUT { mouseData = delta, dwFlags = 0x0800 /* MOUSEEVENTF_WHEEL */ } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy, mouseData;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);
}
