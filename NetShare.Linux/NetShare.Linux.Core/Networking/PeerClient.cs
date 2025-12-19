using System.Net;
using System.Net.Sockets;
using NetShare.Linux.Core.Protocol;
using NetShare.Linux.Core.Security;
using NetShare.Linux.Core.Settings;

namespace NetShare.Linux.Core.Networking;

public sealed class PeerClient : IDisposable
{
    private readonly JsonCodec _json = new();

    private readonly AppSettings _settings;

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private FrameReader? _reader;
    private FrameWriter? _writer;

    private string? _serverId;
    private byte[]? _serverNonce;
    private string? _selectedAuth;
    private bool _authRequired;

    public PeerClient(AppSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public async Task ConnectAsync(IPAddress address, int port, CancellationToken ct)
    {
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(address, port, ct);
        _stream = _tcp.GetStream();
        _stream.ReadTimeout = NetShareProtocol.DefaultSocketTimeoutMs;
        _stream.WriteTimeout = NetShareProtocol.DefaultSocketTimeoutMs;
        _reader = new FrameReader(_stream);
        _writer = new FrameWriter(_stream);
    }

    public async Task HelloAndAuthAsync(CancellationToken ct)
    {
        EnsureConnected();

        var reqId = Guid.NewGuid().ToString();
        var hello = new Dictionary<string, object?>
        {
            { "type", "HELLO" },
            { "reqId", reqId },
            { "proto", NetShareProtocol.ProtocolVersion },
            { "deviceId", _settings.DeviceId },
            { "deviceName", _settings.DeviceName },
            // Prefer open by default, but we will follow server's authRequired/selectedAuth.
            { "auth", string.IsNullOrWhiteSpace(_settings.AccessKey) || _settings.OpenMode ? "open" : "psk-hmac-sha256" }
        };
        SendJson(hello);

        var ack = await ReceiveJsonAsync(ct);
        var type = GetString(ack, "type");
        if (!string.Equals(type, "HELLO_ACK", StringComparison.Ordinal))
            throw new IOException("Expected HELLO_ACK");

        var ok = GetBool(ack, "ok");
        if (!ok)
            throw new IOException($"HELLO failed: {GetErrorSummary(ack)}");

        _serverId = GetString(ack, "serverId");
        _serverNonce = Convert.FromBase64String(GetString(ack, "nonce") ?? "");
        _authRequired = GetBool(ack, "authRequired");
        _selectedAuth = GetString(ack, "selectedAuth") ?? "open";

        if (_authRequired || string.Equals(_selectedAuth, "psk-hmac-sha256", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(_settings.AccessKey))
                throw new IOException("Server requires PSK but Access Key is not configured.");

            var clientNonce = HmacAuth.RandomNonce();
            var mac = HmacAuth.ComputeMac(_settings.AccessKey!, _serverNonce!, clientNonce, _serverId ?? "", _settings.DeviceId);

            var authReqId = Guid.NewGuid().ToString();
            var auth = new Dictionary<string, object?>
            {
                { "type", "AUTH" },
                { "reqId", authReqId },
                { "clientNonce", Convert.ToBase64String(clientNonce) },
                { "mac", Convert.ToBase64String(mac) }
            };
            SendJson(auth);

            var authOk = await ReceiveJsonAsync(ct);
            if (!string.Equals(GetString(authOk, "type"), "AUTH_OK", StringComparison.Ordinal))
                throw new IOException("Expected AUTH_OK");
            if (!GetBool(authOk, "ok"))
                throw new IOException($"AUTH failed: {GetErrorSummary(authOk)}");
        }
    }

    public async Task<List<Dictionary<string, object?>>> ListSharesAsync(CancellationToken ct)
    {
        var reqId = Guid.NewGuid().ToString();
        SendJson(new Dictionary<string, object?> { { "type", "LIST_SHARES" }, { "reqId", reqId } });

        var resp = await ReceiveJsonAsync(ct);
        if (!string.Equals(GetString(resp, "type"), "LIST_SHARES_RESP", StringComparison.Ordinal))
            throw new IOException("Expected LIST_SHARES_RESP");
        if (!GetBool(resp, "ok"))
            throw new IOException(GetErrorSummary(resp));

        return (resp.TryGetValue("shares", out var v) && v is List<object?> arr)
            ? arr.OfType<Dictionary<string, object?>>().ToList()
            : new List<Dictionary<string, object?>>();
    }

    public async Task<List<PeerDirectoryEntry>> ListDirAsync(string shareId, string path, CancellationToken ct)
    {
        var reqId = Guid.NewGuid().ToString();
        SendJson(new Dictionary<string, object?>
        {
            { "type", "LIST_DIR" },
            { "reqId", reqId },
            { "shareId", shareId },
            { "path", path ?? "" }
        });

        var resp = await ReceiveJsonAsync(ct);
        if (!string.Equals(GetString(resp, "type"), "LIST_DIR_RESP", StringComparison.Ordinal))
            throw new IOException("Expected LIST_DIR_RESP");
        if (!GetBool(resp, "ok"))
            throw new IOException(GetErrorSummary(resp));

        var list = new List<PeerDirectoryEntry>();
        if (resp.TryGetValue("entries", out var v) && v is List<object?> entries)
        {
            foreach (var e in entries.OfType<Dictionary<string, object?>>())
            {
                var name = GetString(e, "name") ?? "";
                var isDir = GetBool(e, "isDir");

                DateTime? mtime = null;
                var mtimeStr = GetString(e, "mtimeUtc");
                if (!string.IsNullOrWhiteSpace(mtimeStr) && DateTime.TryParse(mtimeStr, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt))
                    mtime = dt;

                long? size = null;
                if (e.TryGetValue("size", out var sizeObj) && sizeObj != null)
                    size = Convert.ToInt64(sizeObj);

                list.Add(new PeerDirectoryEntry { Name = name, IsDir = isDir, Size = size, MtimeUtc = mtime });
            }
        }

        return list;
    }

    public async Task<(long Size, string Sha256)> StatAsync(string shareId, string path, CancellationToken ct)
    {
        var reqId = Guid.NewGuid().ToString();
        SendJson(new Dictionary<string, object?>
        {
            { "type", "STAT" },
            { "reqId", reqId },
            { "shareId", shareId },
            { "path", path ?? "" }
        });

        var resp = await ReceiveJsonAsync(ct);
        if (!string.Equals(GetString(resp, "type"), "STAT_RESP", StringComparison.Ordinal))
            throw new IOException("Expected STAT_RESP");
        if (!GetBool(resp, "ok"))
            throw new IOException(GetErrorSummary(resp));

        var stat = (Dictionary<string, object?>)resp["stat"]!;
        var size = Convert.ToInt64(stat["size"]!);
        var sha = (stat["sha256"]?.ToString() ?? "");
        return (size, sha);
    }

    public async Task DownloadAsync(string shareId, string path, string localFile, long offset, IProgress<(long done, long total)>? progress, CancellationToken ct)
    {
        EnsureConnected();

        Directory.CreateDirectory(Path.GetDirectoryName(localFile)!);

        var transferId = Guid.NewGuid().ToString();
        var reqId = Guid.NewGuid().ToString();
        SendJson(new Dictionary<string, object?>
        {
            { "type", "DOWNLOAD_REQ" },
            { "reqId", reqId },
            { "transferId", transferId },
            { "shareId", shareId },
            { "path", path ?? "" },
            { "offset", offset }
        });

        var ack = await ReceiveJsonAsync(ct);
        if (!string.Equals(GetString(ack, "type"), "DOWNLOAD_ACK", StringComparison.Ordinal))
            throw new IOException("Expected DOWNLOAD_ACK");
        if (!GetBool(ack, "ok"))
            throw new IOException(GetErrorSummary(ack));

        var fileObj = (Dictionary<string, object?>)ack["file"]!;
        long total = Convert.ToInt64(fileObj["size"]!);
        var expectedSha = (fileObj["sha256"]?.ToString() ?? "");

        long serverOffset = Convert.ToInt64(ack["offset"]!);

        using var sha = System.Security.Cryptography.SHA256.Create();

        // Seed SHA with existing local prefix if resuming.
        if (serverOffset > 0 && File.Exists(localFile))
        {
            using var prefix = new FileStream(localFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            HashPrefix(sha, prefix, serverOffset);
        }

        using var fs = new FileStream(localFile, serverOffset > 0 ? FileMode.OpenOrCreate : FileMode.Create, FileAccess.Write, FileShare.Read);
        fs.Position = serverOffset;

        long written = serverOffset;
        while (true)
        {
            var hdr = await ReceiveJsonAsync(ct);
            var type = GetString(hdr, "type");

            if (string.Equals(type, "FILE_END", StringComparison.Ordinal))
            {
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

                var endFile = (Dictionary<string, object?>)hdr["file"]!;
                var endSha = (endFile["sha256"]?.ToString() ?? "");
                var actual = Util.HashUtil.ToHexLower(sha.Hash!);

                if (!string.Equals(expectedSha, actual, StringComparison.OrdinalIgnoreCase) || !string.Equals(endSha, actual, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"INTEGRITY_FAILED: expected={expectedSha} end={endSha} actual={actual}");

                progress?.Report((total, total));
                return;
            }

            if (!string.Equals(type, "FILE_CHUNK", StringComparison.Ordinal))
                throw new IOException($"Unexpected message: {type}");

            var length = Convert.ToInt32(hdr["length"]!);
            var bin = ReceiveFrame(ct);
            if (bin is null || bin.Kind != FrameKind.Binary || bin.Payload.Length != length)
                throw new IOException("Bad binary chunk.");

            fs.Write(bin.Payload, 0, bin.Payload.Length);
            sha.TransformBlock(bin.Payload, 0, bin.Payload.Length, null, 0);

            written += bin.Payload.Length;
            progress?.Report((written, total));
        }
    }

    public async Task UploadAsync(string shareId, string path, string localFile, IProgress<(long done, long total)>? progress, CancellationToken ct)
    {
        EnsureConnected();

        var fi = new FileInfo(localFile);
        if (!fi.Exists) throw new FileNotFoundException(localFile);

        string shaHex;
        using (var rs = fi.OpenRead())
            shaHex = Util.HashUtil.Sha256HexLower(rs);

        var transferId = Guid.NewGuid().ToString();
        var reqId = Guid.NewGuid().ToString();

        SendJson(new Dictionary<string, object?>
        {
            { "type", "UPLOAD_REQ" },
            { "reqId", reqId },
            { "transferId", transferId },
            { "shareId", shareId },
            { "path", path ?? "" },
            { "file", new Dictionary<string, object?> { { "size", fi.Length }, { "sha256", shaHex } } }
        });

        var ack = await ReceiveJsonAsync(ct);
        if (!string.Equals(GetString(ack, "type"), "UPLOAD_ACK", StringComparison.Ordinal))
            throw new IOException("Expected UPLOAD_ACK");
        if (!GetBool(ack, "ok"))
            throw new IOException(GetErrorSummary(ack));

        long offset = Convert.ToInt64(ack["offset"]!);
        if (offset < 0) offset = 0;
        if (offset > fi.Length) offset = 0;

        using var rs2 = fi.OpenRead();
        rs2.Position = offset;

        long sent = offset;
        var buffer = new byte[NetShareProtocol.DefaultChunkSize];

        while (sent < fi.Length)
        {
            int read = await rs2.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (read <= 0) break;

            var hdr = new Dictionary<string, object?>
            {
                { "type", "FILE_CHUNK" },
                { "reqId", Guid.NewGuid().ToString() },
                { "transferId", transferId },
                { "offset", sent },
                { "length", read }
            };
            SendJson(hdr);

            var chunk = new byte[read];
            Buffer.BlockCopy(buffer, 0, chunk, 0, read);
            SendBinary(chunk);

            sent += read;
            progress?.Report((sent, fi.Length));
        }

        // FILE_END
        SendJson(new Dictionary<string, object?>
        {
            { "type", "FILE_END" },
            { "reqId", Guid.NewGuid().ToString() },
            { "transferId", transferId },
            { "file", new Dictionary<string, object?> { { "size", fi.Length }, { "sha256", shaHex } } }
        });

        var done = await ReceiveJsonAsync(ct);
        if (!string.Equals(GetString(done, "type"), "UPLOAD_DONE", StringComparison.Ordinal))
            throw new IOException("Expected UPLOAD_DONE");

        if (!GetBool(done, "ok"))
            throw new IOException(GetErrorSummary(done));

        progress?.Report((fi.Length, fi.Length));
    }

    private void SendJson(Dictionary<string, object?> msg)
    {
        EnsureConnected();
        _writer!.WriteFrame(new Frame(FrameKind.Json, _json.Encode(msg)));
    }

    private void SendBinary(byte[] bytes)
    {
        EnsureConnected();
        _writer!.WriteFrame(new Frame(FrameKind.Binary, bytes));
    }

    private async Task<Dictionary<string, object?>> ReceiveJsonAsync(CancellationToken ct)
    {
        // FrameReader is sync; wrap in Task to avoid blocking GUI thread.
        return await Task.Run(() =>
        {
            var f = ReceiveFrame(ct);
            if (f is null) throw new IOException("Connection closed.");
            if (f.Kind != FrameKind.Json) throw new IOException("Expected JSON frame.");
            var obj = _json.DecodeUntyped(f.Payload);
            return (Dictionary<string, object?>)(obj ?? throw new IOException("Bad JSON"));
        }, ct);
    }

    private Frame? ReceiveFrame(CancellationToken ct)
    {
        EnsureConnected();
        ct.ThrowIfCancellationRequested();
        return _reader!.ReadFrame();
    }

    private void EnsureConnected()
    {
        if (_tcp is null || _stream is null || _reader is null || _writer is null)
            throw new InvalidOperationException("Not connected.");
    }

    private static string? GetString(Dictionary<string, object?> obj, string key)
    {
        return obj.TryGetValue(key, out var v) ? v?.ToString() : null;
    }

    private static bool GetBool(Dictionary<string, object?> obj, string key)
    {
        return obj.TryGetValue(key, out var v) && v is bool b && b;
    }

    private static string GetErrorSummary(Dictionary<string, object?> obj)
    {
        if (!obj.TryGetValue("error", out var e) || e is not Dictionary<string, object?> err) return "error";
        var code = err.TryGetValue("code", out var c) ? c?.ToString() : "";
        var msg = err.TryGetValue("message", out var m) ? m?.ToString() : "";
        return $"{code}: {msg}";
    }

    private static void HashPrefix(System.Security.Cryptography.SHA256 sha, Stream stream, long bytes)
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

    public void Dispose()
    {
        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        _stream = null;
        _tcp = null;
        _reader = null;
        _writer = null;
    }
}
