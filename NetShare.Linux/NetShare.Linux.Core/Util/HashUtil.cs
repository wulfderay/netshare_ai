using System.Security.Cryptography;
using System.Text;

namespace NetShare.Linux.Core.Util;

public static class HashUtil
{
    public static string Sha256HexLower(Stream stream)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return ToHexLower(hash);
    }

    public static string ToHexLower(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }
}
