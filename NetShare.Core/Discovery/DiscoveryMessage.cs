using System;
using System.Collections.Generic;

namespace NetShare.Core.Discovery
{
    public sealed class DiscoveryMessage
    {
        public string proto { get; set; }
        public string type { get; set; }
        public string deviceId { get; set; }
        public string deviceName { get; set; }
        public int tcpPort { get; set; }
        public int discoveryPort { get; set; }
        public string timestampUtc { get; set; }
        public Dictionary<string, object> cap { get; set; }

        public static DiscoveryMessage CreateAnnounce(string proto, string deviceId, string deviceName, int tcpPort, int discoveryPort)
        {
            return new DiscoveryMessage
            {
                proto = proto,
                type = "DISCOVERY_ANNOUNCE",
                deviceId = deviceId,
                deviceName = deviceName,
                tcpPort = tcpPort,
                discoveryPort = discoveryPort,
                timestampUtc = DateTime.UtcNow.ToString("o"),
                cap = new Dictionary<string, object>
                {
                    { "auth", new[] { "open", "psk-hmac-sha256" } },
                    { "resume", true }
                }
            };
        }
    }
}
