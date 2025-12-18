using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using NetShare.Core.Logging;
using NetShare.Core.Protocol;
using NetShare.Core.Settings;
using NetShare.Core.Sharing;

namespace NetShare.Core.Transfers
{
    public sealed class TransferServer
    {
        private readonly JsonCodec _json = new JsonCodec();
        private readonly ShareManager _shares;
        private readonly AppSettings _settings;

        public TransferServer(ShareManager shares, AppSettings settings)
        {
            _shares = shares ?? throw new ArgumentNullException(nameof(shares));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public void HandleStat(FrameWriter writer, string reqId, Dictionary<string, object> msg)
        {
            var shareId = GetString(msg, "shareId");
            var path = GetString(msg, "path") ?? "";

            Logger.Debug("TransferServer", "STAT. ShareId=" + shareId + " Path=" + path);
            if (!_shares.TryGetShare(shareId, out var share))
            {
                SendError(writer, reqId, "STAT_RESP", ErrorCodes.NotFound, "Share not found.");
                return;
            }

            try
            {
                var full = SafePath.CombineAndValidate(share.LocalPath, path);
                if (!File.Exists(full))
                {
                    SendError(writer, reqId, "STAT_RESP", ErrorCodes.NotFound, "File not found.");
                    return;
                }

                var fi = new FileInfo(full);
                string sha;
                using (var fs = fi.OpenRead())
                using (var sha256 = SHA256.Create())
                {
                    sha = ToHex(sha256.ComputeHash(fs));
                }
                var stat = new Dictionary<string, object>
                {
                    { "size", fi.Length },
                    { "mtimeUtc", fi.LastWriteTimeUtc.ToString("o") },
                    { "sha256", sha }
                };
                var resp = new Dictionary<string, object>
                {
                    { "type", "STAT_RESP" },
                    { "reqId", reqId },
                    { "ok", true },
                    { "stat", stat }
                };
                writer.WriteFrame(new Frame(FrameKind.Json, _json.Encode(resp)));
            }
            catch (InvalidOperationException)
            {
                SendError(writer, reqId, "STAT_RESP", ErrorCodes.PathTraversal, "Path traversal rejected.");
            }
            catch (Exception ex)
            {
                SendError(writer, reqId, "STAT_RESP", ErrorCodes.IoError, ex.Message);
            }
        }

        public void HandleDownload(System.Net.Sockets.TcpClient client, FrameWriter writer, FrameReader reader, string reqId, Dictionary<string, object> msg)
        {
            var shareId = GetString(msg, "shareId");
            var path = GetString(msg, "path") ?? "";
            var offset = GetLong(msg, "offset");
            var transferId = GetString(msg, "transferId") ?? "";

            var remote = "";
            try { remote = client?.Client?.RemoteEndPoint == null ? "" : client.Client.RemoteEndPoint.ToString(); } catch { }
            Logger.Info("TransferServer", "DOWNLOAD_REQ. Remote=" + remote + " TransferId=" + transferId + " ShareId=" + shareId + " Path=" + path + " Offset=" + offset);

            if (!_shares.TryGetShare(shareId, out var share))
            {
                SendError(writer, reqId, "DOWNLOAD_ACK", ErrorCodes.NotFound, "Share not found.");
                return;
            }

            try
            {
                var full = SafePath.CombineAndValidate(share.LocalPath, path);
                if (!File.Exists(full))
                {
                    SendError(writer, reqId, "DOWNLOAD_ACK", ErrorCodes.NotFound, "File not found.");
                    return;
                }

                var fi = new FileInfo(full);
                string sha;
                using (var fs = fi.OpenRead())
                using (var sha256 = SHA256.Create())
                {
                    sha = ToHex(sha256.ComputeHash(fs));
                }

                if (offset < 0) offset = 0;
                if (offset > fi.Length) offset = fi.Length;

                var ack = new Dictionary<string, object>
                {
                    { "type", "DOWNLOAD_ACK" },
                    { "reqId", reqId },
                    { "ok", true },
                    { "file", new Dictionary<string, object> { { "size", fi.Length }, { "sha256", sha } } },
                    { "offset", offset }
                };
                writer.WriteFrame(new Frame(FrameKind.Json, _json.Encode(ack)));

                using (var fs = fi.OpenRead())
                using (var sha256 = SHA256.Create())
                {
                    if (offset > 0)
                    {
                        HashPrefix(sha256, fs, offset);
                    }

                    fs.Position = offset;
                    long sent = offset;
                    var buffer = new byte[NetShareProtocol.DefaultChunkSize];
                    while (sent < fi.Length)
                    {
                        int read = fs.Read(buffer, 0, buffer.Length);
                        if (read <= 0) break;

                        var hdr = new Dictionary<string, object>
                        {
                            { "type", "FILE_CHUNK" },
                            { "reqId", Guid.NewGuid().ToString() },
                            { "transferId", transferId },
                            { "offset", sent },
                            { "length", read }
                        };
                        writer.WriteFrame(new Frame(FrameKind.Json, _json.Encode(hdr)));

                        var chunk = new byte[read];
                        Buffer.BlockCopy(buffer, 0, chunk, 0, read);
                        writer.WriteFrame(new Frame(FrameKind.Binary, chunk));
                        sha256.TransformBlock(chunk, 0, chunk.Length, null, 0);
                        sent += read;
                    }

                    sha256.TransformFinalBlock(new byte[0], 0, 0);
                    var end = new Dictionary<string, object>
                    {
                        { "type", "FILE_END" },
                        { "reqId", Guid.NewGuid().ToString() },
                        { "transferId", transferId },
                        { "ok", true },
                        { "file", new Dictionary<string, object> { { "size", fi.Length }, { "sha256", ToHex(sha256.Hash) } } }
                    };
                    writer.WriteFrame(new Frame(FrameKind.Json, _json.Encode(end)));
                }

                Logger.Info("TransferServer", "DOWNLOAD complete. Remote=" + remote + " TransferId=" + transferId + " Bytes=" + fi.Length);
            }
            catch (InvalidOperationException)
            {
                SendError(writer, reqId, "DOWNLOAD_ACK", ErrorCodes.PathTraversal, "Path traversal rejected.");
            }
            catch (Exception ex)
            {
                SendError(writer, reqId, "DOWNLOAD_ACK", ErrorCodes.IoError, ex.Message);
            }
        }

        public void HandleUpload(System.Net.Sockets.TcpClient client, FrameWriter writer, FrameReader reader, string reqId, Dictionary<string, object> msg)
        {
            var shareId = GetString(msg, "shareId");
            var path = GetString(msg, "path") ?? "";
            var transferId = GetString(msg, "transferId") ?? "";

            var remote = "";
            try { remote = client?.Client?.RemoteEndPoint == null ? "" : client.Client.RemoteEndPoint.ToString(); } catch { }
            Logger.Info("TransferServer", "UPLOAD_REQ. Remote=" + remote + " TransferId=" + transferId + " ShareId=" + shareId + " Path=" + path);

            if (!_shares.TryGetShare(shareId, out var share))
            {
                SendError(writer, reqId, "UPLOAD_ACK", ErrorCodes.NotFound, "Share not found.");
                return;
            }
            if (share.ReadOnly)
            {
                SendError(writer, reqId, "UPLOAD_ACK", ErrorCodes.ReadOnly, "Share is read-only.");
                return;
            }

            try
            {
                var full = SafePath.CombineAndValidate(share.LocalPath, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full));

                var fileObj = (Dictionary<string, object>)msg["file"];
                var totalSize = Convert.ToInt64(fileObj["size"]);
                var expected = fileObj["sha256"].ToString();

                long offset = 0;
                if (File.Exists(full))
                {
                    var existing = new FileInfo(full);
                    offset = existing.Length;
                    if (offset > totalSize) offset = 0;
                }

                var ack = new Dictionary<string, object>
                {
                    { "type", "UPLOAD_ACK" },
                    { "reqId", reqId },
                    { "ok", true },
                    { "offset", offset }
                };
                writer.WriteFrame(new Frame(FrameKind.Json, _json.Encode(ack)));

                // Resume requires reading the existing prefix to seed the SHA-256.
                using (var fs = new FileStream(full, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
                using (var sha = SHA256.Create())
                {
                    if (offset > 0)
                    {
                        fs.Position = 0;
                        HashPrefix(sha, fs, offset);
                    }
                    fs.Position = offset;

                    long written = offset;
                    while (true)
                    {
                        var hdrFrame = reader.ReadFrame();
                        if (hdrFrame == null) { SendError(writer, reqId, "UPLOAD_DONE", ErrorCodes.IoError, "Connection closed."); return; }
                        if (hdrFrame.Kind != FrameKind.Json) { SendError(writer, reqId, "UPLOAD_DONE", ErrorCodes.BadRequest, "Expected JSON header."); return; }
                        var hdr = (Dictionary<string, object>)_json.DecodeUntyped(hdrFrame.Payload);
                        var type = hdr["type"].ToString();
                        if (string.Equals(type, "FILE_END", StringComparison.OrdinalIgnoreCase))
                        {
                            sha.TransformFinalBlock(new byte[0], 0, 0);
                            var actual = ToHex(sha.Hash);
                            var endFile = (Dictionary<string, object>)hdr["file"];
                            var endHash = endFile["sha256"].ToString();

                            if (!string.Equals(endHash, actual, StringComparison.OrdinalIgnoreCase) || !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                            {
                                SendError(writer, reqId, "UPLOAD_DONE", ErrorCodes.IntegrityFailed, "Upload hash mismatch.");
                                return;
                            }

                            var done = new Dictionary<string, object> { { "type", "UPLOAD_DONE" }, { "reqId", reqId }, { "ok", true } };
                            writer.WriteFrame(new Frame(FrameKind.Json, _json.Encode(done)));

                            Logger.Info("TransferServer", "UPLOAD complete. Remote=" + remote + " TransferId=" + transferId + " Bytes=" + written + "/" + totalSize);
                            return;
                        }

                        if (!string.Equals(type, "FILE_CHUNK", StringComparison.OrdinalIgnoreCase))
                        {
                            SendError(writer, reqId, "UPLOAD_DONE", ErrorCodes.BadRequest, "Unexpected message.");
                            return;
                        }

                        var length = Convert.ToInt32(hdr["length"]);
                        var bin = reader.ReadFrame();
                        if (bin == null || bin.Kind != FrameKind.Binary || bin.Payload.Length != length)
                        {
                            SendError(writer, reqId, "UPLOAD_DONE", ErrorCodes.BadRequest, "Bad chunk.");
                            return;
                        }

                        fs.Write(bin.Payload, 0, bin.Payload.Length);
                        sha.TransformBlock(bin.Payload, 0, bin.Payload.Length, null, 0);
                        written += bin.Payload.Length;
                        if (written > totalSize)
                        {
                            SendError(writer, reqId, "UPLOAD_DONE", ErrorCodes.BadRequest, "Too much data.");
                            return;
                        }
                    }
                }
            }
            catch (InvalidOperationException)
            {
                SendError(writer, reqId, "UPLOAD_ACK", ErrorCodes.PathTraversal, "Path traversal rejected.");
            }
            catch (Exception ex)
            {
                SendError(writer, reqId, "UPLOAD_ACK", ErrorCodes.IoError, ex.Message);
            }
        }

        private void SendError(FrameWriter writer, string reqId, string respType, string code, string message)
        {
            try
            {
                Logger.Warn("TransferServer", "Error. RespType=" + respType + " Code=" + code + " Msg=" + message);
            }
            catch { }

            var resp = new Dictionary<string, object>
            {
                { "type", respType },
                { "reqId", reqId },
                { "ok", false },
                { "error", new Dictionary<string, object> { { "code", code }, { "message", message } } }
            };
            writer.WriteFrame(new Frame(FrameKind.Json, _json.Encode(resp)));
        }

        private static string GetString(Dictionary<string, object> msg, string key)
        {
            if (!msg.TryGetValue(key, out var v) || v == null) return null;
            return v.ToString();
        }

        private static long GetLong(Dictionary<string, object> msg, string key)
        {
            if (!msg.TryGetValue(key, out var v) || v == null) return 0;
            return Convert.ToInt64(v);
        }

        private static void HashPrefix(SHA256 sha, Stream stream, long bytes)
        {
            var buffer = new byte[64 * 1024];
            long remaining = bytes;
            while (remaining > 0)
            {
                int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read <= 0) throw new EndOfStreamException();
                sha.TransformBlock(buffer, 0, read, null, 0);
                remaining -= read;
            }
        }

        private static string ToHex(byte[] bytes)
        {
            var sb = new System.Text.StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }
    }
}
