using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SuYanShot.ClipboardHistory;

/// Publishes `<name>.local` → this PC's LAN address over multicast DNS, so phones can always open
/// `http://glean.local:8765` whichever computer is running 拾光 (the macOS app does the same through Bonjour).
/// Windows has no public API for publishing a bare host name, so this is a small RFC 6762 responder: it probes the
/// name first and, if another device already answers for it, retries as `<name>-2.local`, `<name>-3.local`, …
internal sealed class LocalNameAdvertiser : IDisposable
{
    private const int MdnsPort = 5353;
    private const ushort TypeA = 1, TypeAny = 255, ClassIn = 1;
    private const uint Ttl = 120;
    private static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");

    private readonly SynchronizationContext ui;
    private Socket? socket;
    private CancellationTokenSource? cancel;
    private string baseName = "glean";
    private int attempt = 1;
    private IPAddress? address;
    private volatile string? answering;   // host name we answer for, once probing succeeded
    private volatile string? probing;     // host name being probed
    private volatile bool conflict;

    /// Host name currently published (e.g. "glean.local"), or null.
    public string? PublishedName { get; private set; }

    /// Raised on the UI thread when <see cref="PublishedName"/> changes.
    public event Action<string?>? Changed;

    /// Why publishing failed (for example, port 5353 could not be opened), or null.
    public string? Problem { get; private set; }

    public LocalNameAdvertiser()
    {
        ui = SynchronizationContext.Current ?? new SynchronizationContext();
    }

    /// (Re)publishes <paramref name="name"/> for <paramref name="ipv4"/>. Does nothing if already published as asked.
    public void Publish(string name, string ipv4)
    {
        if (name == baseName && ipv4 == address?.ToString() && (socket != null)) return;
        if (name != baseName) attempt = 1;
        baseName = name;
        address = IPAddress.Parse(ipv4);
        Restart();
    }

    public void Stop()
    {
        if (socket != null && answering != null) TrySend(BuildResponse(answering, address!, ttl: 0, id: 0), new IPEndPoint(Group, MdnsPort)); // goodbye
        cancel?.Cancel();
        socket?.Dispose();
        socket = null;
        answering = probing = null;
        SetPublished(null);
    }

    private string Candidate => (attempt == 1 ? baseName : $"{baseName}-{attempt}") + ".local";

    private void Restart()
    {
        Stop();
        Problem = null;
        try
        {
            socket = OpenSocket(address!);
        }
        catch (SocketException ex)
        {
            Problem = ex.Message;
            socket = null;
            return;
        }
        cancel = new CancellationTokenSource();
        _ = ReceiveLoop(socket, cancel.Token);
        _ = Probe(Candidate, cancel.Token);
    }

