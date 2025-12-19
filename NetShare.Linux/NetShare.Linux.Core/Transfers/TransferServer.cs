using System.Security.Cryptography;
using NetShare.Linux.Core.Protocol;
using NetShare.Linux.Core.Settings;
using NetShare.Linux.Core.Sharing;
using NetShare.Linux.Core.Util;

namespace NetShare.Linux.Core.Transfers;

public sealed class TransferServer
{
    private readonly JsonCodec _json = new();
    private readonly ShareManager _shares;
    private readonly AppSettings _settings;

    public TransferServer(ShareManager shares, AppSettings settings)
    {
        _shares = shares ?? throw new ArgumentNullException(nameof(shares));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public void HandleStat(FrameWriter writer, string reqId, Dictionary<string, object?> msg)
    {
        var shareId = GetString(msg, "shareId") ?? "";
        var path = GetString(msg, "path") ?? "";

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
                sha = HashUtil.Sha256HexLower(fs);

            var stat = new Dictionary<string, object?>
            {
                { "size", fi.Length },
                { "mtimeUtc", fi.LastWriteTimeUtc.ToString("o") },
                { "sha256", sha }
            };

            writer.WriteFrame(new Frame(FrameKind.Json, _json.Encode(new Dictionary<string, object?>
            {
                { "type", "STAT_RESP" }, { "reqId", reqId }, { "ok", true }, { "stat", stat }
            })));
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

    public void HandleDownload(System.Net.Sockets.TcpClient client, FrameWriter writer, FrameReader reader, string reqId, Dictionary<string, object?> msg)
    {
        var shareId = GetString(msg, "shareId") ?? "";
        var path = GetString(msg, "path") ?? "";
        var transferId = GetString(msg, "transferId") ?? "";
        long offset = GetLong(msg, "offset");

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
            string shaHex;
            using (var fs0 = fi.OpenRead())
                shaHex = HashUtil.Sha256HexLower(fs0);

            if (offset < 0) offset = 0;
            if (offset > fi.Length) offset = fi.Length;

            writer.WriteFrame(new Frame(FrameKind.Json, _json.Encode(new Dictionary<string, object?>
            {
                { "type", "DOWNLOAD_ACK" },
                { "reqId", reqId },
                { "ok", true },
                { "file", new Dictionary<string, object?> { { "size", fi.Length }, { "sha256", shaHex } } },
                { "offset", offset }
            })));

            using var fs = fi.OpenRead();
            using var sha = SHA256.Create();

            if (offset > 0)
                HashPrefix(sha, fs, offset);

            fs.Position = offset;

            long sent = offset;
            var buffer = new byte[NetShareProtocol.DefaultChunkSize];
            while (sent < fi.Length)
            {
                int read = fs.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;

                writer.WriteFrame(new Frame(FrameKind.Json, _json.Encode(new Dictionary<string, object?>
                {
                    { "type", "FILE_CHUNK" },
                    { "reqId", Guid.NewGuid().ToString() },
                    { "transferId", transferId },
                    { "offset", sent },
                    { "length", read }
                })));

                var chunk = new byte[read];
                Buffer.BlockCopy(buffer, 0, chunk, 0, read);

                writer.WriteFrame(new Frame(FrameKind.Binary, chunk));
                sha.TransformBlock(chunk, 0, chunk.Length, null, 0);

                sent += read;
            }

            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            writer.WriteFrame(new Frame(FrameKind.Json, _json.Encode(new Dictionary<string, object?>
            {
                { "type", "FILE_END" },
                { "reqId", Guid.NewGuid().ToString() },
                { "transferId", transferId },
                { "ok", true },
                { "file", new Dictionary<string, object?> { { "size", fi.Length }, { "sha256", HashUtil.ToHexLower(sha.Hash!) } } }
            })));
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

    public void HandleUpload(System.Net.Sockets.TcpClient client, FrameWriter writer, FrameReader reader, string reqId, Dictionary<string, object?> msg)
    {
        var shareId = GetString(msg, "shareId") ?? "";
        var path = GetString(msg, "path") ?? "";

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
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);

            if (msg["file"] is not Dictionary<string, object?> fileObj)
            {
                SendError(writer, reqId, "UPLOAD_ACK", ErrorCodes.BadRequest, "Missing file object.");
                return;
            }

            long totalSize = Convert.ToInt64(fileObj["size"]!);
            var expectedHex = (fileObj["sha256"]?.ToString() ?? "");

            long offset = 0;
            if (File.Exists(full))
            {
                var existing = new FileInfo(full);
                offset = existing.Length;
                if (offset > totalSize) offset = 0;
            }

            writer.WriteFrame(new Frame(FrameKind.Json, _json.Encode(new Dictionary<string, object?>
            {
                { "type", "UPLOAD_ACK" }, { "reqId", reqId }, { "ok", true }, { "offset", offset }
            })));

            using var fs = new FileStream(full, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            using var sha = SHA256.Create();

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
                if (hdrFrame is null)
                {
                    SendError(writer, reqId, "UPLOAD_DONE", ErrorCodes.IoError, "Connection closed.");
                    return;
                }
                if (hdrFrame.Kind != FrameKind.Json)
                {
                    SendError(writer, reqId, "UPLOAD_DONE", ErrorCodes.BadRequest, "Expected JSON header.");
                    return;
                }

                var hdrObj = (Dictionary<string, object?>?)_json.DecodeUntyped(hdrFrame.Payload);
                if (hdrObj is null)
                {
                    SendError(writer, reqId, "UPLOAD_DONE", ErrorCodes.BadRequest, "Bad JSON.");
                    return;
                }

                var type = GetString(hdrObj, "type") ?? "";

                if (string.Equals(type, "FILE_END", StringComparison.OrdinalIgnoreCase))
                {
                    sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    var actualHex = HashUtil.ToHexLower(sha.Hash!);

                    var endFile = (Dictionary<string, object?>)hdrObj["file"]!;
                    var endHex = (endFile["sha256"]?.ToString() ?? "");

                    if (!string.Equals(expectedHex, actualHex, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(endHex, actualHex, StringComparison.OrdinalIgnoreCase))
                    {
                        SendError(writer, reqId, "UPLOAD_DONE", ErrorCodes.IntegrityFailed, "Upload hash mismatch.");
                        return;
                    }

                    writer.WriteFrame(new Frame(FrameKind.Json, _json.Encode(new Dictionary<string, object?>
                    {
                        { "type", "UPLOAD_DONE" }, { "reqId", reqId }, { "ok", true }
                    })));
                    return;
                }

                if (!string.Equals(type, "FILE_CHUNK", StringComparison.OrdinalIgnoreCase))
                {
                    SendError(writer, reqId, "UPLOAD_DONE", ErrorCodes.BadRequest, "Unexpected message.");
                    return;
                }

                int length = Convert.ToInt32(hdrObj["length"]!);
                var bin = reader.ReadFrame();
                if (bin is null || bin.Kind != FrameKind.Binary || bin.Payload.Length != length)
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
        writer.WriteFrame(new Frame(FrameKind.Json, _json.Encode(new Dictionary<string, object?>
        {
            { "type", respType },
            { "reqId", reqId },
            { "ok", false },
            { "error", new Dictionary<string, object?> { { "code", code }, { "message", message } } }
        })));
    }

    private static string? GetString(Dictionary<string, object?> obj, string key)
    {
        return obj.TryGetValue(key, out var v) ? v?.ToString() : null;
    }

    private static long GetLong(Dictionary<string, object?> obj, string key)
    {
        return obj.TryGetValue(key, out var v) && v != null ? Convert.ToInt64(v) : 0;
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
}
