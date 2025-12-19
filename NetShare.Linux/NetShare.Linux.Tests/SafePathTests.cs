using NetShare.Linux.Core.Sharing;
using Xunit;

namespace NetShare.Linux.Tests;

public sealed class SafePathTests
{
    [Fact]
    public void BlocksTraversal()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "netshare-test-root"));
        Directory.CreateDirectory(root);

        Assert.Throws<InvalidOperationException>(() => SafePath.CombineAndValidate(root, "../etc/passwd"));
        Assert.Throws<InvalidOperationException>(() => SafePath.CombineAndValidate(root, "/../etc/passwd"));
    }

    [Fact]
    public void AllowsNormalRelative()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "netshare-test-root2"));
        Directory.CreateDirectory(Path.Combine(root, "sub"));

        var path = SafePath.CombineAndValidate(root, "sub/file.txt");
        Assert.Contains("sub", path);
    }
}
