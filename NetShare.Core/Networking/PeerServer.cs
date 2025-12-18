using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NetShare.Core.Logging;
using NetShare.Core.Protocol;
using NetShare.Core.Security;
using NetShare.Core.Settings;
using NetShare.Core.Sharing;
using NetShare.Core.Transfers;

namespace NetShare.Core.Networking
{
    public sealed class PeerServer : IDisposable
    {
        private readonly JsonCodec _json = new JsonCodec();
        private readonly ShareManager _shares;
        private readonly AppSettings _settings;
        private readonly TransferServer _transferServer;

        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private Task _acceptLoop;

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
            Logger.Info("PeerServer", "Listening. Port=" + port);
            _acceptLoop = Task.Run(() => AcceptLoop(_cts.Token));
        }

        private void AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var client = _listener.AcceptTcpClient();
                    try
                    {
                        var ep = client.Client.RemoteEndPoint == null ? "" : client.Client.RemoteEndPoint.ToString();
                        Logger.Debug("PeerServer", "Client accepted. Remote=" + ep);
                    }
                    catch { }
                    Task.Run(() => HandleClient(client));
                }
                catch (SocketException)
                {
                    if (ct.IsCancellationRequested) return;
                }
                catch
                {
                    if (ct.IsCancellationRequested) return;
                }
            }
        }

        private void HandleClient(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                stream.ReadTimeout = NetShareProtocol.DefaultSocketTimeoutMs;
                stream.WriteTimeout = NetShareProtocol.DefaultSocketTimeoutMs;

                var reader = new FrameReader(stream);
                var writer = new FrameWriter(stream);

                string clientDeviceId = null;
                bool authed = false;
                byte[] serverNonce = HmacAuth.RandomNonce();

                var remote = "";
                try { remote = client.Client.RemoteEndPoint == null ? "" : client.Client.RemoteEndPoint.ToString(); } catch { }

                while (true)
                {
                    var frame = reader.ReadFrame();
                    if (frame == null) return;
                    if (frame.Kind != FrameKind.Json) return;

                    var msg = (Dictionary<string, object>)_json.DecodeUntyped(frame.Payload);
                    if (msg == null) return;

                    var type = GetString(msg, "type");
                    var reqId = GetString(msg, "reqId");

                    if (string.Equals(type, "HELLO", StringComparison.OrdinalIgnoreCase))
                    {
                        clientDeviceId = GetString(msg, "deviceId") ?? "";
                        var proto = GetString(msg, "proto");

                        var requestedAuth = GetString(msg, "auth") ?? "open";
                        Logger.Info("PeerServer", "HELLO. Remote=" + remote + " ClientId=" + clientDeviceId + " Proto=" + proto + " AuthReq=" + requestedAuth);

                        if (!string.Equals(proto, NetShareProtocol.ProtocolVersion, StringComparison.Ordinal))
                        {
                            SendError(remote, writer, reqId, "HELLO_ACK", ErrorCodes.UnsupportedVersion, "Unsupported protocol version.");
                            return;
                        }

                        var authMode = requestedAuth;
                        if (!string.Equals(authMode, "open", StringComparison.OrdinalIgnoreCase) && !string.Equals(authMode, "psk-hmac-sha256", StringComparison.OrdinalIgnoreCase))
                        {
                            SendError(remote, writer, reqId, "HELLO_ACK", ErrorCodes.BadRequest, "Unknown auth mode.");
                            return;
                        }

                        // Server policy: if OpenMode=false, client MUST authenticate regardless of what it asked for.
                        authed = _settings.OpenMode;
                        var resp = new Dictionary<string, object>
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
                            var resp = new Dictionary<string, object>
                            {
                                { "type", "AUTH_OK" },
                                { "reqId", reqId },
                                { "ok", true }
                            };
                            writer.WriteFrame(new Frame(FrameKind.Json, _json.Encode(resp)));
                            authed = true;
                            Logger.Info("PeerServer", "AUTH ok (open-mode). Remote=" + remote + " ClientId=" + (clientDeviceId ?? ""));
                            continue;
                        }

                        if (string.IsNullOrWhiteSpace(_settings.AccessKey))
                        {
                            SendError(remote, writer, reqId, "AUTH_OK", ErrorCodes.AuthRequired, "Access key required.");
                            return;
                        }

                        var clientNonceB64 = GetString(msg, "clientNonce") ?? "";
                        var macB64 = GetString(msg, "mac") ?? "";
                        byte[] clientNonce;
                        byte[] mac;
                        try
                        {
                            clientNonce = Convert.FromBase64String(clientNonceB64);
                            mac = Convert.FromBase64String(macB64);
                        }
                        catch
                        {
                            SendError(remote, writer, reqId, "AUTH_OK", ErrorCodes.BadRequest, "Bad base64 in auth.");
                            return;
                        }

                        var expected = HmacAuth.ComputeMac(_settings.AccessKey, serverNonce, clientNonce, _settings.DeviceId, clientDeviceId ?? "");
                        if (!HmacAuth.ConstantTimeEquals(expected, mac))
                        {
                            Logger.Warn("PeerServer", "AUTH failed. Remote=" + remote + " ClientId=" + (clientDeviceId ?? ""));
                            SendError(remote, writer, reqId, "AUTH_OK", ErrorCodes.AuthFailed, "Authentication failed.");
                            return;
                        }

                        authed = true;
                        var ok = new Dictionary<string, object>
                        {
                            { "type", "AUTH_OK" },
                            { "reqId", reqId },
                            { "ok", true }
                        };
                        writer.WriteFrame(new Frame(FrameKind.Json, _json.Encode(ok)));
                        Logger.Info("PeerServer", "AUTH ok. Remote=" + remote + " ClientId=" + (clientDeviceId ?? ""));
                        continue;
                    }

                    if (!authed)
                    {
                        SendError(remote, writer, reqId, type + "_RESP", ErrorCodes.AuthRequired, "Authenticate first.");
                        return;
                    }

                    if (string.Equals(type, "PING", StringComparison.OrdinalIgnoreCase))
                    {
                        var pong = new Dictionary<string, object>
                        {
                            { "type", "PONG" },
                            { "reqId", reqId },
                            { "ok", true }
                        };
                        writer.WriteFrame(new Frame(FrameKind.Json, _json.Encode(pong)));
                        continue;
                    }

                    if (string.Equals(type, "LIST_SHARES", StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Info("PeerServer", "LIST_SHARES. Remote=" + remote);
                        var shares = _shares.GetShares();
                        var list = new List<Dictionary<string, object>>();
                        foreach (var s in shares)
                        {
                            list.Add(new Dictionary<string, object>
                            {
                                { "shareId", s.ShareId },
                                { "name", s.Name },
                                { "readOnly", s.ReadOnly }
                            });
                        }

                        var resp = new Dictionary<string, object>
                        {
                            { "type", "LIST_SHARES_RESP" },
                            { "reqId", reqId },
                            { "ok", true },
                            { "shares", list }
                        };
                        writer.WriteFrame(new Frame(FrameKind.Json, _json.Encode(resp)));
                        continue;
                    }

                    if (string.Equals(type, "LIST_DIR", StringComparison.OrdinalIgnoreCase))
                    {
                        var shareId = GetString(msg, "shareId");
                        var path = GetString(msg, "path") ?? "";
                        Logger.Debug("PeerServer", "LIST_DIR. Remote=" + remote + " ShareId=" + shareId + " Path=" + path);
                        if (!_shares.TryGetShare(shareId, out var share))
                        {
                            SendError(remote, writer, reqId, "LIST_DIR_RESP", ErrorCodes.NotFound, "Share not found.");
                            continue;
                        }

                        try
                        {
                            var full = Sharing.SafePath.CombineAndValidate(share.LocalPath, path);
                            if (!Directory.Exists(full))
                            {
                                SendError(remote, writer, reqId, "LIST_DIR_RESP", ErrorCodes.NotFound, "Directory not found.");
                                continue;
                            }

                            var entries = new List<Dictionary<string, object>>();
                            foreach (var dir in Directory.GetDirectories(full))
                            {
                                var di = new DirectoryInfo(dir);
                                entries.Add(new Dictionary<string, object> { { "name", di.Name }, { "isDir", true } });
                            }
                            foreach (var file in Directory.GetFiles(full))
                            {
                                var fi = new FileInfo(file);
                                entries.Add(new Dictionary<string, object> { { "name", fi.Name }, { "isDir", false }, { "size", fi.Length }, { "mtimeUtc", fi.LastWriteTimeUtc.ToString("o") } });
                            }

                            var resp = new Dictionary<string, object>
                            {
                                { "type", "LIST_DIR_RESP" },
                                { "reqId", reqId },
                                { "ok", true },
                                { "entries", entries }
                            };
                            writer.WriteFrame(new Frame(FrameKind.Json, _json.Encode(resp)));
                        }
                        catch (InvalidOperationException)
                        {
                            SendError(remote, writer, reqId, "LIST_DIR_RESP", ErrorCodes.PathTraversal, "Path traversal rejected.");
                        }
                        catch (Exception ex)
                        {
                            SendError(remote, writer, reqId, "LIST_DIR_RESP", ErrorCodes.IoError, ex.Message);
                        }
                        continue;
                    }

                    if (string.Equals(type, "STAT", StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Debug("PeerServer", "STAT. Remote=" + remote);
                        _transferServer.HandleStat(writer, reqId, msg);
                        continue;
                    }

                    if (string.Equals(type, "DOWNLOAD_REQ", StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Info("PeerServer", "DOWNLOAD_REQ. Remote=" + remote);
                        _transferServer.HandleDownload(client, writer, reader, reqId, msg);
                        return;
                    }

                    if (string.Equals(type, "UPLOAD_REQ", StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Info("PeerServer", "UPLOAD_REQ. Remote=" + remote);
                        _transferServer.HandleUpload(client, writer, reader, reqId, msg);
                        return;
                    }

                    SendError(remote, writer, reqId, type + "_RESP", ErrorCodes.BadRequest, "Unknown message type.");
                }
            }
        }

        private static string GetString(Dictionary<string, object> msg, string key)
        {
            if (msg == null || key == null) return null;
            if (!msg.TryGetValue(key, out var v) || v == null) return null;
            return v.ToString();
        }

        private void SendError(string remoteEndpoint, FrameWriter writer, string reqId, string respType, string code, string message)
        {
            try
            {
                Logger.Warn("PeerServer", "Error. Remote=" + (remoteEndpoint ?? "") + " RespType=" + respType + " Code=" + code + " Msg=" + message);
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

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
        }
    }
}
