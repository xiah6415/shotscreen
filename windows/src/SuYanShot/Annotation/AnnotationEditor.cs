using System.Drawing.Drawing2D;
using SuYanShot.Capture;
using SuYanShot.Native;
using SuYanShot.Storage;

namespace SuYanShot.Annotations;

/// One editor window per capture, as on macOS: toolbar on top, canvas below; copy / save export the flattened image.
/// Ctrl+Z / Ctrl+Shift+Z (or Ctrl+Y) undo and redo, Ctrl+C copies, Ctrl+S saves to the Desktop, Ctrl+W closes,
/// and each tool has a single-key shortcut.
internal sealed class AnnotationEditor : Form
{
    private readonly AnnotationEditorState state;
    private readonly AnnotationCanvas canvas;
    private readonly AnnotationToolbar toolbar;
    private readonly DateTime capturedAt;
    private readonly ImageExporter exporter;

    public AnnotationEditor(Bitmap source, DateTime capturedAt, ImageExporter exporter)
    {
        this.capturedAt = capturedAt;
        this.exporter = exporter;
        var screen = ScreenCapture.ScreenUnderCursor();
        var scale = NativeMethods.DpiOf(screen) / 96f;
        state = new AnnotationEditorState(scale);
        canvas = new AnnotationCanvas(state, source) { Dock = DockStyle.Fill };
        toolbar = new AnnotationToolbar(state) { Dock = DockStyle.Top };
        toolbar.CopyClicked += Copy;
        toolbar.SaveClicked += Save;

        Text = "標註";
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(40, 40, 43);
        Controls.Add(canvas);
        Controls.Add(toolbar);

        // Open at the capture's real size when it fits, otherwise scaled into most of the screen.
        var area = screen.WorkingArea;
        var toolbarHeight = (int)(48 * scale);
        var chrome = SystemInformation.CaptionHeight + SystemInformation.FrameBorderSize.Height * 4;
        var fit = Math.Min(1f, Math.Min(area.Width * 0.85f / source.Width, (area.Height * 0.85f - toolbarHeight - chrome) / source.Height));
        var client = new Size(Math.Max((int)(900 * scale), (int)(source.Width * fit) + (int)(24 * scale)),
                              Math.Max((int)(420 * scale), (int)(source.Height * fit) + toolbarHeight + (int)(24 * scale)));
        ClientSize = new Size(Math.Min(client.Width, area.Width), Math.Min(client.Height, area.Height - chrome));
        MinimumSize = new Size((int)(900 * scale), (int)(420 * scale));
        Location = new Point(area.X + (area.Width - Width) / 2, area.Y + (area.Height - Height) / 2);
        toolbar.Height = toolbarHeight;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        canvas.Focus();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (canvas.IsEditingText) return base.ProcessCmdKey(ref msg, keyData);
        switch (keyData)
        {
            case Keys.Control | Keys.Z:
                canvas.DiscardCropDraft();
                state.Undo();
                return true;
            case Keys.Control | Keys.Shift | Keys.Z or Keys.Control | Keys.Y:
                canvas.DiscardCropDraft();
                state.Redo();
                return true;
            case Keys.Control | Keys.C:
                Copy();
                return true;
            case Keys.Control | Keys.S:
                Save();
                return true;
            case Keys.Control | Keys.W:
                Close();
                return true;
        }
        if (canvas.HandleKey(keyData)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private Bitmap Rendered()
    {
        canvas.CommitText();
        canvas.CommitCrop();
        return AnnotationRenderer.Flatten(canvas.Source, canvas.Effects, state.Annotations, state.Crop);
    }

    private void Copy()
    {
        using var image = Rendered();
        ClipboardManager.Copy(image);
        toolbar.Flash("已複製");
    }

    private void Save()
    {
        try
        {
            using var image = Rendered();
            exporter.ExportPng(image, date: capturedAt);
            toolbar.Flash("已存到桌面");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        {
            MessageBox.Show(this, ex.Message, "無法儲存", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        canvas.Source.Dispose();
        base.OnFormClosed(e);
    }
}

/// The editor toolbar: tools, colours, sizes, undo/redo, feedback and copy/save, all drawn in code.
internal sealed class AnnotationToolbar : Control
{
    private abstract record Item
    {
        public sealed record Tool(AnnotationTool Value) : Item;
        public sealed record Swatch(Color Value) : Item;
        public sealed record Size(int Level) : Item;
        public sealed record Undo : Item;
        public sealed record Redo : Item;
        public sealed record Copy : Item;
        public sealed record Save : Item;
    }

    private readonly AnnotationEditorState state;
    private readonly List<(Item Item, RectangleF Bounds)> items = new();
    private readonly ToolTip tip = new() { InitialDelay = 400 };
    private readonly System.Windows.Forms.Timer flashTimer = new() { Interval = 1400 };
    private Item? hovered;
    private string? feedback;

    public event Action? CopyClicked;
    public event Action? SaveClicked;

    public AnnotationToolbar(AnnotationEditorState state)
    {
        this.state = state;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false); // clicks must not take keyboard focus from the canvas
        BackColor = Color.FromArgb(32, 32, 34);
        state.Changed += Invalidate;
        flashTimer.Tick += (_, _) => { flashTimer.Stop(); feedback = null; Invalidate(); };
    }

    private float S(float value) => value * DeviceDpi / 96f;

    public void Flash(string message)
    {
        feedback = message;
        flashTimer.Stop();
        flashTimer.Start();
        Invalidate();
    }

    private void LayoutItems()
    {
        items.Clear();
        var x = S(12);
        var size = S(32);
        var y = (Height - size) / 2;
        foreach (var tool in Enum.GetValues<AnnotationTool>())
        {
            items.Add((new Item.Tool(tool), new RectangleF(x, y, size, size)));
            x += size + S(2);
        }
        x += S(12);
        foreach (var color in AnnotationEditorState.Palette)
        {
            items.Add((new Item.Swatch(color), new RectangleF(x, y, S(26), size)));
            x += S(26);
        }
        x += S(12);
        for (var level = 0; level < AnnotationEditorState.SizeLevels; level++)
        {
            items.Add((new Item.Size(level), new RectangleF(x, y, S(28), size)));
            x += S(28);
        }
        x += S(12);
        items.Add((new Item.Undo(), new RectangleF(x, y, size, size)));
        items.Add((new Item.Redo(), new RectangleF(x + size + S(2), y, size, size)));
        items.Add((new Item.Save(), new RectangleF(Width - S(12) - size, y, size, size)));
        items.Add((new Item.Copy(), new RectangleF(Width - S(14) - size * 2, y, size, size)));
    }

    private static string Tooltip(Item item) => item switch
    {
        Item.Tool tool => $"{tool.Value.Title()}（{tool.Value.Key()}）",
        Item.Size size => new[] { "細", "中", "粗" }[size.Level],
        Item.Undo => "復原（Ctrl+Z）",
        Item.Redo => "重做（Ctrl+Shift+Z）",
        Item.Copy => "複製到剪貼簿（Ctrl+C）",
        Item.Save => "存到桌面（Ctrl+S）",
        _ => "",
    };

    private bool IsEnabled(Item item) => item switch
    {
        Item.Undo => state.CanUndo,
        Item.Redo => state.CanRedo,
        _ => true,
    };

    private bool IsOn(Item item) => item switch
    {
        Item.Tool tool => state.Tool == tool.Value,
        Item.Swatch swatch => state.Color == swatch.Value,
        Item.Size size => state.SizeLevel == size.Level,
        _ => false,
    };

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var hit = items.FirstOrDefault(i => i.Bounds.Contains(e.Location)).Item;
        if (hit == hovered) return;
        hovered = hit;
        Cursor = hit != null && IsEnabled(hit) ? Cursors.Hand : Cursors.Default;
        var text = hit == null ? "" : Tooltip(hit);
        if (text.Length > 0) tip.SetToolTip(this, text); else tip.SetToolTip(this, null);
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        hovered = null;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        var hit = items.FirstOrDefault(i => i.Bounds.Contains(e.Location)).Item;
        if (hit == null || !IsEnabled(hit)) return;
        switch (hit)
        {
            case Item.Tool tool: state.Tool = tool.Value; break;
            case Item.Swatch swatch: state.SetColor(swatch.Value); break;
            case Item.Size size: state.SetSizeLevel(size.Level); break;
            case Item.Undo: state.Undo(); break;
            case Item.Redo: state.Redo(); break;
            case Item.Copy: CopyClicked?.Invoke(); break;
            case Item.Save: SaveClicked?.Invoke(); break;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        LayoutItems();
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        var accent = Color.FromArgb(10, 132, 255);
        foreach (var (item, bounds) in items)
        {
            var on = IsOn(item);
            var enabled = IsEnabled(item);
            if (item is not Item.Swatch && (on || (hovered == item && enabled)))
            {
                using var background = new SolidBrush(on ? Color.FromArgb(60, accent) : Color.FromArgb(40, 255, 255, 255));
                using var path = AnnotationRenderer.RoundedRect(bounds, S(6));
                g.FillPath(background, path);
            }
            var ink = !enabled ? Color.FromArgb(80, 255, 255, 255) : on ? Color.FromArgb(100, 170, 255) : Color.FromArgb(235, 235, 235);
            var icon = RectangleF.Inflate(bounds, -S(8), -S(8));
            switch (item)
            {
                case Item.Tool tool:
                    ToolbarIcons.Draw(g, tool.Value, icon, ink, S(1.6f));
                    break;
                case Item.Swatch swatch:
                    var circle = new RectangleF(bounds.X + (bounds.Width - S(18)) / 2, bounds.Y + (bounds.Height - S(18)) / 2, S(18), S(18));
                    using (var fill = new SolidBrush(swatch.Value)) g.FillEllipse(fill, circle);
                    using (var edge = new Pen(Color.FromArgb(80, 255, 255, 255), 1)) g.DrawEllipse(edge, circle);
                    if (on)
                        using (var ring = new Pen(accent, S(2))) g.DrawEllipse(ring, RectangleF.Inflate(circle, S(3), S(3)));
                    break;
                case Item.Size size:
                    var d = S(new[] { 5f, 8f, 12f }[size.Level]);
                    using (var dot = new SolidBrush(ink))
                        g.FillEllipse(dot, bounds.X + (bounds.Width - d) / 2, bounds.Y + (bounds.Height - d) / 2, d, d);
                    break;
                case Item.Undo or Item.Redo:
                    ToolbarIcons.DrawUndo(g, icon, ink, S(1.6f), mirrored: item is Item.Redo);
                    break;
                case Item.Copy:
                    ToolbarIcons.DrawCopy(g, icon, ink, S(1.6f));
                    break;
                case Item.Save:
                    ToolbarIcons.DrawSave(g, icon, ink, S(1.6f));
                    break;
            }
        }
        if (feedback != null)
        {
            using var font = new Font("Microsoft JhengHei UI", S(13), FontStyle.Bold, GraphicsUnit.Pixel);
            var size = g.MeasureString("✓ " + feedback, font);
            var copy = items.First(i => i.Item is Item.Copy).Bounds;
            using var green = new SolidBrush(Color.FromArgb(52, 199, 89));
            g.DrawString("✓ " + feedback, font, green, copy.X - size.Width - S(12), (Height - size.Height) / 2);
        }
        using var line = new Pen(Color.FromArgb(60, 255, 255, 255), 1);
        g.DrawLine(line, 0, Height - 1, Width, Height - 1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            state.Changed -= Invalidate;
            flashTimer.Dispose();
            tip.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// Line icons for the editor toolbar, drawn in code so the app ships no icon files.
internal static class ToolbarIcons
{
    public static void Draw(Graphics g, AnnotationTool tool, RectangleF r, Color color, float width)
    {
        using var pen = new Pen(color, width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(color);
        float l = r.Left, t = r.Top, rt = r.Right, b = r.Bottom, w = r.Width, h = r.Height;
        switch (tool)
        {
            case AnnotationTool.Select:
                g.FillPolygon(brush, new[] { new PointF(l + w * 0.2f, t), new PointF(l + w * 0.2f, b - h * 0.1f), new PointF(l + w * 0.45f, b - h * 0.35f),
                                             new PointF(l + w * 0.65f, b), new PointF(l + w * 0.78f, b - h * 0.08f), new PointF(l + w * 0.58f, b - h * 0.42f),
                                             new PointF(rt - w * 0.1f, b - h * 0.45f) });
                break;
            case AnnotationTool.Crop:
                g.DrawLines(pen, new[] { new PointF(l + w * 0.25f, t), new PointF(l + w * 0.25f, b - h * 0.25f), new PointF(rt, b - h * 0.25f) });
                g.DrawLines(pen, new[] { new PointF(l, t + h * 0.25f), new PointF(rt - w * 0.25f, t + h * 0.25f), new PointF(rt - w * 0.25f, b) });
                break;
            case AnnotationTool.Arrow:
                g.DrawLine(pen, l + w * 0.1f, b - h * 0.1f, rt - w * 0.15f, t + h * 0.15f);
                g.DrawLines(pen, new[] { new PointF(rt - w * 0.55f, t + h * 0.1f), new PointF(rt - w * 0.1f, t + h * 0.1f), new PointF(rt - w * 0.1f, t + h * 0.55f) });
                break;
            case AnnotationTool.Rectangle:
                using (var path = AnnotationRenderer.RoundedRect(new RectangleF(l, t + h * 0.15f, w, h * 0.7f), w * 0.1f)) g.DrawPath(pen, path);
                break;
            case AnnotationTool.Ellipse:
                g.DrawEllipse(pen, l, t + h * 0.05f, w, h * 0.9f);
                break;
            case AnnotationTool.Line:
                g.DrawLine(pen, l + w * 0.1f, b - h * 0.1f, rt - w * 0.1f, t + h * 0.1f);
                break;
            case AnnotationTool.Pen:
                g.DrawBezier(pen, new PointF(l, b - h * 0.3f), new PointF(l + w * 0.3f, t - h * 0.1f), new PointF(l + w * 0.6f, b + h * 0.2f), new PointF(rt, t + h * 0.25f));
                break;
            case AnnotationTool.Highlighter:
                using (var marker = new Pen(Color.FromArgb(150, color), width * 3.2f) { StartCap = LineCap.Square, EndCap = LineCap.Square })
                    g.DrawLine(marker, l + w * 0.1f, t + h * 0.62f, rt - w * 0.1f, t + h * 0.62f);
                g.DrawLine(pen, l + w * 0.25f, t + h * 0.2f, rt - w * 0.25f, t + h * 0.2f);
                break;
            case AnnotationTool.Text:
                g.DrawLine(pen, l + w * 0.1f, t + h * 0.12f, rt - w * 0.1f, t + h * 0.12f);
                g.DrawLine(pen, l + w * 0.5f, t + h * 0.12f, l + w * 0.5f, b - h * 0.05f);
                break;
            case AnnotationTool.Counter:
                g.DrawEllipse(pen, r);
                using (var font = new Font("Segoe UI", h * 0.6f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("1", font, brush, RectangleF.Inflate(r, 0, h * 0.05f), centered);
                break;
            case AnnotationTool.Pixelate:
                var cell = w / 3;
                for (var y = 0; y < 3; y++)
                    for (var x = 0; x < 3; x++)
                        if ((x + y) % 2 == 0) g.FillRectangle(brush, l + x * cell, t + y * cell, cell, cell);
                g.DrawRectangle(pen, l, t, w, h);
                break;
            default: // blur: a droplet half filled
                using (var path = new GraphicsPath())
                {
                    path.AddEllipse(l + w * 0.12f, t + h * 0.28f, w * 0.76f, h * 0.72f);
                    g.DrawPath(pen, path);
                    var state = g.Save();
                    g.SetClip(new RectangleF(l, t, w / 2, h));
                    g.FillPath(brush, path);
                    g.Restore(state);
                }
                g.DrawLine(pen, l + w * 0.5f, t, l + w * 0.2f, t + h * 0.45f);
                g.DrawLine(pen, l + w * 0.5f, t, l + w * 0.8f, t + h * 0.45f);
                break;
        }
    }

    public static void DrawUndo(Graphics g, RectangleF r, Color color, float width, bool mirrored)
    {
        var state = g.Save();
        if (mirrored)
        {
            g.TranslateTransform(r.Left + r.Right, 0);
            g.ScaleTransform(-1, 1);
        }
        using var pen = new Pen(color, width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawArc(pen, r.Left + r.Width * 0.15f, r.Top + r.Height * 0.2f, r.Width * 0.8f, r.Height * 0.7f, 200, 230);
        g.DrawLines(pen, new[] { new PointF(r.Left + r.Width * 0.1f, r.Top + r.Height * 0.15f), new PointF(r.Left + r.Width * 0.15f, r.Top + r.Height * 0.5f),
                                 new PointF(r.Left + r.Width * 0.5f, r.Top + r.Height * 0.45f) });
        g.Restore(state);
    }

    public static void DrawCopy(Graphics g, RectangleF r, Color color, float width)
    {
        using var pen = new Pen(color, width) { LineJoin = LineJoin.Round };
        g.DrawRectangle(pen, r.Left + r.Width * 0.3f, r.Top, r.Width * 0.7f, r.Height * 0.7f);
        g.DrawLines(pen, new[] { new PointF(r.Left + r.Width * 0.18f, r.Top + r.Height * 0.3f), new PointF(r.Left, r.Top + r.Height * 0.3f),
                                 new PointF(r.Left, r.Bottom), new PointF(r.Left + r.Width * 0.7f, r.Bottom), new PointF(r.Left + r.Width * 0.7f, r.Bottom - r.Height * 0.12f) });
    }

    public static void DrawSave(Graphics g, RectangleF r, Color color, float width)
    {
        using var pen = new Pen(color, width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLine(pen, r.Left + r.Width / 2, r.Top, r.Left + r.Width / 2, r.Bottom - r.Height * 0.3f);
        g.DrawLines(pen, new[] { new PointF(r.Left + r.Width * 0.22f, r.Top + r.Height * 0.4f), new PointF(r.Left + r.Width / 2, r.Bottom - r.Height * 0.3f),
                                 new PointF(r.Right - r.Width * 0.22f, r.Top + r.Height * 0.4f) });
        g.DrawLines(pen, new[] { new PointF(r.Left, r.Bottom - r.Height * 0.2f), new PointF(r.Left, r.Bottom), new PointF(r.Right, r.Bottom), new PointF(r.Right, r.Bottom - r.Height * 0.2f) });
    }
}
