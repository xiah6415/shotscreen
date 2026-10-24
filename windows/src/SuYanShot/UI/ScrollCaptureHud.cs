using System.Drawing.Drawing2D;
using SuYanShot.Capture;
using SuYanShot.Native;

namespace SuYanShot.UI;

/// While a scroll capture runs, as on macOS: a click-through dashed border just outside the region, a control bar next to
/// it with progress, auto scroll and 取消 / 完成, and a live preview of what is stitched so far. None of these windows take
/// focus (scrolling must keep going to the app underneath) and all are excluded from capture.
internal sealed class ScrollCaptureHud : IDisposable
{
    private readonly BorderWindow border;
    private readonly ControlBar controls;
    private readonly PreviewWindow preview;

    /// <param name="region">Screen pixels.</param>
    public ScrollCaptureHud(Rectangle region, Action onDone, Action onCancel, Action<bool> onAutoScroll)
    {
        var screen = Screen.FromRectangle(region);
        var scale = NativeMethods.DpiOf(screen) / 96f;
        var area = screen.WorkingArea;
        border = new BorderWindow(Rectangle.Inflate(region, (int)(4 * scale), (int)(4 * scale)));

        var size = new Size((int)(500 * scale), (int)(52 * scale));
        var gap = (int)(14 * scale);
        var x = Math.Clamp(region.X + region.Width / 2 - size.Width / 2, area.Left + 8, Math.Max(area.Left + 8, area.Right - size.Width - 8));
        // Below the region if there is room, otherwise above, otherwise inside along the bottom edge.
        var y = region.Bottom + gap + size.Height <= area.Bottom ? region.Bottom + gap
              : region.Top - gap - size.Height >= area.Top ? region.Top - gap - size.Height
              : region.Bottom - size.Height - gap;
        controls = new ControlBar(new Rectangle(new Point(x, y), size), onDone, onCancel, onAutoScroll);

        // Preview beside the region: right if it fits, else left, else inside the right edge.
        var previewWidth = (int)(166 * scale);
        var previewHeight = Math.Min(Math.Max(region.Height, (int)(160 * scale)), area.Height - 16);
        var px = region.Right + gap;
        if (px + previewWidth > area.Right - 8) px = region.Left - gap - previewWidth;
        if (px < area.Left + 8) px = region.Right - previewWidth - 12;
        var py = Math.Clamp(region.Top, area.Top + 8, Math.Max(area.Top + 8, area.Bottom - previewHeight - 8));
        preview = new PreviewWindow(new Rectangle(px, py, previewWidth, previewHeight));
    }

    public void Show()
    {
        border.Show();
        preview.Show();
        controls.Show();
    }

    public void Update(ScrollCaptureProgress progress)
    {
        if (progress.Preview != null) preview.SetImage(progress.Preview);
        controls.StitchedHeight = progress.Height;
        controls.Auto = progress.Auto;
        controls.Message = (progress.Auto, progress.LastResult) switch
        {
            (_, FrameResult.LimitReached) => "已達長度上限，請按完成",
            (AutoScrollStatus.Checkpoint, _) => "已自動往下捲了一段，要繼續嗎？",
            (AutoScrollStatus.ReachedEnd, _) => "已經到底了，按完成產生長圖",
            (AutoScrollStatus.Running, _) => null,
            (AutoScrollStatus.Off, FrameResult.Rejected) => "沒對上，請慢一點往下捲",
            (AutoScrollStatus.Off, FrameResult.Appended) => null,
            _ => controls.Message,
        };
        controls.Invalidate();
    }

    public void Dispose()
    {
        border.Close();
        controls.Close();
        preview.Close();
    }

    /// Shared setup: borderless, topmost, never activated, excluded from capture.
    private abstract class HudWindow : Form
    {
        private readonly bool clickThrough;

        protected HudWindow(Rectangle bounds, bool clickThrough)
        {
            this.clickThrough = clickThrough;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Bounds = bounds;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
            Text = "拾光 捲動截圖";
        }

        protected float S(float value) => value * DeviceDpi / 96f;

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= (int)(NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE);
                if (clickThrough) cp.ExStyle |= 0x00080000 | 0x00000020; // WS_EX_LAYERED | WS_EX_TRANSPARENT
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            NativeMethods.ExcludeFromCapture(Handle);
        }

