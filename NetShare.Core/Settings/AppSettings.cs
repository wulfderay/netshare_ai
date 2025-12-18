using System;
using System.Collections.Generic;
using NetShare.Core.Protocol;

namespace NetShare.Core.Settings
{
    public sealed class ConfiguredShare
    {
        public string ShareId { get; set; }
        public string Name { get; set; }
        public string LocalPath { get; set; }
        public bool ReadOnly { get; set; }
    }

    public sealed class AppSettings
    {
        public string DeviceId { get; set; } = Guid.NewGuid().ToString();
        public string DeviceName { get; set; } = Environment.MachineName;

        public int DiscoveryPort { get; set; } = NetShareProtocol.DefaultDiscoveryPort;
        public int TcpPort { get; set; } = NetShareProtocol.DefaultTcpPort;

        // Optional: bind discovery to a specific NIC (NetworkInterface.Id). Empty = Auto.
        public string PreferredInterfaceId { get; set; } = "";

        public bool OpenMode { get; set; } = true;
        public string AccessKey { get; set; } = "";

        public bool EnableFileLogging { get; set; } = false;

        public string DownloadDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        // Local shares configured by the user. Missing paths remain listed but are not served.
        public List<ConfiguredShare> Shares { get; set; } = new List<ConfiguredShare>();
    }
}
