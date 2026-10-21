using System.Drawing.Imaging;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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

/// Drives the real server over loopback, like the curl checks done for the macOS version.
public sealed class PhoneShareRoutesTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "SuYanShotTests", Guid.NewGuid().ToString("N"));
    private readonly List<ClipboardItem> putOnClipboard = new();

    [Fact]
    public async Task RoutesWorkAndNeedTheToken()
    {
        var store = new ClipboardStore(folder);
        store.Add(new ClipboardSnapshot(ClipboardKind.Text) { Text = "來自電腦" });
        var share = new PhoneShare(store, putOnClipboard.Add);
        share.Start();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{share.Port}") };
            var t = "t=" + share.Token;

            Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/?t=wrong")).StatusCode);

            var page = await http.GetStringAsync("/?" + t);
            Assert.Contains("傳文字到電腦", page);

            var list = JsonDocument.Parse(await http.GetStringAsync("/api/items?" + t)).RootElement;
            Assert.False(list.GetProperty("unchanged").GetBoolean());
            Assert.Equal("來自電腦", list.GetProperty("items")[0].GetProperty("text").GetString());
            Assert.Equal("text", list.GetProperty("items")[0].GetProperty("kind").GetString());
            var version = list.GetProperty("version").GetString();
            var again = JsonDocument.Parse(await http.GetStringAsync($"/api/items?v={version}&{t}")).RootElement;
            Assert.True(again.GetProperty("unchanged").GetBoolean());

            var sent = await http.PostAsync("/api/send?" + t, new StringContent("手機傳來的字", Encoding.UTF8, "text/plain"));
            Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
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
            Assert.Equal(HttpStatusCode.OK, (await http.PostAsync("/api/send?" + t, upload)).StatusCode);
            var image = store.Items[0];
            Assert.Equal(ClipboardKind.Image, image.Kind);
            Assert.Equal(900, image.ImageWidth);

            var thumb = await http.GetAsync($"/api/items/{image.Id}/image?thumb=1&{t}");
            Assert.Equal("image/jpeg", thumb.Content.Headers.ContentType!.MediaType);
            var full = await http.GetAsync($"/api/items/{image.Id}/image?{t}");
            Assert.Equal("image/png", full.Content.Headers.ContentType!.MediaType);

            var older = store.Items.Last();
            Assert.Equal(HttpStatusCode.OK, (await http.PostAsync($"/api/items/{older.Id}/to-mac?{t}", null)).StatusCode);
            Assert.Equal(older.Id, store.Items[0].Id);

            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/items/{Guid.NewGuid()}/image?{t}")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync("/api/send?" + t, new StringContent("  "))).StatusCode);

            var oldToken = share.Token;
            share.RegenerateLink();
            Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/?t=" + oldToken)).StatusCode);
        }
        finally
        {
            share.Stop();
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
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
