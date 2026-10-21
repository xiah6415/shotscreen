using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace SuYanShot.ClipboardHistory;

/// Tiny HTTP/1.1 server (one request per connection) used to show clipboard history on a phone on the same network.
/// Every request must carry the session token; connections from outside private address ranges are refused.
/// A raw TcpListener rather than HttpListener, because HttpListener needs administrator rights to listen beyond localhost.
internal sealed class PhoneShareServer
{
    internal sealed record Request(string Method, string Path, IReadOnlyDictionary<string, string> Query,
                                   IReadOnlyDictionary<string, string> Headers, byte[] Body);

    internal sealed record Response(int Status, string ContentType, byte[] Body)
    {
        public static Response Text(string text, int status = 200, string type = "text/plain; charset=utf-8") =>
            new(status, type, Encoding.UTF8.GetBytes(text));
    }

    public enum State { Stopped, Running, Failed }

    private const int MaxRequestSize = 40 * 1024 * 1024;

    private readonly Func<Request, Task<Response>> handler;
    private SynchronizationContext ui = new();
    private TcpListener? listener;
    private CancellationTokenSource? cancel;

    public State Status { get; private set; } = State.Stopped;
    public int Port { get; private set; }
    public string Token { get; private set; } = "";
    public string? Failure { get; private set; }

    /// Raised on the UI thread.
    public event Action? StateChanged;

    /// Raised on the UI thread for every authenticated request.
    public event Action? Activity;

    /// <param name="handler">Runs on the UI thread, so it may use the clipboard store directly.</param>
    public PhoneShareServer(Func<Request, Task<Response>> handler)
    {
        this.handler = handler;
    }

    /// Call on the UI thread; request handling is marshalled back to it.
    public void Start()
    {
        Stop();
        ui = SynchronizationContext.Current ?? ui;
        Token = MakeToken();
        try
        {
            listener = new TcpListener(IPAddress.Any, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            cancel = new CancellationTokenSource();
            _ = AcceptLoop(listener, cancel.Token);
            Status = State.Running;
            Failure = null;
        }
        catch (SocketException ex)
        {
            Status = State.Failed;
            Failure = ex.Message;
            listener = null;
        }
        StateChanged?.Invoke();
    }

    public void Stop()
    {
        cancel?.Cancel();
        listener?.Stop();
        listener = null;
        if (Status == State.Stopped) return;
        Status = State.Stopped;
        StateChanged?.Invoke();
    }

    /// Invalidates links already handed out.
    public void RegenerateToken()
    {
        Token = MakeToken();
        StateChanged?.Invoke();
    }

    // MARK: Connections

    private async Task AcceptLoop(TcpListener server, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await server.AcceptTcpClientAsync(token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = Serve(client, token);
        }
    }

    private async Task Serve(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            if (client.Client.RemoteEndPoint is not IPEndPoint remote || !IsLocal(remote.Address)) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var stream = client.GetStream();
            Response response;
            try
            {
                response = await ReadRequest(stream, timeout.Token) switch
                {
                    { Error: Response error } => error,
                    { Request: Request request } => await OnUiThread(() => Route(request)),
                    _ => Response.Text("Bad Request", 400),
                };
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                return;
            }
            try
            {
                await Send(stream, response, timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
            }
        }
    }

    private Task<Response> Route(Request request)
    {
        var supplied = request.Query.GetValueOrDefault("t") ?? request.Headers.GetValueOrDefault("x-token") ?? "";
        if (Token.Length == 0 || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(Token)))
        {
            return Task.FromResult(Response.Text(
                "<!doctype html><meta name=viewport content='width=device-width'><body style='font:17px -apple-system,sans-serif;padding:40px;text-align:center'>連結已失效，請在電腦上重新掃描 QR code。",
                403, "text/html; charset=utf-8"));
        }
        Activity?.Invoke();
        return handler(request);
    }

