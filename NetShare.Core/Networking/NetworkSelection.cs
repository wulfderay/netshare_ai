using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;

namespace NetShare.Core.Networking
{
    public static class NetworkSelection
    {
        public sealed class AdapterOption
        {
            public string InterfaceId;
            public string DisplayName;
            public IPAddress IPv4Address;
            public IPAddress IPv4Mask;

            public override string ToString()
            {
                var ip = IPv4Address == null ? "" : IPv4Address.ToString();
                return (DisplayName ?? "") + " — " + ip;
            }
        }

        public static List<AdapterOption> GetIPv4AdapterOptions()
        {
            var list = new List<AdapterOption>();

            NetworkInterface[] nics;
            try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
            catch { return list; }

            foreach (var nic in nics ?? new NetworkInterface[0])
            {
                if (nic == null) continue;

                IPInterfaceProperties props;
                try { props = nic.GetIPProperties(); }
                catch { continue; }

                if (props == null) continue;

                foreach (var uni in props.UnicastAddresses)
                {
                    if (uni == null) continue;
                    if (uni.Address == null) continue;
                    if (uni.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;

                    // Prefer usable addresses; still list them all so the user can pick.
                    var name = string.IsNullOrWhiteSpace(nic.Name) ? nic.Description : nic.Name;

                    IPAddress mask = null;
                    try { mask = uni.IPv4Mask; }
                    catch { }

                    list.Add(new AdapterOption
                    {
                        InterfaceId = nic.Id,
                        DisplayName = name,
                        IPv4Address = uni.Address,
                        IPv4Mask = mask
                    });
                }
            }

            // Keep it stable and user-friendly.
            return list
                .OrderBy(o => o.DisplayName ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(o => o.IPv4Address == null ? "" : o.IPv4Address.ToString(), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static bool TryResolve(string interfaceId, out IPAddress bindAddress, out IPAddress broadcastAddress)
        {
            bindAddress = null;
            broadcastAddress = null;

            if (string.IsNullOrWhiteSpace(interfaceId)) return false;

            var opt = GetIPv4AdapterOptions()
                .FirstOrDefault(o => string.Equals(o.InterfaceId, interfaceId, StringComparison.OrdinalIgnoreCase));

            if (opt == null || opt.IPv4Address == null) return false;

            bindAddress = opt.IPv4Address;
            broadcastAddress = ComputeDirectedBroadcast(opt.IPv4Address, opt.IPv4Mask) ?? IPAddress.Broadcast;
            return true;
        }

        private static IPAddress ComputeDirectedBroadcast(IPAddress ipv4Address, IPAddress ipv4Mask)
        {
            if (ipv4Address == null || ipv4Mask == null) return null;
            if (ipv4Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return null;
            if (ipv4Mask.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return null;

            var ip = ipv4Address.GetAddressBytes();
            var mask = ipv4Mask.GetAddressBytes();
            if (ip.Length != 4 || mask.Length != 4) return null;

            var bcast = new byte[4];
            for (int i = 0; i < 4; i++)
            {
                bcast[i] = (byte)(ip[i] | (byte)~mask[i]);
            }
            return new IPAddress(bcast);
        }
    }
}
