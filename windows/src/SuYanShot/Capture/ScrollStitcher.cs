using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace SuYanShot.Capture;

internal abstract record FrameResult
{
    public sealed record First : FrameResult;
    public sealed record Appended(int Rows) : FrameResult;
    public sealed record Unchanged : FrameResult;
    public sealed record Rejected : FrameResult;
    public sealed record LimitReached : FrameResult;
}

/// Stitches frames of a fixed screen region, captured while the user scrolls down, into one tall image.
/// A port of the macOS Capture/ScrollStitcher.swift with the same thresholds; keep the two in step.
///
/// Each frame is compared with the last accepted frame to find how many pixel rows the content moved.
/// Rows that do not move (sticky headers/footers, toolbars) are detected and left out of the match, so
/// they appear once instead of repeating. Frames that don't line up are skipped, not guessed.
/// Used serially by one capture session (frames are added off the UI thread).
internal sealed class ScrollStitcher : IDisposable
{
    private sealed class Frame
    {
        public required Bitmap Image { get; init; }
        /// Grayscale, <see cref="Columns"/> samples per row, top row first.
        public required byte[] Rows { get; init; }
        /// Same, averaged over blocks of <see cref="CoarseFactor"/> rows, for a fast first pass.
        public required byte[] Coarse { get; init; }
        /// Small copy for the live preview, made once a frame is accepted.
        public Bitmap? Thumbnail { get; set; }
    }

    private const int Columns = 96;
    private const int CoarseFactor = 4;

    // Mean absolute difference per sample (0–255).
    private const double IdenticalThreshold = 0.6;
    private const double StaticRowThreshold = 1.5;
    private const double MatchThreshold = 3.0;
    private const int CandidateCount = 6;
    /// Bottom rows of every frame kept out of the seams (only the last frame contributes them, once).
    private const int EdgeMargin = 4;

    private Frame? first;
    private Frame? last;
    /// Frames appended after the first, with how many new rows each contributed.
    private readonly List<(Frame Frame, int Rows)> strips = new();
    /// Height of a static bottom bar, fixed once detected so the composition stays consistent.
    private int? footer;

    /// Stitched height so far, in pixels.
    public int Height { get; private set; }
    public int MaxHeight { get; }
    /// Width of the live preview produced by <see cref="MakePreview"/>.
    public int PreviewWidth { get; }

    public ScrollStitcher(int maxHeight = 40_000, int previewWidth = 200)
    {
        MaxHeight = maxHeight;
        PreviewWidth = previewWidth;
    }

    /// Takes ownership of <paramref name="image"/> when it is kept (First / Appended); otherwise the caller disposes it.
    public FrameResult Add(Bitmap image)
    {
        var rows = Signature(image, Columns);
        var current = new Frame { Image = image, Rows = rows, Coarse = Downsample(rows, image.Height) };
        if (last == null)
        {
            current.Thumbnail = Thumbnail(image);
            first = last = current;
            Height = image.Height;
            return new FrameResult.First();
        }
        if (image.Width != last.Image.Width || image.Height != last.Image.Height) return new FrameResult.Rejected();
        var h = image.Height;

        if (Score(last.Rows, current.Rows, 0, 0, h, 2) < IdenticalThreshold) return new FrameResult.Unchanged();

        // Static bars at the top and bottom don't scroll; keep them out of the match.
        var limit = (int)(h * 0.35);
        var header = Math.Min(limit, StaticRun(last.Rows, current.Rows, h, fromTop: true));
        var bottom = Math.Min(limit, StaticRun(last.Rows, current.Rows, h, fromTop: false));
        var minOverlap = Math.Max(24, h / 8);
        var maxOffset = h - header - bottom - minOverlap;
        if (maxOffset < 1) return new FrameResult.Rejected();

        // Coarse pass on 1/4-height signatures. A true offset that isn't a multiple of 4 blurs in the coarse
        // signatures and can lose to a merely similar region, so refine several distinct candidates at full resolution.
        var coarseHeight = h / CoarseFactor;
        var coarseScores = new List<(int Offset, double Score)>();
        for (var offset = 1; offset <= Math.Max(1, maxOffset / CoarseFactor); offset++)
        {
            var lower = header / CoarseFactor + 1;
            var upper = Math.Max(lower, (h - bottom) / CoarseFactor - offset - 1);
            if (upper - lower < minOverlap / CoarseFactor / 2) continue;
            coarseScores.Add((offset, Score(last.Coarse, current.Coarse, offset, lower, upper, 1, coarseHeight)));
        }
        var candidates = new List<int>();
        foreach (var entry in coarseScores.OrderBy(e => e.Score))
        {
            if (candidates.Count >= CandidateCount) break;
            if (candidates.All(c => Math.Abs(c - entry.Offset) > 2)) candidates.Add(entry.Offset);
        }
        var best = 0;
        var bestScore = double.PositiveInfinity;
        var refined = new HashSet<int>();
        foreach (var center in candidates.Select(c => c * CoarseFactor))
        {
            for (var offset = Math.Max(1, center - CoarseFactor - 2); offset <= Math.Max(1, Math.Min(maxOffset, center + CoarseFactor + 2)); offset++)
            {
                if (!refined.Add(offset)) continue;
                var value = Score(last.Rows, current.Rows, offset, header, h - bottom - offset, 1);
                if (value < bestScore) { bestScore = value; best = offset; }
            }
        }
        var stillScore = Score(last.Rows, current.Rows, 0, header, h - bottom, 2);
        if (best <= 0 || bestScore >= MatchThreshold || bestScore >= stillScore * 0.6) return new FrameResult.Rejected();
        best = Disambiguate(last, current, best, header, bottom, minOverlap);
        if (Height + best > MaxHeight) return new FrameResult.LimitReached();

        if (footer == null && bottom > 0) footer = bottom;
        current.Thumbnail = Thumbnail(image);
        strips.Add((current, best));
        Height += best;
        last = current;
        return new FrameResult.Appended(best);
    }

