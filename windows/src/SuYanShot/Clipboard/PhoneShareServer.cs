using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SuYanShot.ClipboardHistory;

/// Tiny HTTP/1.1 server (one request per connection) used to show clipboard history on a phone on the same network.
/// Connections from outside private address ranges are refused; authentication is up to the handler (as on macOS).
/// A raw TcpListener rather than HttpListener, because HttpListener needs administrator rights to listen beyond localhost.
internal sealed class PhoneShareServer
{
    internal sealed record Request(string Method, string Path, IReadOnlyDictionary<string, string> Query,
                                   IReadOnlyDictionary<string, string> Headers, byte[] Body)
    {
        /// Client IP, for login rate limiting.
        public string RemoteAddress { get; init; } = "?";

        public IReadOnlyDictionary<string, string> Cookies
        {
            get
            {
                var result = new Dictionary<string, string>();
                foreach (var part in (Headers.GetValueOrDefault("cookie") ?? "").Split(';'))
                {
                    var eq = part.IndexOf('=');
                    if (eq > 0) result[part[..eq].Trim()] = part[(eq + 1)..].Trim();
                }
                return result;
            }
        }
    }

    internal sealed record Response(int Status, string ContentType, byte[] Body)
    {
        public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

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
    public string? Failure { get; private set; }
    public bool FailedBecausePortInUse { get; private set; }

    /// Raised on the UI thread.
    public event Action? StateChanged;

    /// <param name="handler">Runs on the UI thread, so it may use the clipboard store directly.</param>
    public PhoneShareServer(Func<Request, Task<Response>> handler)
    {
        this.handler = handler;
    }

    /// Call on the UI thread; request handling is marshalled back to it.
    /// Listens on a fixed port so bookmarked / Home Screen links keep working (0 picks a free one, for tests).
    public void Start(int port)
    {
        Stop();
        ui = SynchronizationContext.Current ?? ui;
        try
        {
            listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            cancel = new CancellationTokenSource();
            _ = AcceptLoop(listener, cancel.Token);
            Status = State.Running;
            Failure = null;
            FailedBecausePortInUse = false;
        }
        catch (SocketException ex)
        {
            Status = State.Failed;
            Failure = ex.Message;
            FailedBecausePortInUse = ex.SocketErrorCode == SocketError.AddressAlreadyInUse;
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
            var remoteAddress = (remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address).ToString();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var stream = client.GetStream();
            Response response;
            try
            {
                response = await ReadRequest(stream, timeout.Token) switch
                {
                    { Error: Response error } => error,
                    { Request: Request request } => await OnUiThread(() => handler(request with { RemoteAddress = remoteAddress })),
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
            200 => "OK", 303 => "See Other", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found",
            413 => "Payload Too Large", 429 => "Too Many Requests", _ => "Internal Server Error",
        };
        var head = new StringBuilder();
        head.Append($"HTTP/1.1 {response.Status} {reason}\r\n");
        head.Append($"Content-Type: {response.ContentType}\r\nContent-Length: {response.Body.Length}\r\n");
        head.Append("Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nX-Frame-Options: DENY\r\n");
        foreach (var (name, value) in response.Headers) head.Append($"{name}: {value}\r\n");
        head.Append("Connection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.UTF8.GetBytes(head.ToString()), token);
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

    /// Loopback, link-local and private (RFC 1918 / ULA) addresses only.
    internal static bool IsLocal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork) return IsPrivate(address.GetAddressBytes());
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;
        var bytes = address.GetAddressBytes();
        return IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || (bytes[0] & 0xFE) == 0xFC;
    }

    internal static bool IsPrivate(byte[] b) =>
        b.Length == 4 && (b[0] == 10 || b[0] == 127 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254));
}
