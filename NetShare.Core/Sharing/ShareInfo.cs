namespace NetShare.Core.Sharing
{
    public sealed class ShareInfo
    {
        public string ShareId { get; set; }
        public string Name { get; set; }
        public string LocalPath { get; set; }
        public bool ReadOnly { get; set; }
    }
}
