namespace SuYanShot.Annotations;

/// Editor document: annotations plus tool settings, with snapshot-based undo (same behaviour as macOS).
internal sealed class AnnotationEditorState
{
    /// The macOS palette: red, orange, yellow, green, blue, black, white.
    public static readonly Color[] Palette =
    {
        Color.FromArgb(255, 59, 48), Color.FromArgb(255, 149, 0), Color.FromArgb(255, 204, 0), Color.FromArgb(52, 199, 89),
        Color.FromArgb(0, 122, 255), Color.Black, Color.White,
    };

    public const int SizeLevels = 3;

    private sealed record Snapshot(IReadOnlyList<Annotation> Annotations, Rectangle? Crop);

    private readonly Stack<Snapshot> undo = new();
    private readonly Stack<Snapshot> redo = new();
    private List<Annotation> annotations = new();
    private AnnotationTool tool = AnnotationTool.Arrow;
    private Guid? selectedId;

    /// Raised after any change, so the toolbar and canvas repaint.
    public event Action? Changed;

    /// Image pixels per logical pixel at capture time, so default sizes look the same at any display scaling.
    public float PixelScale { get; }

    public AnnotationEditorState(float pixelScale) => PixelScale = pixelScale;

    public IReadOnlyList<Annotation> Annotations => annotations;
    public Color Color { get; private set; } = Palette[0];
    public int SizeLevel { get; private set; } = 1;

    /// Applied crop in image pixels; null shows the whole capture.
    public Rectangle? Crop { get; private set; }

    /// Tool to return to after a crop is applied or cancelled.
    public AnnotationTool ToolBeforeCrop { get; private set; } = AnnotationTool.Arrow;

    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    public AnnotationTool Tool
    {
        get => tool;
        set
        {
            if (value != AnnotationTool.Select) selectedId = null;
            if (value == AnnotationTool.Crop && tool != AnnotationTool.Crop) ToolBeforeCrop = tool;
            tool = value;
            Changed?.Invoke();
        }
    }

    public Guid? SelectedId
    {
        get => selectedId;
        set { selectedId = value; Changed?.Invoke(); }
    }

    public float LineWidth => new[] { 3f, 5f, 9f }[SizeLevel] * PixelScale;
    public float FontSize => new[] { 16f, 22f, 32f }[SizeLevel] * PixelScale;

    public Annotation? Selected => annotations.Find(a => a.Id == selectedId);

    public int NextCounter => annotations.Where(a => a.Kind == AnnotationKind.Counter).Select(a => a.Number).DefaultIfEmpty(0).Max() + 1;

    public Annotation? NewAnnotation(PointF point)
    {
        AnnotationKind? kind = tool switch
        {
            AnnotationTool.Arrow => AnnotationKind.Arrow,
            AnnotationTool.Rectangle => AnnotationKind.Rectangle,
            AnnotationTool.Ellipse => AnnotationKind.Ellipse,
            AnnotationTool.Line => AnnotationKind.Line,
            AnnotationTool.Pen => AnnotationKind.Pen,
            AnnotationTool.Highlighter => AnnotationKind.Highlighter,
            AnnotationTool.Pixelate => AnnotationKind.Pixelate,
            AnnotationTool.Blur => AnnotationKind.Blur,
            AnnotationTool.Counter => AnnotationKind.Counter,
            _ => null,
        };
        if (kind is not AnnotationKind k) return null;
        var single = k is AnnotationKind.Pen or AnnotationKind.Highlighter or AnnotationKind.Counter;
        return new Annotation
        {
            Kind = k,
            Points = single ? new[] { point } : new[] { point, point },
            Color = Color,
            LineWidth = LineWidth,
            FontSize = FontSize,
            Number = k == AnnotationKind.Counter ? NextCounter : 0,
        };
    }

    public Annotation NewText(string text, PointF point) =>
        new() { Kind = AnnotationKind.Text, Points = new[] { point }, Color = Color, LineWidth = LineWidth, FontSize = FontSize, Text = text };

    /// Changing color or size also restyles the selected mark.
    public void SetColor(Color color)
    {
        Color = color;
        if (Selected is Annotation selected)
        {
            Checkpoint();
            Replace(selected with { Color = color });
        }
        Changed?.Invoke();
    }

    public void SetSizeLevel(int level)
    {
        SizeLevel = Math.Clamp(level, 0, SizeLevels - 1);
        if (Selected is Annotation selected)
        {
            Checkpoint();
            Replace(selected with { LineWidth = LineWidth, FontSize = FontSize });
        }
        Changed?.Invoke();
    }

    public void Checkpoint()
    {
        undo.Push(new Snapshot(annotations.ToList(), Crop));
        redo.Clear();
    }

    /// Null (or a rect covering the whole image, or a tiny one) removes the crop.
    public void ApplyCrop(Rectangle? rect, Size imageSize)
    {
        var full = new Rectangle(Point.Empty, imageSize);
        Rectangle? clipped = rect is Rectangle r ? Rectangle.Intersect(r, full) : null;
        if (clipped is Rectangle c && (c == full || c.Width < 2 || c.Height < 2)) clipped = null;
        if (clipped == Crop) return;
        Checkpoint();
        Crop = clipped;
        Changed?.Invoke();
    }

    /// Replaces a text mark's string; an empty string deletes it.
    public void UpdateText(Guid id, string text)
    {
        if (annotations.Find(a => a.Id == id) is not Annotation existing || existing.Kind != AnnotationKind.Text || existing.Text == text) return;
        Checkpoint();
        if (text.Length == 0)
        {
            annotations.Remove(existing);
            if (selectedId == id) selectedId = null;
        }
        else
        {
            Replace(existing with { Text = text });
        }
        Changed?.Invoke();
    }

    public void Add(Annotation annotation)
    {
        Checkpoint();
        annotations.Add(annotation);
        Changed?.Invoke();
    }

    /// Call <see cref="Checkpoint"/> once before a drag, then Move for each step.
    public void Move(Guid id, float dx, float dy)
    {
        if (annotations.Find(a => a.Id == id) is not Annotation existing) return;
        Replace(existing.Translated(dx, dy));
        Changed?.Invoke();
    }

    public void RemoveSelected()
    {
        if (Selected is not Annotation selected) return;
        Checkpoint();
        annotations.Remove(selected);
        selectedId = null;
        Changed?.Invoke();
    }

    public void Undo()
    {
        if (undo.Count == 0) return;
        redo.Push(new Snapshot(annotations.ToList(), Crop));
        Restore(undo.Pop());
    }

    public void Redo()
    {
        if (redo.Count == 0) return;
        undo.Push(new Snapshot(annotations.ToList(), Crop));
        Restore(redo.Pop());
    }

    private void Restore(Snapshot snapshot)
    {
        annotations = snapshot.Annotations.ToList();
        Crop = snapshot.Crop;
        selectedId = null;
        Changed?.Invoke();
    }

    public Annotation? Hit(PointF point, float tolerance) =>
        Enumerable.Reverse(annotations).FirstOrDefault(a => AnnotationRenderer.HitTest(a, point, tolerance));

    private void Replace(Annotation updated)
    {
        var index = annotations.FindIndex(a => a.Id == updated.Id);
        if (index >= 0) annotations[index] = updated;
    }
}
