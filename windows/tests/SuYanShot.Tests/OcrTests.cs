using SuYanShot.Ocr;
using Xunit;

namespace SuYanShot.Tests;

/// The selection rules shared with macOS (scripts/ocr-selection-test) and the word joining Windows' OCR engine needs.
public sealed class OcrTests
{
    // Three lines top to bottom, in image pixels with a top-left origin.
    private static OcrTextSelection Sample() => new(new[]
    {
        new RecognizedLine("Glean screenshot tool", new RectangleF(40, 30, 500, 50)),
        new RecognizedLine("拾光截圖工具", new RectangleF(40, 140, 300, 50)),
        new RecognizedLine("Copy 42 lines", new RectangleF(40, 250, 320, 50)),
    });

    private static PointF Mid(OcrTextSelection s, int i) => new(s.Lines[i].Box.X + s.Lines[i].Box.Width / 2, s.Lines[i].Box.Y + s.Lines[i].Box.Height / 2);

    [Fact]
    public void NothingSelectedCopiesEverythingInReadingOrder()
    {
        Assert.Equal("Glean screenshot tool\n拾光截圖工具\nCopy 42 lines", Sample().TextToCopy);
    }

    [Fact]
    public void ClickShiftClickAndClickOnEmptySpace()
    {
        var s = Sample();
        s.Click(Mid(s, 1), toggle: false);
        Assert.Equal(new[] { 1 }, s.Selected.OrderBy(i => i));
        Assert.Equal("拾光截圖工具", s.TextToCopy);
        s.Click(Mid(s, 2), toggle: true);
        Assert.Equal(new[] { 1, 2 }, s.Selected.OrderBy(i => i));
        s.Click(Mid(s, 1), toggle: true);
        Assert.Equal(new[] { 2 }, s.Selected.OrderBy(i => i));
        s.Click(new PointF(880, 340), toggle: false);
        Assert.Empty(s.Selected);
    }

    [Fact]
    public void DragSelectsTouchedLinesAndShiftDragAdds()
    {
        var s = Sample();
        s.BeginDrag(adding: false);
        s.Drag(RectangleF.FromLTRB(60, Mid(s, 0).Y, 110, Mid(s, 1).Y));
        Assert.Equal(new[] { 0, 1 }, s.Selected.OrderBy(i => i));
        Assert.Equal("Glean screenshot tool\n拾光截圖工具", s.TextToCopy);
        s.BeginDrag(adding: true);
        s.Drag(new RectangleF(60, Mid(s, 2).Y, 10, 1));
        Assert.Equal(new[] { 0, 1, 2 }, s.Selected.OrderBy(i => i));
        s.BeginDrag(adding: false);
        s.Drag(new RectangleF(60, Mid(s, 2).Y, 10, 1));
        Assert.Equal(new[] { 2 }, s.Selected.OrderBy(i => i));
        s.Clear();
        s.SelectAll();
        Assert.Equal(3, s.Selected.Count);
    }

    [Fact]
    public void ToleranceCatchesClicksJustOutsideAShortLine()
    {
        var s = Sample();
        Assert.Null(s.LineAt(new PointF(38, 55)));
        Assert.Equal(0, s.LineAt(new PointF(38, 55), tolerance: 4));
    }

    [Theory]
    [InlineData(new[] { "拾", "光", "截", "圖" }, "拾光截圖")]
    [InlineData(new[] { "Copy", "42", "lines" }, "Copy 42 lines")]
    [InlineData(new[] { "用", "Glean", "截", "圖", "，", "OK" }, "用Glean截圖，OK")]
    [InlineData(new[] { " ", "a", "" }, "a")]
    public void ChineseCharactersAreJoinedWithoutSpaces(string[] words, string expected)
    {
        Assert.Equal(expected, TextRecognitionService.JoinWords(words));
    }
}
