namespace NetShare.Linux.Core;

public static class NetShareProtocol
{
    public const string ProtocolVersion = "1.0";

    public const int DefaultDiscoveryPort = 40123;
    public const int DefaultTcpPort = 40124;

    // Implementation-truth: 2000ms announce
    public const int DiscoveryAnnounceIntervalMs = 2000;

    // Implementation-truth: 7000ms offline
    public const int PeerOfflineAfterMs = 7000;

    public const int DefaultSocketTimeoutMs = 30_000;

    public const int DefaultChunkSize = 256 * 1024;
}
