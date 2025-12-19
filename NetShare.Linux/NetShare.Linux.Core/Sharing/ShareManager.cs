namespace NetShare.Linux.Core.Sharing;

public sealed class ShareManager
{
    private readonly object _gate = new();
    private readonly List<ShareInfo> _shares;

    public ShareManager(IEnumerable<ShareInfo>? shares = null)
    {
        _shares = shares?.ToList() ?? new List<ShareInfo>();
    }

    public IReadOnlyList<ShareInfo> GetShares()
    {
        lock (_gate) return _shares.Select(Clone).ToList();
    }

    public bool TryGetShare(string? shareId, out ShareInfo share)
    {
        lock (_gate)
        {
            var s = _shares.FirstOrDefault(x => string.Equals(x.ShareId, shareId, StringComparison.Ordinal));
            if (s is null)
            {
                share = new ShareInfo();
                return false;
            }
            share = Clone(s);
            return true;
        }
    }

    public void Upsert(ShareInfo share)
    {
        ArgumentNullException.ThrowIfNull(share);
        lock (_gate)
        {
            var idx = _shares.FindIndex(s => s.ShareId == share.ShareId);
            if (idx >= 0) _shares[idx] = Clone(share);
            else _shares.Add(Clone(share));
        }
    }

    public bool Remove(string shareId)
    {
        lock (_gate)
        {
            var idx = _shares.FindIndex(s => s.ShareId == shareId);
            if (idx < 0) return false;
            _shares.RemoveAt(idx);
            return true;
        }
    }

    private static ShareInfo Clone(ShareInfo s) => new()
    {
        ShareId = s.ShareId,
        Name = s.Name,
        LocalPath = s.LocalPath,
        ReadOnly = s.ReadOnly
    };
}
