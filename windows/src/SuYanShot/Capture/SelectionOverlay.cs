using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using SuYanShot.Native;

namespace SuYanShot.Capture;

internal enum SelectionMode
{
    /// Drag a rectangle (Esc or right-click cancels).
    Region,

    /// Hover highlights the front-most window under the pointer; click picks it.
    Window,
}

internal abstract record SelectionResult
{
    public sealed record Region(Rectangle ScreenRect) : SelectionResult;

    public sealed record Window(WindowInfo Info) : SelectionResult;
}

/// One borderless overlay per monitor, all drawing the same frozen desktop, sharing one selection.
/// Every rectangle here is in screen pixels (the process is per-monitor DPI aware), so mixed-DPI setups need no conversion.
/// Like the macOS app, a region stays within the monitor where the drag started.
internal sealed class SelectionSession
{
    private readonly FrozenDesktop desktop;
    private readonly IReadOnlyList<WindowInfo> windows;
    private readonly List<OverlayForm> forms = new();
    private readonly TaskCompletionSource<SelectionResult?> completion = new();

    public SelectionMode Mode { get; }
    public Point Pointer { get; private set; } = Cursor.Position;
    public Rectangle? Highlight { get; private set; }
    public bool Dragging => anchor != null;

    private Point? anchor;
    private Rectangle dragMonitor;
    private WindowInfo? hovered;

    private SelectionSession(FrozenDesktop desktop, SelectionMode mode, IReadOnlyList<WindowInfo> windows)
    {
        this.desktop = desktop;
        this.windows = windows;
        Mode = mode;
    }

    public static Task<SelectionResult?> Run(FrozenDesktop desktop, SelectionMode mode, IReadOnlyList<WindowInfo> windows)
    {
        var session = new SelectionSession(desktop, mode, windows);
        session.Show();
        return session.completion.Task;
    }

    private void Show()
    {
        foreach (var screen in Screen.AllScreens)
            forms.Add(new OverlayForm(this, desktop, screen.Bounds));
        foreach (var form in forms) form.Show();
        UpdateHover(Cursor.Position);
        var active = forms.FirstOrDefault(f => f.Monitor.Contains(Cursor.Position)) ?? forms[0];
        active.Activate();
        NativeMethods.SetForegroundWindow(active.Handle);
    }

    public void PointerMoved(Point screenPoint)
    {
        Pointer = screenPoint;
        if (anchor is Point start)
        {
            var end = Clamp(screenPoint, dragMonitor);
            Highlight = Rectangle.FromLTRB(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y),
                                           Math.Max(start.X, end.X), Math.Max(start.Y, end.Y));
        }
        else
        {
            UpdateHover(screenPoint);
        }
        Redraw();
    }

    public void PointerPressed(Point screenPoint, Rectangle monitor)
    {
        if (Mode == SelectionMode.Window)
        {
            UpdateHover(screenPoint);
            if (hovered is WindowInfo window) Finish(new SelectionResult.Window(window));
            return;
        }
        anchor = screenPoint;
        dragMonitor = monitor;
        Highlight = new Rectangle(screenPoint, Size.Empty);
        Redraw();
    }

    public void PointerReleased(Point screenPoint)
    {
        if (anchor == null) return;
        PointerMoved(screenPoint);
        anchor = null;
        if (Highlight is Rectangle rect && rect.Width >= 3 && rect.Height >= 3)
        {
            Finish(new SelectionResult.Region(rect));
            return;
        }
        Highlight = null; // a click without a drag: let the user try again
        Redraw();
    }

    public void Cancel() => Finish(null);

    private void UpdateHover(Point screenPoint)
    {
        if (Mode != SelectionMode.Window) return;
        hovered = WindowList.At(windows, screenPoint);
        Highlight = hovered?.Bounds;
    }

    private void Finish(SelectionResult? result)
    {
        if (completion.Task.IsCompleted) return;
        foreach (var form in forms) form.Close();
        forms.Clear();
        completion.SetResult(result);
    }

    private void Redraw()
    {
        foreach (var form in forms) form.Invalidate();
    }

    private static Point Clamp(Point point, Rectangle bounds) =>
        new(Math.Clamp(point.X, bounds.Left, bounds.Right), Math.Clamp(point.Y, bounds.Top, bounds.Bottom));
}

internal sealed class OverlayForm : Form
{
    private const int WM_DPICHANGED = 0x02E0;

    private readonly SelectionSession session;
    private readonly Bitmap bright;
    private readonly Bitmap dimmed;

