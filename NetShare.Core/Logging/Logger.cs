using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace NetShare.Core.Logging
{
    public static class Logger
    {
        public const int DefaultRingBufferCapacity = 2000;

        private static readonly object Gate = new object();
        private static readonly LogEntry[] Buffer = new LogEntry[DefaultRingBufferCapacity];
        private static int _start;
        private static int _count;
        private static long _nextSeq;

        private static bool _fileEnabled;
        private static string _filePath;
        private static bool _fileFailureReported;

        public static event Action<LogEntry> EntryAdded;

        public static void ConfigureFileLogging(bool enabled, string logFilePath = null)
        {
            lock (Gate)
            {
                _fileEnabled = enabled;
                _filePath = string.IsNullOrWhiteSpace(logFilePath) ? GetDefaultLogFilePath() : logFilePath;
                _fileFailureReported = false;
            }

            if (enabled)
            {
                try
                {
                    var dir = Path.GetDirectoryName(_filePath);
                    if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
                }
                catch (Exception ex)
                {
                    lock (Gate)
                    {
                        _fileEnabled = false;
                    }
                    InternalLog(LogLevel.Warn, "Logger", "Failed to initialize file logging; continuing in memory.", ex);
                }
            }
        }

        public static void Shutdown()
        {
            lock (Gate)
            {
                _fileEnabled = false;
            }
        }

        public static void Clear()
        {
            lock (Gate)
            {
                Array.Clear(Buffer, 0, Buffer.Length);
                _start = 0;
                _count = 0;
            }
        }

        public static IReadOnlyList<LogEntry> Snapshot()
        {
            lock (Gate)
            {
                var list = new List<LogEntry>(_count);
                for (int i = 0; i < _count; i++)
                {
                    int idx = (_start + i) % Buffer.Length;
                    var e = Buffer[idx];
                    if (e != null) list.Add(e);
                }
                return list;
            }
        }

        public static IReadOnlyList<LogEntry> GetSince(long afterSequence)
        {
            lock (Gate)
            {
                var list = new List<LogEntry>(_count);
                for (int i = 0; i < _count; i++)
                {
                    int idx = (_start + i) % Buffer.Length;
                    var e = Buffer[idx];
                    if (e != null && e.Sequence > afterSequence) list.Add(e);
                }
                return list;
            }
        }

        public static void Debug(string source, string message) => InternalLog(LogLevel.Debug, source, message, null);
        public static void Info(string source, string message) => InternalLog(LogLevel.Info, source, message, null);
        public static void Warn(string source, string message) => InternalLog(LogLevel.Warn, source, message, null);
        public static void Error(string source, string message) => InternalLog(LogLevel.Error, source, message, null);

        public static void Debug(string source, string message, Exception ex) => InternalLog(LogLevel.Debug, source, message, ex);
        public static void Info(string source, string message, Exception ex) => InternalLog(LogLevel.Info, source, message, ex);
        public static void Warn(string source, string message, Exception ex) => InternalLog(LogLevel.Warn, source, message, ex);
        public static void Error(string source, string message, Exception ex) => InternalLog(LogLevel.Error, source, message, ex);

        private static void InternalLog(LogLevel level, string source, string message, Exception ex)
        {
            LogEntry entry;
            bool fileEnabled;
            string filePath;

            lock (Gate)
            {
                entry = new LogEntry
                {
                    Sequence = ++_nextSeq,
                    TimestampUtc = DateTime.UtcNow,
                    Level = level,
                    Source = source ?? "",
                    Message = message ?? "",
                    ExceptionText = ex == null ? null : ex.ToString()
                };

                int idx;
                if (_count < Buffer.Length)
                {
                    idx = (_start + _count) % Buffer.Length;
                    _count++;
                }
                else
                {
                    idx = _start;
                    _start = (_start + 1) % Buffer.Length;
                }
                Buffer[idx] = entry;

                fileEnabled = _fileEnabled;
                filePath = _filePath;
            }

            TryWriteFile(entry, fileEnabled, filePath);
            Publish(entry);
        }

        private static void Publish(LogEntry entry)
        {
            var handlers = EntryAdded;
            if (handlers == null) return;

            // Never block callers (network/transfer threads). Invoke on ThreadPool.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    foreach (Action<LogEntry> h in handlers.GetInvocationList())
                    {
                        try { h(entry); }
                        catch { /* ignore subscriber exceptions */ }
                    }
                }
                catch { }
            });
        }

        private static void TryWriteFile(LogEntry entry, bool enabled, string filePath)
        {
            if (!enabled) return;
            if (string.IsNullOrWhiteSpace(filePath)) return;

            try
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

                using (var fs = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read))
                using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
                {
                    sw.WriteLine(FormatForFile(entry));
                }
            }
            catch (Exception ex)
            {
                bool shouldReport;
                lock (Gate)
                {
                    shouldReport = !_fileFailureReported;
                    _fileFailureReported = true;
                    _fileEnabled = false; // avoid repeated IO failures / recursion
                }

                if (shouldReport)
                {
                    // Important: don't call file writer again while reporting.
                    lock (Gate)
                    {
                        // ensure disabled for the report entry
                    }
                    PublishFileFailure(ex, filePath);
                }
            }
        }

        private static void PublishFileFailure(Exception ex, string filePath)
        {
            // Report in memory only (file logging is already disabled).
            InternalLog(LogLevel.Warn, "Logger", "File logging disabled due to write failure (" + filePath + ").", ex);
        }

        private static string FormatForFile(LogEntry entry)
        {
            // Single line, tab-separated. Strip newlines to keep one-entry-per-line.
            var src = (entry.Source ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            var msg = (entry.Message ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            var ex = (entry.ExceptionText ?? "").Replace("\r", " ").Replace("\n", " ").Trim();

            if (!string.IsNullOrWhiteSpace(ex))
                return entry.TimestampUtc.ToString("o") + "\t" + entry.Level + "\t" + src + "\t" + msg + "\t" + ex;
            return entry.TimestampUtc.ToString("o") + "\t" + entry.Level + "\t" + src + "\t" + msg;
        }

        public static string GetDefaultLogFilePath()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetShare", "logs");
            return Path.Combine(dir, "netshare.log");
        }
    }
}
