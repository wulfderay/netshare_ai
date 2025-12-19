using NetShare.Linux.Core.Security;
using Xunit;

namespace NetShare.Linux.Tests;

public sealed class GoldenMessageTests
{
    [Fact]
    public void Hmac_MessageShape_IsStable()
    {
        var key = "secret";
        var serverNonce = new byte[32];
        var clientNonce = new byte[32];
        for (int i = 0; i < 32; i++) { serverNonce[i] = (byte)i; clientNonce[i] = (byte)(255 - i); }

        var mac = HmacAuth.ComputeMac(key, serverNonce, clientNonce, "server", "client");
        Assert.Equal(32, mac.Length);
    }
}
