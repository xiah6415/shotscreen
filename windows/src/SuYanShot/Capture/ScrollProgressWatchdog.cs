namespace SuYanShot.Capture;

/// Why auto scroll paused. These are observations, not guesses that the document has reached its end.
internal enum ScrollPauseReason
{
    NoMovement,
    CannotStitch,
    NoProgress,
    CaptureFailed,
}

/// Bounded retries and monotonic no-progress timing, independent of UI and screen capture (as on macOS).
internal sealed class ScrollProgressWatchdog
{
    internal enum Frame { First, Appended, Unchanged, Rejected }

    public static readonly TimeSpan NoProgressTimeout = TimeSpan.FromSeconds(10);
    private readonly Func<TimeSpan> clock;
    private TimeSpan lastProgress;
    private int unchangedFrames;
    private int rejectedFrames;
    private int captureFailures;

    /// <param name="clock">Monotonic time; defaults to a stopwatch. Tests pass their own.</param>
    public ScrollProgressWatchdog(Func<TimeSpan>? clock = null)
    {
        if (clock == null)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            clock = () => stopwatch.Elapsed;
        }
        this.clock = clock;
        Reset();
    }

    public void Reset()
    {
        lastProgress = clock();
        unchangedFrames = 0;
        rejectedFrames = 0;
        captureFailures = 0;
    }

    public void CaptureSucceeded() => captureFailures = 0;

    /// Three failures in a row pause the capture.
    public ScrollPauseReason? CaptureFailed() => ++captureFailures >= 3 ? ScrollPauseReason.CaptureFailed : null;

    public ScrollPauseReason? Observe(Frame frame)
    {
        var now = clock();
        switch (frame)
        {
            case Frame.First or Frame.Appended:
                lastProgress = now;
                unchangedFrames = 0;
                rejectedFrames = 0;
                return null;
            case Frame.Unchanged:
                unchangedFrames++;
                rejectedFrames = 0;
                break;
            case Frame.Rejected:
                rejectedFrames++;
                unchangedFrames = 0;
                break;
        }
        if (unchangedFrames >= 4) return ScrollPauseReason.NoMovement;
        if (rejectedFrames >= 8) return ScrollPauseReason.CannotStitch;
        if (now - lastProgress >= NoProgressTimeout) return ScrollPauseReason.NoProgress;
        return null;
    }
}
