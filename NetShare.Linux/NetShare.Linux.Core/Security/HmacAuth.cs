using System.Security.Cryptography;
using System.Text;

namespace NetShare.Linux.Core.Security;

public static class HmacAuth
{
    public static byte[] RandomNonce(int bytes = 32)
    {
        var buf = new byte[bytes];
        RandomNumberGenerator.Fill(buf);
        return buf;
    }

    public static byte[] ComputeMac(string sharedKey, byte[] serverNonce, byte[] clientNonce, string serverId, string clientId)
    {
        ArgumentNullException.ThrowIfNull(sharedKey);
        ArgumentNullException.ThrowIfNull(serverNonce);
        ArgumentNullException.ThrowIfNull(clientNonce);
        ArgumentNullException.ThrowIfNull(serverId);
        ArgumentNullException.ThrowIfNull(clientId);

        var keyBytes = Encoding.UTF8.GetBytes(sharedKey);
        using var hmac = new HMACSHA256(keyBytes);

        // Windows implementation-truth: message = serverNonce || clientNonce || UTF8(serverId) || UTF8(clientId)
        var msg = Concat(serverNonce, clientNonce, Encoding.UTF8.GetBytes(serverId), Encoding.UTF8.GetBytes(clientId));
        return hmac.ComputeHash(msg);
    }

    public static bool ConstantTimeEquals(byte[]? a, byte[]? b)
    {
        if (a is null || b is null) return false;
        return CryptographicOperations.FixedTimeEquals(a, b);
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
