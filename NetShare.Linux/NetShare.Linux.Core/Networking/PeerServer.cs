using System.Net;
using System.Net.Sockets;
using NetShare.Linux.Core.Protocol;
using NetShare.Linux.Core.Security;
using NetShare.Linux.Core.Settings;
using NetShare.Linux.Core.Sharing;
using NetShare.Linux.Core.Transfers;

namespace NetShare.Linux.Core.Networking;

public sealed class PeerServer : IDisposable
{
    private readonly JsonCodec _json = new();
    private readonly ShareManager _shares;
    private readonly AppSettings _settings;
    private readonly TransferServer _transferServer;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public PeerServer(ShareManager shares, AppSettings settings)
    {
        _shares = shares ?? throw new ArgumentNullException(nameof(shares));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _transferServer = new TransferServer(_shares, _settings);
    }

    public void Start(int port)
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _ = Task.Run(() => AcceptLoop(_cts.Token));
    }

    private void AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = _listener!.AcceptTcpClient();
                _ = Task.Run(() => HandleClient(client, ct));
            }
            catch
            {
                if (ct.IsCancellationRequested) return;
            }
        }
    }

    private void HandleClient(TcpClient client, CancellationToken ct)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            stream.ReadTimeout = NetShareProtocol.DefaultSocketTimeoutMs;
            stream.WriteTimeout = NetShareProtocol.DefaultSocketTimeoutMs;

            var reader = new FrameReader(stream);
            var writer = new FrameWriter(stream);

            string clientDeviceId = "";
            bool authed = false;
            byte[] serverNonce = HmacAuth.RandomNonce();

            while (!ct.IsCancellationRequested)
            {
                var frame = reader.ReadFrame();
                if (frame is null) return;
                if (frame.Kind != FrameKind.Json) return;

                var msg = (Dictionary<string, object?>?)_json.DecodeUntyped(frame.Payload);
                if (msg is null) return;

                var type = GetString(msg, "type") ?? "";
                var reqId = GetString(msg, "reqId") ?? "";

                if (string.Equals(type, "HELLO", StringComparison.OrdinalIgnoreCase))
                {
                    clientDeviceId = GetString(msg, "deviceId") ?? "";
                    var proto = GetString(msg, "proto") ?? "";
                    var requestedAuth = GetString(msg, "auth") ?? "open";

                    if (!string.Equals(proto, NetShareProtocol.ProtocolVersion, StringComparison.Ordinal))
                    {
                        SendError(writer, reqId, "HELLO_ACK", ErrorCodes.UnsupportedVersion, "Unsupported protocol version.");
                        return;
                    }

                    if (!string.Equals(requestedAuth, "open", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(requestedAuth, "psk-hmac-sha256", StringComparison.OrdinalIgnoreCase))
                    {
                        SendError(writer, reqId, "HELLO_ACK", ErrorCodes.BadRequest, "Unknown auth mode.");
                        return;
                    }

                    // Windows semantics: if OpenMode=false, AUTH required regardless of what client asked.
                    authed = _settings.OpenMode;

                    var resp = new Dictionary<string, object?>
                    {
                        { "type", "HELLO_ACK" },
                        { "reqId", reqId },
                        { "ok", true },
                        { "serverId", _settings.DeviceId },
                        { "nonce", Convert.ToBase64String(serverNonce) },
                        { "auth", new[] { "open", "psk-hmac-sha256" } },
                        { "authRequired", !_settings.OpenMode },
                        { "selectedAuth", _settings.OpenMode ? "open" : "psk-hmac-sha256" }
                    };
                    writer.WriteFrame(new Frame(FrameKind.Json, _json.Encode(resp)));
                    continue;
                }

                if (string.Equals(type, "AUTH", StringComparison.OrdinalIgnoreCase))
                {
                    if (_settings.OpenMode)
                    {
                        writer.WriteFrame(new Frame(FrameKind.Json, _json.Encode(new Dictionary<string, object?>
                        {
                            { "type", "AUTH_OK" }, { "reqId", reqId }, { "ok", true }
                        })));
                        authed = true;
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(_settings.AccessKey))
                    {
                        SendError(writer, reqId, "AUTH_OK", ErrorCodes.AuthRequired, "Access key required.");
                        return;
                    }

                    byte[] clientNonce;
                    byte[] mac;
                    try
                    {
                        clientNonce = Convert.FromBase64String(GetString(msg, "clientNonce") ?? "");
                        mac = Convert.FromBase64String(GetString(msg, "mac") ?? "");
                    }
                    catch
                    {
                        SendError(writer, reqId, "AUTH_OK", ErrorCodes.BadRequest, "Bad base64 in auth.");
                        return;
                    }

                    var expected = HmacAuth.ComputeMac(_settings.AccessKey!, serverNonce, clientNonce, _settings.DeviceId, clientDeviceId);
                    if (!HmacAuth.ConstantTimeEquals(expected, mac))
                    {
                        SendError(writer, reqId, "AUTH_OK", ErrorCodes.AuthFailed, "Authentication failed.");
                        return;
                    }

                    authed = true;
                    writer.WriteFrame(new Frame(FrameKind.Json, _json.Encode(new Dictionary<string, object?>
                    {
                        { "type", "AUTH_OK" }, { "reqId", reqId }, { "ok", true }
                    })));
                    continue;
                }

                if (!authed)
                {
                    SendError(writer, reqId, type + "_RESP", ErrorCodes.AuthRequired, "Authenticate first.");
                    return;
                }

                if (string.Equals(type, "PING", StringComparison.OrdinalIgnoreCase))
                {
                    writer.WriteFrame(new Frame(FrameKind.Json, _json.Encode(new Dictionary<string, object?>
                    {
                        { "type", "PONG" }, { "reqId", reqId }, { "ok", true }
                    })));
                    continue;
                }

                if (string.Equals(type, "LIST_SHARES", StringComparison.OrdinalIgnoreCase))
                {
                    var shares = _shares.GetShares();
                    var list = shares.Select(s => new Dictionary<string, object?>
                    {
                        { "shareId", s.ShareId },
                        { "name", s.Name },
                        { "readOnly", s.ReadOnly }
                    }).ToList();

                    writer.WriteFrame(new Frame(FrameKind.Json, _json.Encode(new Dictionary<string, object?>
                    {
                        { "type", "LIST_SHARES_RESP" },
                        { "reqId", reqId },
                        { "ok", true },
                        { "shares", list }
                    })));
                    continue;
                }

                if (string.Equals(type, "LIST_DIR", StringComparison.OrdinalIgnoreCase))
                {
                    var shareId = GetString(msg, "shareId") ?? "";
                    var path = GetString(msg, "path") ?? "";

                    if (!_shares.TryGetShare(shareId, out var share))
                    {
                        SendError(writer, reqId, "LIST_DIR_RESP", ErrorCodes.NotFound, "Share not found.");
                        continue;
                    }

                    try
                    {
                        var full = SafePath.CombineAndValidate(share.LocalPath, path);
                        if (!Directory.Exists(full))
                        {
                            SendError(writer, reqId, "LIST_DIR_RESP", ErrorCodes.NotFound, "Directory not found.");
                            continue;
                        }

                        var entries = new List<Dictionary<string, object?>>();
                        foreach (var dir in Directory.GetDirectories(full))
                        {
                            var di = new DirectoryInfo(dir);
                            entries.Add(new Dictionary<string, object?> { { "name", di.Name }, { "isDir", true } });
                        }
                        foreach (var file in Directory.GetFiles(full))
                        {
                            var fi = new FileInfo(file);
                            entries.Add(new Dictionary<string, object?>
                            {
                                { "name", fi.Name },
                                { "isDir", false },
                                { "size", fi.Length },
                                { "mtimeUtc", fi.LastWriteTimeUtc.ToString("o") }
                            });
                        }

                        writer.WriteFrame(new Frame(FrameKind.Json, _json.Encode(new Dictionary<string, object?>
                        {
                            { "type", "LIST_DIR_RESP" },
                            { "reqId", reqId },
                            { "ok", true },
                            { "entries", entries }
                        })));
                    }
                    catch (InvalidOperationException)
                    {
                        SendError(writer, reqId, "LIST_DIR_RESP", ErrorCodes.PathTraversal, "Path traversal rejected.");
                    }
                    catch (Exception ex)
                    {
                        SendError(writer, reqId, "LIST_DIR_RESP", ErrorCodes.IoError, ex.Message);
                    }

                    continue;
                }

                if (string.Equals(type, "STAT", StringComparison.OrdinalIgnoreCase))
                {
                    _transferServer.HandleStat(writer, reqId, msg);
                    continue;
                }

                if (string.Equals(type, "DOWNLOAD_REQ", StringComparison.OrdinalIgnoreCase))
                {
                    _transferServer.HandleDownload(client, writer, reader, reqId, msg);
                    return;
                }

                if (string.Equals(type, "UPLOAD_REQ", StringComparison.OrdinalIgnoreCase))
                {
                    _transferServer.HandleUpload(client, writer, reader, reqId, msg);
                    return;
                }

                SendError(writer, reqId, type + "_RESP", ErrorCodes.BadRequest, "Unknown message type.");
            }
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

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
    }
}
