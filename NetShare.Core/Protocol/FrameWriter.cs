using System;
using System.IO;
using System.Net;

namespace NetShare.Core.Protocol
{
    public sealed class FrameWriter
    {
        private readonly Stream _stream;

        public FrameWriter(Stream stream)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        }

        public void WriteFrame(Frame frame)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));

            var length = frame.Payload.Length;
            if (length < 0) throw new InvalidOperationException("Invalid payload length.");

            _stream.WriteByte((byte)frame.Kind);

            var lenBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(length));
            if (lenBytes.Length != 4) throw new InvalidOperationException("Unexpected int size.");
            _stream.Write(lenBytes, 0, 4);
            _stream.Write(frame.Payload, 0, length);
            _stream.Flush();
        }
    }
}
