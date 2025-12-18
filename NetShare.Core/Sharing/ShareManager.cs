using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NetShare.Core.Sharing
{
    public sealed class ShareManager
    {
        private readonly object _gate = new object();
        private readonly List<ShareInfo> _shares = new List<ShareInfo>();

        public IReadOnlyList<ShareInfo> GetShares()
        {
            lock (_gate)
            {
                return _shares.Select(s => new ShareInfo { ShareId = s.ShareId, Name = s.Name, LocalPath = s.LocalPath, ReadOnly = s.ReadOnly }).ToList();
            }
        }

        public ShareInfo AddShare(string folderPath, bool readOnly)
        {
            if (string.IsNullOrWhiteSpace(folderPath)) throw new ArgumentNullException(nameof(folderPath));
            if (!Directory.Exists(folderPath)) throw new DirectoryNotFoundException(folderPath);

            return AddShare(folderPath, readOnly, null, null);
        }

        public ShareInfo AddShare(string folderPath, bool readOnly, string shareId, string name)
        {
            if (string.IsNullOrWhiteSpace(folderPath)) throw new ArgumentNullException(nameof(folderPath));
            if (!Directory.Exists(folderPath)) throw new DirectoryNotFoundException(folderPath);

            var fullPath = Path.GetFullPath(folderPath);
            var trimmed = folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var resolvedName = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(trimmed) : name;
            var resolvedId = string.IsNullOrWhiteSpace(shareId) ? Guid.NewGuid().ToString() : shareId;

            lock (_gate)
            {
                // If the caller supplies a known ShareId, treat it as authoritative and update in-place.
                var byId = _shares.FirstOrDefault(s => string.Equals(s.ShareId, resolvedId, StringComparison.OrdinalIgnoreCase));
                if (byId != null)
                {
                    byId.Name = resolvedName;
                    byId.LocalPath = fullPath;
                    byId.ReadOnly = readOnly;
                    return new ShareInfo { ShareId = byId.ShareId, Name = byId.Name, LocalPath = byId.LocalPath, ReadOnly = byId.ReadOnly };
                }

                // Otherwise, de-dupe by path (case-insensitive on Windows).
                var byPath = _shares.FirstOrDefault(s => string.Equals(s.LocalPath, fullPath, StringComparison.OrdinalIgnoreCase));
                if (byPath != null)
                {
                    byPath.Name = resolvedName;
                    byPath.ReadOnly = readOnly;
                    return new ShareInfo { ShareId = byPath.ShareId, Name = byPath.Name, LocalPath = byPath.LocalPath, ReadOnly = byPath.ReadOnly };
                }

                var info = new ShareInfo
                {
                    ShareId = resolvedId,
                    Name = resolvedName,
                    LocalPath = fullPath,
                    ReadOnly = readOnly
                };

                _shares.Add(info);
                return new ShareInfo { ShareId = info.ShareId, Name = info.Name, LocalPath = info.LocalPath, ReadOnly = info.ReadOnly };
            }
        }

        public bool RemoveShare(string shareId)
        {
            if (shareId == null) return false;
            lock (_gate)
            {
                var idx = _shares.FindIndex(s => string.Equals(s.ShareId, shareId, StringComparison.OrdinalIgnoreCase));
                if (idx < 0) return false;
                _shares.RemoveAt(idx);
                return true;
            }
        }

        public bool ToggleReadOnly(string shareId)
        {
            lock (_gate)
            {
                var s = _shares.FirstOrDefault(x => string.Equals(x.ShareId, shareId, StringComparison.OrdinalIgnoreCase));
                if (s == null) return false;
                s.ReadOnly = !s.ReadOnly;
                return true;
            }
        }

        public bool TryGetShare(string shareId, out ShareInfo share)
        {
            lock (_gate)
            {
                var s = _shares.FirstOrDefault(x => string.Equals(x.ShareId, shareId, StringComparison.OrdinalIgnoreCase));
                if (s == null)
                {
                    share = null;
                    return false;
                }

                share = new ShareInfo { ShareId = s.ShareId, Name = s.Name, LocalPath = s.LocalPath, ReadOnly = s.ReadOnly };
                return true;
            }
        }
    }
}
