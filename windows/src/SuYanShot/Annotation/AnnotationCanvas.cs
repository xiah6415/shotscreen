using System.Drawing.Drawing2D;

namespace SuYanShot.Annotations;

/// Shows the capture scaled to fit (never larger than its real size) and turns mouse input into annotations.
/// A port of the macOS AnnotationCanvasView; coordinates are image pixels with a top-left origin.
internal sealed class AnnotationCanvas : Control
{
    public Bitmap Source { get; }
    public AnnotationEffects Effects { get; }

    private readonly AnnotationEditorState state;
    private Annotation? draft;
    private PointF? dragAnchor;
    private bool didCheckpointDrag;
    private PointF? cropAnchor;
    private RectangleF? cropDraft;
    private TextBox? textBox;
    private Guid? editingTextId;
    private PointF textAnchor;

    public AnnotationCanvas(AnnotationEditorState state, Bitmap source)
    {
        this.state = state;
        Source = source;
        Effects = new AnnotationEffects(source);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Color.FromArgb(40, 40, 43);
        state.Changed += StateChanged;
    }

    private float S(float value) => value * DeviceDpi / 96f;

    private void StateChanged()
    {
        if (state.Tool is not (AnnotationTool.Text or AnnotationTool.Select)) CommitText();
        if (state.Tool != AnnotationTool.Crop) CommitCrop();
        Cursor = state.Tool switch
        {
            AnnotationTool.Select => Cursors.Default,
            AnnotationTool.Text => Cursors.IBeam,
            _ => Cursors.Cross,
        };
        Invalidate();
        if (textBox == null && !Focused) Focus();
    }

    // MARK: Geometry

    private Size ImageSize => Source.Size;
    private Rectangle FullRect => new(Point.Empty, ImageSize);

    /// The part of the image on screen: everything while cropping, otherwise the applied crop.
    private Rectangle VisibleRegion => state.Tool == AnnotationTool.Crop ? FullRect : state.Crop ?? FullRect;

    /// Screen pixels per image pixel: fit the canvas, but never larger than the capture's real size.
    private float ViewScale
    {
        get
        {
            var region = VisibleRegion;
            var fit = Math.Min((Width - S(24)) / (float)region.Width, (Height - S(24)) / (float)region.Height);
            return Math.Max(0.01f, Math.Min(fit, 1f));
        }
    }

    private RectangleF ImageRect
    {
        get
        {
            var size = new SizeF(VisibleRegion.Width * ViewScale, VisibleRegion.Height * ViewScale);
            return new RectangleF(MathF.Round((Width - size.Width) / 2), MathF.Round((Height - size.Height) / 2), size.Width, size.Height);
        }
    }

    private PointF ToImage(PointF view)
    {
        var rect = ImageRect;
        return new PointF(VisibleRegion.X + (view.X - rect.X) / ViewScale, VisibleRegion.Y + (view.Y - rect.Y) / ViewScale);
    }

    private PointF ToImageClamped(Point view)
    {
        var rect = ImageRect;
        return ToImage(new PointF(Math.Clamp(view.X, rect.Left, rect.Right), Math.Clamp(view.Y, rect.Top, rect.Bottom)));
    }

    private PointF ToView(PointF image)
    {
        var rect = ImageRect;
        return new PointF(rect.X + (image.X - VisibleRegion.X) * ViewScale, rect.Y + (image.Y - VisibleRegion.Y) * ViewScale);
    }

    private RectangleF ToView(RectangleF image)
    {
        var origin = ToView(image.Location);
        return new RectangleF(origin.X, origin.Y, image.Width * ViewScale, image.Height * ViewScale);
    }

    // MARK: Drawing

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var rect = ImageRect;
        var region = VisibleRegion;
        var state0 = g.Save();
        g.SetClip(rect);
        g.TranslateTransform(rect.X - region.X * ViewScale, rect.Y - region.Y * ViewScale);
        g.ScaleTransform(ViewScale, ViewScale);
        g.InterpolationMode = ViewScale >= 0.999f ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBilinear;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(Source, 0, 0, Source.Width, Source.Height);
        AnnotationRenderer.Prepare(g);
        foreach (var annotation in state.Annotations.Where(a => a.Id != editingTextId))
            AnnotationRenderer.Draw(g, annotation, Effects);
        if (draft != null) AnnotationRenderer.Draw(g, draft, Effects);
        g.Restore(state0);

        if (state.Tool == AnnotationTool.Crop) DrawCropOverlay(g);

