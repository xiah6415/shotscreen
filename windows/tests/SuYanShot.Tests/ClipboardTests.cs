using System.Drawing.Imaging;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SuYanShot.App;
using SuYanShot.ClipboardHistory;
using SuYanShot.UI;
using Xunit;

namespace SuYanShot.Tests;

public sealed class ClipboardStoreTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "SuYanShotTests", Guid.NewGuid().ToString("N"));

    private static ClipboardSnapshot Text(string text) => new(ClipboardKind.Text) { Text = text, SourceAppName = "Notepad" };

    private static byte[] Png(int width, int height, Color color)
    {
        using var bitmap = new Bitmap(width, height);
        using (var g = Graphics.FromImage(bitmap)) g.Clear(color);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    [Fact]
    public void NewestFirstAndDuplicatesMoveToTop()
    {
        var store = new ClipboardStore(folder);
        store.Add(Text("a"));
        store.Add(Text("b"));
        var firstA = store.Items[1].Id;
        store.Add(Text("a"));
        Assert.Equal(new[] { "a", "b" }, store.Items.Select(i => i.Text));
        Assert.Equal(firstA, store.Items[0].Id);
    }

    [Fact]
    public void PayloadsThumbnailsAndPersistence()
    {
        var store = new ClipboardStore(folder);
        store.Add(new ClipboardSnapshot(ClipboardKind.Image) { Png = Png(1200, 600, Color.Teal), ImageSize = new Size(1200, 600) });
        store.Add(new ClipboardSnapshot(ClipboardKind.RichText) { Text = "bold", Rtf = Encoding.UTF8.GetBytes(@"{\rtf1 bold}"), Html = Encoding.UTF8.GetBytes("<b>bold</b>") });
        store.Add(new ClipboardSnapshot(ClipboardKind.File) { FilePaths = new() { @"C:\a\report.pdf" } });

        var image = store.Items.Single(i => i.Kind == ClipboardKind.Image);
        Assert.NotNull(store.Payload(image.Id, "png"));
        Assert.Equal(new Size(480, 240), store.Thumbnail(image)!.Size);

        var reloaded = new ClipboardStore(folder);
        Assert.Equal(store.Items.Select(i => (i.Id, i.Kind)), reloaded.Items.Select(i => (i.Id, i.Kind)));
        var rich = reloaded.Items.Single(i => i.Kind == ClipboardKind.RichText);
        Assert.True(rich.HasRtf && rich.HasHtml);
        Assert.Equal("<b>bold</b>", Encoding.UTF8.GetString(reloaded.Payload(rich.Id, "html")!));
        Assert.Equal("report.pdf", reloaded.Items.Single(i => i.Kind == ClipboardKind.File).SearchText);
        // Same raw values as the macOS index.json.
        Assert.Contains("\"kind\": \"richText\"".Replace(" ", ""), File.ReadAllText(Path.Combine(folder, "index.json")));
    }

    [Fact]
    public void TrimKeepsFavouritesAndTaggedEntries()
    {
        var store = new ClipboardStore(folder, limit: 2);
        store.Add(Text("pinned"));
        store.TogglePin(store.Items[0].Id);
        store.Add(Text("tagged"));
        store.CreateTag("工作", store.Items[0].Id);
        store.Add(Text("1"));
        store.Add(Text("2"));
        store.Add(Text("3"));
        Assert.Equal(new[] { "3", "2", "tagged", "pinned" }, store.Items.Select(i => i.Text));

        store.ClearUnkept();
        Assert.Equal(new[] { "tagged", "pinned" }, store.Items.Select(i => i.Text));
    }

    [Fact]
    public void TagsCreateRenameToggleDelete()
    {
        var store = new ClipboardStore(folder);
        store.Add(Text("x"));
        var id = store.Items[0].Id;
        var tag = store.CreateTag(" 工作 ", id)!;
        Assert.Same(tag, store.CreateTag("工作")); // same name is reused
        Assert.Equal("工作", tag.Name);
        store.RenameTag(tag.Id, "專案");
        Assert.Equal("專案", new ClipboardStore(folder).Tags.Single().Name);

        store.ToggleTag(tag.Id, id);
        Assert.Empty(store.Items[0].Tags);
        store.ToggleTag(tag.Id, id);
        store.DeleteTag(tag.Id);
        Assert.Empty(store.Tags);
        Assert.Empty(store.Items[0].Tags);
        Assert.Single(store.Items); // entries stay
    }

    [Fact]
    public void DeleteRemovesPayloadFiles()
    {
        var store = new ClipboardStore(folder);
        store.Add(new ClipboardSnapshot(ClipboardKind.Image) { Png = Png(10, 10, Color.Red), ImageSize = new Size(10, 10) });
        store.Delete(store.Items[0].Id);
        Assert.Empty(Directory.GetFiles(folder, "*.png"));
    }

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }
}

