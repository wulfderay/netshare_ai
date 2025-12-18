namespace NetShare.Core.Protocol
{
    public static class NetShareProtocol
    {
        public const string ProtocolVersion = "1.0";

        public const int DefaultDiscoveryPort = 40123;
        public const int DefaultTcpPort = 40124;

        public const int DiscoveryAnnounceIntervalMs = 2000;
        public const int PeerOfflineAfterMs = 7000;

        public const int DefaultChunkSize = 64 * 1024;
        public const int DefaultSocketTimeoutMs = 15000;
    }
}
