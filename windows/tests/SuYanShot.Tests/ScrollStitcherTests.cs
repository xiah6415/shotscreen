using System.Drawing.Imaging;
using SuYanShot.Capture;
using Xunit;
using Xunit.Abstractions;

namespace SuYanShot.Tests;

/// The macOS synthetic regression test (scripts/stitcher-test/main.swift), ported.
///
/// Builds a 1000×7000 "page" (text-like rows, gradient blocks, a 700 px blank gap), cuts 800 px frames at random scroll
/// offsets (pauses, 1–12 px nudges, 30–520 px jumps), adds sticky bars and a moving scrollbar, then checks the stitched
/// result against the ground truth.
///   normal mode: 70 px sticky header + 50 px sticky footer must appear once; content rows must match exactly.
///   edge mode:   no sticky bars, but every frame's bottom 2 rows are washed out towards white, like the blended edge of a
///                real capture; only the last frame's 2 rows may end up in the result.
public sealed class ScrollStitcherTests(ITestOutputHelper output)
{
    private const int W = 1000, ContentH = 7000, H = 800;

    private sealed class Seeded(ulong state)
    {
        public int Next(int min, int max) // inclusive
        {
            state = unchecked(state * 6364136223846793005UL + 1442695040888963407UL);
            return min + (int)((state >> 33) % (ulong)(max - min + 1));
        }
    }

    public static IEnumerable<object[]> Cases() =>
        new ulong[] { 42, 7, 1234, 99, 2026, 5, 77, 31337 }.SelectMany(seed => new[] { new object[] { seed, false }, new object[] { seed, true } });

    [Theory]
    [MemberData(nameof(Cases))]
    public void StitchesSyntheticPagesExactly(ulong seed, bool edgeMode)
    {
        var random = new Seeded(seed);
        int header = edgeMode ? 0 : 70, footer = edgeMode ? 0 : 50;
        using var content = Page(random);

        Bitmap Frame(int offset, double scrollbar)
        {
            var f = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(f);
            g.DrawImage(content, new Rectangle(0, 0, W, H), new Rectangle(0, offset, W, H), GraphicsUnit.Pixel);
            if (header > 0)
            {
                using (var blue = new SolidBrush(Color.FromArgb(26, 77, 204))) g.FillRectangle(blue, 0, 0, W, header);
                g.FillRectangle(Brushes.White, 30, 25, 200, 20);
            }
            if (footer > 0)
            {
                using (var light = new SolidBrush(Color.FromArgb(235, 235, 235))) g.FillRectangle(light, 0, H - footer, W, footer);
                using (var dark = new SolidBrush(Color.FromArgb(77, 77, 77))) g.FillRectangle(dark, 400, H - 35, 200, 20);
            }
            var level = (int)(scrollbar * 255);
            using (var bar = new SolidBrush(Color.FromArgb(level, level, level))) g.FillRectangle(bar, W - 14, H - (int)(scrollbar * 500) - 200, 10, 200);
            if (edgeMode)
                using (var wash = new SolidBrush(Color.FromArgb(128, 255, 255, 255))) g.FillRectangle(wash, 0, H - 2, W, 2);
            return f;
        }

        var offsets = new List<int> { 0 };
        for (var offset = 0; offset < ContentH - H - 10;)
        {
            var roll = random.Next(0, 9);
            var step = roll == 0 ? 0 : roll < 3 ? random.Next(1, 12) : random.Next(30, 520);
            offset = Math.Min(ContentH - H, offset + step);
            offsets.Add(offset);
        }

        using var stitcher = new ScrollStitcher();
        var lastAccepted = 0;
        var wrongWithContent = 0;
        var started = DateTime.Now;
        for (var index = 0; index < offsets.Count; index++)
        {
            var frame = Frame(offsets[index], index % 7 / 7.0);
            var result = stitcher.Add(frame);
            if (result is FrameResult.Appended appended)
            {
                // A wrong offset is only acceptable when the whole overlap is blank (nothing to match on).
                int overlapTop = offsets[index] + header, overlapBottom = lastAccepted + H - footer;
                var blank = overlapTop >= 3000 - 40 && overlapBottom <= 3700 + 40;
                if (appended.Rows != offsets[index] - lastAccepted && !blank) wrongWithContent++;
                if (appended.Rows != offsets[index] - lastAccepted) output.WriteLine($"  frame {index} at {offsets[index]}: got {appended.Rows}, true {offsets[index] - lastAccepted}, blank {blank}");
                lastAccepted = offsets[index];
            }
            else if (result is not FrameResult.First)
            {
                frame.Dispose();
            }
        }
        var msPerFrame = (DateTime.Now - started).TotalMilliseconds / offsets.Count;
        using var stitched = stitcher.MakeImage()!;

        // Ground truth: header + content + footer, as the last accepted frame would show them.
        using var truth = new Bitmap(W, lastAccepted + H, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(truth))
        {
            g.DrawImage(content, new Rectangle(0, 0, W, lastAccepted + H), new Rectangle(0, 0, W, lastAccepted + H), GraphicsUnit.Pixel);
            using var firstFrame = Frame(0, 0);
            if (header > 0) g.DrawImage(firstFrame, new Rectangle(0, 0, W, header), new Rectangle(0, 0, W, header), GraphicsUnit.Pixel);
            if (footer > 0) g.DrawImage(firstFrame, new Rectangle(0, lastAccepted + H - footer, W, footer), new Rectangle(0, H - footer, W, footer), GraphicsUnit.Pixel);
        }
        var a = ContentRows(stitched);
        var b = ContentRows(truth);
        var differing = a.Count == b.Count ? a.Zip(b).Count(pair => pair.First.Zip(pair.Second).Sum(p => Math.Abs(p.First - p.Second)) > pair.First.Length * 2) : -1;
        using var preview = stitcher.MakePreview();
        output.WriteLine($"seed {seed}{(edgeMode ? " edge" : "")}: frames {offsets.Count}, content rows {a.Count}/{b.Count}, differing {differing}, {msPerFrame:F1} ms/frame");

        Assert.Equal(0, wrongWithContent);
        Assert.Equal(b.Count, a.Count);
        Assert.InRange(differing, 0, edgeMode ? 2 : 0); // in edge mode the last frame's 2 washed-out rows legitimately differ
        Assert.Equal(stitcher.PreviewWidth, preview!.Width);
    }