public sealed class PhoneShareServerTests
{
    [Fact]
    public void ParsesRequestsAndWaitsForTheBody()
    {
        var head = "POST /api/send?t=abc&x=a%20b HTTP/1.1\r\nHost: x\r\nContent-Type: text/plain\r\nContent-Length: 5\r\n\r\n";
        Assert.Null(PhoneShareServer.Parse(Encoding.UTF8.GetBytes(head + "he")));
        var parsed = PhoneShareServer.Parse(Encoding.UTF8.GetBytes(head + "hello"))!.Value;
        Assert.Equal("POST", parsed.Request!.Method);
        Assert.Equal("/api/send", parsed.Request.Path);
        Assert.Equal("abc", parsed.Request.Query["t"]);
        Assert.Equal("a b", parsed.Request.Query["x"]);
        Assert.Equal("text/plain", parsed.Request.Headers["content-type"]);
        Assert.Equal("hello", Encoding.UTF8.GetString(parsed.Request.Body));
        Assert.Null(PhoneShareServer.Parse(Encoding.UTF8.GetBytes("GET / HTTP/1.1\r\n")));
    }

    [Theory]
    [InlineData("192.168.1.20", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.20.1.1", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::ffff:192.168.0.9", true)]
    [InlineData("fe80::1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("2001:4860::8888", false)]
    public void OnlyLocalAddresses(string address, bool local) => Assert.Equal(local, PhoneShareServer.IsLocal(IPAddress.Parse(address)));
}

/// Drives the real server over loopback, like the harness checks done for the macOS version.
public sealed class PhoneShareRoutesTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "SuYanShotTests", Guid.NewGuid().ToString("N"));
    private readonly List<ClipboardItem> putOnClipboard = new();
    private LocalNetwork? network = new("127.0.0.1", "192.168.1.1");
    private string routerId = "aa:bb:cc:dd:ee:01";

    private PhoneShare Share(ClipboardStore store, AppSettings? settings = null) =>
        new(store, putOnClipboard.Add, settings ?? new AppSettings { FilePath = Path.Combine(folder, "settings.json") }, new PhoneShare.Options
        {
            Port = 0,
            FindNetwork = () => network,
            IdentifyRouter = router => new NetworkIdentity(routerId, router, "路由器 " + router),
            Advertise = false,
            AuthPath = Path.Combine(folder, "auth.json"),
        });

    private static HttpClient Client(PhoneShare share) =>
        new(new HttpClientHandler { UseCookies = true, CookieContainer = new CookieContainer() }) { BaseAddress = new Uri($"http://127.0.0.1:{share.Port}") };

    private static HttpRequestMessage Post(string path, HttpContent? content = null, bool glean = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        if (glean) request.Headers.Add("X-Requested-With", "glean");
        return request;
    }

    private static StringContent LoginBody(string password) =>
        new($$"""{"password":"{{password}}","device":"測試 iPhone"}""", Encoding.UTF8, "application/json");

    [Fact]
    public void StaysOffUntilThereIsAPasswordAndOnlyOnAllowedNetworks()
    {
        var store = new ClipboardStore(folder);
        using var share = Share(store);
        Assert.Equal(PhoneShareStatus.Off, share.Status);

        share.SetEnabled(true);
        Assert.Equal(PhoneShareStatus.NeedsPassword, share.Status);
        Assert.Single(share.HomeNetworks); // the network it was turned on in is trusted

        share.SetPassword("hunter22");
        Assert.Equal(PhoneShareStatus.Running, share.Status);

        routerId = "aa:bb:cc:dd:ee:02"; // another network
        share.Refresh();
        Assert.Equal(PhoneShareStatus.OtherNetwork, share.Status);
        share.AllowCurrentNetwork();
        Assert.Equal(PhoneShareStatus.Running, share.Status);

        network = null;
        share.Refresh();
        Assert.Equal(PhoneShareStatus.NoNetwork, share.Status);

        network = new("127.0.0.1", "192.168.1.1");
        share.SetRestrictToHomeNetworks(false);
        routerId = "aa:bb:cc:dd:ee:03";
        share.Refresh();
        Assert.Equal(PhoneShareStatus.Running, share.Status);

        var saved = AppSettings.Load(Path.Combine(folder, "settings.json"));
        Assert.True(saved.PhoneShareEnabled);
        Assert.Equal(2, saved.PhoneShareHomeNetworks.Count);
        Assert.False(saved.PhoneShareHomeOnly);
    }

