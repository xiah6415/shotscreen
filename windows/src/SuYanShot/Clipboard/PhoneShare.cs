using System.Drawing.Imaging;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SuYanShot.App;
using SuYanShot.UI;
using static SuYanShot.ClipboardHistory.PhoneShareServer;

namespace SuYanShot.ClipboardHistory;

internal enum PhoneShareStatus { Off, NeedsPassword, NoNetwork, OtherNetwork, Starting, Running, Failed }

/// Serves the clipboard history to phones on the same network at a fixed address (`http://glean.local:8765`, published
/// over multicast DNS), and accepts text/images sent back. Same design, routes and pages as macOS: a switch in the tray
/// menu turns it on and it then starts with the app; devices sign in once with a password; it only runs on networks
/// the user allowed.
internal sealed class PhoneShare : IDisposable
{
    public const int DefaultPort = 8765;
    private const int MaxTextLength = 100_000;
    private const int CookieMaxAge = 365 * 24 * 60 * 60;

    /// Seams for tests; the app uses the defaults.
    internal sealed record Options
    {
        public int Port { get; init; } = DefaultPort;
        public Func<LocalNetwork?> FindNetwork { get; init; } = LocalNetwork.Find;
        public Func<string, NetworkIdentity> IdentifyRouter { get; init; } = NetworkIdentity.ForRouter;
        public bool Advertise { get; init; } = true;
        public string? AuthPath { get; init; }
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly Lazy<byte[]> Page = new(() => LoadResource("SuYanShot.PhoneSharePage.html"));
    private static readonly Lazy<byte[]> LoginPage = new(() => LoadResource("SuYanShot.PhoneShareLogin.html"));
    private static readonly Lazy<byte[]> TouchIcon = new(() => AppIcon.Png(180));

    private readonly ClipboardStore store;
    /// Puts an entry on this PC's clipboard (without recording it again).
    private readonly Action<ClipboardItem> putOnClipboard;
    private readonly AppSettings settings;
    private readonly Options options;
    private readonly PhoneShareServer server;
    private readonly LocalNameAdvertiser? advertiser;
    private readonly NetworkChangeMonitor networkMonitor = new();

    public PhoneShareAuth Auth { get; }
    public PhoneShareStatus Status { get; private set; } = PhoneShareStatus.Off;
    public string? FailureMessage { get; private set; }
    public NetworkIdentity? CurrentNetwork { get; private set; }
    public string? Address { get; private set; }
    public string? PublishedName => advertiser?.PublishedName;
    public string? NameProblem => advertiser?.Problem;
    public DateTimeOffset? LastActivity { get; private set; }

    public bool IsEnabled => settings.PhoneShareEnabled;
    public string LocalName => LocalNameAdvertiser.Sanitize(settings.PhoneShareName);
    public bool RestrictToHomeNetworks => settings.PhoneShareHomeOnly;
    public IReadOnlyList<NetworkIdentity> HomeNetworks => settings.PhoneShareHomeNetworks;
    public bool IsServing => Status == PhoneShareStatus.Running;
    public int Port => server.Status == State.Running ? server.Port : options.Port;

    /// The address to bookmark: the same on every computer running 拾光 with the default name.
    public string? Url => PublishedName is string name ? $"http://{name}:{Port}/" : null;

    /// Fallback if the phone can't resolve .local names.
    public string? IpUrl => Address is string address ? $"http://{address}:{Port}/" : null;

    /// Raised on the UI thread whenever anything shown in the menu or settings window changes.
    public event Action? Changed;

    public PhoneShare(ClipboardStore store, Action<ClipboardItem> putOnClipboard, AppSettings settings, Options? options = null)
    {
        this.store = store;
        this.putOnClipboard = putOnClipboard;
        this.settings = settings;
        this.options = options ?? new Options();
        Auth = new PhoneShareAuth(this.options.AuthPath);
        Auth.Changed += () => Changed?.Invoke();
        server = new PhoneShareServer(Handle);
        server.StateChanged += ServerStateChanged;
        if (this.options.Advertise)
        {
            advertiser = new LocalNameAdvertiser();
            advertiser.Changed += _ => Changed?.Invoke();
        }
        networkMonitor.Changed += Refresh;
        Refresh();
    }

    // MARK: Settings

    public void SetEnabled(bool enabled)
    {
        settings.PhoneShareEnabled = enabled;
        // First time on: trust the network the PC is on now.
        if (enabled && RestrictToHomeNetworks && HomeNetworks.Count == 0 && CurrentIdentity() is NetworkIdentity network) Allow(network);
        settings.Save();
        Refresh();
    }

    public void SetLocalName(string name)
    {
        settings.PhoneShareName = LocalNameAdvertiser.Sanitize(name);
        settings.Save();
        Refresh();
    }

    public void SetRestrictToHomeNetworks(bool restrict)
    {
        settings.PhoneShareHomeOnly = restrict;
        settings.Save();
        Refresh();
    }

    public void AllowCurrentNetwork()
    {
        if (CurrentIdentity() is NetworkIdentity network) Allow(network);
        settings.Save();
        Refresh();
    }

    public void RemoveNetwork(string id)
    {
        settings.PhoneShareHomeNetworks.RemoveAll(n => n.Id == id);
        settings.Save();
        Refresh();
    }

    public void SetPassword(string password)
    {
        Auth.SetPassword(password);
        Refresh();
    }

    private void Allow(NetworkIdentity network)
    {
        if (!settings.PhoneShareHomeNetworks.Any(n => n.Id == network.Id)) settings.PhoneShareHomeNetworks.Add(network);
    }

