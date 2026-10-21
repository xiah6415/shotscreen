namespace SuYanShot.UI;

/// A minimal QR Code encoder (byte mode, error correction level M, versions 1–40), so phone sharing needs no
/// third-party package. Follows ISO/IEC 18004 as laid out in Project Nayuki's reference implementation.
internal sealed class QrCode
{
    public int Size { get; }
    private readonly bool[,] modules;   // [y, x], true = dark
    private readonly bool[,] isFunction;

    public bool this[int x, int y] => modules[y, x];

    // Level M tables, indexed by version (index 0 unused).
    private static readonly int[] EccCodewordsPerBlock =
        { -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28 };

    private static readonly int[] NumErrorCorrectionBlocks =
        { -1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49 };

    private const int FormatBitsLevelM = 0;

    public static QrCode Encode(string text) => Encode(System.Text.Encoding.UTF8.GetBytes(text));

    public static QrCode Encode(byte[] data)
    {
        for (var version = 1; version <= 40; version++)
        {
            var capacityBits = NumDataCodewords(version) * 8;
            var usedBits = 4 + (version <= 9 ? 8 : 16) + data.Length * 8;
            if (usedBits <= capacityBits) return new QrCode(version, BuildCodewords(version, data));
        }
        throw new ArgumentException("Data too long for a QR Code.", nameof(data));
    }

    private QrCode(int version, byte[] dataCodewords)
    {
        Size = version * 4 + 17;
        modules = new bool[Size, Size];
        isFunction = new bool[Size, Size];
        DrawFunctionPatterns(version);
        DrawCodewords(AddEccAndInterleave(version, dataCodewords));

        // Pick the mask with the lowest penalty, as decoders read the most reliably then.
        var best = 0;
        var bestPenalty = int.MaxValue;
        for (var mask = 0; mask < 8; mask++)
        {
            ApplyMask(mask);
            DrawFormatBits(mask);
            var penalty = Penalty();
            if (penalty < bestPenalty) { best = mask; bestPenalty = penalty; }
            ApplyMask(mask); // XOR again to undo
        }
        ApplyMask(best);
        DrawFormatBits(best);
    }

