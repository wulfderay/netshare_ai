using System.Buffers;
using System.Net;

namespace NetShare.Linux.Core.Protocol;

public sealed class FrameReader
{
    private readonly Stream _stream;

    public FrameReader(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    public Frame? ReadFrame(int maxLen = 1024 * 1024 * 1024)
    {
        int kindByte = _stream.ReadByte();
        if (kindByte < 0) return null;

        var kind = (FrameKind)(byte)kindByte;
        if (kind is not FrameKind.Json and not FrameKind.Binary)
            throw new InvalidDataException("Unknown frame kind.");

        Span<byte> lenBuf = stackalloc byte[4];
        ReadExact(lenBuf);

        // Windows implementation-truth: signed int32 length, big-endian.
        int len = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(lenBuf));
        if (len < 0 || len > maxLen)
            throw new InvalidDataException("Invalid frame length.");

        byte[] payload = ArrayPool<byte>.Shared.Rent(len);
        try
        {
            var mem = payload.AsMemory(0, len);
            ReadExact(mem.Span);

            var exact = new byte[len];
            Buffer.BlockCopy(payload, 0, exact, 0, len);
            return new Frame(kind, exact);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(payload);
        }
    }

    private void ReadExact(Span<byte> dest)
    {
        int offset = 0;
        while (offset < dest.Length)
        {
            int read = _stream.Read(dest.Slice(offset));
            if (read <= 0) throw new EndOfStreamException();
            offset += read;
        }
    }
}
