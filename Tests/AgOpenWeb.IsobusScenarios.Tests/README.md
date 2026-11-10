# ISOBUS scenarios: the careful and the careless operator

An end-to-end harness for AgOpenWeb's link to an ISOBUS task controller
([AOG-TaskController](https://github.com/AgOpenGPS-Official/AOG-TaskController), standalone or
in the HAT daemon; `Docs/PGN.md` § "ISOBUS task controller"): the heartbeat with the implement's
own sections (PGN 240), the ISOBUS button and the ISOBUS row under the section buttons, the
section control mode (PGN 241), the sections the task controller is given (PGN 229), its
hardware messages (PGN 221), on a normal day and on a bad one.

The whole backend runs in the test process, as the Desktop head boots it, on a temporary
`AGOPENWEB_DATA` folder (the rig of `AgOpenWeb.MachineModuleScenarios.Tests`), **with its real
UDP service** on port 9999. Settings and taps go in exactly as the web client sends them
(`config.set|key:value`, `profile.save`, `field.*`, `track.*`, `section.master`,
`isobus.sectionControl`, `net.subnet`).

On the loopback, next to it:

- **an emulated task controller** (`EmulatedTaskController.cs`) on 127.0.0.1:8888, which does
  what AOG-TaskController's `docs/PROTOCOL.md` and `src/app.cpp` say (2026-11): every 100 ms
  one PGN 240 per implement with sections (mode, section count, the implement's actual section
  states, section 1 in bit 0) or, with no such implement and `aogHeartbeatEnabled`, a bare
  `{0, 0}` beacon; PGN 221 "Implement: name (n sections, w m)" when an implement registers and
  "Implement lost: name" when it leaves; PGN 229 (the 64 setpoints), 241 (mode) and 242
  (process data) taken from source 0x7F only, PGN 214 (fix quality) from source 0x7C; the
  checksum counted but not enforced, as the real one. It can stop and restart (its clients
  come back with section control off), lose its network, take and lose implements, ignore or
  delay a mode request, have its mode switched on the terminal, slow its heartbeat, and send
  any raw frame;
- **the implements** on its bus (`Implement`): a section count, a width, the sections they
  really have on. In automatic section control they follow the setpoints after a valve delay
  (300 ms); in manual they do what their own switches say; a stuck section never comes on;
- **a steer module stub** (`SteerModuleStub.cs`) that sends PGN 253 ten times a second,
  because AgOpenWeb sends its settings again when a steer module appears and the task
  controller hears those too;
- **a tractor** (`IsobusSession.cs`) that sends `$PAOGI` fixes at 10 Hz in real time,
  driving north at 8 km/h on fresh ground.

Every 100 ms the harness compares the sections on AgOpenWeb's screen with the setpoints the
task controller received and with the implement's own sections, checks that the ISOBUS row
(what AgOpenWeb reports from PGN 240) matches the implement, that the status frame and the
service agree on whether a task controller is heard, that the mode on the button is the task
controller's, and counts the coverage painted while the implement did nothing.

Two operators:

- **Careful** (`CarefulOperator.cs`): task controller and steer module on first, the tool typed
  in with the implement's section count. C1 an 8-section sprayer; C2 a 24-section planter; C3
  master off and on, manual and back; C4 the task controller restarts; C5 the planter unplugged
  and plugged back; C6 AgOpenWeb restarted with the task controller on.
- **Careless** (`CarelessOperator.cs`): K1 the task controller started after AgOpenWeb; K2 a
  task controller that ignores the request, then one that takes 1.5 s; K3 the mode switched on
  the tractor's terminal; K4 the task controller's link down for 5 s and 30 s; K5 malformed and
  odd frames; K6 64 sections; K7 the tool typed with 20 sections, the implement has 12; K8 two
  implements with sections; K9 beacon off and no implement; K10 the field closed in automatic;
  K11 the module subnet changed.

## Running

```bash
dotnet test Tests/AgOpenWeb.IsobusScenarios.Tests/ --logger "console;verbosity=detailed"
```

About 6 minutes (careful about 1.5, careless about 4). Environment variables:

| Variable | Meaning |
|---|---|
| `AGOPENWEB_SCENARIO_REPORT` | where the Markdown report goes (default: the test's work folder) |
| `AGOPENWEB_SCENARIO_OPERATOR` | `careful` or `careless` (default: both) |
| `AGOPENWEB_SCENARIO_ONLY` | scenario codes, comma-separated (`K2,K8`; `C` = the careful day) |
| `AGOPENWEB_SCENARIO_VERBOSE` | `0` to keep the console quiet |

The test fails only when the harness itself breaks (a `HARNESS` finding, or nothing run).
The findings are the product: each kind once in the report, most serious first, with the
scenarios it was seen in, and each scenario's checks, what the task controller received and a
timeline with the task controller's own log. Finding codes (`ISOBUS-…`) and scenario codes are
fixed identifiers, so one night's report can be compared with the next.

It needs UDP ports 9999, 2211, 2222 and 8888 free on the machine (stop a running AgOpenWeb,
a module simulator and the other scenario harnesses first: they share the ports).

## Limits

- The task controller is its UDP side only: no CAN, no DDOP, no virtual terminal. The VT cards
  (`AgOpenWeb.VirtualTerminal`, SharpIsoTerm) are not covered.
- One task controller on the loopback: scans (202) and a task controller on another box are
  not exercised.
- The tractor drives straight on fresh ground: section switching at boundaries and on
  coverage belongs to the field harnesses.
