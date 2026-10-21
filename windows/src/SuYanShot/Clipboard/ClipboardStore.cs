using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SuYanShot.ClipboardHistory;

/// Clipboard history on disk: `index.json` + `tags.json` plus one payload file per image/RTF/HTML entry,
/// in %LOCALAPPDATA%\SuYanShot\Clipboard. Nothing leaves this PC unless phone sharing is turned on.
/// Same behaviour as the macOS store: de-duplication by content hash, favourites and tagged entries are never trimmed.
internal sealed class ClipboardStore
{
    public static readonly int[] LimitOptions = { 50, 100, 200, 500 };

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly List<ClipboardItem> items = new();
    private readonly List<ClipboardTag> tags = new();
    private readonly Dictionary<Guid, Image> thumbnails = new();
    private int limit;

    public string Directory { get; }
    public IReadOnlyList<ClipboardItem> Items => items;
    public IReadOnlyList<ClipboardTag> Tags => tags;

    /// Raised after any change; the panel and phone page refresh from it.
    public event Action? Changed;

    public ClipboardStore(string? directory = null, int limit = 200)
    {
        Directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SuYanShot", "Clipboard");
        this.limit = limit;
        System.IO.Directory.CreateDirectory(Directory);
        Load();
    }

    /// Entries kept, not counting favourites and tagged ones.
    public int Limit
    {
        get => limit;
        set
        {
            limit = value;
            Trim();
            Save();
        }
    }

    // MARK: Adding

    public void Add(ClipboardSnapshot snapshot)
    {
        var hash = Hash(snapshot);
        var existing = items.FindIndex(i => i.ContentHash == hash);
        if (existing >= 0)
        {
            // Same content copied again: move it to the top, keep favourite/tags and payload.
            var item = items[existing];
            items.RemoveAt(existing);
            item.CreatedAt = DateTimeOffset.Now;
            item.SourceAppName = snapshot.SourceAppName ?? item.SourceAppName;
            items.Insert(0, item);
            Save();
            return;
        }
        var created = new ClipboardItem
        {
            Id = Guid.NewGuid(),
            Kind = snapshot.Kind,
            CreatedAt = DateTimeOffset.Now,
            Text = snapshot.Text,
            FilePaths = snapshot.FilePaths,
            HasRtf = snapshot.Rtf != null,
            HasHtml = snapshot.Html != null,
            ImageWidth = snapshot.ImageSize?.Width,
            ImageHeight = snapshot.ImageSize?.Height,
            SourceAppName = snapshot.SourceAppName,
            ContentHash = hash,
        };
        Write(snapshot.Rtf, created.Id, "rtf");
        Write(snapshot.Html, created.Id, "html");
        Write(snapshot.Png, created.Id, "png");
        items.Insert(0, created);
        Trim();
        Save();
    }

    /// Pasting an entry makes it the most recent one.
    public void Bump(Guid id)
    {
        var index = items.FindIndex(i => i.Id == id);
        if (index < 0) return;
        var item = items[index];
        items.RemoveAt(index);
        item.CreatedAt = DateTimeOffset.Now;
        items.Insert(0, item);
        Save();
    }

    public ClipboardItem? Find(Guid id) => items.Find(i => i.Id == id);

    public void TogglePin(Guid id)
    {
        if (Find(id) is not ClipboardItem item) return;
        item.Pinned = !item.Pinned;
        Trim();
        Save();
    }

    public void Delete(Guid id)
    {
        var index = items.FindIndex(i => i.Id == id);
        if (index < 0) return;
        RemovePayloads(items[index]);
        items.RemoveAt(index);
        Save();
    }

    /// Keeps favourites and tagged entries.
    public void ClearUnkept()
    {
        foreach (var item in items.Where(i => !i.IsKept)) RemovePayloads(item);
        items.RemoveAll(i => !i.IsKept);
        Save();
    }

    // MARK: Tags

    public ClipboardTag? Tag(Guid id) => tags.Find(t => t.Id == id);

    /// Creates a tag (or returns the existing one with the same name), optionally adding it to an entry.
    public ClipboardTag? CreateTag(string rawName, Guid? addingTo = null)
    {
        var name = rawName.Trim();
        if (name.Length == 0) return null;
        var tag = tags.Find(t => string.Equals(t.Name, name, StringComparison.CurrentCultureIgnoreCase));
        if (tag == null)
        {
            tag = new ClipboardTag { Id = Guid.NewGuid(), Name = name, ColorIndex = tags.Count };
            tags.Add(tag);
            SaveTags();
        }
        if (addingTo is Guid itemId && Find(itemId) is ClipboardItem item && !item.Tags.Contains(tag.Id))
        {
            item.TagIds = item.Tags.Append(tag.Id).ToList();
            Save();
        }
        else
        {
            Changed?.Invoke();
        }
        return tag;
    }