    /// Renders with a 4-module quiet zone, black on white.
    public Bitmap ToBitmap(int scale)
    {
        const int border = 4;
        var pixels = (Size + border * 2) * scale;
        var bitmap = new Bitmap(pixels, pixels);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.White);
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
                if (modules[y, x]) g.FillRectangle(Brushes.Black, (x + border) * scale, (y + border) * scale, scale, scale);
        return bitmap;
    }

    // MARK: Data

    private static byte[] BuildCodewords(int version, byte[] data)
    {
        var bits = new List<bool>();
        void Append(int value, int length)
        {
            for (var i = length - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0);
        }
        Append(0x4, 4); // byte mode
        Append(data.Length, version <= 9 ? 8 : 16);
        foreach (var b in data) Append(b, 8);

        var capacity = NumDataCodewords(version) * 8;
        Append(0, Math.Min(4, capacity - bits.Count)); // terminator
        Append(0, (8 - bits.Count % 8) % 8);
        for (var pad = 0xEC; bits.Count < capacity; pad ^= 0xEC ^ 0x11) Append(pad, 8);

        var result = new byte[bits.Count / 8];
        for (var i = 0; i < bits.Count; i++)
            if (bits[i]) result[i >> 3] |= (byte)(1 << (7 - (i & 7)));
        return result;
    }

    private static int NumRawDataModules(int version)
    {
        var result = (16 * version + 128) * version + 64;
        if (version >= 2)
        {
            var numAlign = version / 7 + 2;
            result -= (25 * numAlign - 10) * numAlign - 55;
            if (version >= 7) result -= 36;
        }
        return result;
    }

    private static int NumDataCodewords(int version) =>
        NumRawDataModules(version) / 8 - EccCodewordsPerBlock[version] * NumErrorCorrectionBlocks[version];

    private static byte[] AddEccAndInterleave(int version, byte[] data)
    {
        var numBlocks = NumErrorCorrectionBlocks[version];
        var blockEccLen = EccCodewordsPerBlock[version];
        var rawCodewords = NumRawDataModules(version) / 8;
        var numShortBlocks = numBlocks - rawCodewords % numBlocks;
        var shortBlockLen = rawCodewords / numBlocks;

        var divisor = ReedSolomonDivisor(blockEccLen);
        var blocks = new List<byte[]>();
        for (int i = 0, k = 0; i < numBlocks; i++)
        {
            var length = shortBlockLen - blockEccLen + (i < numShortBlocks ? 0 : 1);
            var dat = data.AsSpan(k, length).ToArray();
            k += length;
            var ecc = ReedSolomonRemainder(dat, divisor);
            var block = new byte[shortBlockLen + 1];
            // Short blocks get a placeholder byte so all blocks align; it is skipped when interleaving.
            if (i < numShortBlocks)
            {
                dat.CopyTo(block, 0);
                ecc.CopyTo(block, length + 1);
            }
            else
            {
                dat.CopyTo(block, 0);
                ecc.CopyTo(block, length);
            }
            blocks.Add(block);
        }

        var result = new List<byte>(rawCodewords);
        for (var i = 0; i < blocks[0].Length; i++)
            for (var j = 0; j < blocks.Count; j++)
                if (i != shortBlockLen - blockEccLen || j >= numShortBlocks)
                    result.Add(blocks[j][i]);
        return result.ToArray();
    }

    private static byte[] ReedSolomonDivisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        var root = 1;
        for (var i = 0; i < degree; i++)
        {
            for (var j = 0; j < result.Length; j++)
            {
                result[j] = (byte)Multiply(result[j], root);
                if (j + 1 < result.Length) result[j] ^= result[j + 1];
            }
            root = Multiply(root, 0x02);
        }
        return result;
    }

    private static byte[] ReedSolomonRemainder(byte[] data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (var b in data)
        {
            var factor = b ^ result[0];
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (var i = 0; i < result.Length; i++) result[i] ^= (byte)Multiply(divisor[i], factor);
        }
        return result;
    }

    private static int Multiply(int x, int y)
    {
        var z = 0;
        for (var i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return z;
    }

    // MARK: Layout

    private void SetFunction(int x, int y, bool dark)
    {
        modules[y, x] = dark;
        isFunction[y, x] = true;
    }

    private void DrawFunctionPatterns(int version)
    {
        for (var i = 0; i < Size; i++)
        {
            SetFunction(6, i, i % 2 == 0);
            SetFunction(i, 6, i % 2 == 0);
        }
        DrawFinder(3, 3);
        DrawFinder(Size - 4, 3);
        DrawFinder(3, Size - 4);

        var positions = AlignmentPositions(version);
        var last = positions.Length - 1;
        for (var i = 0; i < positions.Length; i++)
            for (var j = 0; j < positions.Length; j++)
                if (!(i == 0 && j == 0 || i == 0 && j == last || i == last && j == 0))
                    DrawAlignment(positions[i], positions[j]);

        DrawFormatBits(0); // reserve the area; overwritten after masking
        DrawVersion(version);
    }

    private void DrawFinder(int x, int y)
    {
        for (var dy = -4; dy <= 4; dy++)
            for (var dx = -4; dx <= 4; dx++)
            {
                var distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
                int xx = x + dx, yy = y + dy;
                if (xx >= 0 && xx < Size && yy >= 0 && yy < Size) SetFunction(xx, yy, distance != 2 && distance != 4);
            }
    }

    private void DrawAlignment(int x, int y)
    {
        for (var dy = -2; dy <= 2; dy++)
            for (var dx = -2; dx <= 2; dx++)
                SetFunction(x + dx, y + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
    }

    private int[] AlignmentPositions(int version)
    {
        if (version == 1) return Array.Empty<int>();
        var numAlign = version / 7 + 2;
        var step = (version * 8 + numAlign * 3 + 5) / (numAlign * 4 - 4) * 2;
        var result = new int[numAlign];
        result[0] = 6;
        for (int i = numAlign - 1, pos = Size - 7; i >= 1; i--, pos -= step) result[i] = pos;
        return result;
    }

    private void DrawFormatBits(int mask)
    {
        var data = FormatBitsLevelM << 3 | mask;
        var rem = data;
        for (var i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        var bits = (data << 10 | rem) ^ 0x5412;
        bool Bit(int i) => ((bits >> i) & 1) != 0;

        for (var i = 0; i <= 5; i++) SetFunction(8, i, Bit(i));
        SetFunction(8, 7, Bit(6));
        SetFunction(8, 8, Bit(7));
        SetFunction(7, 8, Bit(8));
        for (var i = 9; i < 15; i++) SetFunction(14 - i, 8, Bit(i));

        for (var i = 0; i < 8; i++) SetFunction(Size - 1 - i, 8, Bit(i));
        for (var i = 8; i < 15; i++) SetFunction(8, Size - 15 + i, Bit(i));
        SetFunction(8, Size - 8, true); // always dark
    }

    private void DrawVersion(int version)
    {
        if (version < 7) return;
        var rem = version;
        for (var i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
        var bits = version << 12 | rem;
        for (var i = 0; i < 18; i++)
        {
            var dark = ((bits >> i) & 1) != 0;
            int a = Size - 11 + i % 3, b = i / 3;
            SetFunction(a, b, dark);
            SetFunction(b, a, dark);
        }
    }

    private void DrawCodewords(byte[] data)
    {
        var i = 0;
        for (var right = Size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5; // skip the vertical timing pattern
            for (var vert = 0; vert < Size; vert++)
                for (var j = 0; j < 2; j++)
                {
                    var x = right - j;
                    var upward = ((right + 1) & 2) == 0;
                    var y = upward ? Size - 1 - vert : vert;
                    if (isFunction[y, x] || i >= data.Length * 8) continue;
                    modules[y, x] = ((data[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                    i++;
                }
        }
    }

    private void ApplyMask(int mask)
    {
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                var invert = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => (x / 3 + y / 2) % 2 == 0,
                    5 => x * y % 2 + x * y % 3 == 0,
                    6 => (x * y % 2 + x * y % 3) % 2 == 0,
                    _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
                };
                if (!isFunction[y, x] && invert) modules[y, x] = !modules[y, x];
            }
    }

    /// The standard penalty rules for runs, 2×2 blocks, finder-like patterns and dark/light balance.
    private int Penalty()
    {
        var result = 0;
        for (var pass = 0; pass < 2; pass++)
            for (var a = 0; a < Size; a++)
            {
                var run = 0;
                var color = false;
                for (var b = 0; b < Size; b++)
                {
                    var dark = pass == 0 ? modules[a, b] : modules[b, a];
                    if (b > 0 && dark == color) { run++; continue; }
                    if (run >= 5) result += run - 2;
                    color = dark;
                    run = 1;
                }
                if (run >= 5) result += run - 2;
                for (var b = 0; b + 7 <= Size; b++)
                    if (MatchesFinderLike(pass, a, b)) result += 40;
            }
        for (var y = 0; y < Size - 1; y++)
            for (var x = 0; x < Size - 1; x++)
            {
                var c = modules[y, x];
                if (c == modules[y, x + 1] && c == modules[y + 1, x] && c == modules[y + 1, x + 1]) result += 3;
            }
        var darkCount = 0;
        foreach (var m in modules) if (m) darkCount++;
        var total = Size * Size;
        var k = (Math.Abs(darkCount * 20 - total * 10) + total - 1) / total - 1;
        return result + Math.Max(0, k) * 10;
    }

    private bool MatchesFinderLike(int pass, int line, int start)
    {
        // 1:1:3:1:1 dark pattern with four light modules on either side: 0000 1011101 or 1011101 0000.
        ReadOnlySpan<bool> core = stackalloc bool[] { true, false, true, true, true, false, true };
        bool At(int i)
        {
            if (i < 0 || i >= Size) return false;
            return pass == 0 ? modules[line, i] : modules[i, line];
        }
        for (var i = 0; i < 7; i++) if (At(start + i) != core[i]) return false;
        var lightBefore = true;
        var lightAfter = true;
        for (var i = 1; i <= 4; i++)
        {
            lightBefore &= !At(start - i);
            lightAfter &= !At(start + 6 + i);
        }
        return lightBefore || lightAfter;
    }
}
