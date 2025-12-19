# NetShare Linux ↔ Windows Compatibility Notes (Implementation-Truth)

This document is **normative for the Linux port** when it differs from `../PROTOCOL.md`.

The Windows `NetShare.Core` implementation in this repository is the source of truth.

## Critical deviations / clarifications (must-match Windows)

### 1) Error codes
Error codes are **uppercase with underscores**.

Linux MUST support at least:

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
- `INVALID_RANGE`

### 2) Response message types are explicit
Windows uses explicit response types:

- `LIST_SHARES` → `LIST_SHARES_RESP`
- `LIST_DIR` → `LIST_DIR_RESP`
- `STAT` → `STAT_RESP`
- `AUTH` → `AUTH_OK` (even when `ok=false`)
- `HELLO` → `HELLO_ACK` (even when `ok=false`)

Unknown request types MUST respond with:

- `${type}_RESP` with `ok=false` and `error.code=BAD_REQUEST`

### 3) Discovery broadcast timing and address
Windows:

- Broadcast destination: **255.255.255.255** by default
- Announce interval: **2000ms**
- Offline threshold: **7000ms** without any announce/response

### 4) SHA-256 encoding
SHA-256 values are encoded as **lowercase hex** (two hex chars per byte), not base64.

### 5) TCP framing
Windows uses a simple framing protocol:

- 1 byte: kind `'J'` (JSON) or `'B'` (binary)
- 4 bytes: payload length **int32** big-endian (network byte order)
- N bytes: payload

Unknown `kind` MUST be rejected.
Negative length MUST be rejected.

### 6) HASH_REQ over the network
`HASH_REQ / HASH_RESP` exists in the transfer server code, but the Windows peer server dispatcher in this repository **does not route** `HASH_REQ`.

Therefore:
- Linux client MUST NOT rely on `HASH_REQ` for resume/verification.
- Linux client MAY implement `HASH_REQ` as an optional optimization, but MUST gracefully handle `BAD_REQUEST` / disconnect / no response.
