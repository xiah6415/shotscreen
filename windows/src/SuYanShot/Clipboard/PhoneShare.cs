using System.Drawing.Imaging;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static SuYanShot.ClipboardHistory.PhoneShareServer;

namespace SuYanShot.ClipboardHistory;

/// Serves the clipboard history to a phone browser on the same network, and accepts text/images sent back.
/// Same routes and page as macOS. Runs only after the user starts it, and stops by itself after 30 idle minutes.
internal sealed class PhoneShare
{
    public static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(30);
    private const int MaxTextLength = 100_000;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly ClipboardStore store;
    /// Puts an entry on this PC's clipboard (without recording it again).
    private readonly Action<ClipboardItem> putOnClipboard;
    private readonly PhoneShareServer server;
    private readonly System.Windows.Forms.Timer idleTimer = new() { Interval = (int)IdleLimit.TotalMilliseconds };
    private static readonly Lazy<byte[]> Page = new(LoadPage);

    public bool IsRunning => server.Status == State.Running;
    public string? Url { get; private set; }
    public string? Problem { get; private set; }
    public DateTimeOffset? LastActivity { get; private set; }

    /// Raised on the UI thread whenever the state, link or last activity changes.
    public event Action? Changed;

    public PhoneShare(ClipboardStore store, Action<ClipboardItem> putOnClipboard)
    {
        this.store = store;
        this.putOnClipboard = putOnClipboard;
        server = new PhoneShareServer(Handle);
        server.StateChanged += ServerStateChanged;
        server.Activity += () =>
        {
            LastActivity = DateTimeOffset.Now;
            RestartIdleTimer();
            Changed?.Invoke();
        };
        idleTimer.Tick += (_, _) => Stop();
    }

    public void Start()
    {
        if (IsRunning) return;
        Problem = null;
        LastActivity = null;
        server.Start();
        RestartIdleTimer();
    }

    public void Stop()
    {
        idleTimer.Stop();
        server.Stop();
    }

    public void RegenerateLink() => server.RegenerateToken();

    internal int Port => server.Port;
    internal string Token => server.Token;

    private void RestartIdleTimer()
    {
        idleTimer.Stop();
        idleTimer.Start();
    }

    private void ServerStateChanged()
    {
        switch (server.Status)
        {
            case State.Running:
                if (LocalIPv4Address() is string address)
                {
                    Url = $"http://{address}:{server.Port}/?t={server.Token}";
                    Problem = null;
                }
                else
                {
                    Url = null;
                    Problem = "找不到區域網路位址，請確認這台電腦已連上 Wi-Fi 或有線網路。";
                }
                break;
            case State.Failed:
                Url = null;
                Problem = $"無法開始分享：{server.Failure}";
                break;
            default:
                Url = null;
                break;
        }
        Changed?.Invoke();
    }

    // MARK: Routes

    private async Task<Response> Handle(Request request)
    {
        var parts = request.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var method = request.Method;
        if (method == "GET" && parts.Length == 0) return new Response(200, "text/html; charset=utf-8", Page.Value);
        if (method == "GET" && parts is ["api", "items"]) return ItemsResponse(request.Query.GetValueOrDefault("v"));
        if (method == "POST" && parts is ["api", "send"]) return await Receive(request);
        // /api/items/<id>/image and /api/items/<id>/to-mac (the route name is shared with the macOS page)
        if (parts is not ["api", "items", var id, var action] || !Guid.TryParse(id, out var guid) || store.Find(guid) is not ClipboardItem item)
            return Response.Text("Not Found", 404);
        switch (method, action)
        {
            case ("GET", "image"):
                if (item.Kind != ClipboardKind.Image) return Response.Text("Not Found", 404);
                if (request.Query.ContainsKey("thumb") && store.Thumbnail(item) is Image thumb)
                {
                    using var jpeg = new MemoryStream();
                    thumb.Save(jpeg, ImageFormat.Jpeg);
                    return new Response(200, "image/jpeg", jpeg.ToArray());
                }
                return store.Payload(item.Id, "png") is byte[] png ? new Response(200, "image/png", png) : Response.Text("Not Found", 404);
            case ("POST", "to-mac"):
                putOnClipboard(item);
                store.Bump(item.Id);
                return Response.Text("{\"ok\":true}", type: "application/json");
            default:
                return Response.Text("Not Found", 404);
        }
    }

    /// Text or an image from the phone: recorded in history (source 手機) and put on this PC's clipboard.
    private async Task<Response> Receive(Request request)
    {
        var type = request.Headers.GetValueOrDefault("content-type") ?? "";
        ClipboardSnapshot snapshot;
        if (type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            if (await DecodeToPng(request.Body) is not (byte[] png, Size size))
                return Response.Text("{\"ok\":false,\"error\":\"看不懂這張圖片\"}", 400, "application/json");
            snapshot = new ClipboardSnapshot(ClipboardKind.Image) { Png = png, ImageSize = size, SourceAppName = "手機" };
        }
        else
        {
            var text = Encoding.UTF8.GetString(request.Body);
            if (string.IsNullOrWhiteSpace(text)) return Response.Text("{\"ok\":false,\"error\":\"沒有內容\"}", 400, "application/json");
            snapshot = new ClipboardSnapshot(ClipboardKind.Text) { Text = text, SourceAppName = "手機" };
        }
        store.Add(snapshot);
        if (store.Items.FirstOrDefault() is ClipboardItem newest) putOnClipboard(newest);
        return Response.Text("{\"ok\":true}", type: "application/json");
    }

