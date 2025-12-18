# NetShare Protocol (NSP)

This document is **normative**. The key words **MUST**, **MUST NOT**, **REQUIRED**, **SHALL**, **SHALL NOT**, **SHOULD**, **SHOULD NOT**, **RECOMMENDED**, **MAY**, and **OPTIONAL** are to be interpreted as described in RFC 2119.

## 1. Overview

NetShare is a LAN-only peer discovery and file sharing protocol designed for Windows 7 SP1 through Windows 11.

- Discovery: **UDP broadcast**
- Control + transfer: **TCP**
- Control messages: **UTF-8 JSON**
- Framing: fixed header + length prefix
- Security: OPTIONAL **pre-shared access key** using **HMAC-SHA256 challenge/response** (no key sent over the wire)

## 2. Versioning

Protocol versions are `MAJOR.MINOR`.

- Peers with different **MAJOR** versions MUST NOT interoperate.
- Peers with the same **MAJOR** and different **MINOR** versions SHOULD interoperate by ignoring unknown fields and message types.
- Implementations MUST include `proto` in discovery and `HELLO`.

Current version: **1.0**

## 3. Ports

Defaults (configurable):

- UDP Discovery Port: **40123**
- TCP Service Port: **40124**

## 4. Identity

Each node has:

- `deviceId` (UUID string)
- `deviceName` (human-readable)

## 5. Discovery (UDP)

### 5.1 Transport

- IPv4 UDP broadcast to `255.255.255.255:<discoveryPort>`.
- Peers MUST listen on `<discoveryPort>`.
- A node SHOULD announce every **2 seconds**.
- A node SHOULD consider a peer **offline** if no announce is received for **7 seconds**.

### 5.2 Message format

UDP payload is UTF-8 JSON (no framing). Each datagram contains exactly one JSON object.

Common fields:

- `proto`: string, e.g. `"1.0"`
- `type`: string
- `deviceId`: string UUID
- `deviceName`: string
- `tcpPort`: number
- `discoveryPort`: number
- `timestampUtc`: ISO-8601 string
- `cap`: object (capabilities)

#### 5.2.1 `DISCOVERY_ANNOUNCE`

Sender periodically broadcasts:

```json
{
  "proto": "1.0",
  "type": "DISCOVERY_ANNOUNCE",
  "deviceId": "b7f2c5b5-2b7c-46f3-8a0e-0f4a778b3dbe",
  "deviceName": "ALICE-PC",
  "tcpPort": 40124,
  "discoveryPort": 40123,
  "timestampUtc": "2025-12-18T18:00:00Z",
  "cap": { "auth": ["open", "psk-hmac-sha256"], "resume": true }
}
```

#### 5.2.2 `DISCOVERY_QUERY` / `DISCOVERY_RESPONSE`

- A node MAY send `DISCOVERY_QUERY` on startup to accelerate discovery.
- Nodes receiving `DISCOVERY_QUERY` SHOULD respond with `DISCOVERY_RESPONSE` via unicast UDP back to the sender’s source IP:port.

## 6. TCP Framing

All TCP traffic is a sequence of **frames**.

### 6.1 Frame header

Each frame:

```
byte 0     : FrameKind (ASCII) 'J' for JSON, 'B' for binary
bytes 1..4 : Length (uint32, big-endian) of payload in bytes
bytes 5..  : Payload
```

### 6.2 JSON payload

JSON frames MUST be UTF-8 encoded JSON objects.

Every JSON message MUST include:

- `type`: message type string
- `reqId`: string (client-generated request id) for request/response correlation

Responses MUST include:

- `ok`: boolean
- `error`: object if `ok=false`

## 7. Session Setup

### 7.1 `HELLO` / `HELLO_ACK`

Client MUST send `HELLO` first:

```json
{ "type": "HELLO", "reqId": "...", "proto": "1.0", "deviceId": "...", "deviceName": "...", "auth": "open" }
```

Server responds:

```json
{
  "type": "HELLO_ACK",
  "reqId": "...",
  "ok": true,
  "serverId": "...",
  "nonce": "base64...",
  "auth": ["open","psk-hmac-sha256"],
  "authRequired": true,
  "selectedAuth": "psk-hmac-sha256"
}
```

If `authRequired=true`, the client MUST complete authentication (Section 7.2) before any other operation.

### 7.2 Optional authentication (PSK)

If `auth` is `psk-hmac-sha256`, client MUST then send `AUTH`:

