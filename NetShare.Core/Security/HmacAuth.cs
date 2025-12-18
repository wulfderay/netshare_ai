using System;
using System.Security.Cryptography;
using System.Text;

namespace NetShare.Core.Security
{
    public static class HmacAuth
    {
        public static byte[] RandomNonce(int bytes = 32)
        {
            var buf = new byte[bytes];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(buf);
            }
            return buf;
        }

        public static byte[] ComputeMac(string sharedKey, byte[] serverNonce, byte[] clientNonce, string serverId, string clientId)
        {
            if (sharedKey == null) throw new ArgumentNullException(nameof(sharedKey));
            if (serverNonce == null) throw new ArgumentNullException(nameof(serverNonce));
            if (clientNonce == null) throw new ArgumentNullException(nameof(clientNonce));
            if (serverId == null) throw new ArgumentNullException(nameof(serverId));
            if (clientId == null) throw new ArgumentNullException(nameof(clientId));

            var keyBytes = Encoding.UTF8.GetBytes(sharedKey);
            using (var hmac = new HMACSHA256(keyBytes))
            {
                var message = Concat(serverNonce, clientNonce, Encoding.UTF8.GetBytes(serverId), Encoding.UTF8.GetBytes(clientId));
                return hmac.ComputeHash(message);
            }
        }

        public static bool ConstantTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private static byte[] Concat(params byte[][] arrays)
        {
            int total = 0;
            for (int i = 0; i < arrays.Length; i++) total += arrays[i].Length;
            var buf = new byte[total];
            int offset = 0;
            for (int i = 0; i < arrays.Length; i++)
            {
                Buffer.BlockCopy(arrays[i], 0, buf, offset, arrays[i].Length);
                offset += arrays[i].Length;
            }
            return buf;
        }
    }
}
