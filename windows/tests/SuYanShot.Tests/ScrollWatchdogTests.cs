using SuYanShot.Capture;
using SuYanShot.UI;
using Xunit;
using Frame = SuYanShot.Capture.ScrollProgressWatchdog.Frame;

namespace SuYanShot.Tests;

/// The macOS scripts/scroll-watchdog-test, ported: bounded retries and no-progress timing for auto scroll.
public sealed class ScrollWatchdogTests
{
    private TimeSpan now;
    private ScrollProgressWatchdog Make() => new(() => now);

    [Fact]
    public void FourUnchangedFramesMeanNoMovement()
    {
        var watchdog = Make();
        for (var i = 0; i < 3; i++) Assert.Null(watchdog.Observe(Frame.Unchanged));
        Assert.Equal(ScrollPauseReason.NoMovement, watchdog.Observe(Frame.Unchanged));
    }

    [Fact]
    public void EightRejectedFramesMeanCannotStitch()
    {
        var watchdog = Make();
        for (var i = 0; i < 7; i++) Assert.Null(watchdog.Observe(Frame.Rejected));
        Assert.Equal(ScrollPauseReason.CannotStitch, watchdog.Observe(Frame.Rejected));
    }

    [Fact]
    public void ProgressResetsTheCounters()
    {
        var watchdog = Make();
        for (var i = 0; i < 3; i++) watchdog.Observe(Frame.Unchanged);
        Assert.Null(watchdog.Observe(Frame.Appended));
        for (var i = 0; i < 3; i++) Assert.Null(watchdog.Observe(Frame.Unchanged));
    }

    [Fact]
    public void AlternatingFramesStillPauseAfterTenSecondsWithoutProgress()
    {
        var watchdog = Make();
        for (var i = 0; i < 9; i++)
        {
            now += TimeSpan.FromSeconds(1);
            Assert.Null(watchdog.Observe(i % 2 == 0 ? Frame.Unchanged : Frame.Rejected));
        }
        now += TimeSpan.FromSeconds(1);
        Assert.Equal(ScrollPauseReason.NoProgress, watchdog.Observe(Frame.Unchanged));
    }

    [Fact]
    public void ThreeCaptureFailuresInARowPause()
    {
        var watchdog = Make();
        Assert.Null(watchdog.CaptureFailed());
        Assert.Null(watchdog.CaptureFailed());
        watchdog.CaptureSucceeded();
        Assert.Null(watchdog.CaptureFailed());
        Assert.Null(watchdog.CaptureFailed());
        Assert.Equal(ScrollPauseReason.CaptureFailed, watchdog.CaptureFailed());
        watchdog.Reset();
        Assert.Null(watchdog.CaptureFailed());
    }

    [Theory]
    [InlineData(nameof(ScrollPauseReason.NoMovement), "畫面未移動，可能到底或選錯範圍")]
    [InlineData(nameof(ScrollPauseReason.CaptureFailed), "畫面擷取失敗，請按重試")]
    public void HudExplainsWhyItPaused(string reasonName, string message) // the enum is internal; public test methods can't take it
    {
        var reason = Enum.Parse<ScrollPauseReason>(reasonName);
        var progress = new ScrollCaptureProgress(400, new FrameResult.Unchanged(), null, AutoScrollStatus.Paused, reason);
        Assert.Equal(message, ScrollCaptureHud.Message(progress));
    }
}
