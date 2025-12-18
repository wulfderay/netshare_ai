You are a senior Windows desktop engineer and network-protocol designer. Build a Windows LAN file/directory sharing tool (like a lightweight “local Dropbox/Windows share” but custom) with: (1) an easy-to-use GUI with proper controls/menus, (2) automatic discovery of clients on the local network, (3) compatibility from Windows 11 back to at least Windows 7, and (4) a fully documented protocol specification.

Hard constraints
OS support: Windows 7 SP1 through Windows 11.
Prefer a conservative stack that runs on Win7: .NET Framework 4.8 + WinForms (or .NET Framework 4.7.2 if you justify; avoid WinUI/MAUI).
Do not rely on Windows 10/11-only APIs.
Do not require third-party services. LAN-only operation.
Discovery must work without Bonjour/mDNS installs. Use UDP broadcast/multicast with a clear, documented mechanism.
Produce a documented, versioned protocol that another team could implement independently.
Keep the UX simple and “obviously usable”. No extra pages/features beyond what’s needed.
Deliverables (must output all)
A complete Visual Studio solution:
NetShare.sln
NetShare.App (WinForms GUI)
NetShare.Core (protocol + transfer engine)
Optional: NetShare.Tests (basic unit tests for protocol framing/parser)
A human-readable protocol spec: PROTOCOL.md (normative language: MUST/SHOULD/MAY).
A user/admin README: README.md with setup, firewall notes, troubleshooting.
Build/run instructions for Visual Studio (and msbuild) that work on Windows 7–11.
Minimal but functional UI (menus, dialogs, tray optional only if truly necessary).
Functional requirements (minimum feature set)
A) GUI
Main window with:
Peers panel: auto-discovered devices list (name, IP, status, last-seen).
Shares panel: choose local folders to share (add/remove, read-only toggle).
Remote Browse: browse a selected peer’s shared folders and file tree.
Transfers: progress list (download/upload), pause/cancel, speed, ETA.
Settings: ports, device name, optional shared “access key/password”, download directory.
Standard menus:
File (Exit)
Share (Add Folder…, Remove, Toggle Read-Only)
Peer (Connect, Refresh, Add Peer by IP… fallback)
Help (About, Open Protocol Doc)
Use Windows-native controls (ListView, TreeView, ProgressBar) and typical keyboard shortcuts.
B) Discovery (automatic)
Implement peer discovery over local subnets using UDP:
Periodic announcements (“I am here”) and listener for others.
Show peers within ~5 seconds of startup on typical LAN.
Maintain last-seen timestamps and offline detection.
Provide a manual fallback: “Add Peer by IP…” for networks blocking broadcast.
C) Compatibility
Runs on Windows 7+ with .NET Framework present/installed.
Avoid dependencies that break on Win7.
Provide clear firewall guidance (in README) and graceful error messages.
D) Protocol (documented)
Define:
Wire framing (length-prefix, delimiters, etc.)
Message types for:
discovery announce/query/response
capability negotiation + protocol versioning
auth (at least a shared secret option) and/or “open mode”
list shares
list directory contents
file metadata/stat
file download (chunked)
file upload (optional if too large; if you omit, justify and ensure “sharing tool” still meets intent—prefer including upload)
transfer resume (offset) and integrity (hash)
errors (standard codes)
keepalive/heartbeat
Document port usage, timeouts, and retry behavior.
Protocol must be implementable in any language (no .NET object serialization). Prefer JSON for control messages + binary for file chunks, or a compact binary schema if fully specified.
Security baseline (keep pragmatic)
Must not send arbitrary filesystem access; only the explicitly shared folders.
Path traversal protections MUST be included and documented.
Offer at least one of:
Pre-shared access key to authorize requests (simple HMAC over challenge), OR
TLS with self-signed certs (heavier for Win7; only if you can do it reliably).
If implementing the access key approach:
Use challenge/response so the key isn’t sent directly.
Use HMAC-SHA256.
Integrity for transferred file chunks: SHA-256 over whole file, and optionally per-chunk hash.
Suggested technical approach (you may refine, but keep within constraints)
Transport:
Discovery: UDP broadcast on a configurable port (default e.g. 40123).
Control + transfer: TCP on configurable port (default e.g. 40124).
Framing:
For TCP control messages: 4-byte big-endian length prefix + UTF-8 JSON payload.
For file transfer: separate message type that switches into chunk mode, or a “DATA” frame with length + bytes.
Concurrency:
Background threads/Tasks for discovery listener, TCP server, transfers.
UI thread updates via BeginInvoke.
Acceptance criteria (must explicitly verify in your output)
Two Windows machines on the same LAN discover each other automatically.
User can select a local folder to share on machine A.
On machine B, user can browse machine A’s shares and download at least one file successfully with progress.
Works on Windows 11 and is designed to run on Windows 7 (no unsupported APIs; target .NET Framework).
PROTOCOL.md contains:
versioning rules
all message types
state machines or sequence diagrams for discovery + transfer
error codes
security considerations
Output format requirements
Output the repository tree first.
Then output each file’s full contents with clear separators, including:
Solution/projects
Key C# source files
README.md
PROTOCOL.md
Keep code reasonably complete (not pseudocode). It should compile with minimal adjustments.
If you must omit a feature, call it out explicitly under “Known limitations” and provide the smallest next step to complete it.