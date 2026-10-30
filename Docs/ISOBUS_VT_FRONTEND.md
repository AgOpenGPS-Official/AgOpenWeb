# ISOBUS Virtual Terminal as an Adjunct Front End

**Status:** discussion artifact — not a decision, not a plan to implement.
**Date:** 2026-09-30
**Purpose:** Explore driving the guidance backend from an in-cab ISO 11783-6 Virtual
Terminal (VT) as a *second*, reduced front end alongside the web client. Records the
parallels with the web wire, where they break, where the code would go, and what the
HAT needs. Companion to [PGN_ISOBUS_MAPPING.md](PGN_ISOBUS_MAPPING.md) (data layer);
this document is about the UI layer.

---

## 1. Framing

The web client is the UI on every head: `AgOpenWeb.RemoteServer` streams projections of
the VM (Scene / Tick / Status / Config frames) over a binary WebSocket and the browser
renders them and sends back ~250 string command ids (`autosteer.*`, `tool.*`, …). The
backend never renders anything; that isolation is what makes a second front end
thinkable at all.

A VT is **not** a programmable client. It cannot run `app.js` (5.9 k lines) or
`index.html` (2.7 k lines). It shows a fixed set of screens ("masks") described by an
**object pool (IOP)** that the ECU uploads once, and afterwards only accepts small
"change this value / this string / this attribute" messages and reports soft-key,
button and input events. Everything the browser does today — screen flow, unit
formatting, edit buffers, camera, interpolation, drawing — has to move into a host-side
**VT presenter**.

So the honest shape is:

- **Adjunct, not replacement.** The run screen and joystick buttons on the tractor's own
  terminal; setup, field building and the moving map stay in the browser.
- **The seam is above the transport.** `WebSocketHub` / `WireCodec` are not where a VT
  plugs in. The VT presenter is a second consumer of the projector DTOs and a second
  caller of the same command handler.
- **Linux-only, HAT-only.** CAN arrives via the HAT's three MCP251863 controllers
  (SocketCAN `can0..2`). No iOS/Android/Windows/macOS path; it is an optional platform
  service, like the battery service.

---

## 2. The parallel, concretely

In ISOBUS terms AgOpenWeb is the **VT client** (a Working Set); the cab terminal is the
**VT server**. (Terminology is inverted from the web, where the app is the server.)

| Web client today | VT equivalent | Notes |
|---|---|---|
| `index.html` + `app.js` fetched on connect | Object pool uploaded via TP/ETP on first connect; the VT stores it under a 7-char version label and later connects skip the upload (Load Version) | Pool size matters for first-connect time on a 250 kbit/s bus |
| `TickDto` 10 Hz, `StatusDto` ~2 Hz, `ConfigDto` on fingerprint change | Change Numeric Value / Change String Value / Change Attribute / Change Active Mask (ECU→VT, PGN 0xE700) | Every value change is one 8-byte frame; batch and rate-limit |
| ~250 command ids from the browser | Soft Key Activation / Button Activation / VT Change Numeric Value / VT Select Input Object (VT→ECU, PGN 0xE600) | Presenter maps object ids → command ids |
| `ControlAuthority` deadman (1.5 s heartbeat) | Working Set Maintenance (ECU→VT, 1 Hz) and VT Status (VT→ECU, 1 Hz); 3 s without VT Status = VT lost | VT loss maps onto the existing failsafe (disengage autosteer, sections off) |
| Toast / hardware message (PGN 221) / `PromptDto` | Alarm masks (priority, optional acoustic signal) | Confirm prompts = alarm mask with two soft keys |
| `PlaySound` | Alarm mask acoustic signal / VT beep command | VT hardware decides what "beep" sounds like |
| Host-driven `WizardDto` | A mask per `StepKind` with numeric outputs bound to live values | Already server-driven UI; the closest thing to a free port |
| Translations (`TRANSLATION_WORKFLOW.md`) | Language command (PGN 0xFE0F) from the VT → pick a pool per language, or Change String Value at runtime | Generate pools per language from the same string tables |
| — (no equivalent) | **AUX-N**: operator assigns our functions to tractor joystick buttons | The one thing the browser cannot offer |

### What has no VT equivalent

- **The moving map.** 250 kbit/s ≈ 15 kB/s of payload with the whole bus to yourself;
  in practice a polite ECU uses a small fraction of that. Coverage cells, imagery, the
  perspective view and 60 fps are out. A VT4+ **Graphics Context** object can draw a
  wireframe (boundary, guidance line, next track, vehicle marker) at a few Hz — the
  same class of display the OEM terminals fall back to for third-party working sets.
  Terminal support for Graphics Context is uneven; treat it as optional (§5 phase 3).
- **Map-centric editing:** draw-boundary-on-imagery, headland drag handles, field
  builder, flags, tram editing, charts, log viewer, AgShare.
