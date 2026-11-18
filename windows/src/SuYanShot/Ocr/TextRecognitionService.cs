using System.Drawing.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace SuYanShot.Ocr;

/// One recognized line: its text and where it is, in image pixels (top-left origin, like the editor's image).
internal sealed record RecognizedLine(string Text, RectangleF Box);

/// OCR with Windows' own offline engine (Windows.Media.Ocr), as macOS uses Vision. Traditional Chinese first, then the
/// user's languages. Windows only recognizes languages whose OCR pack is installed (設定 → 時間與語言 → 語言).
internal static class TextRecognitionService
{
    /// Thrown when no OCR language is installed; the message tells the user how to add one.
    internal sealed class NoLanguageException() : Exception(
        "Windows 沒有可用的文字辨識語言。到「設定 → 時間與語言 → 語言與地區」，在「中文 (台灣)」的「語言選項」安裝「光學字元辨識」，再試一次。");

    private static readonly string[] Preferred = { "zh-Hant-TW", "zh-TW", "zh-Hant" };

    private static OcrEngine CreateEngine()
    {
        foreach (var tag in Preferred)
        {
            try
            {
                var language = new Language(tag);
                if (OcrEngine.IsLanguageSupported(language) && OcrEngine.TryCreateFromLanguage(language) is OcrEngine engine) return engine;
            }
            catch (ArgumentException) { } // tag unknown to this Windows build
        }
        return OcrEngine.TryCreateFromUserProfileLanguages() ?? throw new NoLanguageException();
    }

    public static async Task<string> Recognize(Bitmap image) => OcrTextSelection.Join((await RecognizeLines(image)).Select(l => l.Text));

    /// Lines in the engine's reading order.
    public static async Task<IReadOnlyList<RecognizedLine>> RecognizeLines(Bitmap image)
    {
        var engine = CreateEngine();
        // The engine refuses images larger than MaxImageDimension; scale down and map the boxes back.
        var limit = (double)OcrEngine.MaxImageDimension;
        var scale = Math.Min(1.0, Math.Min(limit / image.Width, limit / image.Height));
        using var input = scale < 1 ? new Bitmap(image, Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale))) : null;
        using var bitmap = await ToSoftwareBitmap(input ?? image);
        var result = await engine.RecognizeAsync(bitmap);
        var lines = new List<RecognizedLine>();
        foreach (var line in result.Lines)
        {
            var words = line.Words.ToList();
            if (words.Count == 0) continue;
            var text = JoinWords(words.Select(w => w.Text));
            if (text.Length == 0) continue;
            var left = words.Min(w => w.BoundingRect.X);
            var top = words.Min(w => w.BoundingRect.Y);
            var right = words.Max(w => w.BoundingRect.X + w.BoundingRect.Width);
            var bottom = words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);
            lines.Add(new RecognizedLine(text, new RectangleF((float)(left / scale), (float)(top / scale),
                                                              (float)((right - left) / scale), (float)((bottom - top) / scale))));
        }
        return lines;
    }

    /// The engine returns Chinese and Japanese one character per "word"; only Latin-script words get a space between them.
    internal static string JoinWords(IEnumerable<string> words)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var raw in words)
        {
            var word = raw.Trim();
            if (word.Length == 0) continue;
            if (builder.Length > 0 && !IsCjk(builder[^1]) && !IsCjk(word[0])) builder.Append(' ');
            builder.Append(word);
        }
        return builder.ToString();
    }

    /// Han, kana, Hangul and full-width punctuation, which are written without spaces.
    internal static bool IsCjk(char c) =>
        c is >= '⺀' and <= '鿿' or >= '가' and <= '힯' or >= '豈' and <= '﫿' or >= '＀' and <= '￯';

    private static async Task<SoftwareBitmap> ToSoftwareBitmap(Bitmap image)
    {
        using var png = new MemoryStream();
        image.Save(png, ImageFormat.Png);
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        await stream.WriteAsync(png.ToArray().AsBuffer());
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    }
}
