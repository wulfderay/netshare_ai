using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using NetShare.Core.Discovery;
using NetShare.Core.Networking;
using NetShare.Core.Protocol;
using NetShare.Core.Settings;

namespace NetShare.Monitor
{
    internal static class Program
    {
        private sealed class Options
        {
            public int? DiscoveryPortOverride;
            public int RefreshIntervalMs = 1000;
            public bool Once;
        }

        private static int Main(string[] args)
        {
            var options = ParseArgs(args);
            if (options == null) return 2;

            var store = new SettingsStore();
            var settings = store.LoadOrCreate();

            var discoveryPort = options.DiscoveryPortOverride ?? settings.DiscoveryPort;

            var peersById = new Dictionary<string, PeerInfo>(StringComparer.OrdinalIgnoreCase);
            var gate = new object();

            using (var svc = new DiscoveryService())
            {
                svc.OnMessage += (ep, msg) =>
                {
                    if (msg == null) return;
                    if (string.IsNullOrWhiteSpace(msg.deviceId)) return;
                    if (string.Equals(msg.deviceId, settings.DeviceId, StringComparison.OrdinalIgnoreCase)) return;
                    if (!string.Equals(msg.proto, NetShareProtocol.ProtocolVersion, StringComparison.Ordinal)) return;

                    // Accept both announces and direct responses.
                    if (!string.Equals(msg.type, "DISCOVERY_ANNOUNCE", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(msg.type, "DISCOVERY_RESPONSE", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    lock (gate)
                    {
                        if (!peersById.TryGetValue(msg.deviceId, out var peer))
                        {
                            peer = new PeerInfo { DeviceId = msg.deviceId };
                            peersById[msg.deviceId] = peer;
                        }

                        peer.DeviceName = msg.deviceName;
                        peer.Address = ep.Address;
                        peer.TcpPort = msg.tcpPort;
                        peer.LastSeenUtc = DateTime.UtcNow;
                        peer.Online = true;
                    }
                };

                // Listen-only: don't announce ourselves and don't respond to queries.
                IPAddress bind;
                IPAddress broadcast;
                if (NetworkSelection.TryResolve(settings.PreferredInterfaceId, out bind, out broadcast))
                {
                    svc.Start(discoveryPort, announceFactory: null, enableAnnounce: false, bindAddress: bind, broadcastAddress: broadcast);
                }
                else
                {
                    svc.Start(discoveryPort, announceFactory: null, enableAnnounce: false);
                }
                svc.SendQuery();

                if (options.Once)
                {
                    Thread.Sleep(750);
                    Render(peersById, gate, discoveryPort, allowClear: false);
                    return 0;
                }

                var cts = new CancellationTokenSource();
                Console.CancelKeyPress += (s, e) =>
                {
                    e.Cancel = true;
                    cts.Cancel();
                };

                while (!cts.IsCancellationRequested)
                {
                    Render(peersById, gate, discoveryPort, allowClear: true);
                    cts.Token.WaitHandle.WaitOne(options.RefreshIntervalMs);
                }

                return 0;
            }
        }

        private static void Render(Dictionary<string, PeerInfo> peersById, object gate, int discoveryPort, bool allowClear)
        {
            List<PeerInfo> peers;
            lock (gate)
            {
                var now = DateTime.UtcNow;
                foreach (var p in peersById.Values)
                {
                    p.Online = (now - p.LastSeenUtc).TotalMilliseconds <= NetShareProtocol.PeerOfflineAfterMs;
                }

                peers = peersById.Values
                    .OrderByDescending(p => p.Online)
                    .ThenBy(p => p.DeviceName ?? "", StringComparer.OrdinalIgnoreCase)
                    .ThenBy(p => p.Address?.ToString() ?? "", StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            if (allowClear && !Console.IsOutputRedirected)
            {
                Console.Clear();
            }
            Console.WriteLine("NetShare Monitor (protocol {0})", NetShareProtocol.ProtocolVersion);
            Console.WriteLine("Discovery port: {0}    Updated: {1}", discoveryPort, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            Console.WriteLine();

            if (peers.Count == 0)
            {
                Console.WriteLine("No peers discovered yet. (Waiting for announces / responses...) ");
                Console.WriteLine("Tip: ensure UDP broadcast is allowed; press Ctrl+C to exit.");
                return;
            }

            Console.WriteLine("{0,-28} {1,-16} {2,6} {3,-8} {4}", "Name", "IP", "TCP", "Status", "Last Seen");
            Console.WriteLine(new string('-', 80));

            foreach (var p in peers)
            {
                var status = p.Online ? "Online" : "Offline";
                var lastSeen = p.LastSeenUtc.ToLocalTime().ToString("HH:mm:ss");
                Console.WriteLine("{0,-28} {1,-16} {2,6} {3,-8} {4}",
                    Truncate(p.DeviceName ?? "(unknown)", 28),
                    (p.Address ?? IPAddress.None).ToString(),
                    p.TcpPort,
                    status,
                    lastSeen);
            }
        }

        private static string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return value;
            if (value.Length <= max) return value;
            if (max <= 1) return value.Substring(0, max);
            return value.Substring(0, max - 1) + "…";
        }

        private static Options ParseArgs(string[] args)
        {
            var opt = new Options();

            for (var i = 0; i < args.Length; i++)
            {
                var a = args[i] ?? "";
                if (a == "--help" || a == "-h" || a == "/?")
                {
                    PrintHelp();
                    return null;
                }

                if (a == "--once")
                {
                    opt.Once = true;
                    continue;
                }

                if (a == "--port")
                {
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("Missing value for --port");
                        return null;
                    }

                    if (!int.TryParse(args[++i], out var port) || port <= 0 || port > 65535)
                    {
                        Console.Error.WriteLine("Invalid --port value");
                        return null;
                    }

                    opt.DiscoveryPortOverride = port;
                    continue;
                }

                if (a == "--interval-ms")
                {
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("Missing value for --interval-ms");
                        return null;
                    }

                    if (!int.TryParse(args[++i], out var ms) || ms < 100)
                    {
                        Console.Error.WriteLine("Invalid --interval-ms value (min 100)");
                        return null;
                    }

                    opt.RefreshIntervalMs = ms;
                    continue;
                }

                Console.Error.WriteLine("Unknown argument: {0}", a);
                PrintHelp();
                return null;
            }

            return opt;
        }

        private static void PrintHelp()
        {
            Console.WriteLine("NetShare.Monitor - command line peer monitor");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  NetShare.Monitor.exe [--port <discoveryPort>] [--interval-ms <ms>] [--once]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --port <n>         Override discovery port (default: settings.json value)");
            Console.WriteLine("  --interval-ms <n>  Refresh interval (default: 1000)");
            Console.WriteLine("  --once             Print one snapshot then exit");
        }
    }
}
