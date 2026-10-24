using System.Runtime.InteropServices;

namespace SuYanShot.Capture;

internal enum AutoScrollStatus
{
    Off,
    Running,
    /// Stopped after a stretch of auto scrolling; waiting for the user to continue or finish.
    Checkpoint,
    /// Nothing moves any more.
    ReachedEnd,
}

internal sealed record ScrollCaptureProgress(int Height, FrameResult LastResult, Bitmap? Preview, AutoScrollStatus Auto);

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
        public int StalledFrames;
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
            var began = DateTime.UtcNow;
            Bitmap frame;
            try
            {
                frame = ScreenCapture.CaptureRect(region);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // The desktop can be briefly unavailable (secure desktop, lock screen); try again next tick.
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
            if (result is FrameResult.LimitReached) StopAuto(AutoScrollStatus.Off);
            AdvanceAutoScroll(result);
            onProgress(new ScrollCaptureProgress(stitcher.Height, result, preview, autoStatus));
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

    /// Turns auto scroll on or off (also resumes after a checkpoint).
    public void SetAutoScroll(bool enabled)
    {
        if (!enabled)
        {
            StopAuto(AutoScrollStatus.Off);
            return;
        }
        auto ??= new AutoScrollState { StepPixels = Math.Max(60, (int)(region.Height * 0.35)) };
        auto.SinceCheckpoint = 0;
        auto.StalledFrames = 0;
        autoStatus = AutoScrollStatus.Running;
        // Wheel input goes to the window under the pointer, so park it over the captured region.
        SetCursorPos(region.X + region.Width / 2, region.Y + region.Height / 2);
    }

    /// One auto-scroll decision per captured frame: scroll on, back off, pause at a checkpoint, or stop at the end.
    private void AdvanceAutoScroll(FrameResult result)
    {
        if (autoStatus != AutoScrollStatus.Running || auto is not AutoScrollState state) return;
        switch (result)
        {
            case FrameResult.Appended appended:
                state.SinceCheckpoint += appended.Rows;
                state.StalledFrames = 0;
                if (state.LastNotches > 0)
                    state.PixelsPerNotch = state.PixelsPerNotch * 0.5 + appended.Rows / (double)state.LastNotches * 0.5;
                break;
            case FrameResult.Unchanged:
                state.StalledFrames++;
                break;
            case FrameResult.Rejected:
                // Scrolled further than the stitcher can follow; take smaller steps.
                state.StepPixels = Math.Max(30, state.StepPixels * 2 / 3);
                break;
        }

        if (state.StalledFrames >= 4)
        {
            // Nothing moved. If nothing was ever stitched, the wheel direction may be inverted; try the other way once.
            if (!state.TriedOtherDirection && stitcher.Height == region.Height)
            {
                state.TriedOtherDirection = true;
                state.Direction = -state.Direction;
                state.StalledFrames = 0;
            }
            else
            {
                StopAuto(AutoScrollStatus.ReachedEnd);
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
        if (status == AutoScrollStatus.Off) auto = null;
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