    private static Socket OpenSocket(IPAddress local)
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        // Windows' own mDNS service already listens on 5353; sharing the port lets both receive multicast queries.
        s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        s.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
        s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group, local));
        s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
        s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
        s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
        return s;
    }

    /// Three queries 250 ms apart; any answer for the name from someone else means it is taken (RFC 6762 §8.1).
    private async Task Probe(string name, CancellationToken token)
    {
        probing = name;
        conflict = false;
        try
        {
            for (var i = 0; i < 3 && !conflict; i++)
            {
                TrySend(BuildQuery(name), new IPEndPoint(Group, MdnsPort));
                await Task.Delay(250, token);
            }
            await Task.Delay(250, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        probing = null;
        if (conflict)
        {
            if (attempt >= 9) { ui.Post(_ => Stop(), null); return; }
            attempt++;
            ui.Post(_ => Restart(), null);
            return;
        }
        answering = name;
        ui.Post(_ => SetPublished(name), null);
        // Announce twice, a second apart, so caches pick the name up right away.
        for (var i = 0; i < 2 && !token.IsCancellationRequested; i++)
        {
            TrySend(BuildResponse(name, address!, Ttl, id: 0), new IPEndPoint(Group, MdnsPort));
            try { await Task.Delay(1000, token); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task ReceiveLoop(Socket s, CancellationToken token)
    {
        var buffer = new byte[9000];
        while (!token.IsCancellationRequested)
        {
            SocketReceiveFromResult received;
            try
            {
                received = await s.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            var from = (IPEndPoint)received.RemoteEndPoint;
            try
            {
                Handle(buffer, received.ReceivedBytes, from);
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or FormatException)
            {
                // Malformed packet; ignore.
            }
        }
    }

    private void Handle(byte[] buffer, int length, IPEndPoint from)
    {
        var parsed = DnsMessage.Parse(buffer.AsSpan(0, length));
        if (!parsed.IsResponse)
        {
            if (answering is not string name || address == null) return;
            foreach (var q in parsed.Questions)
            {
                if (!q.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || (q.Type != TypeA && q.Type != TypeAny) || (q.Class & 0x7FFF) != ClassIn) continue;
                // Legacy (non-5353) queriers and "unicast response" questions get a direct reply; everyone else, multicast.
                var unicast = from.Port != MdnsPort || (q.Class & 0x8000) != 0;
                var reply = BuildResponse(name, address, Ttl, id: from.Port != MdnsPort ? parsed.Id : (ushort)0,
                                          echoQuestion: from.Port != MdnsPort ? q : null);
                TrySend(reply, unicast ? from : new IPEndPoint(Group, MdnsPort));
                return;
            }
            return;
        }
        if (probing is string probed && address != null)
        {
            foreach (var answer in parsed.Answers)
                if (answer.Name.Equals(probed, StringComparison.OrdinalIgnoreCase) && answer.Type == TypeA && !answer.Data.AsSpan().SequenceEqual(address.GetAddressBytes()))
                    conflict = true;
        }
    }

    private void TrySend(byte[] packet, IPEndPoint to)
    {
        try { socket?.SendTo(packet, to); }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
    }

    private void SetPublished(string? name)
    {
        if (PublishedName == name) return;
        PublishedName = name;
        Changed?.Invoke(name);
    }

    public void Dispose() => Stop();

    // MARK: Wire format

    internal static byte[] BuildQuery(string name)
    {
        var packet = new List<byte>(64);
        WriteHeader(packet, id: 0, flags: 0, questions: 1, answers: 0);
        WriteName(packet, name);
        WriteUInt16(packet, TypeA);
        WriteUInt16(packet, ClassIn);
        return packet.ToArray();
    }

    internal static byte[] BuildResponse(string name, IPAddress ipv4, uint ttl, ushort id, DnsMessage.Question? echoQuestion = null)
    {
        var packet = new List<byte>(64);
        WriteHeader(packet, id, flags: 0x8400, questions: (ushort)(echoQuestion == null ? 0 : 1), answers: 1); // response, authoritative
        if (echoQuestion is DnsMessage.Question q)
        {
            WriteName(packet, q.Name);
            WriteUInt16(packet, q.Type);
            WriteUInt16(packet, (ushort)(q.Class & 0x7FFF));
        }
        WriteName(packet, name);
        WriteUInt16(packet, TypeA);
        WriteUInt16(packet, 0x8000 | ClassIn); // cache-flush: this host owns the name
        WriteUInt16(packet, (ushort)(ttl >> 16));
        WriteUInt16(packet, (ushort)ttl);
        WriteUInt16(packet, 4);
        packet.AddRange(ipv4.GetAddressBytes());
        return packet.ToArray();
    }

    private static void WriteHeader(List<byte> packet, ushort id, ushort flags, ushort questions, ushort answers)
    {
        WriteUInt16(packet, id);
        WriteUInt16(packet, flags);
        WriteUInt16(packet, questions);
        WriteUInt16(packet, answers);
        WriteUInt16(packet, 0);
        WriteUInt16(packet, 0);
    }

    private static void WriteName(List<byte> packet, string name)
    {
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            packet.Add((byte)bytes.Length);
            packet.AddRange(bytes);
        }
        packet.Add(0);
    }

    private static void WriteUInt16(List<byte> packet, ushort value)
    {
        packet.Add((byte)(value >> 8));
        packet.Add((byte)value);
    }

    /// Lowercase letters, digits and hyphens, as DNS labels allow (same rule as macOS).
    public static string Sanitize(string name)
    {
        var allowed = new string(name.ToLowerInvariant().Select(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' ? c : '-').ToArray()).Trim('-');
        return allowed.Length == 0 ? "glean" : allowed[..Math.Min(40, allowed.Length)];
    }
}

/// Just enough DNS message parsing for mDNS: questions and answer records, with name compression.
internal sealed class DnsMessage
{
    internal readonly record struct Question(string Name, ushort Type, ushort Class);
    internal readonly record struct Record(string Name, ushort Type, ushort Class, byte[] Data);

    public ushort Id { get; private init; }
    public bool IsResponse { get; private init; }
    public List<Question> Questions { get; } = new();
    public List<Record> Answers { get; } = new();

    public static DnsMessage Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12) throw new FormatException("short DNS header");
        var message = new DnsMessage
        {
            Id = BinaryPrimitives.ReadUInt16BigEndian(data),
            IsResponse = (data[2] & 0x80) != 0,
        };
        int questions = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
        int records = BinaryPrimitives.ReadUInt16BigEndian(data[6..]) + BinaryPrimitives.ReadUInt16BigEndian(data[8..]) + BinaryPrimitives.ReadUInt16BigEndian(data[10..]);
        var offset = 12;
        for (var i = 0; i < questions; i++)
        {
            var name = ReadName(data, ref offset);
            message.Questions.Add(new Question(name, BinaryPrimitives.ReadUInt16BigEndian(data[offset..]), BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..])));
            offset += 4;
        }
        for (var i = 0; i < records && offset < data.Length; i++)
        {
            var name = ReadName(data, ref offset);
            var type = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            var cls = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
            int length = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 8)..]);
            offset += 10;
            message.Answers.Add(new Record(name, type, cls, data.Slice(offset, length).ToArray()));
            offset += length;
        }
        return message;
    }

    private static string ReadName(ReadOnlySpan<byte> data, ref int offset)
    {
        var labels = new List<string>();
        var position = offset;
        var jumped = false;
        for (var hops = 0; hops < 64; hops++)
        {
            int length = data[position];
            if (length == 0)
            {
                if (!jumped) offset = position + 1;
                return string.Join(".", labels);
            }
            if ((length & 0xC0) == 0xC0)
            {
                var pointer = ((length & 0x3F) << 8) | data[position + 1];
                if (!jumped) offset = position + 2;
                jumped = true;
                position = pointer;
                continue;
            }
            labels.Add(Encoding.UTF8.GetString(data.Slice(position + 1, length)));
            position += length + 1;
        }
        throw new FormatException("DNS name loop");
    }
}
