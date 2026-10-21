using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using SuYanShot.Native;

namespace SuYanShot.Capture;

internal abstract record AllInOneAction
{
    public sealed record Capture(Rectangle ScreenRect) : AllInOneAction;
    public sealed record Window : AllInOneAction;
    public sealed record FullScreen(Rectangle Monitor) : AllInOneAction;
}

/// CleanShot-style All-in-One overlay, as on macOS: drag out a region, adjust it (move / 8 handles, dragging past the
/// opposite edge flips it), then pick an action from the toolbar. Enter or double-click inside captures, Esc / right-click
/// cancels. Draws on the frozen desktop like the plain region selector. 捲動截圖 and 錄影 join the toolbar once they are ported.
internal sealed class AllInOneSession
{
    private readonly List<AllInOneOverlay> overlays = new();
    private readonly TaskCompletionSource<AllInOneAction?> completion = new();

    public static Task<AllInOneAction?> Run(FrozenDesktop desktop)
    {
        var session = new AllInOneSession();
        foreach (var screen in Screen.AllScreens)
            session.overlays.Add(new AllInOneOverlay(session, desktop, screen.Bounds));
        var underMouse = session.overlays.FirstOrDefault(o => o.Monitor.Contains(Cursor.Position)) ?? session.overlays[0];
        foreach (var overlay in session.overlays)
        {
            overlay.ShowsIdleToolbar = overlay == underMouse;
            overlay.Show();
        }
        underMouse.Activate();
        NativeMethods.SetForegroundWindow(underMouse.Handle);
        return session.completion.Task;
    }

    /// A selection lives on one monitor at a time; only that monitor keeps a toolbar.
    public void SelectionStarted(AllInOneOverlay owner)
    {
        foreach (var overlay in overlays.Where(o => o != owner))
        {
            overlay.ClearSelection();
            overlay.ShowsIdleToolbar = false;
        }
        owner.ShowsIdleToolbar = true;
        owner.Activate();
    }

    public void Finish(AllInOneAction? action)
    {
        if (completion.Task.IsCompleted) return;
        foreach (var overlay in overlays) overlay.Close();
        overlays.Clear();
        completion.SetResult(action);
    }
}

internal sealed class AllInOneOverlay : Form
{
    private const int WM_DPICHANGED = 0x02E0;

    internal enum Grip { TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left }

    private enum ToolbarButton { Capture, Window, FullScreen, Cancel }

    private abstract record Drag
    {
        public sealed record Creating(Point Anchor) : Drag;
        public sealed record Moving(Point Grab, Rectangle Original) : Drag;
        public sealed record Resizing(Grip Grip, Rectangle Original) : Drag;
    }

    private static readonly (ToolbarButton Button, string Title, string Hint)[] Buttons =
    {
        (ToolbarButton.Capture, "截圖", "截取選取範圍（Enter）"),
        (ToolbarButton.Window, "視窗", "改選一個視窗"),
        (ToolbarButton.FullScreen, "全螢幕", "截取這個螢幕"),
        (ToolbarButton.Cancel, "取消", "取消（Esc）"),
    };

    private readonly AllInOneSession session;
    private readonly Bitmap bright;
    private readonly Bitmap dimmed;
    private Rectangle? selection; // local (client) pixels
    private Drag? drag;
    private Point? pointer;
    private ToolbarButton? hoveredButton;
    private bool showsIdleToolbar;

    public Rectangle Monitor { get; }

    public bool ShowsIdleToolbar
    {
        get => showsIdleToolbar;
        set { showsIdleToolbar = value; Invalidate(); }
    }

