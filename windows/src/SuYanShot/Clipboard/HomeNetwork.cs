using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace SuYanShot.ClipboardHistory;

/// Identifies the network this PC is on by its router, so phone sharing can run only on networks the user allowed
/// (as on macOS). Uses the router's MAC address when ARP can find it, because router IPs like 192.168.1.1 repeat across
/// many networks; this needs no location permission, unlike reading the Wi-Fi name.
internal sealed record NetworkIdentity(string Id, string RouterIP, string Label)
{
    /// The network the phone-facing address is on, or null when offline.
    public static NetworkIdentity? Current() => LocalNetwork.Find() is { Gateway: string router } ? ForRouter(router) : null;

    public static NetworkIdentity ForRouter(string router)
    {
        var mac = RouterMac(router);
        return new NetworkIdentity(mac ?? "ip:" + router, router, "路由器 " + router);
    }

    private static string? RouterMac(string ip)
    {
        if (!IPAddress.TryParse(ip, out var address) || address.AddressFamily != AddressFamily.InterNetwork) return null;
        var mac = new byte[6];
        var length = mac.Length;
        if (SendARP(BitConverter.ToUInt32(address.GetAddressBytes(), 0), 0, mac, ref length) != 0 || length != 6) return null;
        return string.Join(":", mac.Select(b => b.ToString("x2")));
    }

    [DllImport("iphlpapi.dll")]
    private static extern int SendARP(uint destIP, uint srcIP, byte[] macAddress, ref int physicalAddressLength);
}

/// This PC's IPv4 address that a phone on the same network can reach, and that network's router.
internal sealed record LocalNetwork(string Address, string? Gateway)
{
    private static readonly string[] VirtualAdapterHints =
        { "virtual", "hyper-v", "vmware", "virtualbox", "vethernet", "wsl", "bluetooth", "loopback", "tap-", "tailscale", "zerotier" };

    /// Prefers real Wi-Fi/Ethernet adapters with a default gateway over virtual ones (Hyper-V, WSL, VPNs).
    public static LocalNetwork? Find()
    {
        var candidates = new List<(LocalNetwork Network, int Score)>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up) continue;
            if (adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var properties = adapter.GetIPProperties();
            var label = (adapter.Name + " " + adapter.Description).ToLowerInvariant();
            var isVirtual = VirtualAdapterHints.Any(label.Contains);
            var gateway = properties.GatewayAddresses
                .Select(g => g.Address)
                .FirstOrDefault(g => g.AddressFamily == AddressFamily.InterNetwork && !g.Equals(IPAddress.Any))?.ToString();
            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var bytes = unicast.Address.GetAddressBytes();
                if (!PhoneShareServer.IsPrivate(bytes) || bytes[0] is 127 or 169) continue;
                var score = (gateway != null ? 4 : 0) + (isVirtual ? 0 : 2) + (adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 1 : 0);
                candidates.Add((new LocalNetwork(unicast.Address.ToString(), gateway), score));
            }
        }
        return candidates.OrderByDescending(c => c.Score).Select(c => c.Network).FirstOrDefault();
    }
}

/// Raises <see cref="Changed"/> on the UI thread when the PC's network changes (Wi-Fi switch, cable, sleep/wake),
/// after a short pause so DHCP and ARP can settle.
internal sealed class NetworkChangeMonitor : IDisposable
{
    private readonly SynchronizationContext ui;
    private System.Threading.Timer? settle;

    public event Action? Changed;

    public NetworkChangeMonitor()
    {
        ui = SynchronizationContext.Current ?? new SynchronizationContext();
        NetworkChange.NetworkAddressChanged += OnChange;
        NetworkChange.NetworkAvailabilityChanged += OnChange;
    }

    private void OnChange(object? sender, EventArgs e)
    {
        settle?.Dispose();
        settle = new System.Threading.Timer(_ => ui.Post(_ => Changed?.Invoke(), null), null, 2000, Timeout.Infinite);
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnChange;
        NetworkChange.NetworkAvailabilityChanged -= OnChange;
        settle?.Dispose();
    }
}
