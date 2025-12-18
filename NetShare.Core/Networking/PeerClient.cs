using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using NetShare.Core.Logging;
using NetShare.Core.Protocol;
using NetShare.Core.Security;
using NetShare.Core.Settings;

namespace NetShare.Core.Networking
{
    public sealed class PeerClient : IDisposable
    {
        private readonly JsonCodec _json = new JsonCodec();
        private readonly AppSettings _settings;
        private TcpClient _tcp;
        private FrameReader _reader;
        private FrameWriter _writer;
        private string _serverId;
        private byte[] _serverNonce;

        public PeerClient(AppSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public void Connect(IPAddress address, int port, string authMode)
        {
            if (address == null) throw new ArgumentNullException(nameof(address));

            Logger.Info("PeerClient", "Connect start. Endpoint=" + address + ":" + port + " Auth=" + (authMode ?? "open"));

            _tcp = new TcpClient();
            _tcp.Connect(address, port);
            var stream = _tcp.GetStream();
            stream.ReadTimeout = NetShareProtocol.DefaultSocketTimeoutMs;
            stream.WriteTimeout = NetShareProtocol.DefaultSocketTimeoutMs;
            _reader = new FrameReader(stream);
            _writer = new FrameWriter(stream);

            var reqId = Guid.NewGuid().ToString();
            var hello = new Dictionary<string, object>
            {
                { "type", "HELLO" },
                { "reqId", reqId },
                { "proto", NetShareProtocol.ProtocolVersion },
                { "deviceId", _settings.DeviceId },
                { "deviceName", _settings.DeviceName },
                { "auth", authMode ?? "open" }
            };
            _writer.WriteFrame(new Frame(FrameKind.Json, _json.Encode(hello)));

            var resp = ReadJson();
            EnsureOk(resp);
            _serverId = GetString(resp, "serverId") ?? "";
            _serverNonce = Convert.FromBase64String(GetString(resp, "nonce") ?? "");

            Logger.Info("PeerClient", "HELLO ok. ServerId=" + _serverId);

            var negotiatedOpen = _settings.OpenMode || string.Equals(authMode, "open", StringComparison.OrdinalIgnoreCase);
            if (!negotiatedOpen)
            {
                if (string.IsNullOrWhiteSpace(_settings.AccessKey))
                    throw new InvalidOperationException("Access key required for PSK auth.");

                var clientNonce = HmacAuth.RandomNonce();
                var mac = HmacAuth.ComputeMac(_settings.AccessKey, _serverNonce, clientNonce, _serverId, _settings.DeviceId);
                var authReqId = Guid.NewGuid().ToString();
                var auth = new Dictionary<string, object>
                {
                    { "type", "AUTH" },
                    { "reqId", authReqId },
                    { "clientNonce", Convert.ToBase64String(clientNonce) },
                    { "mac", Convert.ToBase64String(mac) }
                };
                _writer.WriteFrame(new Frame(FrameKind.Json, _json.Encode(auth)));
                var authResp = ReadJson();
                EnsureOk(authResp);

                Logger.Info("PeerClient", "AUTH ok. Mode=psk-hmac-sha256");
            }
            else
            {
                Logger.Info("PeerClient", "AUTH ok. Mode=open");
            }
        }

        public List<Dictionary<string, object>> ListShares()
        {
            try
            {
                Logger.Debug("PeerClient", "LIST_SHARES start.");
                var reqId = Guid.NewGuid().ToString();
                var req = new Dictionary<string, object> { { "type", "LIST_SHARES" }, { "reqId", reqId } };
                _writer.WriteFrame(new Frame(FrameKind.Json, _json.Encode(req)));
                var resp = ReadJson();
                EnsureOk(resp);
                var shares = GetListOfDictionaries(resp, "shares");
                Logger.Debug("PeerClient", "LIST_SHARES ok. Count=" + (shares?.Count ?? 0));
                return shares;
            }
            catch (Exception ex)
            {
                Logger.Warn("PeerClient", "LIST_SHARES failed.", ex);
                throw;
            }
        }

        public List<Dictionary<string, object>> ListDir(string shareId, string path)
        {
            try
            {
                Logger.Debug("PeerClient", "LIST_DIR start. ShareId=" + shareId + " Path=" + (path ?? ""));
                var reqId = Guid.NewGuid().ToString();
                var req = new Dictionary<string, object>
                {
                    { "type", "LIST_DIR" },
                    { "reqId", reqId },
                    { "shareId", shareId },
                    { "path", path ?? "" }
                };
                _writer.WriteFrame(new Frame(FrameKind.Json, _json.Encode(req)));
                var resp = ReadJson();
                EnsureOk(resp);
                var entries = GetListOfDictionaries(resp, "entries");
                Logger.Debug("PeerClient", "LIST_DIR ok. Count=" + (entries?.Count ?? 0));
                return entries;
            }
            catch (Exception ex)
            {
                Logger.Warn("PeerClient", "LIST_DIR failed. ShareId=" + shareId + " Path=" + (path ?? ""), ex);
                throw;
            }
        }

        public Dictionary<string, object> Stat(string shareId, string path)
        {
            try
            {
                Logger.Debug("PeerClient", "STAT start. ShareId=" + shareId + " Path=" + (path ?? ""));
                var reqId = Guid.NewGuid().ToString();
                var req = new Dictionary<string, object> { { "type", "STAT" }, { "reqId", reqId }, { "shareId", shareId }, { "path", path ?? "" } };
                _writer.WriteFrame(new Frame(FrameKind.Json, _json.Encode(req)));
                var resp = ReadJson();
                EnsureOk(resp);
                Logger.Debug("PeerClient", "STAT ok.");
                return (Dictionary<string, object>)resp["stat"];
            }
            catch (Exception ex)
            {
                Logger.Warn("PeerClient", "STAT failed. ShareId=" + shareId + " Path=" + (path ?? ""), ex);
                throw;
            }
        }

        private Dictionary<string, object> ReadJson()
        {
            var frame = _reader.ReadFrame();
            if (frame == null) throw new InvalidOperationException("Connection closed.");
            if (frame.Kind != FrameKind.Json) throw new InvalidOperationException("Expected JSON frame.");
            return (Dictionary<string, object>)_json.DecodeUntyped(frame.Payload);
        }

        private static List<Dictionary<string, object>> GetListOfDictionaries(Dictionary<string, object> resp, string key)
        {
            if (resp == null) throw new ArgumentNullException(nameof(resp));
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentNullException(nameof(key));

            if (!resp.TryGetValue(key, out var raw) || raw == null)
                return new List<Dictionary<string, object>>();

            if (raw is List<Dictionary<string, object>> already)
                return already;

            if (raw is object[] arr)
            {
                var list = new List<Dictionary<string, object>>(arr.Length);
                for (var i = 0; i < arr.Length; i++)
                {
                    if (arr[i] is Dictionary<string, object> d) list.Add(d);
                    else throw new InvalidOperationException("Invalid '" + key + "' element type: " + (arr[i]?.GetType().FullName ?? "null"));
                }
                return list;
            }

            if (raw is ArrayList al)
            {
                var list = new List<Dictionary<string, object>>(al.Count);
                foreach (var item in al)
                {
                    if (item is Dictionary<string, object> d) list.Add(d);
                    else throw new InvalidOperationException("Invalid '" + key + "' element type: " + (item?.GetType().FullName ?? "null"));
                }
                return list;
            }

            throw new InvalidOperationException("Invalid '" + key + "' type: " + raw.GetType().FullName);
        }

        private static void EnsureOk(Dictionary<string, object> resp)
        {
            if (resp == null) throw new InvalidOperationException("No response.");
            if (resp.TryGetValue("ok", out var okObj) && okObj is bool ok && ok) return;
            if (resp.TryGetValue("error", out var err) && err is Dictionary<string, object> e)
            {
                var code = e.ContainsKey("code") ? e["code"].ToString() : "ERROR";
                var msg = e.ContainsKey("message") ? e["message"].ToString() : "Unknown error";
                throw new InvalidOperationException(code + ": " + msg);
            }
            throw new InvalidOperationException("Request failed.");
        }

        private static string GetString(Dictionary<string, object> dict, string key)
        {
            if (!dict.TryGetValue(key, out var v) || v == null) return null;
            return v.ToString();
        }

        public void Dispose()
        {
            try { _tcp?.Close(); } catch { }
            try { _tcp?.Dispose(); } catch { }
        }
    }
}
