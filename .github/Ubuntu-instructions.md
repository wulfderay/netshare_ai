You are a senior Linux desktop engineer + network protocol implementer. Recreate the NetShare project for Ubuntu Linux (target Ubuntu 22.04+ and 24.04+) using Ubuntu-appropriate technology and best practices, while remaining interoperable with the existing Windows NetShare implementation (wire-compatible at the TCP/UDP protocol level).

You MUST treat this as a compatibility port: the Ubuntu app must be able to discover, browse, download from, and upload to the Windows app on the same LAN, using the same protocol version and message shapes.

0) Source-of-truth rules (IMPORTANT)
PROTOCOL.md is intended to be normative, BUT the actual Windows implementation is the source of truth when they differ.
These “implementation-truth” notes are based on code review and override the spec where applicable:
Implementation-truth deviations / clarifications:

Error code INVALID_RANGE exists and is used (range-hash feature). The error code list in PROTOCOL.md should include it. Error codes are uppercase with underscores, not NotFound/IoError style.
Response message types are explicit and must match:
LIST_SHARES → LIST_SHARES_RESP
LIST_DIR → LIST_DIR_RESP
STAT → STAT_RESP
AUTH → AUTH_OK (even when ok=false)
HELLO → HELLO_ACK (even when ok=false)
Unknown request type → ${type}_RESP with ok=false and error.code=BAD_REQUEST
HASH_REQ / HASH_RESP is implemented in the transfer server component, but the Windows server dispatcher does NOT route HASH_REQ at all, so Windows builds in this repo effectively do not support HASH_REQ over the network. Therefore:
Ubuntu client MUST NOT rely on HASH_REQ for resume/verification.
Ubuntu client MAY implement HASH_REQ as an optional optimization, but MUST gracefully handle BAD_REQUEST/disconnect/no response when talking to Windows peers.
Discovery broadcast: periodic announces are sent to 255.255.255.255 by default; announce interval and offline threshold are exactly:
announce every 2000ms
peer offline after 7000ms of silence
Hash encoding: SHA-256 values are encoded as lowercase hex (two hex chars per byte), not base64.
TCP framing uses signed int32 length in network byte order:
1 byte: kind 'J' or 'B'
4 bytes: payload length, big-endian, interpreted as int32
then payload
Your implementation must follow these behaviors exactly for interoperability.

1) Platform + stack choice (Ubuntu-appropriate, but compatible)
Pick ONE of the following approaches and stick to it (do not mix stacks):

Recommended: .NET 8 (or .NET 6 LTS if you must) + Avalonia UI for GUI + systemd user service (optional)
Rationale: easiest way to stay byte-for-byte compatible with the Windows behavior and to reuse architecture patterns (TCP framing, HMAC auth, transfer loops).
Alternative: Rust (tokio) + GTK4/libadwaita GUI
Only choose this if you are confident you can deliver a complete GTK UI and correct protocol framing quickly.
Regardless of choice:

Networking must use standard sockets (UDP broadcast + TCP).
No cloud services; LAN-only.
Provide an Ubuntu-friendly config location (XDG Base Directory): ~/.config/netshare/…
Provide an Ubuntu-friendly downloads location: default to ~/Downloads/NetShare
2) Required deliverables (Ubuntu project)
Create a new repository structure (or a parallel folder) for Linux, e.g.:

NetShare.Linux.sln (or equivalent workspace)
NetShare.Linux.App (GUI)
NetShare.Linux.Core (protocol, discovery, transfer engine, share manager)
Optional: NetShare.Linux.Tests (framing + safe path + golden protocol tests)
README.md (Linux install/run, firewall/ufw notes, troubleshooting, interoperability test steps)
PROTOCOL-COMPAT.md (small doc describing the Windows-implementation-truth deviations listed above)
Output format:

Print the repository tree first.
Then print full contents of all created files (not pseudocode). The project must build on Ubuntu.
3) Wire compatibility requirements (MUST MATCH WINDOWS)
Implement these exactly.

3.1 UDP discovery
Bind UDP socket to 0.0.0.0:<discoveryPort> (default 40123)
Enable broadcast (SO_BROADCAST) and reuse address
Every 2000ms send a JSON datagram to 255.255.255.255:<discoveryPort> with fields:

{  "proto": "1.0",  "type": "DISCOVERY_ANNOUNCE",  "deviceId": "<uuid string>",  "deviceName": "<string>",  "tcpPort": 40124,  "discoveryPort": 40123,  "timestampUtc": "<ISO-8601 UTC>",  "cap": { "auth": ["open","psk-hmac-sha256"], "resume": true }}
Also implement:
DISCOVERY_QUERY datagram (startup acceleration) with fields: proto, type, timestampUtc
When receiving DISCOVERY_QUERY, respond unicast back to sender with DISCOVERY_RESPONSE shaped like an announce (same fields) but type="DISCOVERY_RESPONSE".
Peer tracking:

Show peers within ~5 seconds on a typical LAN.
Mark offline after 7000ms without an announce/response.
3.2 TCP framing
Each frame:
byte 0: ASCII 'J' or 'B'
bytes 1..4: int32 length, big-endian
payload bytes
Reject unknown kind; reject negative length; cap maximum length (Windows code allows up to 1 GiB, but you may choose a safer cap if it doesn’t break interoperability—keep at least tens of MB).
3.3 JSON message envelope rules
All JSON messages are objects and MUST include:

type: string
reqId: string (GUID-like is fine)
Responses MUST include:

type: response type string (exact names listed above)
reqId: copied from request
ok: boolean
if ok=false, include error: { code: "<ERROR_CODE>", message: "<string>" }
Use these error codes (exact case):

