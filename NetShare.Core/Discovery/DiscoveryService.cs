using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NetShare.Core.Logging;
using NetShare.Core.Protocol;

namespace NetShare.Core.Discovery
{
    public sealed class DiscoveryService : IDisposable
    {
        private readonly JsonCodec _json = new JsonCodec();
        private readonly object _gate = new object();

        private DateTime _lastListenerErrorUtc;
        private DateTime _lastAnnounceErrorUtc;

        private Func<DiscoveryMessage> _announceFactory;

        private UdpClient _udp;
        private CancellationTokenSource _cts;
        private Task _listener;
        private Task _announcer;

        private IPAddress _broadcastAddress;

        public event Action<IPEndPoint, DiscoveryMessage> OnMessage;

        public int Port { get; private set; }

        public void Start(int port, Func<DiscoveryMessage> announceFactory, bool enableAnnounce = true, IPAddress bindAddress = null, IPAddress broadcastAddress = null)
        {
            if (enableAnnounce && announceFactory == null) throw new ArgumentNullException(nameof(announceFactory));
            lock (_gate)
            {
                if (_cts != null) throw new InvalidOperationException("Discovery already started.");
                Port = port;
                _announceFactory = announceFactory;
            _broadcastAddress = broadcastAddress;
                _cts = new CancellationTokenSource();
                _udp = new UdpClient();
                _udp.EnableBroadcast = true;
                _udp.ExclusiveAddressUse = false;
                _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(bindAddress ?? IPAddress.Any, port));

                _listener = Task.Run(() => ListenerLoop(_cts.Token));
                _announcer = enableAnnounce ? Task.Run(() => AnnounceLoop(announceFactory, _cts.Token)) : null;
            }

            Logger.Info("Discovery", "Started. Port=" + port + " Announce=" + enableAnnounce + (bindAddress != null ? (" Bind=" + bindAddress) : "") + (broadcastAddress != null ? (" Broadcast=" + broadcastAddress) : ""));
        }

        public void SendQuery()
        {
            var msg = new Dictionary<string, object>
            {
                { "proto", NetShareProtocol.ProtocolVersion },
                { "type", "DISCOVERY_QUERY" },
                { "timestampUtc", DateTime.UtcNow.ToString("o") }
            };
            Logger.Info("Discovery", "SendQuery.");
            SendBroadcast(msg);
        }

        private void SendBroadcast(object obj)
        {
            var bytes = _json.Encode(obj);
            var ip = _broadcastAddress ?? IPAddress.Broadcast;
            _udp.Send(bytes, bytes.Length, new IPEndPoint(ip, Port));
        }

        private void AnnounceLoop(Func<DiscoveryMessage> announceFactory, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var msg = announceFactory();
                    var bytes = _json.Encode(msg);
                    _udp.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, Port));
                }
                catch (Exception ex)
                {
                    if ((DateTime.UtcNow - _lastAnnounceErrorUtc).TotalSeconds >= 30)
                    {
                        _lastAnnounceErrorUtc = DateTime.UtcNow;
                        Logger.Warn("Discovery", "Announce loop error (throttled).", ex);
                    }
                }
                ct.WaitHandle.WaitOne(NetShareProtocol.DiscoveryAnnounceIntervalMs);
            }
        }

        private void ListenerLoop(CancellationToken ct)
        {
            var ep = new IPEndPoint(IPAddress.Any, 0);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var bytes = _udp.Receive(ref ep);
                    var msg = _json.Decode<DiscoveryMessage>(bytes);
                    if (msg == null) continue;

                    try
                    {
                        var summary = (msg.type ?? "") + " from " + ep.Address + " id=" + (msg.deviceId ?? "") + " name=" + (msg.deviceName ?? "") + " tcp=" + msg.tcpPort;
                        Logger.Debug("Discovery", "Recv " + summary);
                    }
                    catch { }

                    OnMessage?.Invoke(ep, msg);

                    if (string.Equals(msg.type, "DISCOVERY_QUERY", StringComparison.OrdinalIgnoreCase))
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
                        Logger.Debug("Discovery", "Listener loop error (throttled).", ex);
                    }
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_cts == null) return;
                _cts.Cancel();
                try { _udp.Close(); } catch { }
                try { _udp.Dispose(); } catch { }
                _cts.Dispose();
                _cts = null;
                _udp = null;
            }

            Logger.Info("Discovery", "Stopped.");
        }
    }
}
