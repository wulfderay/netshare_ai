using System.Net;

namespace NetShare.Linux.Core.Networking;

public sealed class PeerInfo
{
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public IPAddress Address { get; set; } = IPAddress.Loopback;

    public int TcpPort { get; set; }
    public int DiscoveryPort { get; set; }

    public DateTime LastSeenUtc { get; set; }

    public bool Online => (DateTime.UtcNow - LastSeenUtc).TotalMilliseconds <= NetShareProtocol.PeerOfflineAfterMs;
}
