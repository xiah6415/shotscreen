using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace SuYanShot.Annotations;

/// Whole-image pixelated and blurred copies of a capture; pixelate/blur marks reveal them through a clip (as on macOS).
internal sealed class AnnotationEffects : IDisposable
{
    public Bitmap Pixelated { get; }
    public Bitmap Blurred { get; }

    public AnnotationEffects(Bitmap source)
    {
        var longSide = Math.Max(source.Width, source.Height);
        // Mosaic: shrink so each block becomes one pixel, then enlarge with nearest-neighbour.
        Pixelated = Resample(source, Math.Max(10f, longSide / 90f), InterpolationMode.NearestNeighbor);
        // Blur: a strong downscale and a smooth upscale, run twice, is close to a Gaussian and fast on any size.
        using var once = Resample(source, Math.Max(4f, longSide / 160f), InterpolationMode.HighQualityBilinear);
        Blurred = Resample(once, 2f, InterpolationMode.HighQualityBilinear);
    }

    private static Bitmap Resample(Bitmap source, float factor, InterpolationMode upscale)
    {
        var small = new Size(Math.Max(1, (int)(source.Width / factor)), Math.Max(1, (int)(source.Height / factor)));
        using var reduced = new Bitmap(small.Width, small.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(reduced))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(source, new Rectangle(Point.Empty, small));
        }
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(result))
        using (var attributes = new ImageAttributes())
        {
            attributes.SetWrapMode(WrapMode.TileFlipXY); // no darkened edges
            g.InterpolationMode = upscale;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(reduced, new Rectangle(0, 0, source.Width, source.Height), 0, 0, small.Width, small.Height, GraphicsUnit.Pixel, attributes);
        }
        return result;
    }

    public Bitmap For(AnnotationKind kind) => kind == AnnotationKind.Blur ? Blurred : Pixelated;

    public void Dispose()
    {
        Pixelated.Dispose();
        Blurred.Dispose();
    }
}

/// Draws annotations into a Graphics whose units are image pixels. Used by both the canvas and export, as on macOS.
/// GDI+ has no shadows or multiply blending, so the soft shadow is a translucent offset stroke and the highlighter is a
/// translucent stroke.
internal static class AnnotationRenderer
{
    private const string FontFamily = "Microsoft JhengHei UI";

