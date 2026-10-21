using System.Drawing.Imaging;
using SuYanShot.Annotations;
using Xunit;

namespace SuYanShot.Tests;

public sealed class AnnotationStateTests
{
    [Fact]
    public void UndoRedoCoversMarksAndCrop()
    {
        var state = new AnnotationEditorState(1);
        state.Tool = AnnotationTool.Rectangle;
        state.Add(state.NewAnnotation(new PointF(10, 10))! with { Points = new[] { new PointF(10, 10), new PointF(50, 40) } });
        state.ApplyCrop(new Rectangle(0, 0, 60, 60), new Size(200, 100));
        Assert.Single(state.Annotations);
        Assert.Equal(new Rectangle(0, 0, 60, 60), state.Crop);

        state.Undo();
        Assert.Null(state.Crop);
        Assert.Single(state.Annotations);
        state.Undo();
        Assert.Empty(state.Annotations);
        Assert.False(state.CanUndo);
        state.Redo();
        state.Redo();
        Assert.Equal(new Rectangle(0, 0, 60, 60), state.Crop);
        Assert.False(state.CanRedo);
    }

    [Fact]
    public void CropCoveringEverythingOrTinyMeansNoCrop()
    {
        var state = new AnnotationEditorState(1);
        state.ApplyCrop(new Rectangle(-10, -10, 500, 500), new Size(200, 100));
        Assert.Null(state.Crop);
        state.ApplyCrop(new Rectangle(5, 5, 1, 40), new Size(200, 100));
        Assert.Null(state.Crop);
        Assert.False(state.CanUndo); // nothing changed, so nothing to undo
    }

    [Fact]
    public void CountersNumberUpAndTextCanBeEditedOrCleared()
    {
        var state = new AnnotationEditorState(2);
        state.Tool = AnnotationTool.Counter;
        state.Add(state.NewAnnotation(new PointF(5, 5))!);
        state.Add(state.NewAnnotation(new PointF(50, 5))!);
        Assert.Equal(new[] { 1, 2 }, state.Annotations.Select(a => a.Number));
        Assert.Equal(44, state.FontSize); // 22 × pixel scale 2 at the middle size

        var text = state.NewText("hello", new PointF(20, 20));
        state.Add(text);
        state.UpdateText(text.Id, "嗨");
        Assert.Equal("嗨", state.Annotations.Last().Text);
        state.UpdateText(text.Id, "");
        Assert.DoesNotContain(state.Annotations, a => a.Kind == AnnotationKind.Text);
    }

    [Fact]
    public void ColourAndSizeRestyleTheSelectedMark()
    {
        var state = new AnnotationEditorState(1);
        state.Tool = AnnotationTool.Line;
        var line = state.NewAnnotation(new PointF(0, 0))! with { Points = new[] { new PointF(0, 0), new PointF(100, 0) } };
        state.Add(line);
        state.Tool = AnnotationTool.Select;
        state.SelectedId = state.Hit(new PointF(50, 2), 4)!.Id;
        state.SetColor(Color.Black);
        state.SetSizeLevel(2);
        Assert.Equal(Color.Black, state.Annotations[0].Color);
        Assert.Equal(9, state.Annotations[0].LineWidth);
        state.Checkpoint();
        state.Move(line.Id, 10, 5);
        Assert.Equal(new PointF(10, 5), state.Annotations[0].Start);
        state.RemoveSelected();
        Assert.Empty(state.Annotations);
    }
}

public sealed class AnnotationRendererTests
{
    private static Bitmap Checkerboard(int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.White);
        for (var y = 0; y < height; y += 20)
            for (var x = (y / 20 % 2) * 20; x < width; x += 40)
                g.FillRectangle(Brushes.SteelBlue, x, y, 20, 20);
        return bitmap;
    }

    private static Annotation Mark(AnnotationKind kind, params PointF[] points) => new()
    {
        Kind = kind, Points = points, Color = AnnotationEditorState.Palette[0], LineWidth = 5, FontSize = 28,
        Text = kind == AnnotationKind.Text ? "標註 Text" : "", Number = kind == AnnotationKind.Counter ? 3 : 0,
    };

    [Fact]
    public void EveryKindChangesTheFlattenedImageAndCropApplies()
    {
        using var source = Checkerboard(640, 400);
        using var effects = new AnnotationEffects(source);
        var marks = new[]
        {
            Mark(AnnotationKind.Arrow, new(20, 20), new(150, 90)),
            Mark(AnnotationKind.Rectangle, new(180, 20), new(300, 100)),
            Mark(AnnotationKind.Ellipse, new(320, 20), new(440, 100)),
            Mark(AnnotationKind.Line, new(460, 20), new(620, 100)),
            Mark(AnnotationKind.Pen, new(20, 150), new(60, 190), new(100, 150), new(140, 190)),
            Mark(AnnotationKind.Highlighter, new(180, 170), new(300, 170)),
            Mark(AnnotationKind.Text, new PointF(320, 150)),
            Mark(AnnotationKind.Counter, new PointF(520, 170)),
            Mark(AnnotationKind.Pixelate, new(20, 240), new(300, 380)),
            Mark(AnnotationKind.Blur, new(340, 240), new(620, 380)),
        };
        using var plain = AnnotationRenderer.Flatten(source, effects, Array.Empty<Annotation>(), null);
        using var flat = AnnotationRenderer.Flatten(source, effects, marks, null);
        Assert.Equal(source.Size, flat.Size);

        foreach (var mark in marks)
        {
            var bounds = Rectangle.Round(AnnotationRenderer.Bounds(mark));
            bounds.Intersect(new Rectangle(Point.Empty, source.Size));
            Assert.True(ChangedPixels(plain, flat, bounds) > 20, $"{mark.Kind} left the image unchanged");
        }

        // Saved next to the test output so the look can be checked by eye.
        flat.Save(Path.Combine(Path.GetTempPath(), "suyanshot-annotations.png"), ImageFormat.Png);

        using var cropped = AnnotationRenderer.Flatten(source, effects, marks, new Rectangle(100, 50, 200, 120));
        Assert.Equal(new Size(200, 120), cropped.Size);
        Assert.Equal(flat.GetPixel(150, 80).ToArgb(), cropped.GetPixel(50, 30).ToArgb());
    }

    [Fact]
    public void HitTestingFollowsTheShape()
    {
        var line = Mark(AnnotationKind.Line, new(0, 0), new(100, 100));
        Assert.True(AnnotationRenderer.HitTest(line, new PointF(50, 52), 3));
        Assert.False(AnnotationRenderer.HitTest(line, new PointF(80, 20), 3));
        var pen = Mark(AnnotationKind.Pen, new(0, 0), new(50, 0), new(50, 50));
        Assert.True(AnnotationRenderer.HitTest(pen, new PointF(50, 25), 2));
        Assert.False(AnnotationRenderer.HitTest(pen, new PointF(20, 30), 2));
    }

    private static int ChangedPixels(Bitmap a, Bitmap b, Rectangle area)
    {
        var count = 0;
        for (var y = area.Top; y < area.Bottom; y += 2)
            for (var x = area.Left; x < area.Right; x += 2)
                if (a.GetPixel(x, y).ToArgb() != b.GetPixel(x, y).ToArgb()) count++;
        return count;
    }
}