- **Free text entry** exists (Input String with the VT's on-screen keyboard) but is
  slow; NTRIP profiles and field naming stay in the browser.
- **Client-side state.** Several web flows keep edit buffers in the browser and write
  back on Save (NTRIP editor, config panels). The VT has no such buffer; the presenter
  either writes each field on change or keeps the buffer host-side.

---

## 3. Where the code goes

### 3.1 Placement decision: app, not HAT daemon

The HAT daemon (`AgOpenWeb-HAT`, C++17) owns CAN today for the brand steering buses,
on its SCHED_FIFO control thread. ISOBUS VT traffic is different in kind: not
real-time, bursty (pool upload), and its content is entirely UI state that lives in the
app (DTOs, command ids, translations, icons). Putting the VT client in the daemon would
mean reimplementing `WireCodec` in C++ and maintaining UI logic in two languages.

**Recommendation:** the VT client lives in AgOpenWeb; the daemon keeps its scope.

- Reserve one of `can0..2` for ISOBUS. The daemon does **not** open it. SocketCAN would
  let both processes share an interface, but two address-claiming CFs from one box buys
  nothing and complicates NAME management.
- Which bus is which is an install-time setting (like `--hat`), not a probe.

### 3.2 Proposed structure

```
Shared/AgOpenWeb.Services/Isobus/
    ICanBus.cs                 raw CAN frame send/receive (interface only)
    IsobusNetwork.cs           NAME, address claim (ISO 11783-5 / J1939-81), TP + ETP
    VtClient.cs                VT connection state machine, pool upload / Load Version,
                               value/attribute/mask commands, event decoding
    ObjectPool/                pool object model + IOP serializer + PNG→8-bit picture graphic

Shared/AgOpenWeb.IsobusVt/     (new project, sibling of RemoteServer)
    VtPresenter.cs             DTO → VT value changes; VT events → command ids
    Pools/                     declarative pool descriptions (run screen, wizard, alarms)

Platforms/AgOpenWeb.Desktop/Services/
    SocketCanBus.cs            Linux SocketCAN via P/Invoke (or SocketCANSharp);
                               registered when the configured interface exists,
                               NullCanBus otherwise
```

### 3.3 The seam

`RemoteServerWiring.Wire` builds one `CommandHandler` closure that posts every command
onto the `HostLoopDispatcher` and validates it against the VM. The VT presenter must
call **that same handler** so the two front ends cannot diverge. Two small refactors
make this possible:

1. `RemoteServerHost` currently owns `SceneProjector` and `ControlAuthority` as private
   fields. Lift them into a shared "front-end core" that both the WebSocket hub and the
   VT presenter consume, or expose them read-only.
2. `ControlAuthority` is keyed by a connection `Guid`. The VT presenter takes a synthetic
   connection id, acquires control when the operator presses an actuation soft key,
   heartbeats it from VT Status, and `Drop`s it on VT loss. The existing single-holder
   rule then arbitrates between a browser and the VT with no new policy.

The presenter runs on its own thread (CAN receive loop + a send scheduler); it reads
`BuildTick()` / `BuildStatus()` / `BuildConfig()` at its own cadence and diffs against
what the VT already shows, sending only changed values.

### 3.4 Bus etiquette

- Send budget: cap the presenter's steady-state output (e.g. ≤ 5 % of bus capacity),
  with the pool upload as the only exception, so an implement sharing the bus is never
  starved.
- Value updates coalesce: at most one Change Numeric Value per object per VT frame
  (~10 Hz for the lightbar/XTE, ~2 Hz for everything else).
- Bit rate 250 kbit/s, set with `ip link` at boot like the other HAT buses.

---

## 4. Object pools

- **Author in code, not in a designer.** The run screen is small enough to hand-lay, but
  the value of a declarative C# description is that config-style masks can be generated
  from the same metadata as `ConfigDto`, icons converted from `wwwroot/icons` PNGs to
  8-bit picture graphics, and strings taken from the existing translation tables. One
  pool per language, built at compile time or on first use.
- **Mask sizing.** Data masks are square; VTs scale the pool to their own mask size
  (200×200 minimum on old terminals, 480×480 typical now). Design at 480 and let the VT
  downscale; keep text large.
- **Soft keys.** Physical soft-key count varies (6 minimum). Design for 6 with paging;
  don't assume 10 or 12.
- **VT version.** Target VT version 3 for the run screen (widest compatibility, AUX-N
  available); gate the wireframe map on version ≥ 4 via the VT's reported version.

---

## 5. Scope, in phases

| Phase | Content | Depends on |
|---|---|---|
| 0 — Spike | One run-screen pool driven with dummy values against AgIsoVirtualTerminal on `vcan0`. No AgOpenWeb integration. Answers "is a map-less run screen worth having?" and "how does the pool look on a real terminal?" | A Linux box (a CM4 will do) |
| 1 — Run screen + alarms | Autosteer engage, section master / manual, per-section state, U-turn arm + direction, nudge, track cycle, XTE lightbar, speed, fix + age, pass number, headland distance. Alarm masks for prompts and hardware messages. Authority hookup and failsafe. | Stack (§3.2), seam refactor (§3.3) |
| 2 — AUX-N + wizard | Assignable functions: autosteer, U-turn, nudge L/R, section master, track cycle. Steer wizard masks from `WizardDto`. | Phase 1 |
| 3 — Wireframe map (optional) | Graphics Context: boundary, guidance line, next track, vehicle marker at 2–5 Hz. Skipped gracefully on VT < 4 or on terminals that render it too slowly. | Phase 1, VT4+ terminal to test on |

Explicitly **out of scope**: setup screens (vehicle / tool / autosteer tuning / fields /
NTRIP / display), coverage, imagery, anything map-editing. These stay in the browser.

---

## 6. HAT hardware notes (for the HAT repo)

The HAT already has 3 × MCP251863 (controller + transceiver) on SPI0 → `mcp251xfd` →
`can0..2`, each with a NUP2105L TVS and a 120 Ω termination behind a solder jumper.
For the bus used as ISOBUS:

- **Termination jumper stays open.** An ECU on the implement bus must not terminate;
  the tractor's Terminating Bias Circuit and the implement's TBC do that. Assembly and
  design-doc note, no schematic change.
- **Connector:** the in-cab connector (ISO 11783-2) carries CAN_H, CAN_L, ECU_PWR,
  ECU_GND and the TBC lines. The HAT needs only CAN_H/CAN_L (the ribbon already pairs
  them on adjacent conductors); it is powered from the vehicle key circuit as today.
- Nothing else on the HAT changes. This document is the only place it is recorded in
  the app repo; the hardware doc set in `AgOpenWeb-HAT` should carry its own line.

---

## 7. Stack options

| Option | For | Against |
|---|---|---|
| **C# implementation in Shared** (recommended) | One deployable; direct access to DTOs and the command handler; NUnit-testable against a fake `ICanBus`; the VT-client subset (address claim, TP/ETP, ~30 VT messages) is bounded | We own the protocol edge cases; no mature C# ISOBUS library exists to lean on |
| AgIsoStack++ in the HAT daemon | Proven, MIT-licensed, has VT client + AUX-N + Graphics Context helpers | UI logic in C++; must reimplement `WireCodec`; grows the daemon beyond its RT scope |
| AgIsoStack++ via a C shim P/Invoked from C# | Proven stack, UI stays in C# | Native build per arch; awkward for pool/callback plumbing |

AgIsoStack++ remains the **reference implementation** to read when the paywalled ISO text
is ambiguous, whichever option is chosen. Its companion **AgIsoVirtualTerminal** is a
VT server application that runs against `vcan`, and is the test target for phases 0–2.

---

## 8. Things to settle before implementation

- **Prior boundary.** [PGN_ISOBUS_MAPPING.md](PGN_ISOBUS_MAPPING.md) §1 says live
  ISO 11783 over CAN is "another group's domain" and AgOpenWeb stays file-level. This
  document crosses that line for the UI layer only; the data layer position is
  unchanged. Decide whether that split is acceptable.
- **Manufacturer code.** The notional code 2017 (0x07E1) in the mapping doc was chosen
  for UDP where nothing else listens. On a physical bus the NAME must not collide with a
  real assignment. Options: obtain an SAE manufacturer code, or ask whether
  Open-Agriculture's registered code may be used by projects built on their work
  (verify; do not assume).
- **Certification.** Without AEF conformance testing this cannot be described as
  ISOBUS-certified, and some OEM terminals are stricter with uncertified working sets
  (refusing pools, hiding AUX functions). Phase 0 on a real terminal is the only way to
  find out which.
- **Which HAT bus** is ISOBUS, and how the install script records it.
- **Authority UX.** Whether the VT acquires control implicitly on the first actuation
  soft key, or via an explicit "Take control" soft key mirroring the browser.

---

## Sources

- AgIsoStack++ (Open-Agriculture): https://github.com/Open-Agriculture/AgIsoStack-plus-plus —
  VT client, AUX-N, Graphics Context, TP/ETP, address claim; the practical reference.
- AgIsoVirtualTerminal (Open-Agriculture): VT server application for testing.
- ISO 11783-6 (Virtual Terminal), 11783-3 (transport), 11783-5 (network management),
  11783-2 (physical layer / connectors). Paywalled; cross-check specifics against
  AgIsoStack++ before relying on them.
- HAT hardware: `AgOpenWeb-HAT/hardware/hat/HARDWARE_HAT_DESIGN.md` (CAN section) and
  `boards/hat/hat_pins.h`.
- Existing wire contract: `Shared/AgOpenWeb.RemoteServer/Contracts.cs`,
  `RemoteServerHost.cs`, `ControlAuthority.cs`; command wiring in
  `Shared/AgOpenWeb.RemoteWiring/RemoteServerWiring*.cs`.
