namespace NetShare.Linux.Core.Protocol;

public enum FrameKind : byte
{
    Json = (byte)'J',
    Binary = (byte)'B'
}

public sealed record Frame(FrameKind Kind, byte[] Payload);