        if (state.Selected is Annotation selected && selected.Id != editingTextId)
        {
            var box = RectangleF.Inflate(ToView(AnnotationRenderer.Bounds(selected)), S(4), S(4));
            using var pen = new Pen(Color.FromArgb(10, 132, 255), 1) { DashStyle = DashStyle.Dash };
            g.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);
        }
    }

    /// Dims everything outside the pending (or current) crop and labels its size.
    private void DrawCropOverlay(Graphics g)
    {
        var pixels = cropDraft ?? (RectangleF?)state.Crop ?? FullRect;
        var box = ToView(pixels);
        using (var shade = new Region(ImageRect))
        {
            shade.Exclude(box);
            using var brush = new SolidBrush(Color.FromArgb(128, 0, 0, 0));
            g.FillRegion(brush, shade);
        }
        g.DrawRectangle(Pens.White, box.X, box.Y, box.Width, box.Height);
        var hint = cropDraft == null ? "拖曳框出要保留的範圍 · Backspace 還原原圖" : "Enter 套用 · Esc 取消";
        var text = $"{(int)pixels.Width} × {(int)pixels.Height}  {hint}";
        using var font = new Font("Microsoft JhengHei UI", S(12), GraphicsUnit.Pixel);
        var size = g.MeasureString(text, font);
        var label = new RectangleF(box.X, box.Y - size.Height - S(10), size.Width + S(12), size.Height + S(6));
        if (label.Y < 0) label.Y = box.Y + S(6);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var background = new SolidBrush(Color.FromArgb(210, 20, 20, 20)))
        using (var path = AnnotationRenderer.RoundedRect(label, S(4)))
            g.FillPath(background, path);
        g.DrawString(text, font, Brushes.White, label.X + S(6), label.Y + S(3));
    }

    protected override void OnResize(EventArgs e)
    {
        CommitText();
        base.OnResize(e);
    }

    // MARK: Mouse

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        if (e.Button != MouseButtons.Left) return;
        CommitText();
        var point = ToImageClamped(e.Location);
        var tolerance = S(6) / ViewScale;
        switch (state.Tool)
        {
            case AnnotationTool.Select:
                var hit = state.Hit(point, tolerance);
                if (e.Clicks == 2 && hit is { Kind: AnnotationKind.Text })
                {
                    BeginText(point, hit);
                    return;
                }
                state.SelectedId = hit?.Id;
                dragAnchor = hit == null ? null : point;
                didCheckpointDrag = false;
                break;
            case AnnotationTool.Crop:
                cropAnchor = point;
                cropDraft = null;
                break;
            case AnnotationTool.Text:
                var textHit = state.Hit(point, tolerance);
                BeginText(point, textHit is { Kind: AnnotationKind.Text } ? textHit : null);
                break;
            case AnnotationTool.Counter:
                if (state.NewAnnotation(point) is Annotation counter) state.Add(counter);
                break;
            default:
                draft = state.NewAnnotation(point);
                break;
        }
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        var point = ToImageClamped(e.Location);
        var shift = (ModifierKeys & Keys.Shift) != 0;
        if (state.Tool == AnnotationTool.Select && dragAnchor is PointF anchor && state.SelectedId is Guid id)
        {
            if (!didCheckpointDrag) { state.Checkpoint(); didCheckpointDrag = true; }
            state.Move(id, point.X - anchor.X, point.Y - anchor.Y);
            dragAnchor = point;
        }
        else if (state.Tool == AnnotationTool.Crop && cropAnchor is PointF start)
        {
            var end = shift ? Constrained(start, point, AnnotationKind.Rectangle) : point;
            var box = RectangleF.FromLTRB(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y), Math.Max(start.X, end.X), Math.Max(start.Y, end.Y));
            cropDraft = RectangleF.Intersect(box, FullRect);
            Invalidate();
        }
        else if (draft != null)
        {
            draft = draft.Kind is AnnotationKind.Pen or AnnotationKind.Highlighter
                ? draft with { Points = draft.Points.Append(point).ToList() }
                : draft with { Points = new[] { draft.Start, shift ? Constrained(draft.Start, point, draft.Kind) : point } };
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        dragAnchor = null;
        if (state.Tool == AnnotationTool.Crop)
        {
            cropAnchor = null;
            if (cropDraft is RectangleF pending && (pending.Width * ViewScale < 4 || pending.Height * ViewScale < 4)) cropDraft = null;
            Invalidate();
            return;
        }
        if (draft is not Annotation finished) return;
        draft = null;
        var isStroke = finished.Kind is AnnotationKind.Pen or AnnotationKind.Highlighter;
        var dx = finished.End.X - finished.Start.X;
        var dy = finished.End.Y - finished.Start.Y;
        if (isStroke || MathF.Sqrt(dx * dx + dy * dy) * ViewScale >= 3) state.Add(finished);
        Invalidate();
    }

    /// Shift: squares/circles for boxes, 45° steps for lines and arrows.
    private static PointF Constrained(PointF start, PointF point, AnnotationKind kind)
    {
        float dx = point.X - start.X, dy = point.Y - start.Y;
        if (kind is AnnotationKind.Line or AnnotationKind.Arrow)
        {
            const float step = MathF.PI / 4;
            var angle = MathF.Round(MathF.Atan2(dy, dx) / step) * step;
            var length = MathF.Sqrt(dx * dx + dy * dy);
            return new PointF(start.X + MathF.Cos(angle) * length, start.Y + MathF.Sin(angle) * length);
        }
        var side = Math.Max(Math.Abs(dx), Math.Abs(dy));
        return new PointF(start.X + (dx < 0 ? -side : side), start.Y + (dy < 0 ? -side : side));
    }

    // MARK: Crop

    /// Applies a pending crop drag, if any.
    public void CommitCrop()
    {
        if (cropDraft is not RectangleF pending) return;
        cropDraft = null;
        state.ApplyCrop(Rectangle.Round(pending), ImageSize);
    }

    private void LeaveCropMode(bool apply)
    {
        if (apply) CommitCrop(); else cropDraft = null;
        state.Tool = state.ToolBeforeCrop;
        Invalidate();
    }

    // MARK: Keyboard

    /// Called by the editor window for keys that are not shortcuts of its own. Returns true when handled.
    public bool HandleKey(Keys keyData)
    {
        if (textBox != null) return false;
        var key = keyData & Keys.KeyCode;
        var modifiers = keyData & Keys.Modifiers;
        if (modifiers is not (Keys.None or Keys.Shift)) return false;

        if (state.Tool == AnnotationTool.Crop)
        {
            switch (key)
            {
                case Keys.Enter: LeaveCropMode(apply: true); return true;
                case Keys.Escape: LeaveCropMode(apply: false); return true;
                case Keys.Back or Keys.Delete:
                    cropDraft = null;
                    state.ApplyCrop(null, ImageSize);
                    return true;
            }
        }
        switch (key)
        {
            case Keys.Back or Keys.Delete:
                state.RemoveSelected();
                return true;
            case Keys.Enter when state.Selected is { Kind: AnnotationKind.Text } text:
                BeginText(text.Start, text);
                return true;
            case Keys.Escape:
                if (draft != null) { draft = null; Invalidate(); } else state.SelectedId = null;
                return true;
        }
        foreach (var tool in Enum.GetValues<AnnotationTool>())
            if (tool.Key() == key && modifiers == Keys.None)
            {
                state.Tool = tool;
                return true;
            }
        return false;
    }

    public bool IsEditingText => textBox != null;

    /// Undo/redo also drop a crop drag in progress.
    public void DiscardCropDraft() => cropDraft = null;

    // MARK: Text

    /// Opens an in-place text box for a new text mark at the point, or over an existing one to edit it.
    private void BeginText(PointF point, Annotation? existing)
    {
        var fontSize = (existing?.FontSize ?? state.FontSize) * ViewScale;
        var color = existing?.Color ?? state.Color;
        var font = AnnotationRenderer.TextFont(fontSize);
        var height = (int)Math.Ceiling(font.GetHeight()) + 6;
        var anchor = existing != null ? ToView(existing.Start) : new PointF(ToView(point).X, ToView(point).Y - height / 2f);
        textAnchor = ToImage(anchor);
        var luminance = 0.299 * color.R + 0.587 * color.G + 0.114 * color.B;
        var box = new TextBox
        {
            Text = existing?.Text ?? "",
            Font = font,
            ForeColor = color,
            // WinForms text boxes cannot be transparent; pick a backdrop the colour stands out on.
            BackColor = luminance > 140 ? Color.FromArgb(45, 45, 48) : Color.FromArgb(245, 245, 245),
            BorderStyle = BorderStyle.None,
            Location = Point.Round(anchor),
            Width = (int)Math.Max(S(180), ImageRect.Right - anchor.X),
            PlaceholderText = "輸入文字，按 Enter 完成",
        };
        box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; CommitText(); }
            else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; CancelText(); }
        };
        box.LostFocus += (_, _) => BeginInvoke(CommitText);
        Controls.Add(box);
        box.Focus();
        box.SelectAll();
        textBox = box;
        editingTextId = existing?.Id;
        Invalidate();
    }

    /// Turns the text box into a text mark, updates the mark being edited, or drops an empty new one.
    public void CommitText()
    {
        if (textBox is not TextBox box) return;
        textBox = null;
        var text = box.Text.Trim();
        var edited = editingTextId;
        editingTextId = null;
        Controls.Remove(box);
        box.Dispose();
        Focus();
        if (edited is Guid id) state.UpdateText(id, text);
        else if (text.Length > 0) state.Add(state.NewText(text, new PointF(textAnchor.X + 1, textAnchor.Y + 1)));
        Invalidate();
    }

    private void CancelText()
    {
        if (textBox is not TextBox box) return;
        textBox = null;
        editingTextId = null;
        Controls.Remove(box);
        box.Dispose();
        Focus();
        Invalidate();
    }

    protected override bool IsInputKey(Keys keyData) => true;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            state.Changed -= StateChanged;
            Effects.Dispose();
        }
        base.Dispose(disposing);
    }
}
