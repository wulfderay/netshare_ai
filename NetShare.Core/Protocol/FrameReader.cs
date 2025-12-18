using System;
using System.IO;
using System.Net;

namespace NetShare.Core.Protocol
{
    public sealed class FrameReader
    {
        private readonly Stream _stream;

        public FrameReader(Stream stream)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        }

        public Frame ReadFrame()
        {
            int kindByte = _stream.ReadByte();
            if (kindByte < 0) return null;

            var kind = (FrameKind)(byte)kindByte;
            if (kind != FrameKind.Json && kind != FrameKind.Binary)
                throw new InvalidDataException("Unknown frame kind.");

            var lenBuf = ReadExact(4);
            var len = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(lenBuf, 0));
            if (len < 0 || len > 1024 * 1024 * 1024) throw new InvalidDataException("Invalid frame length.");

            var payload = ReadExact(len);
            return new Frame(kind, payload);
        }

        private byte[] ReadExact(int count)
        {
            var buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = _stream.Read(buffer, offset, count - offset);
                if (read <= 0) throw new EndOfStreamException();
                offset += read;
            }
            return buffer;
        }
    }
}
