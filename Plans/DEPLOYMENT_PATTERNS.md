# Deployment Patterns — Hosting the Brain + Real-Time Control in One Box

Status: **exploration** · No branch yet · Owner: chris

The Web UI migration turns AgOpenWeb into a **headless host + thin browser
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

### Two physical realizations of the same contract

The logical contract above is identical everywhere, but **"a shared data
structure in memory" is only literally true on one of the two topologies** —
worth knowing before assuming you can just declare a struct both sides poke:

- **Single-die AMP SoC** (STM32MP1/MP2, NXP i.MX8M-Plus, TI AM62x — a Cortex-A
  cluster + a Cortex-M core sharing **one** physical DRAM): the contract *is* a
  literal coherent shared-memory struct. Both cores map the same region;
  synchronization is **OpenAMP/RPMsg + mailbox IRQs**. "Shared structure in
  memory" at face value, and you own the seqlock/double-buffer/barriers.
- **Two-chip board** (Uno Q = QRB2210 + **separate** STM32U585; or Pattern D's
  discrete MCU): the two processors are distinct silicon and **do not share
  physical DRAM**. They cannot map one struct. Arduino bridges them with **RPC
  over a shared-memory transport** (the Uno Q "shared-memory bridge" — shared
  memory is the *transport underneath*, RPC is the *model you program against*).
  The same logical contract holds, but you reach it through the bridge API, and
  the sequencing/ownership discipline is the bridge's job, not hand-rolled.

So the contract section's double-buffered-struct sketch describes the
single-die case directly; on a two-chip bridge it's the *wire shape behind* the
RPC, not something both sides mutate in place.

