using System;
using System.Net;

namespace NetShare.Core.Networking
{
    public sealed class PeerInfo
    {
        public string DeviceId { get; set; }
        public string DeviceName { get; set; }
        public IPAddress Address { get; set; }
        public int TcpPort { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public bool Online { get; set; }

        public override string ToString()
        {
            return DeviceName + " (" + Address + ")";
        }
    }
}