```json
{ "type": "AUTH", "reqId": "...", "clientNonce": "base64...", "mac": "base64..." }
```

MAC computation:

$$ mac = HMAC\_SHA256(key, nonce || clientNonce || serverId || deviceId) $$

Server MUST verify and respond with `AUTH_OK` or error code `AUTH_FAILED`.

## 8. Core Operations

### 8.1 List shares

Request: `LIST_SHARES`

Response data:

```json
{ "shares": [ { "shareId": "...", "name": "Photos", "readOnly": true } ] }
```

### 8.2 List directory

Request: `LIST_DIR` with `shareId` and `path` (relative, `/` separators).

Server MUST enforce path traversal protection and MUST reject any path resolving outside the share root (`PATH_TRAVERSAL`).

### 8.3 Stat

Request: `STAT` returns file metadata including size and SHA-256.

### 8.4 Download

Client sends `DOWNLOAD_REQ`:

```json
{ "type": "DOWNLOAD_REQ", "reqId": "...", "transferId": "uuid...", "shareId": "...", "path": "relative/file.bin", "offset": 0 }
```

Server responds `DOWNLOAD_ACK` with size/hash. Then data is streamed as chunk pairs:

1) JSON `FILE_CHUNK` header

```json
{ "type": "FILE_CHUNK", "reqId": "...", "transferId": "...", "offset": 0, "length": 65536 }
```

2) Binary frame payload: exactly `length` bytes.

End of stream is `FILE_END` with final SHA-256. Client MUST verify hash and MUST error with `INTEGRITY_FAILED` on mismatch.

### 8.5 Upload

Similar to download.

- Client sends `UPLOAD_REQ` including size/hash.
- Server replies `UPLOAD_ACK` including accepted offset (for resume).
- Client sends chunk headers + binary payloads.
- Server verifies final SHA-256.
- After receiving `FILE_END`, the server MUST respond with `UPLOAD_DONE` (JSON) with `ok=true` on success.

Example `UPLOAD_DONE`:

```json
{ "type": "UPLOAD_DONE", "reqId": "...", "ok": true }
```

If the upload integrity check fails, the server MUST respond with `ok=false` and `error.code=INTEGRITY_FAILED`.

Server MUST reject uploads into read-only shares (`READ_ONLY`).

## 9. Keepalive

Either side MAY send `PING` and the peer MUST respond with `PONG`.

Recommended interval: 10–20 seconds.

## 9.1 Timeouts and retries

- Implementations SHOULD use a TCP read/write timeout of **~15 seconds** for control messages.
- For discovery, a node SHOULD transmit `DISCOVERY_ANNOUNCE` every **2 seconds**.
- For offline detection, a node SHOULD consider a peer offline after **~7 seconds** without receiving an announce.
- Clients MAY retry failed TCP connects with a short backoff (e.g., 250ms → 500ms → 1s) up to a small limit.

## 10. Error Codes

Standard `error` object:

```json
{ "code": "PATH_TRAVERSAL", "message": "...", "detail": {} }
```

Codes:

- `BAD_REQUEST`
- `UNSUPPORTED_VERSION`
- `AUTH_REQUIRED`
- `AUTH_FAILED`
- `NOT_FOUND`
- `READ_ONLY`
- `PATH_TRAVERSAL`
- `IO_ERROR`
- `INTEGRITY_FAILED`
- `INTERNAL_ERROR`

## 11. Sequence Diagrams

### 11.1 Discovery

```
A -> broadcast: DISCOVERY_ANNOUNCE (every 2s)
B -> broadcast: DISCOVERY_ANNOUNCE (every 2s)
A receives B and updates lastSeen
B receives A and updates lastSeen
```

### 11.2 Download

```
Client -> Server: HELLO
Server -> Client: HELLO_ACK (nonce)
Client -> Server: AUTH (optional)
Server -> Client: AUTH_OK
Client -> Server: DOWNLOAD_REQ
Server -> Client: DOWNLOAD_ACK
loop chunks
  Server -> Client: FILE_CHUNK (JSON)
  Server -> Client: <binary bytes>
end
Server -> Client: FILE_END
```

## 12. Security Considerations

- Implementations MUST only serve content within explicitly configured shares.
- Implementations MUST canonicalize paths and MUST reject traversal attempts.
- PSK mode MUST use challenge/response; the key MUST NOT be sent directly.
- Integrity MUST be checked using SHA-256 for full file transfers.
