# Deployment Patterns — Hosting the Brain + Real-Time Control in One Box

Status: **exploration** · No branch yet · Owner: chris

The Web UI migration turns AgValoniaGPS into a **headless host + thin browser
client** model: the guidance "brain" runs as a server, the cab tablet is just a
browser over WiFi. That seam opens up new hardware deployment patterns — instead
of "cab PC + AiO/Teensy box on Ethernet," we can collapse the whole stack into a
single box. This document captures the patterns explored, the constraints
discovered, and the recommendation.

**Scope assumption for all patterns below:** CAN-controlled tractors (CAN
steering valve / curve command), **not** PWM/H-bridge motor steering. This drops
the entire motor-driver hardware path and narrows the firmware to CAN + sensor
I/O + the control loop.

**The transport reframe:** there is **no Ethernet and no PGN-over-UDP** in any of
these patterns. The firmware↔server link becomes shared memory (or shared-memory
IPC between processes). The single largest subsystem of the current Teensy
firmware — the QNEthernet/lwIP network stack (122 `lwip/*` + 34 `QNEthernet`
includes) — is **deleted, not ported**.

---

## The one invariant across every pattern: the .NET / RT split

The guidance app is **.NET (managed, GC'd)** and cannot run in a hard-real-time
context — GC pauses and the managed runtime are not RT-safe. Therefore **every**
pattern has the same fundamental seam:

```
  [ RT control core: C/C++ ]  ──shared memory──  [ guidance + web host: .NET ]
        50 Hz steer                                   guidance, UI host
        100 Hz section                                WiFi → tablet
        CAN I/O, sensor fusion
```

Whether the two sides are two chips or two scheduling domains on one chip, the
contract is identical. **Define it once; it is reusable across all patterns.**

### Shared-memory contract (sketch)

Fixed-layout, double-buffered struct with a sequence counter + CRC. Not a chatty
RPC. RT side reads latest setpoint each control tick; .NET side reads latest
telemetry each guidance/render tick. Mirrors the existing decoupled cadences
(steer @50 Hz, machine/section/coverage @100 Hz, GPS @10 Hz, screen pose
dead-reckoned).

- **Up (RT → .NET):** fused position, heading, WAS angle, section feedback,
  engage state, fix quality, RT-side health/heartbeat.
- **Down (.NET → RT):** steer setpoint (target curvature/angle), section
  commands, engage request, config (brand profile, WAS cal, gains), and a
  **heartbeat / sequence counter** the RT side watchdogs for safety.

---

## Pattern A — Arduino Uno Q (dual-brain SoC + MCU)

One board: Qualcomm Dragonwing **QRB2210** (quad Cortex-A53 @ 2.0 GHz, Debian
Linux, 2–4 GB RAM, dual-band WiFi) + **STM32U585** (Cortex-M33 @ 160 MHz).
MPU↔MCU communicate over the Uno Q **shared-memory bridge** (no Ethernet on the
board — only WiFi/BT + USB-C).

```
STM32U585 (front-end + real-time control)
  • GPS serial (UBX/NMEA)  ┐
  • IMU (onboard)          ├─ fused on-MCU (as Teensy does today)
  • control loop @50/100Hz ┘
  • CAN: native FDCAN  ──▶ steering valve / curve command   (lowest latency)
         MCP2518FD #1 ──▶ 2nd bus  (SPI)
         MCP2518FD #2 ──▶ 3rd bus  (SPI)
  • SAFETY AUTHORITY: watchdogs Linux heartbeat; lost beat ⇒ disengage
        ▲
        │  shared memory (Uno Q bridge)
        ▼
Qualcomm Linux: AgValonia server + RemoteServer web host ──WiFi──▶ tablet
```

**Decisions taken (this exploration):**
- GPS/IMU stay **on the STM32** (serial/onboard), fused on-MCU like the Teensy —
  so that code is *reused*, not rewritten on Linux.
- Multi-bus brands handled via **SPI MCP2518FD CAN-FD expanders**. Native FDCAN
  → steering bus (lowest latency); SPI expanders → slower K-Bus/ISOBUS.

**Pros:** Hardware-guaranteed RT isolation (the MCU physically cannot be starved
by Linux). Strong fault isolation. Linux side *is* the web-UI headless host the
migration already assumes. One physical box.

**Cons / risks:**
- **Port to a constrained MCU.** 160 MHz Cortex-M33 vs the Teensy 4.1's 600 MHz
  Cortex-M7. The current firmware's build flags already fight for flash on the
  *Teensy* (`-Os`, `-fno-exceptions`, `-fno-rtti`).
- **One FDCAN instance** on the U585 vs three native CAN on the Teensy — hence
  the SPI expanders. The current firmware uses CAN1/2/3 simultaneously per
  brand (V-Bus / K-Bus / ISOBUS).
- **MCU compute budget is the make-or-break number:** GPS parse + IMU fusion +
  3 CAN buses (1 native + 2 SPI) + control loop, all at 160 MHz. Must be
  measured, not assumed.

---

## Pattern B — Single SBC + PREEMPT_RT (recommended single-box option)

One SBC runs everything. The C/C++ control core runs as an **RT-priority
process** pinned to an isolated core under **mainline PREEMPT_RT**; the .NET
guidance/host runs on the normal cores. CAN via SPI (MCP2518FD) or native
on-SoC FDCAN if the board has it.

**Why PREEMPT_RT and not a dual kernel:**
- PREEMPT_RT was **merged into mainline Linux 6.12 (Sept 2024), ARM64
  supported** — it's a **stock-kernel config option**, survives kernel upgrades,
  no out-of-tree patches.
- Worst-case scheduling jitter on modern ARM64 is **tens of microseconds** —
  well under **1%** of a 10 ms (100 Hz) period. The loop rates here do not need
  finer determinism.

**Pros:** No port to a weak MCU — recompile the C++ control core on a GHz-class
ARM64 with ample headroom. One box, one stock kernel, low maintenance.

**Cons / risks:**
- **No physical fault isolation.** A kernel panic / Linux hang takes the
  steering loop with it. **Requires an external hardware watchdog / safety relay**
  that drops steer-enable if the RT process stops kicking it. Non-negotiable for
  a moving machine.
- **I/O path caps determinism** (see shared note below). SPI-CAN under
  PREEMPT_RT needs RT priority on the SPI IRQ/worker threads. USB-CAN is poor.

---

## Pattern C — Single SBC + Xenomai Cobalt (dual kernel) — NOT recommended

Same single-box idea, but RT tasks run in the **Cobalt co-kernel** alongside
Linux via the **Dovetail** interrupt pipeline (e.g. LinRT "6.12-dovetail" BSP,
Xenomai 3.3).

**Why it's not recommended here:**
- **Solves a problem we don't have.** Cobalt buys single-digit-µs determinism;
  our loops are 10–20 ms. Three orders of magnitude of headroom we'd pay for in
  maintenance.
- **The win evaporates at the I/O boundary.** Cobalt's µs guarantees only hold
  for **RTDM** drivers in the co-kernel domain. There are **no RTDM drivers for
  MCP2518FD SPI-CAN or USB-CAN** — CAN frames exit to normal Linux at the driver
  boundary, so real determinism reverts to the SPI/USB driver's jitter anyway.
- **Maintenance tax:** patched kernel pinned to specific versions, separate RT
  API (POSIX/alchemy skin) for RT code, harder debugging, BSP fragility.
- Same weak fault isolation as Pattern B (still needs a HW watchdog).

**Reserve Cobalt only if** measured PREEMPT_RT jitter genuinely breaks the loop
**and** native RTDM-capable CAN is available — both unlikely here.

---

## Pattern D — Dumb MCU front-end over USB to any SBC

Keep a Teensy/STM32 as a **dumb hard-RT CAN + sensor front-end**, connected over
**USB** to any SBC running the .NET guidance + web host. This is Pattern A in
discrete parts.

**Pros:** Physical RT isolation (like A) **and** freedom to pick a fast SBC with
native CAN (like B). Sidesteps both the 160 MHz squeeze *and* the RT-kernel
question. Reuses the existing Teensy as-is (minus networking).

**Cons:** Adds a box back (defeats the "one box" goal). USB link adds latency/
jitter on the bridge — fine for the decoupled-cadence model (MCU stays
autonomous between setpoints), but it's a non-RT transport.

---

## Cross-pattern note: CAN I/O dominates real-time determinism

Ranked best → worst for jitter, regardless of kernel:

1. **Native on-SoC CAN** (SocketCAN over on-chip FDCAN) — best. Many SBCs lack it.
2. **SPI MCP2518FD** (CAN-FD) with RT-tuned driver — realistic best without native CAN.
3. **USB-CAN** — worst; polled, host-controller scheduling, ms-class jitter.
   Avoid for the steering bus.

**Put the steering / curve-command bus on the lowest-latency path available**
(native FDCAN, else the dedicated SPI controller); push slower section/ISOBUS
traffic to expanders.

---

## The deciding axis

It is **not** the kernel. It is **how much you trust Linux with the steering loop
on a moving tractor:**

- **Physical isolation** → Pattern A (Uno Q) or Pattern D (discrete MCU). The RT
  chip cannot be starved by Linux.
- **Software isolation + hardware watchdog** → Pattern B (PREEMPT_RT single SBC).
  Simpler hardware, more compute, but a Linux fault is a control fault unless the
  external watchdog catches it.

## Recommendation

- **For a true single box with strongest safety story:** Pattern A (Uno Q),
  accepting the MCU port + compute-budget risk.
- **For most compute / least porting in one box:** Pattern B (PREEMPT_RT) with a
  mandatory external hardware watchdog. **Use PREEMPT_RT, never Xenomai Cobalt.**
- **Lowest RT risk overall:** Pattern D (discrete MCU over USB), at the cost of a
  second box.

## Suggested de-risk spikes (order)

1. **Linux host spike** (lowest risk, pattern-independent): AgValonia headless
   server + RemoteServer on the target Debian/arm64, tablet on WiFi. Proves the
   brain + UI half.
2. **Shared-memory bridge spike:** trivial RT↔.NET loop (Uno Q bridge, or
   shared-mem IPC on one SBC). Push a counter up, a setpoint down; measure
   round-trip latency + jitter at 50/100 Hz. Validates transport *and* the safety
   heartbeat timing.
3. **RT / MCU load spike:** GPS parse + IMU fusion + native CAN + one SPI CAN,
   measure CPU headroom (on the U585 for A, on the SBC under PREEMPT_RT for B).
   This is the make-or-break number.
4. Only then port the control core.

Steps 1 and 2 are independent and can run in parallel.

## References

- PREEMPT_RT mainlined in Linux 6.12 (Sept 2024), ARM64 supported —
  <https://en.wikipedia.org/wiki/PREEMPT_RT>
- Xenomai 3 Cobalt / Dovetail dual-kernel overview — <https://v3.xenomai.org/overview/>
- LinRT Cobalt BSP (6.12-dovetail, Xenomai 3.3) — <https://www.linrt.com/>
- Arduino UNO Q (QRB2210 + STM32U585) — <https://docs.arduino.cc/hardware/uno-q>
- STM32U585 datasheet (1× FDCAN) — <https://www.st.com/resource/en/datasheet/stm32u585ai.pdf>
- Related: `Plans/WEBUI_MIGRATION_PLAN.md`, `Plans/REMOTE_WEB_UI_SPLIT.md`
