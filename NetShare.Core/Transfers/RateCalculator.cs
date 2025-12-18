using System;

namespace NetShare.Core.Transfers
{
    public sealed class RateCalculator
    {
        private long _lastBytes;
        private DateTime _lastTimeUtc;
        public double BytesPerSecond { get; private set; }

        public void Reset(long currentBytes)
        {
            _lastBytes = currentBytes;
            _lastTimeUtc = DateTime.UtcNow;
            BytesPerSecond = 0;
        }

        public void Sample(long currentBytes)
        {
            var now = DateTime.UtcNow;
            var dt = (now - _lastTimeUtc).TotalSeconds;
            if (dt <= 0.2) return;

            var delta = currentBytes - _lastBytes;
            if (delta < 0) delta = 0;

            BytesPerSecond = delta / dt;
            _lastBytes = currentBytes;
            _lastTimeUtc = now;
        }

        public TimeSpan? EstimateEta(long currentBytes, long totalBytes)
        {
            if (BytesPerSecond <= 1) return null;
            var remaining = totalBytes - currentBytes;
            if (remaining <= 0) return TimeSpan.Zero;
            return TimeSpan.FromSeconds(remaining / BytesPerSecond);
        }
    }
}
