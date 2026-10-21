using System.Drawing.Drawing2D;
using SuYanShot.Capture;
using SuYanShot.Native;

namespace SuYanShot.UI;

/// A round countdown in the middle of the monitor under the pointer. Clicking it cancels.
internal sealed class CountdownHud : Form
{
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly TaskCompletionSource<bool> completion = new();
    private int remaining;

    private CountdownHud(int seconds, Screen screen)
    {
        remaining = seconds;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        BackColor = Color.Black;
        Cursor = Cursors.Hand;
        var size = (int)(120 * NativeMethods.DpiOf(screen) / 96f);
        var area = screen.Bounds;
        Bounds = new Rectangle(area.X + (area.Width - size) / 2, area.Y + (area.Height - size) / 2, size, size);
        Opacity = 0.82;
        timer.Tick += (_, _) =>
        {
            remaining--;
            if (remaining > 0) { Invalidate(); return; }
            Finish(true);
        };
    }

    /// Resolves true when the countdown reaches zero, false if the user clicked to cancel.
    public static Task<bool> Run(int seconds)
    {
        var hud = new CountdownHud(seconds, ScreenCapture.ScreenUnderCursor());
        hud.Show();
        hud.timer.Start();
        return hud.completion.Task;
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.ExcludeFromCapture(Handle);
        using var path = new GraphicsPath();
        path.AddEllipse(0, 0, Width, Height);
        Region = new Region(path);
    }

    protected override void OnMouseUp(MouseEventArgs e) => Finish(false);

    private void Finish(bool reachedZero)
    {
        timer.Stop();
        Close();
        completion.TrySetResult(reachedZero);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var font = new Font("Segoe UI Semibold", Height * 0.45f, GraphicsUnit.Pixel);
        var text = remaining.ToString();
        var size = g.MeasureString(text, font);
        g.DrawString(text, font, Brushes.White, (Width - size.Width) / 2, (Height - size.Height) / 2);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        timer.Dispose();
        base.OnFormClosed(e);
    }
}
