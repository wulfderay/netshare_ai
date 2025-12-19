# NetShare (Linux port)

Ubuntu-focused GUI for the NetShare LAN file sharing protocol. This is a **compatibility port**: it is intended to interoperate with the Windows implementation in this repository.

- Target: **Ubuntu 22.04+ / 24.04+**
- Stack: **.NET 8 + Avalonia UI**
- LAN-only: **UDP discovery + TCP control/transfer**

## Project layout

- `NetShare.Linux.sln`
  - `NetShare.Linux.Core` (protocol+engine)
  - `NetShare.Linux.App` (Avalonia GUI)
  - `NetShare.Linux.Tests` (basic tests)

## Build & run (Ubuntu)

Prerequisites:
- .NET SDK 8.x

From the repository root:

- Build:
  - `dotnet build NetShare.Linux/NetShare.Linux.sln -c Release`

- Run GUI:
  - `dotnet run --project NetShare.Linux/NetShare.Linux.App -c Release`

## Build notes (Ubuntu 24.04) / common failures

This section documents the issues observed when building on **Ubuntu 24.04 LTS**.

### 1) `dotnet: command not found`
**Symptom**
- `zsh: command not found: dotnet`

**Cause**
- .NET SDK not installed.

**Fix (apt, Ubuntu 24.04)**
- Install .NET 8 SDK:
  - `sudo apt-get update`
  - `sudo apt-get install -y dotnet-sdk-8.0`

Verify:
- `dotnet --info` should show SDK `8.0.x`.

### 2) `CS0260 Missing partial modifier` for `MainWindow`
**Symptom**
- `MainWindow.axaml.cs(...): error CS0260: Missing partial modifier on declaration of type 'MainWindow'; another partial declaration of this type exists`

**Cause**
- Avalonia generates a partial class from `x:Class=...` in `MainWindow.axaml`, so the code-behind must be `partial`.

**Fix**
- Ensure `MainWindow.axaml.cs` declares:
  - `public sealed partial class MainWindow : Window`

### 3) `AVLN2000 Unable to resolve type DataGrid`
**Symptom**
- `Avalonia error AVLN2000: Unable to resolve type DataGrid from namespace https://github.com/avaloniaui`

**Cause**
- Avalonia `DataGrid` is not part of the base `Avalonia` package; it lives in a separate package.

**Fix**
- Add a package reference to `NetShare.Linux.App`:
  - `Avalonia.Controls.DataGrid` (same version as Avalonia, e.g. `11.2.0`).

## Tests

Run tests:
- `dotnet test NetShare.Linux/NetShare.Linux.sln -c Release`

## Runtime notes / known issues

### Discovery listener "Call from invalid thread"
When running the GUI on Ubuntu 24.04, the console showed:
- `[Discovery] Listener error: Call from invalid thread`

This indicates a background discovery thread is likely calling into an Avalonia-bound collection or UI object without marshaling back to the UI thread.

**Likely fix direction**
- Ensure any UI collection updates happen on the UI thread using Avalonia dispatching (e.g. `Dispatcher.UIThread.Post(...)`) in the Linux UI/viewmodel layer, not inside the core networking layer.

If you want, I can trace the exact call site and patch it so discovery updates are UI-thread safe.

## Publish

Example (self-contained is optional; framework-dependent is smaller):

- Framework-dependent:
  - `dotnet publish NetShare.Linux/NetShare.Linux.App -c Release -r linux-x64 --self-contained false -o ./out/netshare-linux`

## Ports / firewall

Defaults:
- UDP Discovery: **40123/udp**
- TCP Control+Transfer: **40124/tcp**

If you use `ufw`:

- Allow discovery:
  - `sudo ufw allow 40123/udp`
- Allow TCP:
  - `sudo ufw allow 40124/tcp`

If discovery does not work (broadcast blocked), use **Peer → Add Peer by IP…**.

## Configuration

Linux config is stored in XDG config home:

- `~/.config/netshare/config.json`

Defaults:
- downloads directory: `~/Downloads/NetShare`

## Interoperability acceptance tests

### A) Discovery (Ubuntu ↔ Windows)
1. Start Windows NetShare on machine A.
2. Start Linux NetShare on machine B.
3. Within ~5 seconds, both should show the other as a peer (name/IP/online).

If not:
- Ensure both machines are on the same L2 LAN/VLAN.
- Ensure firewalls allow UDP 40123.
- Try manual add peer by IP.

### B) Browse + download Windows share from Ubuntu
1. On Windows machine A: **Share → Add Folder…** and select a folder containing a file.
2. On Ubuntu machine B: select the Windows peer, connect, then browse the share.
3. Download a file.
4. Verify progress updates and the downloaded file appears under `~/Downloads/NetShare`.

### C) Upload from Ubuntu to Windows
1. On Windows machine A: share a folder with **Read-only disabled**.
2. On Ubuntu machine B: browse the share and upload a file.
3. Verify the file appears on Windows.

### D) PSK mode
1. Set the same **Access Key** on both Windows and Ubuntu.
2. Restart both apps.
3. Verify operations succeed.
4. Change Ubuntu Access Key to a wrong value.
5. Verify auth fails with `AUTH_FAILED`.

## Known limitations

- This Linux port intentionally avoids depending on `HASH_REQ` for correctness (Windows peers in this repo do not route it). Resume is implemented without requiring `HASH_REQ`.
