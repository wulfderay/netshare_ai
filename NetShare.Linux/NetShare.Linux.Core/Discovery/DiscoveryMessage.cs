namespace NetShare.Linux.Core.Discovery;

public sealed class DiscoveryMessage
{
    public string? proto { get; set; }
    public string? type { get; set; }

    public string? deviceId { get; set; }
    public string? deviceName { get; set; }

    public int tcpPort { get; set; }
    public int discoveryPort { get; set; }

    public string? timestampUtc { get; set; }

    public DiscoveryCap? cap { get; set; }
}

public sealed class DiscoveryCap
{
    public string[]? auth { get; set; }
    public bool resume { get; set; }
}