    private Task<Response> OnUiThread(Func<Task<Response>> work)
    {
        var completion = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.Post(async _ =>
        {
            try { completion.SetResult(await work()); }
            catch (Exception ex) { completion.SetResult(Response.Text("Internal Server Error: " + ex.Message, 500)); }
        }, null);
        return completion.Task;
    }

    private static async Task Send(NetworkStream stream, Response response, CancellationToken token)
    {
        var reason = response.Status switch
        {
            200 => "OK", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", 413 => "Payload Too Large", _ => "Internal Server Error",
        };
        var head = $"HTTP/1.1 {response.Status} {reason}\r\nContent-Type: {response.ContentType}\r\nContent-Length: {response.Body.Length}\r\n" +
                   "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), token);
        await stream.WriteAsync(response.Body, token);
    }

    // MARK: Parsing

    internal readonly record struct Parsed(Request? Request, Response? Error);

    private static async Task<Parsed> ReadRequest(NetworkStream stream, CancellationToken token)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, token);
            if (read == 0) return default;
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxRequestSize) return new Parsed(null, Response.Text("太大了", 413));
            var result = Parse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
            if (result is { } done) return done;
        }
    }

    /// Null while the request is still incomplete.
    internal static Parsed? Parse(ReadOnlySpan<byte> data)
    {
        var headerEnd = data.IndexOf("\r\n\r\n"u8);
        if (headerEnd < 0) return null;
        var lines = Encoding.UTF8.GetString(data[..headerEnd]).Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2) return new Parsed(null, Response.Text("Bad Request", 400));
        var headers = new Dictionary<string, string>();
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
        }
        var length = int.TryParse(headers.GetValueOrDefault("content-length"), out var n) ? n : 0;
        var bodyStart = headerEnd + 4;
        if (length < 0 || data.Length - bodyStart < length) return length < 0 ? new Parsed(null, Response.Text("Bad Request", 400)) : null;
        if (!Uri.TryCreate("http://local" + requestLine[1], UriKind.Absolute, out var uri)) return new Parsed(null, Response.Text("Bad Request", 400));
        var query = new Dictionary<string, string>();
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
            query[key] = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
        }
        return new Parsed(new Request(requestLine[0], Uri.UnescapeDataString(uri.AbsolutePath), query, headers,
                                      data.Slice(bodyStart, length).ToArray()), null);
    }

    // MARK: Network helpers

    private static string MakeToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// Loopback, link-local and private (RFC 1918 / ULA) addresses only.
    internal static bool IsLocal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork) return IsPrivate(address.GetAddressBytes());
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;
        var bytes = address.GetAddressBytes();
        return IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || (bytes[0] & 0xFE) == 0xFC;
    }

    private static bool IsPrivate(byte[] b) =>
        b.Length == 4 && (b[0] == 10 || b[0] == 127 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254));

    private static readonly string[] VirtualAdapterHints = { "virtual", "hyper-v", "vmware", "virtualbox", "vethernet", "wsl", "bluetooth", "loopback", "tap-", "tailscale", "zerotier" };

    /// This PC's IPv4 address on Wi-Fi/Ethernet that a phone on the same network can reach.
    /// Prefers real adapters with a default gateway over virtual ones (Hyper-V, WSL, VPNs).
    public static string? LocalIPv4Address()
    {
        var candidates = new List<(string Address, int Score)>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up) continue;
            if (adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var properties = adapter.GetIPProperties();
            var label = (adapter.Name + " " + adapter.Description).ToLowerInvariant();
            var isVirtual = VirtualAdapterHints.Any(label.Contains);
            var hasGateway = properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var bytes = unicast.Address.GetAddressBytes();
                if (!IsPrivate(bytes) || bytes[0] is 127 or 169) continue;
                var score = (hasGateway ? 4 : 0) + (isVirtual ? 0 : 2) + (adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 1 : 0);
                candidates.Add((unicast.Address.ToString(), score));
            }
        }
        return candidates.OrderByDescending(c => c.Score).Select(c => c.Address).FirstOrDefault();
    }
}
