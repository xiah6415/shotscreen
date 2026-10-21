using System.Text.Json.Serialization;

namespace SuYanShot.ClipboardHistory;

/// Stored as "text", "richText", "image", "file" — the same raw values as macOS.
internal enum ClipboardKind
{
    Text,
    RichText,
    Image,
    File,
}

internal static class ClipboardKindExtensions
{
    public static string Title(this ClipboardKind kind) => kind switch
    {
        ClipboardKind.Text => "文字",
        ClipboardKind.RichText => "格式文字",
        ClipboardKind.Image => "圖片",
        _ => "檔案",
    };

    public static bool IsText(this ClipboardKind kind) => kind is ClipboardKind.Text or ClipboardKind.RichText;
}

/// One clipboard history entry, as on macOS. Small fields live in the index; image/RTF/HTML payloads are files next to it.
internal sealed class ClipboardItem
{
    public Guid Id { get; init; }
    public ClipboardKind Kind { get; init; }
    public DateTimeOffset CreatedAt { get; set; }

    /// Shown as 常用 (favourite) in the UI.
    public bool Pinned { get; set; }

    public List<Guid>? TagIds { get; set; }

    /// Plain text for text kinds.
    public string? Text { get; init; }

    public List<string>? FilePaths { get; init; }
    public bool HasRtf { get; init; }
    public bool HasHtml { get; init; }
    public int? ImageWidth { get; init; }
    public int? ImageHeight { get; init; }
    public string? SourceAppName { get; set; }

    /// Identifies identical content so re-copying moves the old entry to the top instead of duplicating it.
    public string ContentHash { get; init; } = "";

    [JsonIgnore] public IReadOnlyList<Guid> Tags => TagIds ?? (IReadOnlyList<Guid>)Array.Empty<Guid>();

    /// Favourites and tagged entries are kept when the history is trimmed or cleared.
    [JsonIgnore] public bool IsKept => Pinned || Tags.Count > 0;

    [JsonIgnore]
    public string SearchText => Kind switch
    {
        ClipboardKind.Text or ClipboardKind.RichText => Text ?? "",
        ClipboardKind.File => string.Join(" ", (FilePaths ?? new()).Select(Path.GetFileName)),
        _ => "圖片 image",
    };
}

/// A user-made label for clipboard entries.
internal sealed class ClipboardTag
{
    /// The macOS system colours used for tags, so both apps show the same colour for a given index.
    public static readonly Color[] Palette =
    {
        Color.FromArgb(255, 59, 48), Color.FromArgb(255, 149, 0), Color.FromArgb(255, 204, 0), Color.FromArgb(52, 199, 89),
        Color.FromArgb(48, 176, 199), Color.FromArgb(0, 122, 255), Color.FromArgb(175, 82, 222), Color.FromArgb(255, 45, 85),
    };

    public Guid Id { get; init; }
    public string Name { get; set; } = "";
    public int ColorIndex { get; init; }

    [JsonIgnore] public Color Color => Palette[ColorIndex % Palette.Length];
}

/// What the monitor read from the clipboard, before it is stored.
internal sealed record ClipboardSnapshot(ClipboardKind Kind)
{
    public string? Text { get; init; }
    public byte[]? Rtf { get; init; }
    public byte[]? Html { get; init; }
    public byte[]? Png { get; init; }
    public Size? ImageSize { get; init; }
    public List<string>? FilePaths { get; init; }
    public string? SourceAppName { get; init; }
}
