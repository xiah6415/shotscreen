namespace SuYanShot.Annotations;

/// The editor's tools, in toolbar order, with the same titles and single-key shortcuts as macOS.
internal enum AnnotationTool { Select, Crop, Arrow, Rectangle, Ellipse, Line, Pen, Highlighter, Text, Counter, Pixelate, Blur }

internal static class AnnotationToolInfo
{
    public static string Title(this AnnotationTool tool) => tool switch
    {
        AnnotationTool.Select => "選取",
        AnnotationTool.Crop => "裁切",
        AnnotationTool.Arrow => "箭頭",
        AnnotationTool.Rectangle => "矩形",
        AnnotationTool.Ellipse => "橢圓",
        AnnotationTool.Line => "直線",
        AnnotationTool.Pen => "畫筆",
        AnnotationTool.Highlighter => "螢光筆",
        AnnotationTool.Text => "文字",
        AnnotationTool.Counter => "數字標記",
        AnnotationTool.Pixelate => "馬賽克",
        _ => "模糊",
    };

    public static Keys Key(this AnnotationTool tool) => tool switch
    {
        AnnotationTool.Select => Keys.V,
        AnnotationTool.Crop => Keys.C,
        AnnotationTool.Arrow => Keys.A,
        AnnotationTool.Rectangle => Keys.R,
        AnnotationTool.Ellipse => Keys.O,
        AnnotationTool.Line => Keys.L,
        AnnotationTool.Pen => Keys.P,
        AnnotationTool.Highlighter => Keys.H,
        AnnotationTool.Text => Keys.T,
        AnnotationTool.Counter => Keys.N,
        AnnotationTool.Pixelate => Keys.B,
        _ => Keys.U,
    };
}

internal enum AnnotationKind { Arrow, Rectangle, Ellipse, Line, Pen, Highlighter, Pixelate, Blur, Text, Counter }

/// One mark on a capture. Geometry is in image pixels (top-left origin), so the same value
/// renders on screen (scaled) and into the exported image (1:1).
internal sealed record Annotation
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public AnnotationKind Kind { get; init; }

    /// Shapes: [start, end]. Pen/highlighter: the stroke path. Text/counter: [anchor].
    public IReadOnlyList<PointF> Points { get; init; } = Array.Empty<PointF>();

    public Color Color { get; init; }
    public float LineWidth { get; init; }
    public float FontSize { get; init; }
    public string Text { get; init; } = "";
    public int Number { get; init; }

    public PointF Start => Points.Count > 0 ? Points[0] : PointF.Empty;
    public PointF End => Points.Count > 0 ? Points[^1] : Start;

    public RectangleF Rect => RectangleF.FromLTRB(Math.Min(Start.X, End.X), Math.Min(Start.Y, End.Y), Math.Max(Start.X, End.X), Math.Max(Start.Y, End.Y));

    public Annotation Translated(float dx, float dy) => this with { Points = Points.Select(p => new PointF(p.X + dx, p.Y + dy)).ToList() };
}