        // Never take focus, even when clicked.
        protected override void WndProc(ref Message m)
        {
            const int WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3;
            if (m.Msg == WM_MOUSEACTIVATE)
            {
                m.Result = MA_NOACTIVATE;
                return;
            }
            base.WndProc(ref m);
        }
    }

    /// Dashed accent border drawn on a transparent window just outside the region.
    private sealed class BorderWindow : HudWindow
    {
        public BorderWindow(Rectangle bounds) : base(bounds, clickThrough: true)
        {
            BackColor = Color.Magenta;
            TransparencyKey = Color.Magenta;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using var pen = new Pen(Color.FromArgb(10, 132, 255), S(2)) { DashStyle = DashStyle.Dash };
            var inset = S(1.5f);
            e.Graphics.DrawRectangle(pen, inset, inset, Width - inset * 2 - 1, Height - inset * 2 - 1);
        }
    }

    /// Stitched-so-far thumbnail, pinned to its newest (bottom) part once it is taller than the window.
    private sealed class PreviewWindow : HudWindow
    {
        private Bitmap? image;

        public PreviewWindow(Rectangle bounds) : base(bounds, clickThrough: true)
        {
            BackColor = Color.FromArgb(36, 36, 38);
        }

        public void SetImage(Bitmap next)
        {
            image?.Dispose();
            image = next;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var pad = S(8);
            var inner = new RectangleF(pad, pad, Width - pad * 2, Height - pad * 2);
            if (image == null)
            {
                using var font = new Font("Microsoft JhengHei UI", S(11), GraphicsUnit.Pixel);
                using var muted = new SolidBrush(Color.FromArgb(160, 160, 165));
                g.DrawString("準備中…", font, muted, inner, new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
                return;
            }
            var scale = inner.Width / image.Width;
            var drawnHeight = image.Height * scale;
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.SetClip(inner);
            g.DrawImage(image, inner.X, inner.Bottom - Math.Max(drawnHeight, 1), inner.Width, drawnHeight);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) image?.Dispose();
            base.Dispose(disposing);
        }
    }

    /// Message, stitched height, and the 自動捲動 / 暫停 / 繼續往下, 取消 and 完成 buttons.
    private sealed class ControlBar : HudWindow
    {
        private enum Button { Auto, Cancel, Done }

        private readonly Action onDone;
        private readonly Action onCancel;
        private readonly Action<bool> onAutoScroll;
        private Button? hovered;

        public int StitchedHeight { get; set; }
        public AutoScrollStatus Auto { get; set; }
        public string? Message { get; set; }

        public ControlBar(Rectangle bounds, Action onDone, Action onCancel, Action<bool> onAutoScroll) : base(bounds, clickThrough: false)
        {
            this.onDone = onDone;
            this.onCancel = onCancel;
            this.onAutoScroll = onAutoScroll;
            BackColor = Color.FromArgb(36, 36, 38);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            using var path = Annotations.AnnotationRenderer.RoundedRect(new RectangleF(0, 0, Width, Height), S(12));
            Region = new Region(path);
        }

        private string? AutoTitle => Auto switch
        {
            AutoScrollStatus.Off => "▶ 自動捲動",
            AutoScrollStatus.Running => "⏸ 暫停",
            AutoScrollStatus.Checkpoint => "↓ 繼續往下",
            _ => null,
        };

        private RectangleF ButtonRect(Button button)
        {
            var h = S(30);
            var y = (Height - h) / 2;
            var done = new RectangleF(Width - S(12) - S(64), y, S(64), h);
            var cancel = new RectangleF(done.X - S(8) - S(64), y, S(64), h);
            return button switch
            {
                Button.Done => done,
                Button.Cancel => cancel,
                _ => new RectangleF(cancel.X - S(8) - S(104), y, S(104), h),
            };
        }

        private Button? HitTest(Point p)
        {
            foreach (var button in new[] { Button.Done, Button.Cancel, Button.Auto })
                if ((button != Button.Auto || AutoTitle != null) && ButtonRect(button).Contains(p)) return button;
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var hit = HitTest(e.Location);
            if (hit == hovered) return;
            hovered = hit;
            Cursor = hit != null ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hovered = null;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            switch (HitTest(e.Location))
            {
                case Button.Done: onDone(); break;
                case Button.Cancel: onCancel(); break;
                case Button.Auto: onAutoScroll(Auto != AutoScrollStatus.Running); break;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using var bold = new Font("Microsoft JhengHei UI", S(12), FontStyle.Bold, GraphicsUnit.Pixel);
            using var small = new Font("Microsoft JhengHei UI", S(11), GraphicsUnit.Pixel);
            var accent = Color.FromArgb(10, 132, 255);
            var orange = Color.FromArgb(255, 159, 10);

            var placeholder = Auto == AutoScrollStatus.Running ? "自動捲動中…" : "往下捲動要截的內容";
            using (var ink = new SolidBrush(Message == null ? Color.White : orange))
                g.DrawString(Message ?? placeholder, bold, ink, S(14), S(8));
            using (var muted = new SolidBrush(Color.FromArgb(160, 160, 165)))
                g.DrawString(StitchedHeight > 0 ? $"已拼接 {StitchedHeight} px" : "準備中…", small, muted, S(14), S(27));

            void DrawButton(Button button, string title, Color fill, Color text)
            {
                var rect = ButtonRect(button);
                var color = hovered == button ? ControlPaint.Light(fill, 0.25f) : fill;
                using (var brush = new SolidBrush(color))
                using (var path = Annotations.AnnotationRenderer.RoundedRect(rect, S(7)))
                    g.FillPath(brush, path);
                using var ink = new SolidBrush(text);
                g.DrawString(title, small, ink, rect, new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
            }
            if (AutoTitle is string auto)
                DrawButton(Button.Auto, auto, Auto == AutoScrollStatus.Checkpoint ? orange : Color.FromArgb(64, 64, 68), Color.White);
            DrawButton(Button.Cancel, "取消", Color.FromArgb(64, 64, 68), Color.White);
            DrawButton(Button.Done, "完成", accent, Color.White);
        }
    }
}
