using System.Net;
using NetShare.Linux.Core.Discovery;
using NetShare.Linux.Core.Networking;
using NetShare.Linux.Core.Settings;
using NetShare.Linux.Core.Sharing;
using System.Threading;

namespace NetShare.Linux.Core;

/// <summary>
/// Owns the long-running background services (UDP discovery, TCP server) and exposes peer tracking.
/// </summary>
public sealed class AppHost : IDisposable
{
    private readonly SettingsStore _store;

    public AppSettings Settings { get; private set; }
    public ShareManager Shares { get; private set; }

    public DiscoveryService Discovery { get; } = new();
    public PeerServer Server { get; private set; }

    private readonly object _gate = new();
    private readonly Dictionary<string, PeerInfo> _peersById = new();

    public event Action? PeersChanged;

    private SynchronizationContext? _peersChangedContext;

    public AppHost(SettingsStore store)
    {
        _store = store;
        Settings = _store.LoadOrCreateDefault();
        Shares = new ShareManager(Settings.Shares);
        Server = new PeerServer(Shares, Settings);
    }

    public void Start()
    {
        // Capture the current context (UI thread if Start() is called from UI).
        _peersChangedContext = SynchronizationContext.Current;

        Directory.CreateDirectory(Settings.DownloadDirectory);

        Server.Start(Settings.TcpPort);

        // Diagnostics are helpful on Linux where broadcast/interface behavior may vary.
        Discovery.EnableConsoleDiagnostics = true;

        Discovery.OnMessage += OnDiscovery;
        Discovery.Start(Settings.DiscoveryPort, BuildAnnounce, enableAnnounce: true);

        Discovery.SendQuery();
    }

    private DiscoveryMessage BuildAnnounce()
    {
        return new DiscoveryMessage
        {
            proto = NetShareProtocol.ProtocolVersion,
            type = "DISCOVERY_ANNOUNCE",
            deviceId = Settings.DeviceId,
            deviceName = Settings.DeviceName,
            tcpPort = Settings.TcpPort,
            discoveryPort = Settings.DiscoveryPort,
            timestampUtc = DateTime.UtcNow.ToString("o"),
            cap = new DiscoveryCap
            {
                auth = new[] { "open", "psk-hmac-sha256" },
                resume = true
            }
        };
    }

    private void OnDiscovery(IPEndPoint ep, DiscoveryMessage msg)
    {
        if (msg.deviceId is null || string.IsNullOrWhiteSpace(msg.deviceId))
        {
            try { Console.Error.WriteLine($"[AppHost] Ignored discovery from {ep.Address}: missing deviceId (type={msg.type})"); } catch { }
            return;
        }

        if (string.Equals(msg.deviceId, Settings.DeviceId, StringComparison.OrdinalIgnoreCase))
        {
            // self
            return;
        }

        var p = new PeerInfo
        {
            DeviceId = msg.deviceId,
            DeviceName = msg.deviceName ?? msg.deviceId,
            Address = ep.Address,
            TcpPort = msg.tcpPort,
            DiscoveryPort = msg.discoveryPort,
            LastSeenUtc = DateTime.UtcNow
        };

        lock (_gate)
        {
            _peersById[p.DeviceId] = p;
        }

        try { Console.Error.WriteLine($"[AppHost] Peer seen: {p.DeviceName} {p.Address}:{p.TcpPort} id={p.DeviceId}"); } catch { }

        var ctx = _peersChangedContext;
        if (ctx != null)
        {
            ctx.Post(_ =>
            {
                try { PeersChanged?.Invoke(); } catch { }
            }, null);
        }
        else
        {
            PeersChanged?.Invoke();
        }
    }

    public IReadOnlyList<PeerInfo> GetPeersSnapshot()
    {
        lock (_gate)
        {
            // prune offline? keep but set status via Online property.
            return _peersById.Values.OrderByDescending(p => p.LastSeenUtc).ToList();
        }
    }

    public void SaveSettings()
    {
        Settings.Shares = Shares.GetShares().ToList();
        _store.Save(Settings);
    }

    public void Dispose()
    {
        try { Discovery.Dispose(); } catch { }
        try { Server.Dispose(); } catch { }
    }
}