⚠ **.NET binding caveat (two-chip bridges).** Arduino's Bridge/RPC SDK targets
their **App Lab** world — Python on the Linux side ↔ C++ sketches on the MCU.
AgOpenWeb's brain is **.NET**, so the Linux side needs a binding step to reach
the bridge: P/Invoke into the bridge library, a local bridge daemon spoken over
IPC, or reimplementing the shared-region protocol against the same region.
Not hard, but it is **not** "Arduino hands .NET a shared struct." Budget it
explicitly — it folds into de-risk spike #2 (the shared-memory bridge spike).
The single-die AMP path avoids this (you talk RPMsg/`/dev/rpmsg*` directly from
.NET), trading the MCU-port question for an SoC-with-M-core board choice.

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
Qualcomm Linux: AgOpenWeb server + RemoteServer web host ──WiFi──▶ tablet
```

**Decisions taken (this exploration):**
- GPS/IMU stay **on the STM32** (serial/onboard), fused on-MCU like the Teensy —
  so that code is *reused*, not rewritten on Linux.
- Multi-bus brands handled via **SPI MCP2518FD CAN-FD expanders**. Native FDCAN
  → steering bus (lowest latency); SPI expanders → slower K-Bus/ISOBUS.

**Pros:** Hardware-guaranteed RT timing (the MCU physically cannot be starved by
Linux). **Independent failure domain** — a kernel-level fault on the Linux side
(panic, OOM-killer, wedged driver, scheduler stall) does not take the control
chip with it; the MCU keeps running and can hold/safe-stop. **Small, auditable
safe-state path** (IWDG → reset → outputs safe). Linux side *is* the web-UI
headless host the migration already assumes. One physical box.
(The MCU is *not* crash-proof — see the cross-pattern watchdog note; the win is
failure independence + an analyzable safe path, not invulnerability.)

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
- **Bridge is RPC-over-shared-mem, not a raw shared struct, and the SDK is
  .NET-foreign.** The two chips don't share DRAM; you reach the MCU through
  Arduino's Bridge/RPC, whose SDK targets Python/C++ App Lab — so the .NET host
  needs a binding step. See "Two physical realizations of the same contract"
  above. A single-die AMP SoC (Cortex-A + Cortex-M) sidesteps both this and the
  160 MHz squeeze, at the cost of a different board + the RPMsg port.

---

## Pattern B — Single SBC + PREEMPT_RT (recommended single-box option; AutoSD/RHIVOS variant)

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
- **Shared failure domain.** The RT control process and the .NET guidance share
  one kernel and one resource pool. A plain *.NET userspace crash* does **not**
  take the RT process down — but a **kernel-level** fault (panic, OOM-killer,
  wedged driver, scheduler stall) or resource starvation takes *both* down
  together. And the guidance/OS side is the far-more-likely thing to fault.
  Closeable: add an **external safety MCU / hardware watchdog** on the
  steer-enable line (the "safety island" pattern) — see the cross-pattern
  watchdog note. Non-negotiable for a moving machine either way.
- **I/O path caps determinism** (see shared note below). SPI-CAN under
  PREEMPT_RT needs RT priority on the SPI IRQ/worker threads. USB-CAN is poor.

### B-variant: CentOS AutoSD / RHIVOS instead of rolling your own PREEMPT_RT

AutoSD (Automotive Stream Distribution) is the open, in-development upstream
preview of **RHIVOS** (Red Hat In-Vehicle OS). Its `kernel-automotive` is
**PREEMPT_RT** — same mechanism as mainline 6.12, *not* a dual kernel and *not* a
new determinism class. So it does **not** make latency tighter than generic
PREEMPT_RT, and it doesn't change the math for our 10–20 ms loops. What it
changes is **who maintains the kernel and how the RT/non-RT split is sanctioned:**

- **Kills the biggest con of Pattern B.** Replaces "roll-your-own PREEMPT_RT +
  chase BSP fragility" with a **vendor-maintained automotive RT kernel** targeting
  exactly the SoCs we care about (Qualcomm, NXP, TI, Renesas, RPi4; ARM64). The
  kernel becomes a supported dependency instead of a liability.
- **Mixed-criticality partitioning is built-in.** AutoSD is designed to run
  safety-critical and non-critical workloads on one platform "with proven
  isolation using containers and partitioning" — precisely our architecture
  (C/C++ RT control core in an isolated/partitioned container; .NET guidance +
  web host as the non-critical workload). A blessed isolation pattern, not one we
  invent.
- **Credible certification path if this ever becomes a product** — RHIVOS is the
  commercial, safety-certified downstream.

**Caveats — what AutoSD does NOT change:**
- **Functional-safety certification lives behind commercial RHIVOS, not AutoSD.**
  AutoSD is explicitly an in-development preview; its safety requirements/docs are
  "available only to RHIVOS customers." An all-Linux future with real ISO 26262 /
  ASIL paperwork = a paid RHIVOS relationship. Fine for AgOpen's open ecosystem
  (never a certified ASIL product; steering safety stays with operator + engage
  logic + HW watchdog), but don't mistake the free distro for a certified OS.
- **I/O path still dominates** — PREEMPT_RT CAN drivers are normal RT-priority
  kernel threads (which is fine, and notably *avoids* Xenomai's RTDM-driver gap),
  but native on-SoC CAN > SPI > USB still holds.
- **Shared failure domain is unchanged.** Partitioning isolates the RT workload
  from the *.NET* workload, not from a kernel-level fault. **Independent external
  safety MCU / hardware watchdog on steer-enable still required.**

Net: AutoSD strengthens the all-Linux case on **maintainability + productization**
and leaves the **failure-domain-independence safety axis** exactly where it was — still the
real deciding factor vs Patterns A/D.

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
- Same shared failure domain as Pattern B (still needs an independent HW
  watchdog / safety MCU on steer-enable).

**Reserve Cobalt only if** measured PREEMPT_RT jitter genuinely breaks the loop
**and** native RTDM-capable CAN is available — both unlikely here.

---

## Pattern D — Dumb MCU front-end over USB to any SBC

Keep a Teensy/STM32 as a **dumb hard-RT CAN + sensor front-end**, connected over
**USB** to any SBC running the .NET guidance + web host. This is Pattern A in
discrete parts.

**Pros:** Independent failure domain (like A — a Linux kernel fault doesn't take
the control chip with it) **and** freedom to pick a fast SBC with native CAN
(like B). Sidesteps both the 160 MHz squeeze *and* the RT-kernel question.
Reuses the existing Teensy as-is (minus networking).

**Cons:** Adds a box back (defeats the "one box" goal). USB link adds latency/
jitter on the bridge — fine for the decoupled-cadence model (MCU stays
autonomous between setpoints), but it's a non-RT transport.

---

## Backend compute: x86-64 as the .NET host (Pattern B/D, not A)

The patterns above lean ARM (Uno Q, SBCs), but the .NET "brain + web host" half
runs equally well — arguably **lowest-risk** — on an **x86-64 compute unit**.
This is the original `remote-web-ui` premise ("browser on a **cab-PC** headless
host"); the ARM SBCs were the *collapse-to-one-box* optimization, x86-64 is the
conservative baseline.

**Why compute is a non-issue.** The backend does guidance math + coverage drain
+ WebSocket fan-out — **no rendering** (that's the browser's GPU). Even the
smallest modern x86 (**Intel N100**, 4× Gracemont, ~6 W) is desktop-class and
dwarfs the workload. Size an x86 box for **thermal / power / I/O, not CPU/RAM** —
these boards ship 8–16 GB standard, so the coverage-bitmap RAM concern that sizes
the ARM backend just disappears. (Backend workload framing: an iPad — our 50 FPS
floor device — already does GPS + guidance + coverage **plus full rendering**;
the headless backend drops the rendering, so it sits *below* the floor device's
load.)

**Why it's the lowest-risk .NET path.** .NET's JIT/runtime is most mature on x64,
and AgOpenWeb **already ships a Desktop x64 target** (Win / macOS-x64 / Linux-x64
in CI). An x86-64 Linux backend is *literally the existing Desktop x64 build minus
the UI* — the least-risk headless port of any option.

**What x86-64 changes vs the ARM patterns:**
- **No free RT co-core → Pattern A is off the table.** x86 mini-PCs have no
  integrated Cortex-M and no single-die A+M analog, so you're forced into
  **Pattern B (PREEMPT_RT on x86)** or **Pattern D (discrete MCU over USB)**.
  Upside: **PREEMPT_RT's original home is x86** — best-supported RT platform there
  is, so Pattern B on x86 is rock-solid.
- **Single failure domain → external safety MCU is MANDATORY** (loses Pattern A's
  free failure-domain independence; see the watchdog note below).
- **CAN gets a wrinkle.** No native CAN, and mini-PCs rarely expose SPI headers
  (so no MCP2518FD wiring like on an SBC). Use an **M.2 / mPCIe CAN-FD card**
  (proper controller, best latency — the right call for the steering bus), or
  **USB-CAN** for slower section/ISOBUS buses only (ms-class jitter; avoid for
  steering — matches the I/O ranking below).
- **Power/thermal up.** Stay **Atom-class (N100 / N97 / N305, ~6–15 W)**, not
  desktop i5/Ryzen. Fanless wide-temp industrial N100 boxes are a mature category
  (kiosk/signage/industrial), so fanless + 9–36 V automotive input + sealed
  enclosure is readily sourced. Desktop-CPU mini-PCs are overkill and harder to
  cool in a cab.

**Spec (x86-64 flavor):**

| | Target |
|---|---|
| **CPU** | Intel **N100 / N97 / N305** (Alder Lake-N) class; newer is gravy |
| **RAM** | 8 GB (standard; coverage RAM is a non-issue here) |
| **Storage** | 64–128 GB NVMe/eMMC |
| **CAN** | **M.2/mPCIe CAN-FD card** for the steering bus; USB-CAN only for slow buses |
| **RT** | PREEMPT_RT (mainline 6.12+); pin the control core to an isolated CPU |
| **Safety** | external safety MCU / HW watchdog on steer-enable (**required** — single failure domain) |
| **Enclosure** | fanless, wide-temp, automotive (9–36 V) power conditioning |

**Net:** x86-64 minimizes software/port risk and maximizes headroom; ARM
(Uno Q / single-die AMP) minimizes box count and buys the safety isolation for
free. It's a **risk-vs-integration** tradeoff, not a performance one — the
backend is light enough that any of them has ample compute.

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

## Cross-pattern note: an independent hardware watchdog is the real safety mechanism

No CPU is crash-proof. An STM32 — RTOS *or* bare-metal superloop (the Teensy uses
a superloop, not an RTOS) — can hard-fault, stack-overflow, deadlock, or hang on
a stuck peripheral, exactly as a Linux kernel can panic. So "separate chip" does
not mean "cannot fail," and an RTOS is not what makes a design safe. **What makes
any of these patterns fail-safe is an independent watchdog that forces the
actuator (steer-enable) to a safe state on loss of heartbeat:**

- **On an MCU**, the **STM32 IWDG** runs off its own internal LSI oscillator,
  independent of the main clock. If the firmware hangs — superloop stuck, RTOS
  deadlocked — the IWDG isn't kicked and resets the chip → outputs safe. The path
  from "hung" to "valve de-energized" is tiny and fully auditable.
- **On Linux**, `/dev/watchdog` / the SoC watchdog works, but the kick path runs
  through a userspace daemon → kernel driver → watchdog HW. A kernel wedged in a
  way that still services the watchdog thread can "kick a dead system." The safe
  path is long and harder to prove — which is why a **separate external safety
  MCU** supervising steer-enable (the "safety island" pattern) is the robust
  answer for single-SBC designs. The Uno Q is literally that shape (A53 + M33).

So two-chip designs (A/D) don't buy invulnerability — they buy **failure-domain
independence** (a fault in the complex, failure-prone guidance/OS side doesn't
propagate to the control chip) plus a **small, analyzable safe-state path**. A
single SBC (B/C) can recover most of that property by adding an external safety
MCU/watchdog; it just has to do so deliberately rather than getting it for free.

---

## The deciding axis

It is **not** the kernel, and it is **not** "which chip can't crash" (none can't).
It is: **when the most failure-prone component — the complex guidance/OS — dies,
does the actuator stay controllable long enough to reach a safe state, and how
small/auditable is the path that gets it there?**

- **Failure-domain independence for free** → Pattern A (Uno Q) or Pattern D
  (discrete MCU). A Linux-side kernel fault doesn't reach the control chip; the
  safe path is the MCU's IWDG.
- **Shared failure domain, gap closed deliberately** → Pattern B (PREEMPT_RT
  single SBC). More compute, simpler BOM, but a kernel-level fault is a control
  fault **unless** an external safety MCU / hardware watchdog on steer-enable
  catches it. Add one.

Every pattern needs an independent watchdog on steer-enable regardless; the
question is only whether you get the isolation for free or bolt it on.

## Recommendation

- **For a true single box with failure-domain independence for free:** Pattern A
  (Uno Q), accepting the MCU port + compute-budget risk.
- **For most compute / least porting in one box:** Pattern B (PREEMPT_RT) with a
  mandatory external safety MCU / hardware watchdog on steer-enable (closes the
  shared-failure-domain gap). **Use PREEMPT_RT, never Xenomai Cobalt** —
  and prefer the **AutoSD/RHIVOS variant** to get a vendor-maintained automotive
  RT kernel + sanctioned mixed-criticality partitioning instead of rolling your
  own (see B-variant).
- **Lowest RT risk overall:** Pattern D (discrete MCU over USB), at the cost of a
  second box.

## Running the .NET brain: headless host as a systemd daemon

Across every pattern above, the .NET guidance/UI half ("the brain") is the same
process — and on the cab-PC / single-SBC targets (A's A53 Linux side, B, D's host)
it runs as a **headless systemd service**, not a windowed app. This is the realized
form of de-risk spike #1 and the end-state of the web-UI migration (Phase 10): the
host has **no native UI**; operators use a browser — a remote tablet, or a local
`chromium --kiosk http://localhost:5174` on an attached display. You cannot cleanly
daemonize a windowed Avalonia app; a headless generic-host + Kestrel process is what
runs under systemd.

