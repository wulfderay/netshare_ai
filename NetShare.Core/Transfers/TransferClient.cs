using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using NetShare.Core.Logging;
using NetShare.Core.Protocol;
using NetShare.Core.Settings;

namespace NetShare.Core.Transfers
{
    public sealed class TransferClient
    {
        private readonly JsonCodec _json = new JsonCodec();
        private readonly AppSettings _settings;

        public TransferClient(AppSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public void Download(IPAddress address, int port, string authMode, string shareId, string remotePath, string localPath, long resumeOffset, Action<long, long> progress, System.Threading.CancellationToken ct)
        {
            Download(address, port, authMode, shareId, remotePath, localPath, resumeOffset, Guid.NewGuid().ToString(), progress, ct);
        }

        public void Download(IPAddress address, int port, string authMode, string shareId, string remotePath, string localPath, long resumeOffset, string transferId, Action<long, long> progress, System.Threading.CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(transferId)) transferId = Guid.NewGuid().ToString();

            Logger.Info("TransferClient", "Download start. Endpoint=" + address + ":" + port + " ShareId=" + shareId + " Path=" + (remotePath ?? "") + " Offset=" + resumeOffset + " TransferId=" + transferId);
            using (var tcp = new TcpClient())
            {
                try
                {
                    tcp.Connect(address, port);
                    using (var stream = tcp.GetStream())
                    {
                        stream.ReadTimeout = NetShareProtocol.DefaultSocketTimeoutMs;
                        stream.WriteTimeout = NetShareProtocol.DefaultSocketTimeoutMs;
                        var reader = new FrameReader(stream);
                        var writer = new FrameWriter(stream);

                        Handshake(writer, reader, authMode);

                        var reqId = Guid.NewGuid().ToString();
                        var req = new Dictionary<string, object>
                        {
                            { "type", "DOWNLOAD_REQ" },
                            { "reqId", reqId },
                            { "transferId", transferId },
                            { "shareId", shareId },
                            { "path", remotePath },
                            { "offset", resumeOffset }
                        };
                        writer.WriteFrame(new Frame(FrameKind.Json, _json.Encode(req)));

                        var ack = ReadJson(reader);
                        EnsureOk(ack);
                        var total = Convert.ToInt64(((Dictionary<string, object>)ack["file"])["size"]);
                        var expectedHash = ((Dictionary<string, object>)ack["file"])["sha256"].ToString();
                        var ackOffset = ack.ContainsKey("offset") ? Convert.ToInt64(ack["offset"]) : resumeOffset;

                        var dir = Path.GetDirectoryName(localPath);
                        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

                        // Resume requires reading the existing prefix to seed the SHA-256.
                        using (var file = new FileStream(localPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
                        {
                            if (ackOffset < 0) ackOffset = 0;

                            // If the server clamps offset (e.g., remote file is smaller), keep local file consistent.
                            if (file.Length > ackOffset)
                            {
                                file.SetLength(ackOffset);
                            }
                            // If local file is shorter than the server-ack'd offset (shouldn't happen), clamp down.
                            if (ackOffset > file.Length)
                            {
                                ackOffset = file.Length;
                            }

                            file.Position = ackOffset;

                            using (var sha = SHA256.Create())
                            {
                                if (ackOffset > 0)
                                {
                                    // hash existing bytes for end-to-end verification
                                    file.Position = 0;
                                    HashPrefix(sha, file, ackOffset);
                                    file.Position = ackOffset;
                                }

                                long current = ackOffset;
                                while (true)
                                {
                                    if (ct.IsCancellationRequested) throw new OperationCanceledException();
                                    var hdr = ReadJson(reader);
                                    var type = hdr["type"].ToString();
                                    if (string.Equals(type, "FILE_END", StringComparison.OrdinalIgnoreCase))
                                    {
                                        var final = ((Dictionary<string, object>)hdr["file"])["sha256"].ToString();
                                        sha.TransformFinalBlock(new byte[0], 0, 0);
                                        var actual = ToHex(sha.Hash);
                                        if (!string.Equals(final, actual, StringComparison.OrdinalIgnoreCase) || !string.Equals(expectedHash, actual, StringComparison.OrdinalIgnoreCase))
                                            throw new InvalidOperationException(ErrorCodes.IntegrityFailed + ": hash mismatch");
                                        progress?.Invoke(current, total);
                                        Logger.Info("TransferClient", "Download complete. TransferId=" + transferId + " Bytes=" + current + "/" + total);
                                        return;
                                    }

                                    if (!string.Equals(type, "FILE_CHUNK", StringComparison.OrdinalIgnoreCase))
                                        throw new InvalidOperationException("Unexpected message in transfer.");

                                    var length = Convert.ToInt32(hdr["length"]);
                                    var bin = reader.ReadFrame();
                                    if (bin == null || bin.Kind != FrameKind.Binary) throw new InvalidOperationException("Expected binary chunk.");
                                    if (bin.Payload.Length != length) throw new InvalidOperationException("Chunk length mismatch.");

                                    file.Write(bin.Payload, 0, bin.Payload.Length);
                                    sha.TransformBlock(bin.Payload, 0, bin.Payload.Length, null, 0);

                                    current += bin.Payload.Length;
                                    progress?.Invoke(current, total);
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    Logger.Info("TransferClient", "Download canceled. TransferId=" + transferId);
                    throw;
                }
                catch (Exception ex)
                {
                    Logger.Error("TransferClient", "Download failed. TransferId=" + transferId + " ShareId=" + shareId + " Path=" + (remotePath ?? ""), ex);
                    throw;
                }
            }
        }

        public void Upload(IPAddress address, int port, string authMode, string shareId, string remotePath, string localPath, Action<long, long> progress, System.Threading.CancellationToken ct)
        {
            Upload(address, port, authMode, shareId, remotePath, localPath, Guid.NewGuid().ToString(), progress, ct);
        }

        public void Upload(IPAddress address, int port, string authMode, string shareId, string remotePath, string localPath, string transferId, Action<long, long> progress, System.Threading.CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(transferId)) transferId = Guid.NewGuid().ToString();
            Logger.Info("TransferClient", "Upload start. Endpoint=" + address + ":" + port + " ShareId=" + shareId + " Path=" + (remotePath ?? "") + " TransferId=" + transferId);
            using (var tcp = new TcpClient())
            {
                try
                {
                    tcp.Connect(address, port);
                    using (var stream = tcp.GetStream())
                    {
                        stream.ReadTimeout = NetShareProtocol.DefaultSocketTimeoutMs;
                        stream.WriteTimeout = NetShareProtocol.DefaultSocketTimeoutMs;
                        var reader = new FrameReader(stream);
                        var writer = new FrameWriter(stream);

                        Handshake(writer, reader, authMode);

                        var fi = new FileInfo(localPath);
                        if (!fi.Exists) throw new FileNotFoundException(localPath);

                        string sha256;
                        using (var fs = fi.OpenRead())
                        using (var sha = SHA256.Create())
                        {
                            sha256 = ToHex(sha.ComputeHash(fs));
                        }

                        var reqId = Guid.NewGuid().ToString();
                        var req = new Dictionary<string, object>
                        {
                            { "type", "UPLOAD_REQ" },
                            { "reqId", reqId },
                            { "transferId", transferId },
                            { "shareId", shareId },
                            { "path", remotePath },
                            { "file", new Dictionary<string, object> { { "size", fi.Length }, { "sha256", sha256 } } }
                        };
                        writer.WriteFrame(new Frame(FrameKind.Json, _json.Encode(req)));

                        var ack = ReadJson(reader);
                        EnsureOk(ack);
                        var offset = Convert.ToInt64(ack["offset"]);

                        using (var fs = fi.OpenRead())
                        {
                            fs.Position = offset;
                            long sent = offset;
                            var buffer = new byte[NetShareProtocol.DefaultChunkSize];
                            while (true)
                            {
                                if (ct.IsCancellationRequested) throw new OperationCanceledException();
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

                                sent += read;
                                progress?.Invoke(sent, fi.Length);
                            }

                            var end = new Dictionary<string, object>
                            {
                                { "type", "FILE_END" },
                                { "reqId", Guid.NewGuid().ToString() },
                                { "transferId", transferId },
                                { "file", new Dictionary<string, object> { { "size", fi.Length }, { "sha256", sha256 } } }
                            };
                            writer.WriteFrame(new Frame(FrameKind.Json, _json.Encode(end)));

                            var done = ReadJson(reader);
                            EnsureOk(done);

                            Logger.Info("TransferClient", "Upload complete. TransferId=" + transferId + " Bytes=" + sent + "/" + fi.Length + " Offset=" + offset);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    Logger.Info("TransferClient", "Upload canceled. TransferId=" + transferId);
                    throw;
                }
                catch (Exception ex)
                {
                    Logger.Error("TransferClient", "Upload failed. TransferId=" + transferId + " ShareId=" + shareId + " Path=" + (remotePath ?? ""), ex);
                    throw;
                }
            }
        }

        private void Handshake(FrameWriter writer, FrameReader reader, string authMode)
        {
            var hello = new Dictionary<string, object>
            {
                { "type", "HELLO" },
                { "reqId", Guid.NewGuid().ToString() },
                { "proto", NetShareProtocol.ProtocolVersion },
                { "deviceId", _settings.DeviceId },
                { "deviceName", _settings.DeviceName },
                { "auth", authMode ?? (_settings.OpenMode ? "open" : "psk-hmac-sha256") }
            };
            writer.WriteFrame(new Frame(FrameKind.Json, _json.Encode(hello)));
            var ack = ReadJson(reader);
            EnsureOk(ack);

            if (_settings.OpenMode || string.Equals(authMode, "open", StringComparison.OrdinalIgnoreCase)) return;

            var serverId = ack["serverId"].ToString();
            var serverNonce = Convert.FromBase64String(ack["nonce"].ToString());
            var clientNonce = Security.HmacAuth.RandomNonce();
            var mac = Security.HmacAuth.ComputeMac(_settings.AccessKey ?? "", serverNonce, clientNonce, serverId, _settings.DeviceId);
            var auth = new Dictionary<string, object>
            {
                { "type", "AUTH" },
                { "reqId", Guid.NewGuid().ToString() },
                { "clientNonce", Convert.ToBase64String(clientNonce) },
                { "mac", Convert.ToBase64String(mac) }
            };
            writer.WriteFrame(new Frame(FrameKind.Json, _json.Encode(auth)));
            var ok = ReadJson(reader);
            EnsureOk(ok);
        }

        private Dictionary<string, object> ReadJson(FrameReader reader)
        {
            var frame = reader.ReadFrame();
            if (frame == null) throw new InvalidOperationException("Connection closed.");
            if (frame.Kind != FrameKind.Json) throw new InvalidOperationException("Expected JSON frame.");
            return (Dictionary<string, object>)_json.DecodeUntyped(frame.Payload);
        }

        private static void EnsureOk(Dictionary<string, object> resp)
        {
            if (resp.TryGetValue("ok", out var okObj) && okObj is bool ok && ok) return;
            if (resp.TryGetValue("error", out var err) && err is Dictionary<string, object> e)
            {
                var code = e.ContainsKey("code") ? e["code"].ToString() : "ERROR";
                var msg = e.ContainsKey("message") ? e["message"].ToString() : "Unknown";
                throw new InvalidOperationException(code + ": " + msg);
            }
            throw new InvalidOperationException("Transfer failed.");
        }

        private static string ToHex(byte[] bytes)
        {
            var sb = new System.Text.StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
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

        private long VerifyLocalFileWithHash(IPAddress address, int port, string authMode, string shareId, string remotePath, string localPath)
        {
            Logger.Debug("TransferClient", "Verifying local file before download. ShareId=" + shareId + " Path=" + remotePath);

            var statReq = new Dictionary<string, object>
            {
                { "type", "STAT" },
                { "reqId", Guid.NewGuid().ToString() },
                { "shareId", shareId },
                { "path", remotePath }
            };

            using (var tcp = new TcpClient())
            {
                tcp.Connect(address, port);
                using (var stream = tcp.GetStream())
                {
                    var reader = new FrameReader(stream);
                    var writer = new FrameWriter(stream);

                    Handshake(writer, reader, authMode);
                    writer.WriteFrame(new Frame(FrameKind.Json, _json.Encode(statReq)));

                    var statResp = ReadJson(reader);
                    EnsureOk(statResp);

                    var remoteSize = Convert.ToInt64(((Dictionary<string, object>)statResp["stat"])["size"]);
                    var remoteHash = ((Dictionary<string, object>)statResp["stat"])["sha256"].ToString();

                    if (!File.Exists(localPath)) return 0; // No local file, start fresh.

                    using (var file = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        if (file.Length == remoteSize)
                        {
                            using (var sha = SHA256.Create())
                            {
                                var localHash = ToHex(sha.ComputeHash(file));
                                if (string.Equals(localHash, remoteHash, StringComparison.OrdinalIgnoreCase))
                                {
                                    Logger.Info("TransferClient", "Local file matches remote. Skipping download.");
                                    return -1; // Signal to skip download.
                                }
                            }
                        }

                        if (file.Length < remoteSize)
                        {
                            var hashReq = new Dictionary<string, object>
                            {
                                { "type", "HASH_REQ" },
                                { "reqId", Guid.NewGuid().ToString() },
                                { "shareId", shareId },
                                { "path", remotePath },
                                { "offset", 0 },
                                { "length", file.Length }
                            };
                            writer.WriteFrame(new Frame(FrameKind.Json, _json.Encode(hashReq)));

                            var hashResp = ReadJson(reader);
                            EnsureOk(hashResp);

                            var remotePrefixHash = hashResp["hash"].ToString();
                            using (var sha = SHA256.Create())
                            {
                                HashPrefix(sha, file, file.Length);
                                var localPrefixHash = ToHex(sha.Hash);
                                if (string.Equals(localPrefixHash, remotePrefixHash, StringComparison.OrdinalIgnoreCase))
                                {
                                    Logger.Info("TransferClient", "Local file prefix matches remote. Resuming download.");
                                    return file.Length; // Resume from this offset.
                                }
                            }
                        }
                    }

                    Logger.Info("TransferClient", "Local file does not match remote. Restarting download.");
                    File.Delete(localPath);
                    return 0; // Restart from scratch.
                }
            }
        }
    }
}