    public Rectangle Monitor { get; }

    public OverlayForm(SelectionSession session, FrozenDesktop desktop, Rectangle monitor)
    {
        this.session = session;
        Monitor = monitor;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Bounds = monitor;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        DoubleBuffered = true;
        Cursor = session.Mode == SelectionMode.Region ? Cursors.Cross : Cursors.Hand;
        Text = "拾光";

        // Pre-render once so redrawing on every mouse move stays cheap even on 4K monitors.
        var source = monitor;
        source.Offset(-desktop.Bounds.X, -desktop.Bounds.Y);
        bright = new Bitmap(monitor.Width, monitor.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bright))
            g.DrawImage(desktop.Image, new Rectangle(Point.Empty, monitor.Size), source, GraphicsUnit.Pixel);
        dimmed = new Bitmap(bright);
        using (var g = Graphics.FromImage(dimmed))
        using (var shade = new SolidBrush(Color.FromArgb(110, 0, 0, 0)))
            g.FillRectangle(shade, 0, 0, dimmed.Width, dimmed.Height);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.ExcludeFromCapture(Handle);
    }

    protected override void WndProc(ref Message m)
    {
        // Created directly on its monitor at that monitor's DPI; never let Windows rescale it.
        if (m.Msg == WM_DPICHANGED)
        {
            Bounds = Monitor;
            return;
        }
        base.WndProc(ref m);
    }

    private Point ToScreen(Point client) => new(client.X + Monitor.X, client.Y + Monitor.Y);

    protected override void OnMouseMove(MouseEventArgs e) => session.PointerMoved(ToScreen(e.Location));

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right) session.Cancel();
        else if (e.Button == MouseButtons.Left) session.PointerPressed(ToScreen(e.Location), Monitor);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) session.PointerReleased(ToScreen(e.Location));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) session.Cancel();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Everything is painted in OnPaint; skipping the background avoids flicker.
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.DrawImageUnscaled(dimmed, 0, 0);
        g.CompositingMode = CompositingMode.SourceOver;

        if (session.Highlight is Rectangle screenRect)
        {
            var local = screenRect;
            local.Offset(-Monitor.X, -Monitor.Y);
            var visible = Rectangle.Intersect(local, ClientRectangle);
            if (visible.Width > 0 && visible.Height > 0)
            {
                g.DrawImage(bright, visible, visible, GraphicsUnit.Pixel);
                using var border = new Pen(Color.FromArgb(230, 255, 255, 255), Px(1.5f));
                g.DrawRectangle(border, local.X, local.Y, Math.Max(0, local.Width - 1), Math.Max(0, local.Height - 1));
                if (session.Mode == SelectionMode.Region && local.Width > 0 && local.Height > 0)
                    DrawSizeLabel(g, local, screenRect.Size);
            }
        }

        if (session.Mode == SelectionMode.Region && !session.Dragging && Monitor.Contains(session.Pointer))
            DrawCrosshair(g, new Point(session.Pointer.X - Monitor.X, session.Pointer.Y - Monitor.Y));
    }

    private void DrawCrosshair(Graphics g, Point p)
    {
        using var pen = new Pen(Color.FromArgb(140, 255, 255, 255), 1) { DashStyle = DashStyle.Dash };
        g.DrawLine(pen, 0, p.Y, ClientSize.Width, p.Y);
        g.DrawLine(pen, p.X, 0, p.X, ClientSize.Height);
    }

    private void DrawSizeLabel(Graphics g, Rectangle local, Size pixels)
    {
        var text = $"{pixels.Width} × {pixels.Height}";
        using var font = new Font("Segoe UI", Px(12f), FontStyle.Regular, GraphicsUnit.Pixel);
        var size = g.MeasureString(text, font);
        var padding = Px(6f);
        var box = new RectangleF(local.X, local.Y - size.Height - padding * 2 - Px(4f), size.Width + padding * 2, size.Height + padding);
        if (box.Y < 0) box.Y = local.Y + Px(4f);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var background = new SolidBrush(Color.FromArgb(200, 20, 20, 20));
        using var path = RoundedRect(box, Px(4f));
        g.FillPath(background, path);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.DrawString(text, font, Brushes.White, box.X + padding, box.Y + padding / 2);
        g.SmoothingMode = SmoothingMode.None;
    }

    private float Px(float value) => value * DeviceDpi / 96f;

    internal static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            bright.Dispose();
            dimmed.Dispose();
        }
        base.Dispose(disposing);
    }
}