    private NetworkIdentity? CurrentIdentity() =>
        options.FindNetwork() is { Gateway: string router } ? options.IdentifyRouter(router) : null;

    // MARK: Running

    /// Starts or stops serving to match the switch, the password and the current network.
    public void Refresh()
    {
        var network = options.FindNetwork();
        CurrentNetwork = network is { Gateway: string router } ? options.IdentifyRouter(router) : null;
        if (!IsEnabled) { StopServing(PhoneShareStatus.Off); return; }
        if (!Auth.HasPassword) { StopServing(PhoneShareStatus.NeedsPassword); return; }
        if (network == null || CurrentNetwork == null) { StopServing(PhoneShareStatus.NoNetwork); return; }
        if (RestrictToHomeNetworks && !HomeNetworks.Any(n => n.Id == CurrentNetwork.Id)) { StopServing(PhoneShareStatus.OtherNetwork); return; }
        Address = network.Address;
        if (server.Status != State.Running)
        {
            Status = PhoneShareStatus.Starting;
            server.Start(options.Port);
        }
        advertiser?.Publish(LocalName, network.Address);
        Changed?.Invoke();
    }

    public void Stop() => StopServing(PhoneShareStatus.Off);

    private void StopServing(PhoneShareStatus reason)
    {
        server.Stop();
        advertiser?.Stop();
        Address = null;
        Status = reason;
        Changed?.Invoke();
    }

    private void ServerStateChanged()
    {
        switch (server.Status)
        {
            case State.Running:
                Status = PhoneShareStatus.Running;
                FailureMessage = null;
                break;
            case State.Failed:
                Status = PhoneShareStatus.Failed;
                FailureMessage = server.FailedBecausePortInUse ? $"{options.Port} 埠被其他程式佔用了。" : server.Failure;
                advertiser?.Stop();
                break;
        }
        Changed?.Invoke();
    }

    // MARK: Routes

    private async Task<Response> Handle(Request request)
    {
        var parts = request.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var method = request.Method;

        // Public: what the login page and Home Screen need.
        if (method == "GET" && parts is ["apple-touch-icon.png"]) return new Response(200, "image/png", TouchIcon.Value);
        if (method == "GET" && parts is ["manifest.webmanifest"])
        {
            return Response.Text("""{"name":"拾光剪貼簿","short_name":"拾光","start_url":"/","display":"standalone","background_color":"#000000","icons":[{"src":"/apple-touch-icon.png","sizes":"180x180","type":"image/png"}]}""",
                                 type: "application/manifest+json");
        }
        // Cross-site forms can't set custom headers without a CORS preflight, which this server never answers.
        if (method == "POST" && request.Headers.GetValueOrDefault("x-requested-with") != "glean") return Response.Text("Forbidden", 403);
        if (method == "POST" && parts is ["api", "login"]) return Login(request);

        var device = request.Cookies.TryGetValue(PhoneShareAuth.CookieName, out var token) ? Auth.DeviceForToken(token) : null;
        if (method == "GET" && parts.Length == 0) return new Response(200, "text/html; charset=utf-8", device == null ? LoginPage.Value : Page.Value);
        if (device == null) return Response.Text("""{"ok":false,"error":"login"}""", 401, "application/json");
        LastActivity = DateTimeOffset.Now;
        Changed?.Invoke();

        if (method == "POST" && parts is ["api", "logout"])
        {
            Auth.Revoke(device.Id);
            return Response.Text("""{"ok":true}""", type: "application/json") with
            {
                Headers = new Dictionary<string, string> { ["Set-Cookie"] = $"{PhoneShareAuth.CookieName}=; Path=/; Max-Age=0; HttpOnly; SameSite=Strict" },
            };
        }
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
                return Response.Text("""{"ok":true}""", type: "application/json");
            default:
                return Response.Text("Not Found", 404);
        }
    }

    private sealed record LoginBody(string? Password, string? Device);

    private Response Login(Request request)
    {
        LoginBody? body;
        try { body = JsonSerializer.Deserialize<LoginBody>(request.Body, Json); }
        catch (JsonException) { body = null; }
        if (body?.Password is not string password) return Response.Text("Bad Request", 400);
        return Auth.Login(password, body.Device ?? "", request.RemoteAddress) switch
        {
            PhoneShareAuth.LoginResult.Success success => Response.Text("""{"ok":true}""", type: "application/json") with
            {
                Headers = new Dictionary<string, string>
                {
                    ["Set-Cookie"] = $"{PhoneShareAuth.CookieName}={success.Token}; Path=/; Max-Age={CookieMaxAge}; HttpOnly; SameSite=Strict",
                },
            },
            PhoneShareAuth.LoginResult.WrongPassword wrong => Response.Text($$"""{"ok":false,"error":"wrong","remaining":{{wrong.Remaining}}}""", 401, "application/json"),
            PhoneShareAuth.LoginResult.LockedOut locked => Response.Text($$"""{"ok":false,"error":"locked","seconds":{{locked.Seconds}}}""", 429, "application/json"),
            _ => Response.Text("Bad Request", 400),
        };
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
        return Response.Text("""{"ok":true}""", type: "application/json");
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

    private static byte[] LoadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException(name + " is not embedded.");
        using var reader = new MemoryStream();
        stream.CopyTo(reader);
        return reader.ToArray();
    }

    public void Dispose()
    {
        networkMonitor.Dispose();
        server.Stop();
        advertiser?.Dispose();
    }
}