    public void RenameTag(Guid id, string rawName)
    {
        var name = rawName.Trim();
        if (name.Length == 0 || Tag(id) is not ClipboardTag tag) return;
        tag.Name = name;
        SaveTags();
        Changed?.Invoke();
    }

    /// Removes the tag from every entry; the entries themselves stay.
    public void DeleteTag(Guid id)
    {
        tags.RemoveAll(t => t.Id == id);
        foreach (var item in items.Where(i => i.Tags.Contains(id)))
        {
            var remaining = item.Tags.Where(t => t != id).ToList();
            item.TagIds = remaining.Count == 0 ? null : remaining;
        }
        SaveTags();
        Trim();
        Save();
    }

    public void ToggleTag(Guid tagId, Guid itemId)
    {
        if (Find(itemId) is not ClipboardItem item) return;
        var updated = item.Tags.Contains(tagId) ? item.Tags.Where(t => t != tagId).ToList() : item.Tags.Append(tagId).ToList();
        item.TagIds = updated.Count == 0 ? null : updated;
        Trim();
        Save();
    }

    // MARK: Payloads

    public byte[]? Payload(Guid id, string ext)
    {
        try { return File.ReadAllBytes(PayloadPath(id, ext)); }
        catch (IOException) { return null; }
    }

    /// A ≤480 px thumbnail for image entries, cached in memory.
    public Image? Thumbnail(ClipboardItem item)
    {
        if (thumbnails.TryGetValue(item.Id, out var cached)) return cached;
        if (item.Kind != ClipboardKind.Image || Payload(item.Id, "png") is not byte[] png) return null;
        try
        {
            using var source = new Bitmap(new MemoryStream(png));
            var scale = Math.Min(1.0, 480.0 / Math.Max(source.Width, source.Height));
            var thumb = new Bitmap(source, Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale)));
            thumbnails[item.Id] = thumb;
            return thumb;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private string PayloadPath(Guid id, string ext) => Path.Combine(Directory, $"{id.ToString().ToUpperInvariant()}.{ext}");

    private void Write(byte[]? data, Guid id, string ext)
    {
        if (data != null) File.WriteAllBytes(PayloadPath(id, ext), data);
    }

    private void RemovePayloads(ClipboardItem item)
    {
        foreach (var ext in new[] { "rtf", "html", "png" })
        {
            try { File.Delete(PayloadPath(item.Id, ext)); }
            catch (IOException) { }
        }
        if (thumbnails.Remove(item.Id, out var thumb)) thumb.Dispose();
    }

    // MARK: Persistence

    private string IndexPath => Path.Combine(Directory, "index.json");
    private string TagsPath => Path.Combine(Directory, "tags.json");

    private void Load()
    {
        tags.AddRange(Read<List<ClipboardTag>>(TagsPath) ?? new());
        items.AddRange(Read<List<ClipboardItem>>(IndexPath) ?? new());
    }

    private static T? Read<T>(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : default; }
        catch (Exception ex) when (ex is IOException or JsonException) { return default; }
    }

    private void SaveTags() => WriteAtomic(TagsPath, JsonSerializer.Serialize(tags, Json));

    private void Save()
    {
        WriteAtomic(IndexPath, JsonSerializer.Serialize(items, Json));
        Changed?.Invoke();
    }

    private static void WriteAtomic(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    private void Trim()
    {
        var unkept = 0;
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].IsKept || ++unkept <= limit) continue;
            RemovePayloads(items[i]);
            items.RemoveAt(i--);
        }
    }

    internal static string Hash(ClipboardSnapshot snapshot)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(Encoding.UTF8.GetBytes(snapshot.Kind.ToString()));
        switch (snapshot.Kind)
        {
            case ClipboardKind.Text or ClipboardKind.RichText: sha.AppendData(Encoding.UTF8.GetBytes(snapshot.Text ?? "")); break;
            case ClipboardKind.Image: sha.AppendData(snapshot.Png ?? Array.Empty<byte>()); break;
            case ClipboardKind.File: sha.AppendData(Encoding.UTF8.GetBytes(string.Join("\n", snapshot.FilePaths ?? new()))); break;
        }
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }
}
