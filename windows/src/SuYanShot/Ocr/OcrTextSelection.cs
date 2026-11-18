namespace SuYanShot.Ocr;

/// Which recognized lines are selected in the annotation editor's 辨識文字 tool; the same rules as macOS
/// (OCR/TextRecognitionService.swift): a click picks the line under it (Shift toggles), a drag selects every line the drag
/// rectangle touches (Shift adds), copying joins the selected lines in reading order, and nothing selected copies all.
internal sealed class OcrTextSelection
{
    private readonly HashSet<int> selected = new();
    private HashSet<int> dragBase = new();

    public IReadOnlyList<RecognizedLine> Lines { get; }
    public IReadOnlyCollection<int> Selected => selected;

    public OcrTextSelection(IReadOnlyList<RecognizedLine> lines) => Lines = lines;

    public static string Join(IEnumerable<string> texts) => string.Join("\n", texts).Trim();

    /// The line under a point, with some slack around short lines.
    public int? LineAt(PointF point, float tolerance = 0)
    {
        for (var i = 0; i < Lines.Count; i++)
            if (RectangleF.Inflate(Lines[i].Box, tolerance, tolerance).Contains(point)) return i;
        return null;
    }

    public void BeginDrag(bool adding) => dragBase = adding ? new HashSet<int>(selected) : new HashSet<int>();

    public void Drag(RectangleF rect)
    {
        selected.Clear();
        selected.UnionWith(dragBase);
        for (var i = 0; i < Lines.Count; i++)
            if (Lines[i].Box.IntersectsWith(rect)) selected.Add(i);
    }

    public void Click(PointF point, bool toggle, float tolerance = 0)
    {
        if (LineAt(point, tolerance) is not int index)
        {
            if (!toggle) selected.Clear();
            return;
        }
        if (!toggle)
        {
            selected.Clear();
            selected.Add(index);
        }
        else if (!selected.Remove(index))
        {
            selected.Add(index);
        }
    }

    public void SelectAll()
    {
        for (var i = 0; i < Lines.Count; i++) selected.Add(i);
    }

    public void Clear() => selected.Clear();

    public string TextToCopy =>
        Join((selected.Count == 0 ? Enumerable.Range(0, Lines.Count) : selected.OrderBy(i => i)).Select(i => Lines[i].Text));
}