    /// The capture with all annotations burned in, at full resolution, optionally cropped (image pixels).
    public static Bitmap Flatten(Bitmap source, AnnotationEffects effects, IEnumerable<Annotation> annotations, Rectangle? crop)
    {
        var full = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(full))
        {
            g.DrawImage(source, new Rectangle(0, 0, source.Width, source.Height));
            Prepare(g);
            foreach (var annotation in annotations) Draw(g, annotation, effects);
        }
        if (crop is not Rectangle area) return full;
        area.Intersect(new Rectangle(Point.Empty, source.Size));
        if (area.Width < 1 || area.Height < 1) return full;
        var cropped = full.Clone(area, PixelFormat.Format32bppArgb);
        full.Dispose();
        return cropped;
    }

    public static void Prepare(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    }

    public static void Draw(Graphics g, Annotation a, AnnotationEffects effects)
    {
        switch (a.Kind)
        {
            case AnnotationKind.Pixelate or AnnotationKind.Blur:
                var state = g.Save();
                g.SetClip(a.Rect);
                g.DrawImage(effects.For(a.Kind), 0, 0, effects.Pixelated.Width, effects.Pixelated.Height);
                g.Restore(state);
                return;
            case AnnotationKind.Highlighter:
                using (var marker = new Pen(Color.FromArgb(110, a.Color), a.LineWidth * 3.5f) { StartCap = LineCap.Square, EndCap = LineCap.Square, LineJoin = LineJoin.Round })
                    Stroke(g, marker, a.Points);
                return;
            case AnnotationKind.Text:
                DrawText(g, a);
                return;
            case AnnotationKind.Counter:
                DrawCounter(g, a);
                return;
        }

        // Shadow pass, then the mark itself.
        var offset = a.LineWidth * 0.3f;
        using (var shadow = MakePen(Color.FromArgb(70, 0, 0, 0), a.LineWidth * 1.6f))
        {
            var state = g.Save();
            g.TranslateTransform(0, offset);
            DrawShape(g, a, shadow, Color.FromArgb(70, 0, 0, 0));
            g.Restore(state);
        }
        using var pen = MakePen(a.Color, a.LineWidth);
        DrawShape(g, a, pen, a.Color);
    }

    private static Pen MakePen(Color color, float width) =>
        new(color, width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

    private static void DrawShape(Graphics g, Annotation a, Pen pen, Color fill)
    {
        switch (a.Kind)
        {
            case AnnotationKind.Rectangle:
                var rect = a.Rect;
                var radius = Math.Min(a.LineWidth, Math.Min(rect.Width / 2, rect.Height / 2));
                if (rect.Width < 1 || rect.Height < 1) return;
                using (var path = RoundedRect(rect, radius)) g.DrawPath(pen, path);
                break;
            case AnnotationKind.Ellipse:
                if (a.Rect.Width < 1 || a.Rect.Height < 1) return;
                g.DrawEllipse(pen, a.Rect);
                break;
            case AnnotationKind.Line:
                Stroke(g, pen, new[] { a.Start, a.End });
                break;
            case AnnotationKind.Pen:
                Stroke(g, pen, a.Points);
                break;
            case AnnotationKind.Arrow:
                DrawArrow(g, a, pen, fill);
                break;
        }
    }

    private static void Stroke(Graphics g, Pen pen, IReadOnlyList<PointF> points)
    {
        if (points.Count == 0) return;
        if (points.Count == 1)
        {
            g.DrawLine(pen, points[0], new PointF(points[0].X + 0.01f, points[0].Y));
            return;
        }
        g.DrawLines(pen, points as PointF[] ?? points.ToArray());
    }

    private static void DrawArrow(Graphics g, Annotation a, Pen pen, Color fill)
    {
        var (start, end) = (a.Start, a.End);
        var length = Distance(start, end);
        if (length <= 0.5f) return;
        var angle = MathF.Atan2(end.Y - start.Y, end.X - start.X);
        var head = Math.Min(length * 0.6f, Math.Max(a.LineWidth * 4, 18));
        const float spread = MathF.PI / 7;
        var shaftEnd = new PointF(end.X - MathF.Cos(angle) * head * 0.75f, end.Y - MathF.Sin(angle) * head * 0.75f);
        Stroke(g, pen, new[] { start, shaftEnd });
        var tip = new[]
        {
            end,
            new PointF(end.X - head * MathF.Cos(angle - spread), end.Y - head * MathF.Sin(angle - spread)),
            new PointF(end.X - head * MathF.Cos(angle + spread), end.Y - head * MathF.Sin(angle + spread)),
        };
        using var brush = new SolidBrush(fill);
        g.FillPolygon(brush, tip);
    }

    public static Font TextFont(float size) => new(FontFamily, size, FontStyle.Bold, GraphicsUnit.Pixel);

    private static void DrawText(Graphics g, Annotation a)
    {
        using var font = TextFont(a.FontSize);
        using var shadow = new SolidBrush(Color.FromArgb(90, 0, 0, 0));
        using var ink = new SolidBrush(a.Color);
        var offset = a.FontSize * 0.04f + 1;
        g.DrawString(a.Text, font, shadow, a.Start.X + offset * 0.5f, a.Start.Y + offset, StringFormat.GenericTypographic);
        g.DrawString(a.Text, font, ink, a.Start, StringFormat.GenericTypographic);
    }

    private static float CounterDiameter(Annotation a) => a.FontSize * 1.5f;

    private static void DrawCounter(Graphics g, Annotation a)
    {
        var d = CounterDiameter(a);
        var circle = new RectangleF(a.Start.X - d / 2, a.Start.Y - d / 2, d, d);
        using (var shadow = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
            g.FillEllipse(shadow, circle with { Y = circle.Y + d * 0.05f });
        using (var fill = new SolidBrush(a.Color))
            g.FillEllipse(fill, circle);
        using (var ring = new Pen(Color.White, Math.Max(2, d * 0.07f)))
            g.DrawEllipse(ring, RectangleF.Inflate(circle, -d * 0.035f, -d * 0.035f));
        using var font = new Font("Segoe UI", a.FontSize * 0.8f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var ink = new SolidBrush(Contrasting(a.Color));
        using var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(a.Number.ToString(), font, ink, circle, centered);
    }

    /// Black on light fills (yellow, white), white otherwise.
    private static Color Contrasting(Color c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B > 0.7 * 255 ? Color.Black : Color.White;

    private static readonly Lazy<Graphics> Measurer = new(() => Graphics.FromImage(new Bitmap(1, 1)));

    public static SizeF MeasureText(string text, float fontSize)
    {
        using var font = TextFont(fontSize);
        var size = Measurer.Value.MeasureString(text.Length == 0 ? " " : text, font, PointF.Empty, StringFormat.GenericTypographic);
        return new SizeF(size.Width, Math.Max(size.Height, fontSize * 1.2f));
    }

    public static RectangleF Bounds(Annotation a)
    {
        switch (a.Kind)
        {
            case AnnotationKind.Text:
                return new RectangleF(a.Start, MeasureText(a.Text, a.FontSize));
            case AnnotationKind.Counter:
                var d = CounterDiameter(a);
                return new RectangleF(a.Start.X - d / 2, a.Start.Y - d / 2, d, d);
            case AnnotationKind.Pen or AnnotationKind.Highlighter:
                var box = RectangleF.FromLTRB(a.Points.Min(p => p.X), a.Points.Min(p => p.Y), a.Points.Max(p => p.X), a.Points.Max(p => p.Y));
                return RectangleF.Inflate(box, a.LineWidth * 2, a.LineWidth * 2);
            case AnnotationKind.Pixelate or AnnotationKind.Blur:
                return a.Rect;
            default:
                return RectangleF.Inflate(a.Rect, a.LineWidth, a.LineWidth);
        }
    }

    public static bool HitTest(Annotation a, PointF p, float tolerance)
    {
        var reach = a.LineWidth / 2 + tolerance;
        switch (a.Kind)
        {
            case AnnotationKind.Line or AnnotationKind.Arrow:
                return DistanceToSegment(p, a.Start, a.End) <= reach;
            case AnnotationKind.Pen or AnnotationKind.Highlighter:
                for (var i = 1; i < a.Points.Count; i++)
                    if (DistanceToSegment(p, a.Points[i - 1], a.Points[i]) <= reach * 2) return true;
                return a.Points.Any(q => Distance(p, q) <= reach * 2);
            default:
                return RectangleF.Inflate(Bounds(a), tolerance, tolerance).Contains(p);
        }
    }

    private static float Distance(PointF a, PointF b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    internal static float DistanceToSegment(PointF p, PointF a, PointF b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= 0) return Distance(p, a);
        var t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared, 0, 1);
        return Distance(p, new PointF(a.X + t * dx, a.Y + t * dy));
    }

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0.5f)
        {
            path.AddRectangle(r);
            return path;
        }
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
