namespace NetShare.Linux.Core.Networking;

public sealed class PeerDirectoryEntry
{
    public string Name { get; set; } = "";
    public bool IsDir { get; set; }

    public long? Size { get; set; }
    public DateTime? MtimeUtc { get; set; }
}
