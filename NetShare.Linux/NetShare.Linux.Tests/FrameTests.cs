using System.Net;
using NetShare.Linux.Core.Protocol;
using Xunit;

namespace NetShare.Linux.Tests;

public sealed class FrameTests
{
    [Fact]
    public void JsonFrame_RoundTrip()
    {
        var payload = System.Text.Encoding.UTF8.GetBytes("{\"type\":\"PING\",\"reqId\":\"1\"}");
        var ms = new MemoryStream();

        var w = new FrameWriter(ms);
        w.WriteFrame(new Frame(FrameKind.Json, payload));

        ms.Position = 0;
        var r = new FrameReader(ms);
        var f = r.ReadFrame();

        Assert.NotNull(f);
        Assert.Equal(FrameKind.Json, f!.Kind);
        Assert.Equal(payload, f.Payload);

        // Check signed int32 big-endian length encoding for 1st frame.
        ms.Position = 1;
        var lenBuf = new byte[4];
        _ = ms.Read(lenBuf, 0, 4);
        var len = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(lenBuf, 0));
        Assert.Equal(payload.Length, len);
    }
}