    [Fact]
    public async Task LoginCookieGuardsEveryRoute()
    {
        var store = new ClipboardStore(folder);
        store.Add(new ClipboardSnapshot(ClipboardKind.Text) { Text = "來自電腦" });
        using var share = Share(store);
        share.SetEnabled(true);
        share.SetPassword("hunter22");
        using var http = Client(share);

        // Public routes and the login page.
        Assert.Contains("登入拾光", await http.GetStringAsync("/"));
        Assert.Equal("image/png", (await http.GetAsync("/apple-touch-icon.png")).Content.Headers.ContentType!.MediaType);
        Assert.Contains("拾光", await http.GetStringAsync("/manifest.webmanifest"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/items")).StatusCode);

        // POSTs need the header cross-site forms can't send.
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(Post("/api/login", LoginBody("hunter22"), glean: false))).StatusCode);

        var wrong = await http.SendAsync(Post("/api/login", LoginBody("nope")));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Contains("\"remaining\":4", await wrong.Content.ReadAsStringAsync());

        var login = await http.SendAsync(Post("/api/login", LoginBody("hunter22")));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var cookie = login.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("glean_session=", cookie);
        Assert.Contains("HttpOnly", cookie);
        Assert.Contains("SameSite=Strict", cookie);
        Assert.Equal("測試 iPhone", share.Auth.Devices.Single().Name);
        Assert.DoesNotContain(share.Auth.Devices.Single().TokenHash, cookie); // only the hash is stored

        Assert.Contains("傳文字到電腦", await http.GetStringAsync("/"));
        var list = JsonDocument.Parse(await http.GetStringAsync("/api/items")).RootElement;
        Assert.Equal("來自電腦", list.GetProperty("items")[0].GetProperty("text").GetString());
        var version = list.GetProperty("version").GetString();
        Assert.True(JsonDocument.Parse(await http.GetStringAsync($"/api/items?v={version}")).RootElement.GetProperty("unchanged").GetBoolean());
        Assert.NotNull(share.LastActivity);

        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Post("/api/send", new StringContent("手機傳來的字", Encoding.UTF8, "text/plain")))).StatusCode);
        Assert.Equal("手機傳來的字", store.Items[0].Text);
        Assert.Equal("手機", store.Items[0].SourceAppName);
        Assert.Equal(store.Items[0].Id, putOnClipboard.Last().Id);

        using var jpeg = new MemoryStream();
        using (var photo = new Bitmap(900, 300))
        {
            using (var g = Graphics.FromImage(photo)) g.Clear(Color.Orange);
            photo.Save(jpeg, ImageFormat.Jpeg);
        }
        var upload = new ByteArrayContent(jpeg.ToArray());
        upload.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Post("/api/send", upload))).StatusCode);
        var image = store.Items[0];
        Assert.Equal(900, image.ImageWidth);
        Assert.Equal("image/jpeg", (await http.GetAsync($"/api/items/{image.Id}/image?thumb=1")).Content.Headers.ContentType!.MediaType);
        Assert.Equal("image/png", (await http.GetAsync($"/api/items/{image.Id}/image")).Content.Headers.ContentType!.MediaType);

        var older = store.Items.Last();
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Post($"/api/items/{older.Id}/to-mac"))).StatusCode);
        Assert.Equal(older.Id, store.Items[0].Id);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/items/{Guid.NewGuid()}/image")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(Post("/api/send", new StringContent("  ")))).StatusCode);

        // A forged cookie gets nowhere; logging out revokes the device and clears the cookie.
        using (var forged = new HttpClient { BaseAddress = http.BaseAddress })
        {
            forged.DefaultRequestHeaders.Add("Cookie", "glean_session=" + new string('a', 64));
            Assert.Equal(HttpStatusCode.Unauthorized, (await forged.GetAsync("/api/items")).StatusCode);
        }
        var logout = await http.SendAsync(Post("/api/logout"));
        Assert.Contains("Max-Age=0", logout.Headers.GetValues("Set-Cookie").Single());
        Assert.Empty(share.Auth.Devices);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/items")).StatusCode);
    }

    [Fact]
    public async Task RepeatedWrongPasswordsLockTheAddressAndChangingThePasswordSignsEveryoneOut()
    {
        var store = new ClipboardStore(folder);
        using var share = Share(store);
        share.SetEnabled(true);
        share.SetPassword("hunter22");
        using var http = Client(share);
        HttpResponseMessage last = null!;
        for (var i = 0; i < 5; i++) last = await http.SendAsync(Post("/api/login", LoginBody("wrong")));
        Assert.Equal((HttpStatusCode)429, last.StatusCode);
        // Even the right password is refused while locked.
        Assert.Equal((HttpStatusCode)429, (await http.SendAsync(Post("/api/login", LoginBody("hunter22")))).StatusCode);

        using var other = Client(share);
        share.Auth.Login("hunter22", "x", "10.0.0.9");
        Assert.Single(share.Auth.Devices);
        share.SetPassword("new-password");
        Assert.Empty(share.Auth.Devices);

        // Stored on disk only as salted PBKDF2 and token hashes.
        var file = File.ReadAllText(Path.Combine(folder, "auth.json"));
        Assert.DoesNotContain("new-password", file);
        Assert.True(new PhoneShareAuth(Path.Combine(folder, "auth.json")).CheckPassword("new-password"));
    }

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }
}

