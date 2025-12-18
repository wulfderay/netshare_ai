using System;

namespace NetShare.Core.Logging
{
    public sealed class LogEntry
    {
        public long Sequence { get; internal set; }
        public DateTime TimestampUtc { get; internal set; }
        public LogLevel Level { get; internal set; }
        public string Source { get; internal set; }
        public string Message { get; internal set; }
        public string ExceptionText { get; internal set; }

        public override string ToString()
        {
            var ts = TimestampUtc.ToString("o");
            var src = string.IsNullOrWhiteSpace(Source) ? "-" : Source;
            var msg = Message ?? "";
            if (!string.IsNullOrWhiteSpace(ExceptionText))
            {
                return ts + "\t" + Level + "\t" + src + "\t" + msg + "\t" + ExceptionText;
            }
            return ts + "\t" + Level + "\t" + src + "\t" + msg;
        }
    }
}
