using System;
using System.IO;

namespace NetShare.Core.Sharing
{
    public static class SafePath
    {
        public static string NormalizeRelative(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) return string.Empty;

            var p = relativePath.Replace('\\', '/');
            while (p.StartsWith("/", StringComparison.Ordinal)) p = p.Substring(1);
            return p;
        }

        public static string CombineAndValidate(string shareRoot, string relativePath)
        {
            if (shareRoot == null) throw new ArgumentNullException(nameof(shareRoot));

            var rel = NormalizeRelative(relativePath);

            var combined = Path.Combine(shareRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            var full = Path.GetFullPath(combined);
            var rootFull = Path.GetFullPath(shareRoot);

            // Normalize for comparisons: allow the share root itself (e.g. rel="") and anything under it.
            var rootTrimmed = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullTrimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.Equals(fullTrimmed, rootTrimmed, StringComparison.OrdinalIgnoreCase))
                return full;

            // Ensure "C:\Root" doesn't match "C:\Root2".
            var rootPrefix = rootTrimmed + Path.DirectorySeparatorChar;
            if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Path traversal detected.");

            return full;
        }
    }
}