public sealed class LocalNameAdvertiserTests
{
    [Theory]
    [InlineData("glean", "glean")]
    [InlineData("My PC!", "my-pc")]
    [InlineData("拾光", "glean")]
    [InlineData("--a_b--", "a-b")]
    public void SanitizesLikeMacOS(string input, string expected) => Assert.Equal(expected, LocalNameAdvertiser.Sanitize(input));

    [Fact]
    public void QueriesAndAnswersRoundTrip()
    {
        var query = DnsMessage.Parse(LocalNameAdvertiser.BuildQuery("glean.local"));
        Assert.False(query.IsResponse);
        Assert.Equal(new DnsMessage.Question("glean.local", 1, 1), query.Questions.Single());

        var answer = DnsMessage.Parse(LocalNameAdvertiser.BuildResponse("glean.local", IPAddress.Parse("192.168.1.20"), 120, id: 0));
        Assert.True(answer.IsResponse);
        var record = answer.Answers.Single();
        Assert.Equal("glean.local", record.Name);
        Assert.Equal(1, record.Type);
        Assert.Equal(0x8001, record.Class); // cache-flush + IN
        Assert.Equal(new byte[] { 192, 168, 1, 20 }, record.Data);
    }

    [Fact]
    public void ReadsCompressedNames()
    {
        // A response whose answer name points back at the question name (0xC00C), as real responders send.
        var packet = new byte[]
        {
            0, 7, 0x84, 0, 0, 1, 0, 1, 0, 0, 0, 0,
            5, (byte)'g', (byte)'l', (byte)'e', (byte)'a', (byte)'n', 5, (byte)'l', (byte)'o', (byte)'c', (byte)'a', (byte)'l', 0, 0, 1, 0, 1,
            0xC0, 0x0C, 0, 1, 0x80, 1, 0, 0, 0, 120, 0, 4, 10, 0, 0, 7,
        };
        var message = DnsMessage.Parse(packet);
        Assert.Equal(7, message.Id);
        Assert.Equal("glean.local", message.Answers.Single().Name);
        Assert.Equal(new byte[] { 10, 0, 0, 7 }, message.Answers.Single().Data);
    }
}

public sealed class QrCodeTests
{
    [Fact]
    public void PicksTheSmallestVersionAndDrawsFinderPatterns()
    {
        Assert.Equal(21, QrCode.Encode("hello").Size); // version 1
        // A worst-case sharing link (64 bytes) needs version 5 at level M.
        var link = QrCode.Encode("http://192.168.100.200:65535/?t=0123456789abcdef0123456789abcdef");
        Assert.Equal(37, link.Size);
        foreach (var (x, y) in new[] { (0, 0), (link.Size - 7, 0), (0, link.Size - 7) })
        {
            Assert.True(link[x, y] && link[x + 6, y + 6] && link[x + 3, y + 3]);
            Assert.False(link[x + 1, y + 1]);
        }
        Assert.True(link[8, link.Size - 8]); // the always-dark module
    }

    [Fact]
    public void WritesSampleForManualDecoding()
    {
        // Saved for a decoder check outside the test run (see windows/README.md).
        using var bitmap = QrCode.Encode("http://192.168.1.101:54321/?t=0123456789abcdef0123456789abcdef").ToBitmap(6);
        bitmap.Save(Path.Combine(Path.GetTempPath(), "suyanshot-qr-sample.png"), ImageFormat.Png);
    }
}
