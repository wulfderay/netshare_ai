using System;

namespace NetShare.Core.Transfers
{
    public enum TransferDirection
    {
        Download,
        Upload
    }

    public enum TransferState
    {
        Pending,
        Running,
        Paused,
        Completed,
        Failed,
        Canceled
    }

    public sealed class TransferInfo
    {
        public string TransferId { get; set; }
        public TransferDirection Direction { get; set; }
        public string Peer { get; set; }
        public string ShareName { get; set; }
        public string RemotePath { get; set; }
        public string LocalPath { get; set; }
        public long TotalBytes { get; set; }
        public long TransferredBytes { get; set; }
        public TransferState State { get; set; }
        public string Error { get; set; }
        public DateTime StartedUtc { get; set; }
    }
}