Boot it headless by default; `--windowed` (or `AGOPENWEB_WINDOWED=1`) launches the
legacy native window for local verify/compare. Ship: `deploy/linux/` —
`install.sh` (publishes the **self-contained** build — no .NET runtime on the box;
**not trimmed**, the steer wizard reflects over step ViewModels — to `/opt/agopenweb`,
creates the `agopenweb` user, installs the unit) + `agopenweb.service` + `uninstall.sh`.

The unit (`Plans`-adjacent `deploy/linux/agopenweb.service`):

- **`Type=notify` + `WatchdogSec=30`** — the host sends `READY=1` once it is serving
  (systemd marks the unit started only when the browser endpoint is live) and pets
  the watchdog via `sd_notify` (`SystemdWatchdogService`, pinging at half-interval).
- **`Restart=always`** + **`After=network-online.target`** — the brain returns after
  a crash, and only starts once module/NTRIP sockets have a real network.
- **dedicated unprivileged `agopenweb` user** in `dialout` (USB-serial GPS/AiO) +
  `can` (SocketCAN); **journald** logging; **`StateDirectory`** for field data/config;
  moderate hardening (tighten per site).

> **Do not conflate the two watchdogs.** systemd's `WatchdogSec` restarts a *hung
> brain* — it is an availability mechanism on the failure-prone OS side, and its kick
> path runs through userspace→kernel (the "kick a dead system" caveat from the
> cross-pattern watchdog note). It is **not** the steer-enable safety watchdog. The
> fail-safe that de-energizes the actuator on loss of heartbeat is still the
> **independent hardware/MCU watchdog** described above, and every pattern needs it
> regardless of how the brain is supervised.

