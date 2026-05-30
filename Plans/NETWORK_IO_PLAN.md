# Network IO Panel — Implementation Plan

Status: **planned** (2026-05-30). Combines NTRIP + an expanded module
config/readout (per-module IP) + a module subnet-change action, porting the
AgIO behaviour from `AgOpen_Snapshot/AgIO/`.

## Decisions (locked)

- **Surface:** left-nav **fly-out panel** (non-modal `FloatingPanel`, docked
  beside the nav like Screen & Alerts / Field Ops). Stays open over the map.
- **Per-module IPs:** obtain via **PGN 202 scan → PGN 203 reply** (authoritative
  self-reported IP + subnet, includes GPS), with an explicit **Scan** button.
- **IP change:** a single **Set-Subnet** button broadcasting **PGN 201** to all
  modules (global /24 change — matches AgIO; there is no per-module IP set),
  behind a confirmation.

## Protocol (authoritative, from `AgOpen_Snapshot/AgIO/Source/`)

Framing: `[0x80,0x81, src, PGN, len, …payload…, CRC]`, CRC = `unchecked (byte)`
sum of bytes `[2 .. n-2]`. Module IDs: **120 GPS / 121 IMU / 123 Machine /
126 Steer / 127 AgIO**. AgIO listens on **:9999**; modules listen on **:8888**.

### Scan request — PGN 202 (host → modules)
```
{ 0x80, 0x81, 0x7F, 202, 3, 202, 202, 5, 0x47 }
```
Sent to **255.255.255.255:8888**, once per up IPv4 NIC (socket bound to the
NIC address : 9999, Broadcast + ReuseAddress + DontRoute).
Module gate: `data[4]==3 && data[5]==202 && data[6]==202`.

### Scan reply — PGN 203 (module → host, 13 bytes)
```
[0]=0x80 [1]=0x81 [2]=moduleID [3]=203 [4]=7
[5..8]  = module IP   (4 octets)
[9..11] = subnet      (3 octets)
[12]    = CRC
```
`data[2]` selects which module's IP/subnet to store. Hello PGNs (126/123/121)
carry liveness only — no IP.

### Subnet change — PGN 201 (host → modules, 11 bytes)
```
{ 0x80, 0x81, 0x7F, 201, 5, 201, 201, oct1, oct2, oct3, 0x47 }
```
Broadcast to **255.255.255.255:8888** (same per-NIC pattern). All modules apply
the new first-three octets and restart; the host octet is kept. After sending,
AgIO persists the subnet and rebuilds its module endpoint to `subnet.255:8888`.
Module gate: `data[4]==5 && data[5]==201 && data[6]==201`.

> **CRC note:** AgIO hardcodes the trailing `0x47` for 201/202 and modules
> validate only the magic bytes, not CRC. We replicate AgIO's exact bytes
> (hardcoded `0x47`) for firmware compatibility. (Chris maintains the AiO
> firmware, so this can later move to a real CRC on both sides if desired.)

## What already exists (reuse)

- **NTRIP** — `NtripClientService` / `INtripClientService`, `NtripProfileService`,
  VM state+commands (`MainViewModel.Ntrip.cs`, `…Commands.Ntrip.cs`), and the
  `NtripProfilesDialogPanel` / `NtripProfileEditorPanel` dialogs. Launched today
  from File Menu → "NTRIP Profiles".
- **UDP/modules** — `UdpCommunicationService` (listen :9999, hello PGN 200 →
  :8888, subnet auto-lock, `GetModuleIpAddress(ModuleType)`); `ConnectionState`
  already has `AutoSteerIpAddress` / `MachineIpAddress` / `ImuIpAddress`
  (no GPS IP, no subnet yet).
- **PGN plumbing** — `PgnMessage.PgnNumbers` registry (already defines
  `HELLO_FROM_AGIO`=200, `SCAN_REPLY`=203), `PgnBuilder` outbound factory + CRC
  helper. `Docs/PGN.md` documents 201/202/203.
- **Nav conventions** — fly-out pattern (visibility prop + `Toggle…Command` +
  `CloseAllNavFlyouts` + Canvas host + drag-wire) per the recent Screen & Alerts
  work.

## Build order

### Phase 1 — protocol layer (Models/Services + tests)
- `PgnMessage.PgnNumbers`: add `SET_SUBNET = 201`, `SCAN_REQUEST = 202`.
- `PgnBuilder`: `BuildScanRequest()` and `BuildSubnetChange(byte o1, byte o2,
  byte o3)` returning the exact AgIO byte arrays above.
- Parse **PGN 203** in the UDP receive path → write IP + subnet (and GPS) into
  `ConnectionState`. Add `…Subnet` fields and `GpsIpAddress` to `ConnectionState`.
- `UdpCommunicationService`: add `SendGlobalBroadcast(byte[] pgn)` that iterates
  up IPv4 NICs and sends to `255.255.255.255:8888` (Broadcast/ReuseAddress/
  DontRoute). After a subnet change, repoint the service's own send endpoint to
  `subnet.255:8888`.
- Tests: 202/201 byte-exact build; 203 parse round-trip per module id.

### Phase 2 — ViewModel
- `MainViewModel.NetworkIO.cs` (new partial): observable module rows
  (Type · IP · Subnet · Status · last-seen), subnet octet entry fields,
  `ScanModulesCommand`, `SendSubnetCommand` (confirmation dialog → broadcast 201
  → update send endpoint). Surface NTRIP status (connected, bytes) + Connect/
  Disconnect, and a button to the existing Profiles dialog.
- `IsNetworkIoPanelVisible` + `ToggleNetworkIoPanelCommand`; wire into
  `IsAnyNavFlyoutOpen` and `CloseAllNavFlyouts`.

### Phase 3 — UI + entry point
- `Panels/NetworkIoPanel.axaml(.cs)` — `FloatingPanel` with a Modules section
  (table + Scan + Set-Subnet row) and an NTRIP section (status + Profiles).
- Add the nav button + Canvas host + drag-wire in `LeftNavigationPanel`.
- Move the NTRIP entry: File Menu "NTRIP Profiles" → "Network IO" (the Profiles/
  Editor dialogs remain, launched from inside the panel).

### Phase 4 — persistence + polish
- Persist the chosen subnet in `ConnectionConfig`; restore on startup.
- DI: register any new service members across Desktop/iOS/Android.
- Manual device pass (real modules) — Chris.

## Open risks / notes
- The host's own send endpoint must update immediately after a subnet change or
  it loses the modules until restart (AgIO rebuilds `epModule`).
- GPS IP only appears via PGN 203 (GPS data otherwise arrives as NMEA/PGN with no
  tracked source IP today).
- Global broadcast may need per-NIC iteration to traverse multiple interfaces
  (matches AgIO); verify on the target tablet's networking.
- Suggest a dedicated branch `feature/network-io` (not on the status-strip branch).
