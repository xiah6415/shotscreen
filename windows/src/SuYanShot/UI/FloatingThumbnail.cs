using System.Diagnostics;
using System.Drawing.Drawing2D;
using SuYanShot.Capture;
using SuYanShot.Native;
using SuYanShot.Storage;

namespace SuYanShot.UI;

/// The preview card shown in the bottom-right corner after a capture, like the macOS app:
/// hover shows 複製 / 儲存 / ✕, it closes by itself after 8 s unless hovered, drag it into another app to drop a PNG,
/// and double-click saves it and opens it in the default viewer.
internal sealed class FloatingThumbnail : Form
{
    private const int CardWidth = 260;
    private const int CardMargin = 16;
    private const int AutoDismissMs = 8000;

    private readonly Bitmap image;
    private readonly ImageExporter exporter;
    private readonly System.Windows.Forms.Timer dismissTimer = new() { Interval = AutoDismissMs };
    private readonly System.Windows.Forms.Timer hoverTimer = new() { Interval = 150 };
    private string? savedPath;
    private string? toast;
    private bool hovering;
    private Point? dragStart;

    private static readonly List<FloatingThumbnail> Open = new();

    private enum Button { None, Copy, Save, Close }

    public FloatingThumbnail(Bitmap image, ImageExporter exporter, Screen screen)
    {
        this.image = image;
        this.exporter = exporter;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(32, 32, 32);
        Text = "素顏 Shot";

        var scale = DpiOf(screen) / 96f;
        var width = (int)(CardWidth * scale);
        var thumbHeight = (int)Math.Clamp(width * (double)image.Height / Math.Max(1, image.Width), 60 * scale, 220 * scale);
        var area = screen.WorkingArea;
        var margin = (int)(CardMargin * scale);
        var stackOffset = Open.Where(card => card.Visible).Sum(card => card.Height + margin / 2);
        Bounds = new Rectangle(area.Right - width - margin, area.Bottom - thumbHeight - margin - stackOffset, width, thumbHeight);

        dismissTimer.Tick += (_, _) => { if (!hovering) Close(); };
        // MouseLeave is unreliable while dragging over child-less forms; poll the pointer instead.
        hoverTimer.Tick += (_, _) =>
        {
            var inside = Bounds.Contains(Cursor.Position);
            if (inside == hovering) return;
            hovering = inside;
            if (!inside) { dismissTimer.Stop(); dismissTimer.Start(); }
            Invalidate();
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= (int)(NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE);
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.ExcludeFromCapture(Handle);
        using var path = OverlayForm.RoundedRect(new RectangleF(0, 0, Width, Height), 10 * DeviceDpi / 96f);
        Region = new Region(path);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Open.Add(this);
        dismissTimer.Start();
        hoverTimer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Open.Remove(this);
        dismissTimer.Dispose();
        hoverTimer.Dispose();
        image.Dispose();
        base.OnFormClosed(e);
    }

    private float S(float value) => value * DeviceDpi / 96f;

    private Rectangle ButtonRect(Button button)
    {
        var size = (int)S(30);
        var gap = (int)S(8);
        return button switch
        {
            Button.Close => new Rectangle(Width - size - gap, gap, size, size),
            Button.Copy => new Rectangle(Width / 2 - size - gap / 2, Height / 2 - size / 2, size, size),
            Button.Save => new Rectangle(Width / 2 + gap / 2, Height / 2 - size / 2, size, size),
            _ => Rectangle.Empty,
        };
    }

    private Button HitTest(Point p)
    {
        foreach (var button in new[] { Button.Close, Button.Copy, Button.Save })
            if (ButtonRect(button).Contains(p)) return button;
        return Button.None;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // Fill the card, cropping the image like "aspect fill".
        var scale = Math.Max(Width / (float)image.Width, Height / (float)image.Height);
        var drawn = new SizeF(image.Width * scale, image.Height * scale);
        g.DrawImage(image, new RectangleF((Width - drawn.Width) / 2, (Height - drawn.Height) / 2, drawn.Width, drawn.Height));

        if (hovering)
        {
            using var shade = new SolidBrush(Color.FromArgb(120, 0, 0, 0));
            g.FillRectangle(shade, ClientRectangle);
            DrawButton(g, Button.Copy, "\uE8C8");
            DrawButton(g, Button.Save, "\uE74E");
            DrawButton(g, Button.Close, "\uE711");
        }

        if (toast != null)
        {
            using var font = new Font("Microsoft JhengHei UI", S(12), FontStyle.Bold, GraphicsUnit.Pixel);
            var size = g.MeasureString(toast, font);
            var box = new RectangleF((Width - size.Width) / 2 - S(10), Height - size.Height - S(18), size.Width + S(20), size.Height + S(8));
            using var background = new SolidBrush(Color.FromArgb(220, 20, 20, 20));
            using var path = OverlayForm.RoundedRect(box, S(6));
            g.FillPath(background, path);
            g.DrawString(toast, font, Brushes.White, box.X + S(10), box.Y + S(4));
        }
    }

    private void DrawButton(Graphics g, Button button, string glyph)
    {
        var rect = ButtonRect(button);
        using var background = new SolidBrush(Color.FromArgb(230, 250, 250, 250));
        g.FillEllipse(background, rect);
        using var font = new Font("Segoe Fluent Icons", S(14), GraphicsUnit.Pixel);
        using var fallback = font.Name == "Segoe Fluent Icons" ? null : new Font("Segoe MDL2 Assets", S(14), GraphicsUnit.Pixel);
        var useFont = fallback ?? font;
        var size = g.MeasureString(glyph, useFont);
        using var ink = new SolidBrush(Color.FromArgb(30, 30, 30));
        g.DrawString(glyph, useFont, ink, rect.X + (rect.Width - size.Width) / 2, rect.Y + (rect.Height - size.Height) / 2);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && HitTest(e.Location) == Button.None) dragStart = e.Location;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        Cursor = HitTest(e.Location) == Button.None ? Cursors.Default : Cursors.Hand;
        if (dragStart is not Point start || e.Button != MouseButtons.Left) return;
        var threshold = SystemInformation.DragSize;
        if (Math.Abs(e.X - start.X) < threshold.Width && Math.Abs(e.Y - start.Y) < threshold.Height) return;
        dragStart = null;
        DragOut();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        dragStart = null;
        if (e.Button != MouseButtons.Left) return;
        switch (HitTest(e.Location))
        {
            case Button.Copy:
                ClipboardManager.Copy(image);
                ShowToast("已複製");
                break;
            case Button.Save:
                Save();
                ShowToast("已存到桌面");
                break;
            case Button.Close:
                Close();
                break;
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        if (HitTest(e.Location) != Button.None) return;
        var path = Save();
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        Close();
    }

    private string Save() => savedPath ??= exporter.ExportPng(image);

    /// Drops a real .png file, which works for Explorer, chat apps and browsers alike. Closes after a successful drop.
    private void DragOut()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SuYanShot");
        Directory.CreateDirectory(folder);
        var path = savedPath ?? ImageExporter.UniquePath(folder, ImageExporter.DefaultFilename(DateTime.Now));
        if (savedPath == null) image.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        var data = new DataObject();
        data.SetFileDropList(new System.Collections.Specialized.StringCollection { path });
        data.SetImage(image);
        var effect = DoDragDrop(data, DragDropEffects.Copy | DragDropEffects.Move);
        if (effect != DragDropEffects.None) Close();
    }

    private void ShowToast(string message)
    {
        toast = message;
        Invalidate();
        var timer = new System.Windows.Forms.Timer { Interval = 1200 };
        timer.Tick += (_, _) =>
        {
            timer.Dispose();
            if (IsDisposed) return;
            toast = null;
            Invalidate();
        };
        timer.Start();
    }

    private static int DpiOf(Screen screen) => NativeMethods.DpiOf(screen);
}
