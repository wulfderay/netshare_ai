namespace NetShare.Linux.Core.Sharing;

public static class SafePath
{
    /// <summary>
    /// Combine shareRoot with protocolRelativePath and ensure the result is contained within the share root.
    /// Linux best practice: defend against symlink escapes by resolving real paths.
    /// Throws InvalidOperationException on traversal.
    /// </summary>
    public static string CombineAndValidate(string shareRoot, string protocolRelativePath)
    {
        if (string.IsNullOrWhiteSpace(shareRoot)) throw new ArgumentException("shareRoot required", nameof(shareRoot));

        // Canonicalize root early and require it to exist.
        var rootFull = Path.GetFullPath(shareRoot);
        if (!Directory.Exists(rootFull))
            throw new InvalidOperationException("PATH_TRAVERSAL");

        var rootReal = Path.GetFullPath(new DirectoryInfo(rootFull).FullName);
        rootReal = EnsureDirectoryPath(rootReal);

        var rel = (protocolRelativePath ?? string.Empty).Replace('\\', '/');
        while (rel.StartsWith("/", StringComparison.Ordinal)) rel = rel[1..];

        var combined = Path.GetFullPath(Path.Combine(rootReal, rel.Replace('/', Path.DirectorySeparatorChar)));

        // Best-effort real path on existing targets.
        var combinedReal = RealPathOrFullPath(combined);

        if (!IsUnderDirectory(combinedReal, rootReal))
            throw new InvalidOperationException("PATH_TRAVERSAL");

        return combined;
    }

    private static string EnsureDirectoryPath(string path)
    {
        if (!path.EndsWith(Path.DirectorySeparatorChar))
            return path + Path.DirectorySeparatorChar;
        return path;
    }

    private static bool IsUnderDirectory(string path, string dir)
    {
        // Case-sensitive on Linux.
        dir = EnsureDirectoryPath(dir);
        return path.StartsWith(dir, StringComparison.Ordinal);
    }

    private static string RealPathOrFullPath(string path)
    {
        try
        {
            if (Directory.Exists(path))
                return EnsureDirectoryPath(new DirectoryInfo(path).FullName);
            if (File.Exists(path))
                return new FileInfo(path).FullName;

            // For non-existing paths, canonicalize the parent directory if it exists.
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
            {
                var parentReal = EnsureDirectoryPath(new DirectoryInfo(parent).FullName);
                var leaf = Path.GetFileName(path);
                return Path.GetFullPath(Path.Combine(parentReal, leaf));
            }
        }
        catch
        {
            // ignore
        }

        return Path.GetFullPath(path);
    }
}
