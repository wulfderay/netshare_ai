namespace NetShare.Linux.Core.Settings;

public sealed class AppSettings
{
    public string DeviceId { get; set; } = Guid.NewGuid().ToString();
    public string DeviceName { get; set; } = Environment.MachineName;

    public int DiscoveryPort { get; set; } = NetShareProtocol.DefaultDiscoveryPort;
    public int TcpPort { get; set; } = NetShareProtocol.DefaultTcpPort;

    // Windows semantics: when OpenMode=true, server does not require AUTH.
    public bool OpenMode { get; set; } = true;

    // PSK
    public string? AccessKey { get; set; }

    public string DownloadDirectory { get; set; } = LinuxPaths.DefaultDownloadDir();

    public List<Sharing.ShareInfo> Shares { get; set; } = new();
}