    public AllInOneOverlay(AllInOneSession session, FrozenDesktop desktop, Rectangle monitor)
    {
        this.session = session;
        Monitor = monitor;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Bounds = monitor;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        KeyPreview = true;
        Cursor = Cursors.Cross;
        Text = "素顏 Shot All-in-One";

        var source = monitor;
        source.Offset(-desktop.Bounds.X, -desktop.Bounds.Y);
        bright = new Bitmap(monitor.Width, monitor.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bright))
            g.DrawImage(desktop.Image, new Rectangle(Point.Empty, monitor.Size), source, GraphicsUnit.Pixel);
        dimmed = new Bitmap(bright);
        using (var g = Graphics.FromImage(dimmed))
        using (var shade = new SolidBrush(Color.FromArgb(80, 0, 0, 0)))
            g.FillRectangle(shade, 0, 0, dimmed.Width, dimmed.Height);
    }

    private float S(float value) => value * DeviceDpi / 96f;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.ExcludeFromCapture(Handle);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_DPICHANGED)
        {
            Bounds = Monitor;
            return;
        }
        base.WndProc(ref m);
    }

    public void ClearSelection()
    {
        selection = null;
        drag = null;
        Invalidate();
    }

    private bool IsCreating => drag is Drag.Creating;

    // MARK: Actions

    private void Perform(ToolbarButton button)
    {
        switch (button)
        {
            case ToolbarButton.Capture:
                if (selection is Rectangle rect && rect.Width >= 2 && rect.Height >= 2)
                    session.Finish(new AllInOneAction.Capture(new Rectangle(rect.X + Monitor.X, rect.Y + Monitor.Y, rect.Width, rect.Height)));
                break;
            case ToolbarButton.Window:
                session.Finish(new AllInOneAction.Window());
                break;
            case ToolbarButton.FullScreen:
                session.Finish(new AllInOneAction.FullScreen(Monitor));
                break;
            default:
                session.Finish(null);
                break;
        }
    }

    // MARK: Toolbar layout

    private bool ToolbarVisible => !IsCreating && (selection != null || showsIdleToolbar);

    /// Under the selection if there is room, else above, else inside its bottom edge; bottom-centre before any selection.
    private RectangleF ToolbarRect()
    {
        var size = new SizeF(S(76) * Buttons.Length + S(16), S(58));
        PointF origin;
        if (selection is Rectangle sel)
        {
            origin = new PointF(sel.X + sel.Width / 2f - size.Width / 2, sel.Bottom + S(12));
            if (origin.Y + size.Height > Height - S(8)) origin.Y = sel.Top - size.Height - S(12);
            if (origin.Y < S(8)) origin.Y = sel.Bottom - size.Height - S(12);
        }
        else
        {
            origin = new PointF(Width / 2f - size.Width / 2, Height - size.Height - S(80));
        }
        origin.X = Math.Clamp(origin.X, S(8), Math.Max(S(8), Width - size.Width - S(8)));
        return new RectangleF(origin, size);
    }

    private RectangleF ButtonRect(int index)
    {
        var bar = ToolbarRect();
        return new RectangleF(bar.X + S(8) + index * S(76), bar.Y + S(6), S(72), S(46));
    }

    private ToolbarButton? ButtonAt(Point point)
    {
        if (!ToolbarVisible) return null;
        for (var i = 0; i < Buttons.Length; i++)
            if (ButtonRect(i).Contains(point)) return Buttons[i].Button;
        return null;
    }

    private bool ButtonEnabled(ToolbarButton button) => button != ToolbarButton.Capture || selection != null;

    // MARK: Handles

    private Point HandlePoint(Grip handle, Rectangle r) => handle switch
    {
        Grip.TopLeft => new(r.Left, r.Top),
        Grip.Top => new(r.Left + r.Width / 2, r.Top),
        Grip.TopRight => new(r.Right, r.Top),
        Grip.Right => new(r.Right, r.Top + r.Height / 2),
        Grip.BottomRight => new(r.Right, r.Bottom),
        Grip.Bottom => new(r.Left + r.Width / 2, r.Bottom),
        Grip.BottomLeft => new(r.Left, r.Bottom),
        _ => new(r.Left, r.Top + r.Height / 2),
    };

    private RectangleF HandleRect(Grip handle, Rectangle r, float extra = 0)
    {
        var p = HandlePoint(handle, r);
        var size = S(9) + extra * 2;
        return new RectangleF(p.X - size / 2, p.Y - size / 2, size, size);
    }

    private Grip? HandleAt(Point point)
    {
        if (selection is not Rectangle sel) return null;
        foreach (var handle in Enum.GetValues<Grip>())
            if (HandleRect(handle, sel, S(5)).Contains(point)) return handle;
        return null;
    }

    private static Cursor CursorFor(Grip handle) => handle switch
    {
        Grip.Left or Grip.Right => Cursors.SizeWE,
        Grip.Top or Grip.Bottom => Cursors.SizeNS,
        Grip.TopLeft or Grip.BottomRight => Cursors.SizeNWSE,
        _ => Cursors.SizeNESW,
    };

    // MARK: Mouse

    private Point Clamp(Point p) => new(Math.Clamp(p.X, 0, Width), Math.Clamp(p.Y, 0, Height));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        pointer = e.Location;
        var point = Clamp(e.Location);
        switch (drag)
        {
            case Drag.Creating creating:
                selection = Normalized(creating.Anchor, point);
                break;
            case Drag.Moving moving:
                var moved = moving.Original;
                moved.Offset(point.X - moving.Grab.X, point.Y - moving.Grab.Y);
                moved.X = Math.Clamp(moved.X, 0, Width - moved.Width);
                moved.Y = Math.Clamp(moved.Y, 0, Height - moved.Height);
                selection = moved;
                break;
            case Drag.Resizing resizing:
                selection = Resized(resizing.Original, resizing.Grip, point);
                break;
            default:
                hoveredButton = ButtonAt(e.Location);
                Cursor = hoveredButton != null ? Cursors.Hand
                    : HandleAt(e.Location) is Grip h ? CursorFor(h)
                    : selection is Rectangle sel && sel.Contains(e.Location) ? Cursors.SizeAll
                    : Cursors.Cross;
                break;
        }
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        pointer = null;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            session.Finish(null);
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        if (ButtonAt(e.Location) is ToolbarButton button)
        {
            if (ButtonEnabled(button)) Perform(button);
            return;
        }
        var point = Clamp(e.Location);
        if (selection is Rectangle sel && e.Clicks == 2 && sel.Contains(point))
        {
            Perform(ToolbarButton.Capture);
            return;
        }
        if (selection is Rectangle current && HandleAt(point) is Grip handle)
        {
            drag = new Drag.Resizing(handle, current);
        }
        else if (selection is Rectangle inside && inside.Contains(point))
        {
            drag = new Drag.Moving(point, inside);
        }
        else
        {
            session.SelectionStarted(this);
            drag = new Drag.Creating(point);
            selection = null;
        }
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        var wasCreating = IsCreating;
        drag = null;
        if (wasCreating && selection is Rectangle sel && (sel.Width < 4 || sel.Height < 4)) selection = null;
        Invalidate();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Escape:
                session.Finish(null);
                return true;
            case Keys.Enter:
                Perform(ToolbarButton.Capture);
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private static Rectangle Normalized(Point a, Point b) =>
        Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    /// Moves the dragged edges to the pointer; dragging past the opposite edge flips the box.
    internal static Rectangle Resized(Rectangle r, Grip handle, Point p)
    {
        int left = r.Left, right = r.Right, top = r.Top, bottom = r.Bottom;
        if (handle is Grip.Left or Grip.TopLeft or Grip.BottomLeft) left = p.X;
        if (handle is Grip.Right or Grip.TopRight or Grip.BottomRight) right = p.X;
        if (handle is Grip.Top or Grip.TopLeft or Grip.TopRight) top = p.Y;
        if (handle is Grip.Bottom or Grip.BottomLeft or Grip.BottomRight) bottom = p.Y;
        return Rectangle.FromLTRB(Math.Min(left, right), Math.Min(top, bottom), Math.Max(left, right), Math.Max(top, bottom));
    }

    // MARK: Drawing

    protected override void OnPaintBackground(PaintEventArgs e)
    {
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImageUnscaled(dimmed, 0, 0);
        g.CompositingMode = CompositingMode.SourceOver;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        if (selection is Rectangle sel && sel.Width > 0 && sel.Height > 0)
        {
            g.DrawImage(bright, sel, sel, GraphicsUnit.Pixel);
            using var border = new Pen(Color.White, 1);
            g.DrawRectangle(border, sel.X, sel.Y, sel.Width - 1, sel.Height - 1);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (!IsCreating)
            {
                using var outline = new Pen(Color.FromArgb(90, 0, 0, 0), 1);
                foreach (var handle in Enum.GetValues<Grip>())
                {
                    var rect = HandleRect(handle, sel);
                    using var path = OverlayForm.RoundedRect(rect, S(2));
                    g.FillPath(Brushes.White, path);
                    g.DrawPath(outline, path);
                }
            }
            Label(g, $"{sel.Width} × {sel.Height}", new PointF(sel.X, sel.Y - S(30)), new PointF(sel.X, sel.Y + S(6)));
            g.SmoothingMode = SmoothingMode.None;
        }
        else if (pointer is Point p && !ToolbarRect().Contains(p))
        {
            using var pen = new Pen(Color.FromArgb(140, 255, 255, 255), 1);
            g.DrawLine(pen, 0, p.Y, Width, p.Y);
            g.DrawLine(pen, p.X, 0, p.X, Height);
            Label(g, "拖曳選取範圍 · Esc 取消", new PointF(p.X + S(12), p.Y + S(12)), new PointF(p.X - S(200), p.Y + S(12)));
        }

        if (ToolbarVisible) DrawToolbar(g);
    }

    private void Label(Graphics g, string text, PointF preferred, PointF fallback)
    {
        using var font = new Font("Microsoft JhengHei UI", S(12), GraphicsUnit.Pixel);
        var size = g.MeasureString(text, font);
        var box = new RectangleF(preferred, new SizeF(size.Width + S(12), size.Height + S(6)));
        if (box.Y < 0 || box.Right > Width) box.Location = fallback;
        var smoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var background = new SolidBrush(Color.FromArgb(200, 20, 20, 20));
        using var path = OverlayForm.RoundedRect(box, S(4));
        g.FillPath(background, path);
        g.DrawString(text, font, Brushes.White, box.X + S(6), box.Y + S(3));
        g.SmoothingMode = smoothing;
    }

    private void DrawToolbar(Graphics g)
    {
        var bar = ToolbarRect();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var background = new SolidBrush(Color.FromArgb(240, 36, 36, 38)))
        using (var path = OverlayForm.RoundedRect(bar, S(14)))
            g.FillPath(background, path);
        using var font = new Font("Microsoft JhengHei UI", S(11), GraphicsUnit.Pixel);
        for (var i = 0; i < Buttons.Length; i++)
        {
            var (button, title, hint) = Buttons[i];
            var rect = ButtonRect(i);
            var enabled = ButtonEnabled(button);
            if (enabled && hoveredButton == button)
            {
                using var hover = new SolidBrush(Color.FromArgb(60, 255, 255, 255));
                using var path = OverlayForm.RoundedRect(rect, S(8));
                g.FillPath(hover, path);
            }
            var ink = Color.FromArgb(enabled ? 255 : 90, 255, 255, 255);
            DrawIcon(g, button, new RectangleF(rect.X + rect.Width / 2 - S(10), rect.Y + S(5), S(20), S(20)), ink);
            using var brush = new SolidBrush(ink);
            var size = g.MeasureString(title, font);
            g.DrawString(title, font, brush, rect.X + (rect.Width - size.Width) / 2, rect.Y + S(27));
            _ = hint;
        }
        // Tooltip-style hint for the hovered button.
        if (hoveredButton is ToolbarButton hovered)
        {
            var index = Array.FindIndex(Buttons, b => b.Button == hovered);
            var r = ButtonRect(index);
            Label(g, Buttons[index].Hint, new PointF(r.X, bar.Bottom + S(6)), new PointF(r.X, bar.Top - S(30)));
        }
        g.SmoothingMode = SmoothingMode.None;
    }

    /// Simple line icons drawn in code (no icon font or image files).
    private void DrawIcon(Graphics g, ToolbarButton button, RectangleF r, Color color)
    {
        using var pen = new Pen(color, S(1.6f)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        switch (button)
        {
            case ToolbarButton.Capture:
                var arm = r.Width * 0.3f;
                g.DrawLines(pen, new[] { new PointF(r.Left, r.Top + arm), new PointF(r.Left, r.Top), new PointF(r.Left + arm, r.Top) });
                g.DrawLines(pen, new[] { new PointF(r.Right - arm, r.Top), new PointF(r.Right, r.Top), new PointF(r.Right, r.Top + arm) });
                g.DrawLines(pen, new[] { new PointF(r.Right, r.Bottom - arm), new PointF(r.Right, r.Bottom), new PointF(r.Right - arm, r.Bottom) });
                g.DrawLines(pen, new[] { new PointF(r.Left + arm, r.Bottom), new PointF(r.Left, r.Bottom), new PointF(r.Left, r.Bottom - arm) });
                g.DrawEllipse(pen, RectangleF.Inflate(r, -r.Width * 0.3f, -r.Height * 0.3f));
                break;
            case ToolbarButton.Window:
                var window = RectangleF.Inflate(r, 0, -r.Height * 0.12f);
                g.DrawRectangle(pen, window.X, window.Y, window.Width, window.Height);
                g.DrawLine(pen, window.Left, window.Top + window.Height * 0.25f, window.Right, window.Top + window.Height * 0.25f);
                break;
            case ToolbarButton.FullScreen:
                var screen = new RectangleF(r.X, r.Y + r.Height * 0.08f, r.Width, r.Height * 0.62f);
                g.DrawRectangle(pen, screen.X, screen.Y, screen.Width, screen.Height);
                g.DrawLine(pen, r.X + r.Width / 2, screen.Bottom, r.X + r.Width / 2, r.Bottom - r.Height * 0.08f);
                g.DrawLine(pen, r.X + r.Width * 0.28f, r.Bottom - r.Height * 0.08f, r.Right - r.Width * 0.28f, r.Bottom - r.Height * 0.08f);
                break;
            default:
                var inset = RectangleF.Inflate(r, -r.Width * 0.18f, -r.Height * 0.18f);
                g.DrawLine(pen, inset.Left, inset.Top, inset.Right, inset.Bottom);
                g.DrawLine(pen, inset.Right, inset.Top, inset.Left, inset.Bottom);
                break;
        }
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
