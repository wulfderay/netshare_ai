namespace NetShare.Linux.Core.Sharing;

public sealed class ShareInfo
{
    public string ShareId { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "Share";
    public string LocalPath { get; set; } = "";
    public bool ReadOnly { get; set; }
}