**The co-resident mobile case is NOT a daemon.** When the brain runs *on the tablet
itself* (no separate box), it's an app, not a service: Android = a **foreground
service** (persistent notification, `START_STICKY`); iOS = a **foregrounded app +
Guided Access** (no true background daemon). systemd packaging applies only to the
Linux cab-PC / SBC targets.

## Suggested de-risk spikes (order)

1. **Linux host spike** (lowest risk, pattern-independent): AgOpenWeb headless
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
- CentOS AutoSD (upstream preview of RHIVOS) — <https://sig.centos.org/automotive/>
- AutoSD real-time (kernel-automotive = PREEMPT_RT) — <https://docs.centos.org/automotive-sig-documentation/about/con_real-time-linux-kernel/>
- LinRT Cobalt BSP (6.12-dovetail, Xenomai 3.3) — <https://www.linrt.com/>
- Arduino UNO Q (QRB2210 + STM32U585) — <https://docs.arduino.cc/hardware/uno-q>
- Uno Q inter-processor comms (RPC + shared-memory bridge, two-chip) —
  <https://linuxgizmos.com/arduino-uno-q-combines-qualcomm-dragonwing-qrb2210-and-stm32-mcu/>
- Linux multi-core AMP shared memory (OpenAMP / RPMsg, single-die A+M SoC) —
  <https://www.kernel.org/doc/html/latest/staging/remoteproc.html>
- STM32U585 datasheet (1× FDCAN) — <https://www.st.com/resource/en/datasheet/stm32u585ai.pdf>
- Related: `Plans/WEBUI_MIGRATION_PLAN.md`, `Plans/REMOTE_WEB_UI_SPLIT.md`
