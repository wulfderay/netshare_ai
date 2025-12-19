using System.Net;

namespace NetShare.Linux.Core.Protocol;

public sealed class FrameWriter
{
    private readonly Stream _stream;

    public FrameWriter(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    public void WriteFrame(Frame frame)
    {
        if (frame is null) throw new ArgumentNullException(nameof(frame));

        int length = frame.Payload.Length;

        _stream.WriteByte((byte)frame.Kind);

        // Windows implementation-truth: signed int32 length, big-endian.
        var lenBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(length));
        _stream.Write(lenBytes, 0, 4);
        _stream.Write(frame.Payload, 0, frame.Payload.Length);
        _stream.Flush();
    }
}
