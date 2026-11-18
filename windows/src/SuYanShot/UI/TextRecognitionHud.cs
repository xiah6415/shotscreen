using System.Drawing.Drawing2D;
using SuYanShot.Native;

namespace SuYanShot.UI;

/// The OCR status pill, as on macOS: "OCR 辨識中…" while working, then the result for a couple of seconds, near the bottom
/// of the monitor where the region was picked. Never takes focus and never appears in captures.
internal sealed class TextRecognitionHud : Form
{
    private readonly System.Windows.Forms.Timer hideTimer = new() { Interval = 2400 };
    private string message = "";
    private bool working;

    public TextRecognitionHud()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(32, 32, 34);
        hideTimer.Tick += (_, _) => { hideTimer.Stop(); Hide(); };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var p = base.CreateParams;
            p.ExStyle |= 0x08000000 | 0x00000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            return p;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.ExcludeFromCapture(Handle);
    }

    /// <param name="working">Shows a spinner-like dot and stays until the next message.</param>
    public void ShowMessage(string text, Screen screen, bool working = false)
    {
        message = text;
        this.working = working;
        var dpi = NativeMethods.DpiOf(screen) / 96f;
        using var font = MessageFont(dpi);
        var size = TextRenderer.MeasureText(text, font);
        var width = size.Width + (int)(40 * dpi) + (working ? (int)(18 * dpi) : 0);
        var height = (int)(40 * dpi);
        var area = screen.WorkingArea;
        Bounds = new Rectangle(area.X + (area.Width - width) / 2, area.Bottom - height - (int)(60 * dpi), width, height);
        using (var path = Pill(new RectangleF(0, 0, width, height)))
            Region = new Region(path);
        hideTimer.Stop();
        if (!working) hideTimer.Start();
        if (!Visible) Show();
        Invalidate();
    }

    public void HideNow()
    {
        hideTimer.Stop();
        Hide();
    }

    private static Font MessageFont(float dpi) => new("Microsoft JhengHei UI", 13 * dpi, FontStyle.Bold, GraphicsUnit.Pixel);

    private static GraphicsPath Pill(RectangleF r)
    {
        var path = new GraphicsPath();
        var d = r.Height;
        path.AddArc(r.X, r.Y, d, d, 90, 180);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 180);
        path.CloseFigure();
        return path;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var dpi = DeviceDpi / 96f;
        using var font = MessageFont(dpi);
        var x = 20 * dpi;
        if (working)
        {
            using var dot = new SolidBrush(Color.FromArgb(10, 132, 255));
            g.FillEllipse(dot, x, Height / 2f - 5 * dpi, 10 * dpi, 10 * dpi);
            x += 18 * dpi;
        }
        TextRenderer.DrawText(g, message, font, new Rectangle((int)x, 0, Width - (int)x, Height), Color.White,
                              TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) hideTimer.Dispose();
        base.Dispose(disposing);
    }
}
