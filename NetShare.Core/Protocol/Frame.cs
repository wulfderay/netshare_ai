using System;

namespace NetShare.Core.Protocol
{
    public enum FrameKind : byte
    {
        Json = (byte)'J',
        Binary = (byte)'B'
    }

    public sealed class Frame
    {
        public Frame(FrameKind kind, byte[] payload)
        {
            Kind = kind;
            Payload = payload ?? throw new ArgumentNullException(nameof(payload));
        }

        public FrameKind Kind { get; private set; }
        public byte[] Payload { get; private set; }
    }
}