    /// JPEG/PNG/GIF/BMP through GDI+ (honouring the camera's EXIF rotation); anything else, such as HEIC from an iPhone,
    /// through the Windows imaging codecs (HEIC needs the free HEIF extension from the Microsoft Store).
    internal static async Task<(byte[], Size)?> DecodeToPng(byte[] data)
    {
        try
        {
            using var image = new Bitmap(new MemoryStream(data));
            ApplyExifOrientation(image);
            using var output = new MemoryStream();
            image.Save(output, ImageFormat.Png);
            return (output.ToArray(), image.Size);
        }
        catch (ArgumentException)
        {
        }
        try
        {
            using var input = new MemoryStream(data).AsRandomAccessStream();
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(input);
            var pixels = await decoder.GetSoftwareBitmapAsync(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                new Windows.Graphics.Imaging.BitmapTransform(), Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation,
                Windows.Graphics.Imaging.ColorManagementMode.ColorManageToSRgb);
            using var output = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, output);
            encoder.SetSoftwareBitmap(pixels);
            await encoder.FlushAsync();
            var bytes = new byte[output.Size];
            await output.AsStreamForRead().ReadExactlyAsync(bytes);
            return (bytes, new Size(pixels.PixelWidth, pixels.PixelHeight));
        }
        catch (Exception)
        {
            return null; // not an image any codec on this PC understands
        }
    }

    private static void ApplyExifOrientation(Image image)
    {
        const int OrientationId = 0x0112;
        if (!image.PropertyIdList.Contains(OrientationId)) return;
        var rotate = image.GetPropertyItem(OrientationId)?.Value?[0] switch
        {
            2 => RotateFlipType.RotateNoneFlipX,
            3 => RotateFlipType.Rotate180FlipNone,
            4 => RotateFlipType.Rotate180FlipX,
            5 => RotateFlipType.Rotate90FlipX,
            6 => RotateFlipType.Rotate90FlipNone,
            7 => RotateFlipType.Rotate270FlipX,
            8 => RotateFlipType.Rotate270FlipNone,
            _ => (RotateFlipType?)null,
        };
        if (rotate is RotateFlipType value) image.RotateFlip(value);
    }

    // MARK: JSON

    private sealed record WebItem(Guid Id, string Kind, string? Text, bool Truncated, double CreatedAt, bool Pinned,
                                  IReadOnlyList<Guid> Tags, string? Source, int? Width, int? Height, List<string>? Files);

    private sealed record WebTag(Guid Id, string Name, string Color);

    private sealed record Payload(string Version, bool Unchanged, List<WebItem>? Items, List<WebTag>? Tags);

    /// The phone polls with the last version it saw; an unchanged history answers with no items.
    private Response ItemsResponse(string? knownVersion)
    {
        var version = HistoryVersion();
        Payload payload;
        if (knownVersion == version)
        {
            payload = new Payload(version, true, null, null);
        }
        else
        {
            var items = store.Items.Select(item => new WebItem(
                item.Id, JsonNamingPolicy.CamelCase.ConvertName(item.Kind.ToString()),
                item.Text is { Length: > MaxTextLength } full ? full[..MaxTextLength] : item.Text,
                (item.Text?.Length ?? 0) > MaxTextLength, item.CreatedAt.ToUnixTimeMilliseconds(), item.Pinned, item.Tags,
                item.SourceAppName, item.ImageWidth, item.ImageHeight, item.FilePaths?.Select(p => Path.GetFileName(p)).ToList())).ToList();
            var tags = store.Tags.Select(t => new WebTag(t.Id, t.Name, $"#{t.Color.R:x2}{t.Color.G:x2}{t.Color.B:x2}")).ToList();
            payload = new Payload(version, false, items, tags);
        }
        return new Response(200, "application/json", JsonSerializer.SerializeToUtf8Bytes(payload, Json));
    }

    private string HistoryVersion()
    {
        var text = new StringBuilder();
        foreach (var item in store.Items) text.Append(item.Id).Append(item.CreatedAt.ToUnixTimeMilliseconds()).Append(item.Pinned).AppendJoin(',', item.Tags).Append(';');
        foreach (var tag in store.Tags) text.Append(tag.Id).Append(tag.Name).Append(tag.ColorIndex).Append(';');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())), 0, 8).ToLowerInvariant();
    }

    private static byte[] LoadPage()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SuYanShot.PhoneSharePage.html")
                           ?? throw new InvalidOperationException("PhoneSharePage.html is not embedded.");
        using var reader = new MemoryStream();
        stream.CopyTo(reader);
        return reader.ToArray();
    }
}
