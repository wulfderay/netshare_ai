using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using NetShare.Linux.Core;
using NetShare.Linux.Core.Discovery;
using NetShare.Linux.Core.Networking;
using NetShare.Linux.Core.Settings;

namespace NetShare.Linux.Monitor;

internal static class Program
{
    private sealed class Options
    {
        public int? DiscoveryPortOverride;
        public int RefreshIntervalMs = 1000;
        public bool Once;
    }

    public static int Main(string[] args)
    {
        var options = ParseArgs(args);
        if (options is null) return 2;

        var store = new SettingsStore();
        var settings = store.LoadOrCreateDefault();

        var discoveryPort = options.DiscoveryPortOverride ?? settings.DiscoveryPort;

        var peersById = new Dictionary<string, PeerInfo>(StringComparer.OrdinalIgnoreCase);
        var gate = new object();

        using var svc = new DiscoveryService();
        svc.OnMessage += (ep, msg) =>
        {
            if (msg is null) return;

            if (string.IsNullOrWhiteSpace(msg.deviceId)) return;
            var deviceId = msg.deviceId;

            if (string.Equals(deviceId, settings.DeviceId, StringComparison.OrdinalIgnoreCase)) return;

            if (!string.Equals(msg.proto, NetShareProtocol.ProtocolVersion, StringComparison.Ordinal)) return;

            // Accept only announces + direct responses. Ignore queries and all other traffic.
            if (!string.Equals(msg.type, "DISCOVERY_ANNOUNCE", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(msg.type, "DISCOVERY_RESPONSE", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            lock (gate)
            {
                if (!peersById.TryGetValue(deviceId, out var peer))
                {
                    peer = new PeerInfo
                    {
                        DeviceId = deviceId
                    };
                    peersById[deviceId] = peer;
                }

                peer.DeviceName = msg.deviceName ?? "";
                peer.Address = ep.Address;
                peer.TcpPort = msg.tcpPort;
                peer.DiscoveryPort = msg.discoveryPort;
                peer.LastSeenUtc = DateTime.UtcNow;
            }
        };

        // Listen-only: do not announce and do not respond to queries.
        // (DiscoveryService only responds if announceFactory != null)
        svc.Start(discoveryPort, announceFactory: null, enableAnnounce: false);

        // Prompt peers to respond.
        svc.SendQuery();

        if (options.Once)
        {
            Thread.Sleep(750);
            Render(peersById, gate, discoveryPort, allowClear: false);
            return 0;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
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

    private static void Render(Dictionary<string, PeerInfo> peersById, object gate, int discoveryPort, bool allowClear)
    {
        List<PeerInfo> peers;
        lock (gate)
        {
            // PeerInfo.Online is computed from LastSeenUtc and NetShareProtocol.PeerOfflineAfterMs.
            peers = peersById.Values
                .OrderByDescending(p => p.Online)
                .ThenBy(p => p.DeviceName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.Address?.ToString() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (allowClear && !Console.IsOutputRedirected)
        {
            Console.Clear();
        }

        Console.WriteLine("NetShare Monitor (protocol {0})", NetShareProtocol.ProtocolVersion);
        Console.WriteLine(
            "Discovery port: {0}    Updated: {1}",
            discoveryPort,
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
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

            Console.WriteLine(
                "{0,-28} {1,-16} {2,6} {3,-8} {4}",
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
        return value.Substring(0, max - 1) + "\u2026";
    }

    private static Options? ParseArgs(string[] args)
    {
        var opt = new Options();

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i] ?? string.Empty;

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

                if (!int.TryParse(args[++i], out var port) || port is <= 0 or > 65535)
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
        Console.WriteLine("Usage: NetShare.Monitor.exe [--port <discoveryPort>] [--interval-ms <ms>] [--once]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --port <n>         Override discovery port (default: settings.json value)");
        Console.WriteLine("  --interval-ms <n>  Refresh interval (default: 1000, min: 100)");
        Console.WriteLine("  --once             Print one snapshot then exit");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  NetShare.Linux.Monitor --once");
        Console.WriteLine("  NetShare.Linux.Monitor --port 40123 --interval-ms 500");
    }
}