    /// Windows-only addition (not in the macOS stitcher yet). When the compared overlap is entirely blank, every offset
    /// that keeps it blank scores 0 and the pick is a guess. Blank rows just above the footer then often count as "static"
    /// (they are blank in both frames), which hides content the guess would drop. So in that case only, search again with
    /// those blank rows compared too: any remaining tie then only skips or repeats blank rows, never content.
    private int Disambiguate(Frame last, Frame current, int best, int header, int bottom, int minOverlap)
    {
        var h = current.Image.Height;
        if (!AllUniform(current.Rows, header, h - bottom - best)) return best;
        var matchBottom = bottom;
        while (matchBottom > 0 && IsUniform(current.Rows, h - matchBottom) && IsUniform(last.Rows, h - matchBottom)) matchBottom--;
        if (matchBottom == bottom) return best;
        var maxOffset = h - header - matchBottom - minOverlap;
        var choice = best;
        var choiceScore = double.PositiveInfinity;
        for (var offset = 1; offset <= maxOffset; offset++)
        {
            var value = Score(last.Rows, current.Rows, offset, header, h - matchBottom - offset, 1);
            if (value < choiceScore) { choiceScore = value; choice = offset; }
        }
        return choiceScore < MatchThreshold ? choice : best;
    }

    /// A row with no detail (all samples within 2 levels), such as page background.
    private static bool IsUniform(byte[] rows, int y)
    {
        byte min = 255, max = 0;
        for (var c = 0; c < Columns; c++)
        {
            var v = rows[y * Columns + c];
            if (v < min) min = v;
            if (v > max) max = v;
        }
        return max - min <= 2;
    }

    private static bool AllUniform(byte[] rows, int lower, int upper)
    {
        for (var y = Math.Max(0, lower); y < upper; y++)
            if (!IsUniform(rows, y)) return false;
        return true;
    }

    /// The full-resolution stitched image.
    public Bitmap? MakeImage() => Compose(1, thumbnails: false);

    /// A <see cref="PreviewWidth"/>-wide version built from thumbnails, cheap enough to refresh after every frame.
    public Bitmap? MakePreview() => first == null ? null : Compose(PreviewWidth / (double)first.Image.Width, thumbnails: true);