    [Fact]
    public void IdenticalFramesAreUnchangedAndOtherSizesRejected()
    {
        using var stitcher = new ScrollStitcher();
        using var page = Page(new Seeded(1));
        Bitmap Cut(int y, int height = 400) => page.Clone(new Rectangle(0, y, W, height), PixelFormat.Format32bppArgb);
        Assert.IsType<FrameResult.First>(stitcher.Add(Cut(0)));
        using (var same = Cut(0)) Assert.IsType<FrameResult.Unchanged>(stitcher.Add(same));
        using (var other = Cut(10, 300)) Assert.IsType<FrameResult.Rejected>(stitcher.Add(other));
        Assert.Equal(new FrameResult.Appended(120), stitcher.Add(Cut(120)));
        Assert.Equal(520, stitcher.Height);
    }

    private static Bitmap Page(Seeded random)
    {
        var page = new Bitmap(W, ContentH, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(page);
        g.Clear(Color.White);
        var y = 20;
        while (y < ContentH - 40)
        {
            if (y > 3000 && y < 3700) { y = 3700; continue; } // long blank gap
            if (random.Next(0, 9) == 0)
            {
                var blockHeight = random.Next(120, 300);
                for (var i = 0; i < blockHeight; i++)
                {
                    using var pen = new SolidBrush(Color.FromArgb(255 * i / blockHeight, 102, random.Next(0, 255)));
                    g.FillRectangle(pen, 60, y + i, 700, 1);
                }
                y += blockHeight + 20;
                continue;
            }
            for (var x = 40; x < 900;)
            {
                var w = random.Next(10, 70);
                var level = random.Next(0, 127);
                using var ink = new SolidBrush(Color.FromArgb(level, level, level));
                g.FillRectangle(ink, x, y, w, 14);
                x += w + random.Next(6, 14);
            }
            y += random.Next(22, 34);
        }
        return page;
    }

    /// Rows that aren't pure white (ignoring the scrollbar strip), top first, as grayscale.
    private static List<byte[]> ContentRows(Bitmap image)
    {
        var rows = new List<byte[]>();
        var data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var buffer = new byte[data.Stride * image.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
            for (var r = 0; r < image.Height; r++)
            {
                var row = new byte[W - 40];
                var any = false;
                for (var x = 0; x < W - 40; x++)
                {
                    var i = r * data.Stride + x * 4;
                    row[x] = (byte)((buffer[i + 2] * 299 + buffer[i + 1] * 587 + buffer[i] * 114) / 1000);
                    if (row[x] < 250) any = true;
                }
                if (any) rows.Add(row);
            }
        }
        finally
        {
            image.UnlockBits(data);
        }
        return rows;
    }
}