BAD_REQUEST
UNSUPPORTED_VERSION
AUTH_REQUIRED
AUTH_FAILED
NOT_FOUND
READ_ONLY
PATH_TRAVERSAL
IO_ERROR
INTEGRITY_FAILED
INTERNAL_ERROR
INVALID_RANGE (important)
3.4 Session + auth
Handshake sequence:

Client sends HELLO:

{  "type":"HELLO",  "reqId":"...",  "proto":"1.0",  "deviceId":"...",  "deviceName":"...",  "auth":"open" | "psk-hmac-sha256"}
Server responds HELLO_ACK:

{  "type":"HELLO_ACK",  "reqId":"...",  "ok":true,  "serverId":"<uuid string>",  "nonce":"<base64 32 bytes>",  "auth":["open","psk-hmac-sha256"],  "authRequired":true|false,  "selectedAuth":"open"|"psk-hmac-sha256"}
If server requires PSK, client sends AUTH:

{  "type":"AUTH",  "reqId":"...",  "clientNonce":"<base64 32 bytes>",  "mac":"<base64 hmac>"}
MAC algorithm MUST match Windows:

Key bytes = UTF-8 bytes of the shared key string
Message bytes = serverNonce || clientNonce || UTF8(serverId) || UTF8(clientId)
mac = HMAC_SHA256(keyBytes, messageBytes)
Compare using constant-time compare
Server responds AUTH_OK:
{ type:"AUTH_OK", reqId:"...", ok:true } on success
{ type:"AUTH_OK", reqId:"...", ok:false, error:{...} } on failure
IMPORTANT client behavior for compatibility:

Do NOT decide whether to authenticate solely based on local config.
Prefer the server’s authRequired/selectedAuth (this is more robust than the Windows client, and remains interoperable).
3.5 Listing shares and directories
LIST_SHARES → LIST_SHARES_RESP containing:

shares: [{ shareId, name, readOnly }]
LIST_DIR request fields:

shareId
path is relative and uses / separators (may be empty)
LIST_DIR_RESP contains:

entries: [{ name, isDir, size?, mtimeUtc? }]
Directories: only {name,isDir:true}
Files: {name,isDir:false,size,mtimeUtc}
3.6 Stat
STAT → STAT_RESP with:
stat: { size, mtimeUtc, sha256 }
sha256 is lowercase hex.
3.7 Download
Client sends DOWNLOAD_REQ with:
transferId (string, GUID)
shareId, path, offset (int64)
Server replies DOWNLOAD_ACK with:
file: { size, sha256 }
offset (server-clamped offset)
Then server streams until done:
JSON FILE_CHUNK with { transferId, offset, length } (and type,reqId)
immediately followed by a binary frame of exactly length bytes
final JSON FILE_END with { ok:true, transferId, file:{size,sha256} }
Client MUST:

If resuming, seed SHA-256 by hashing existing prefix bytes locally (like Windows does).
Verify final hash equals both:
DOWNLOAD_ACK.file.sha256
FILE_END.file.sha256
On mismatch: error INTEGRITY_FAILED.
3.8 Upload
Client sends UPLOAD_REQ with:
transferId, shareId, path
file: { size, sha256 } (sha256 lowercase hex)
Server replies UPLOAD_ACK with:
offset (resume offset = existing file length clamped)
Client streams FILE_CHUNK + binary frames, then FILE_END:
FILE_END contains file:{size,sha256} but does not require ok
Server verifies SHA-256 of full final content and responds:
UPLOAD_DONE with ok:true
or UPLOAD_DONE with ok:false and error code INTEGRITY_FAILED
3.9 Path traversal and share isolation (Linux best practice)
Implement SafePath logic compatible with Windows semantics:

Protocol paths use / separators.
Normalize by:
replacing \ with /
stripping leading /
Join with the share root and canonicalize using realpath-equivalent semantics.
Reject traversal (paths that escape the share root) with PATH_TRAVERSAL.
Linux-specific best practice:

Path comparisons should be case-sensitive on Linux.
Defend against symlink escapes: after resolving, ensure the resolved path remains under the resolved share root.
4) Ubuntu GUI requirements (keep minimal, but usable)
Provide a GUI with the same core surfaces as Windows:

Peers list (name, IP, status, last-seen)
Shares list (add/remove local folders, read-only toggle)
Remote browse tree + file list
Transfers list with progress, speed, ETA, cancel
Settings: ports, device name, access key (optional), download directory
Keep to one main window. Standard menus:

File (Quit)
Share (Add Folder…, Remove, Toggle Read-Only)
Peer (Connect, Refresh, Add Peer by IP…)
Help (About, Open Protocol Doc)
Do not add extra pages/features beyond this.

5) Packaging + ops (Ubuntu best practices)
Provide dotnet publish instructions (and/or cargo build if Rust).
Provide firewall instructions for ufw:
UDP 40123 inbound
TCP 40124 inbound
Provide troubleshooting tips for broadcast-blocked networks and manual “Add peer by IP”.
6) Interoperability acceptance tests (must include in README)
Write explicit manual test steps verifying:

Ubuntu app and Windows app discover each other within ~5 seconds.
Windows shares a folder; Ubuntu can browse it and download a file with progress.
Ubuntu can upload a file into a non-read-only Windows share (or vice versa, depending on your implementation focus).
PSK mode works:
With mismatched keys, auth fails with AUTH_FAILED
With correct key, operations succeed.
Now implement the Ubuntu project, output the repo tree, and then output the full contents of every file you created.