    /// First frame down to the footer, each new strip, then the footer once from the last frame.
    private Bitmap? Compose(double scale, bool thumbnails)
    {
        if (first == null || last == null) return null;
        var h = first.Image.Height;
        // Captured frames often have a blended, lighter bottom edge; never let it sit at a seam.
        var foot = Math.Max(footer ?? 0, EdgeMargin);
        var pieces = new List<(Frame Frame, int From, int To)> { (first, 0, h - foot) };
        foreach (var (frame, rows) in strips) pieces.Add((frame, Math.Max(0, h - foot - rows), h - foot));
        pieces.Add((last, h - foot, h));

        var outWidth = Math.Max(1, (int)Math.Round(first.Image.Width * scale));
        var outHeight = Math.Max(1, (int)Math.Round(Height * scale));
        var output = new Bitmap(outWidth, outHeight, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(output);
        g.InterpolationMode = thumbnails ? InterpolationMode.HighQualityBilinear : InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.CompositingMode = CompositingMode.SourceCopy;
        double y = 0; // cursor in the output
        foreach (var (frame, from, to) in pieces)
        {
            if (to <= from) continue;
            var image = thumbnails ? frame.Thumbnail : frame.Image;
            if (image == null) continue;
            var pieceHeight = (to - from) * scale;
            // Source rows in the drawn image's own pixels (thumbnails are smaller than frames).
            var sourceScale = image.Height / (double)h;
            var source = new RectangleF(0, (float)(from * sourceScale), image.Width, (float)((to - from) * sourceScale));
            var top = (int)Math.Round(y);
            var bottom = (int)Math.Round(y + pieceHeight);
            g.DrawImage(image, new Rectangle(0, top, outWidth, bottom - top), source, GraphicsUnit.Pixel);
            y += pieceHeight;
        }
        return output;
    }

    private Bitmap Thumbnail(Bitmap image)
    {
        var scale = PreviewWidth / (double)image.Width;
        var thumb = new Bitmap(PreviewWidth, Math.Max(1, (int)Math.Round(image.Height * scale)), PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(thumb);
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(image, new Rectangle(0, 0, thumb.Width, thumb.Height));
        return thumb;
    }

    // MARK: Signatures

    /// Grayscale samples <paramref name="columns"/> wide and full height, each the mean of its column span.
    /// The rightmost 4% is dropped because overlay scrollbars change between frames.
    internal static unsafe byte[] Signature(Bitmap image, int columns)
    {
        var usable = Math.Max(1, (int)(image.Width * 0.96));
        var result = new byte[columns * image.Height];
        var data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < image.Height; y++)
            {
                var row = (byte*)data.Scan0 + (long)y * data.Stride;
                for (var c = 0; c < columns; c++)
                {
                    int start = c * usable / columns, end = Math.Max(start + 1, (c + 1) * usable / columns);
                    long sum = 0;
                    for (var x = start; x < end; x++)
                    {
                        var p = row + x * 4; // B, G, R, A
                        sum += p[2] * 299 + p[1] * 587 + p[0] * 114;
                    }
                    result[y * columns + c] = (byte)(sum / ((end - start) * 1000L));
                }
            }
        }
        finally
        {
            image.UnlockBits(data);
        }
        return result;
    }

    private static byte[] Downsample(byte[] rows, int height)
    {
        var blocks = height / CoarseFactor;
        var result = new byte[blocks * Columns];
        for (var block = 0; block < blocks; block++)
            for (var column = 0; column < Columns; column++)
            {
                var sum = 0;
                for (var r = 0; r < CoarseFactor; r++) sum += rows[(block * CoarseFactor + r) * Columns + column];
                result[block * Columns + column] = (byte)(sum / CoarseFactor);
            }
        return result;
    }

    /// Mean absolute difference between new rows [lower, upper) and old rows shifted down by <paramref name="offset"/>.
    private static double Score(byte[] old, byte[] now, int offset, int lower, int upper, int step, int? height = null)
    {
        var h = height ?? old.Length / Columns;
        lower = Math.Max(0, lower);
        upper = Math.Min(upper, h - offset);
        if (upper <= lower) return double.PositiveInfinity;
        long total = 0, count = 0;
        for (var y = lower; y < upper; y += step)
        {
            int ra = (y + offset) * Columns, rb = y * Columns;
            for (var column = 0; column < Columns; column++) total += Math.Abs(old[ra + column] - now[rb + column]);
            count += Columns;
        }
        return count == 0 ? double.PositiveInfinity : total / (double)count;
    }

    /// Number of consecutive identical rows from the top (or bottom) edge.
    private static int StaticRun(byte[] old, byte[] now, int height, bool fromTop)
    {
        var run = 0;
        while (run < height)
        {
            var y = fromTop ? run : height - 1 - run;
            if (Score(old, now, 0, y, y + 1, 1) > StaticRowThreshold) break;
            run++;
        }
        return run;
    }

    public void Dispose()
    {
        var frames = new List<Frame>();
        if (first != null) frames.Add(first);
        frames.AddRange(strips.Select(s => s.Frame));
        foreach (var frame in frames.Distinct())
        {
            frame.Image.Dispose();
            frame.Thumbnail?.Dispose();
        }
        strips.Clear();
        first = last = null;
    }
}
