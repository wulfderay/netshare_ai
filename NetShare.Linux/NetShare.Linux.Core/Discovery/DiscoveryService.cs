using System.Net;
using System.Net.Sockets;
using System.Text;
using NetShare.Linux.Core.Protocol;

namespace NetShare.Linux.Core.Discovery;

public sealed class DiscoveryService : IDisposable
{
    private readonly JsonCodec _json = new();
    private readonly object _gate = new();

    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _listener;
    private Task? _announcer;

    private Func<DiscoveryMessage>? _announceFactory;
    private bool _respondToQueries;

    private DateTime _lastListenerErrorUtc;
    private DateTime _lastAnnounceErrorUtc;

    /// <summary>
    /// When enabled, writes basic discovery diagnostics to stderr. Intended for troubleshooting.
    /// </summary>
    public bool EnableConsoleDiagnostics { get; set; }

    private DateTime _lastDiagUtc;

    private void Diag(string message)
    {
        if (!EnableConsoleDiagnostics) return;

        // Throttle to avoid spamming if the LAN is noisy.
        if ((DateTime.UtcNow - _lastDiagUtc).TotalMilliseconds < 200) return;
        _lastDiagUtc = DateTime.UtcNow;

        try
        {
            Console.Error.WriteLine(message);
        }
        catch { }
    }

    public int Port { get; private set; }

    public event Action<IPEndPoint, DiscoveryMessage>? OnMessage;

    public void Start(int port, Func<DiscoveryMessage>? announceFactory, bool enableAnnounce = true, IPAddress? bindAddress = null, IPAddress? broadcastAddress = null)
    {
        lock (_gate)
        {
            if (_cts != null) throw new InvalidOperationException("Discovery already started.");
            Port = port;
            _announceFactory = announceFactory;
            _respondToQueries = enableAnnounce;

            _cts = new CancellationTokenSource();
            _udp = new UdpClient();
            _udp.EnableBroadcast = true;
            _udp.ExclusiveAddressUse = false;
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(bindAddress ?? IPAddress.Any, port));

            _listener = Task.Run(() => ListenerLoop(_cts.Token));
            _announcer = enableAnnounce && announceFactory != null
                ? Task.Run(() => AnnounceLoop(announceFactory, broadcastAddress ?? IPAddress.Broadcast, _cts.Token))
                : null;
        }
    }

    public void SendQuery(IPAddress? broadcastAddress = null)
    {
        if (_udp is null) return;
        var msg = new Dictionary<string, object?>
        {
            { "proto", NetShareProtocol.ProtocolVersion },
            { "type", "DISCOVERY_QUERY" },
            { "timestampUtc", DateTime.UtcNow.ToString("o") }
        };

        var bytes = _json.Encode(msg);
        var ip = broadcastAddress ?? IPAddress.Broadcast;
        _udp.Send(bytes, bytes.Length, new IPEndPoint(ip, Port));
    }

    private void AnnounceLoop(Func<DiscoveryMessage> announceFactory, IPAddress broadcast, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_udp is null) return;
                var msg = announceFactory();
                var bytes = _json.Encode(msg);
                _udp.Send(bytes, bytes.Length, new IPEndPoint(broadcast, Port));
            }
            catch (Exception ex)
            {
                if ((DateTime.UtcNow - _lastAnnounceErrorUtc).TotalSeconds >= 30)
                {
                    _lastAnnounceErrorUtc = DateTime.UtcNow;
                    Console.Error.WriteLine($"[Discovery] Announce error: {ex.Message}");
                }
            }

            ct.WaitHandle.WaitOne(NetShareProtocol.DiscoveryAnnounceIntervalMs);
        }
    }

    private void ListenerLoop(CancellationToken ct)
    {
        if (_udp is null) return;

        var ep = new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var bytes = _udp.Receive(ref ep);

                DiscoveryMessage? decoded;
                try
                {
                    decoded = _json.Decode<DiscoveryMessage>(bytes);
                }
                catch (Exception ex)
                {
                    decoded = null;
                    var preview = SafeUtf8Preview(bytes, 256);
                    Diag($"[Discovery] Decode exception from {ep.Address}: {ex.Message}. PayloadPreview=\"{preview}\"");
                }

                if (decoded is null)
                {
                    var preview = SafeUtf8Preview(bytes, 256);
                    Diag($"[Discovery] Dropped undecodable packet from {ep.Address}. Bytes={bytes.Length} Preview=\"{preview}\"");
                    continue;
                }

                Diag($"[Discovery] Recv {decoded.type} from {ep.Address} id={(decoded.deviceId ?? "")} name={(decoded.deviceName ?? "")} tcp={decoded.tcpPort} dport={decoded.discoveryPort} proto={(decoded.proto ?? "")} ");

                OnMessage?.Invoke(ep, decoded);

                if (_respondToQueries && string.Equals(decoded.type, "DISCOVERY_QUERY", StringComparison.OrdinalIgnoreCase))
                {
                    if (_announceFactory != null)
                    {
                        var resp = _announceFactory();
                        resp.type = "DISCOVERY_RESPONSE";
                        var respBytes = _json.Encode(resp);
                        _udp.Send(respBytes, respBytes.Length, ep);
                    }
                }
            }
            catch (SocketException)
            {
                // ignore transient
            }
            catch (Exception ex)
            {
                if ((DateTime.UtcNow - _lastListenerErrorUtc).TotalSeconds >= 30)
                {
                    _lastListenerErrorUtc = DateTime.UtcNow;
                    Console.Error.WriteLine($"[Discovery] Listener error: {ex.Message}");
                }
            }
        }
    }

    private static string SafeUtf8Preview(byte[] bytes, int maxChars)
    {
        try
        {
            var s = Encoding.UTF8.GetString(bytes);
            s = s.Replace("\r", "\\r").Replace("\n", "\\n");
            if (s.Length <= maxChars) return s;
            return s.Substring(0, maxChars) + "\u2026";
        }
        catch
        {
            return "<non-utf8>";
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_cts is null) return;
            _cts.Cancel();
            try { _udp?.Close(); } catch { }
            try { _udp?.Dispose(); } catch { }
            _udp = null;
            _cts.Dispose();
            _cts = null;
        }
    }
}
