# Working day scenarios: does AgOpenWeb stay as fast and as small after eight hours?

The other harnesses each test one thing for minutes. This one runs AgOpenWeb for a whole
working day in one process and watches the process itself: what a day leaves behind in
memory, threads, timers, open files and the data folder, whether the GPS cycle and the
100 Hz control tick get slower as the day goes on, and what happens to the host when a
browser goes away the ways a tablet or a phone in a tractor cab does.

The whole backend runs in the test process as the Desktop head boots it (the rig of the
field job harness: every service real, its UDP service on port 9999, a temporary data
folder), on the **day's clock**: ten fixes a second of simulated time, paced
against the wall clock (`AGOPENWEB_DAY_PACE`, 8 ms per fix = 12.5x, so an eight-hour day takes
about 40 minutes; 100 = real time). The 100 Hz loop is ticked by the harness, ten ticks per
fix, so the section timing runs on the day's clock too. Around it:

- **The tractor and sprayer** (`TruthMachine.cs`, from the field job harness): the Case Puma
  with the UM982 and the 8 m mounted sprayer in 8 sections, steered by the angle AgOpenWeb sends.
- **An emulated sprayer module** (`EmulatedSprayerModule.cs`) on 127.0.0.1:8888 and the steer
  module's PGN 253 (`SteerModuleStub.cs`).
- **An NTRIP caster** on the loopback (`FakeCaster.cs`, from the GPS and NTRIP harness): a
  profile saved in the morning, corrections all day.
- **Two browsers** (`WebClient.cs`) on the host's WebSocket: the operator's tablet (it holds
  control, takes a free seat back with `control.acquire` when a ControlState frame says the seat
  is free, and sends the presence heartbeat every 500 ms while it holds control, as
  `js/control.js`) and a helper's phone (an observer; it never claims the seat, so the deadman
  tests are the tablet's). Both count every frame by type, the seed on each connection and the
  gaps between Tick frames.

## The day

| Clock (8-hour day) | What happens |
|---|---|
| 07:00 | AgOpenWeb starts on a fresh data folder; the tablet, then the phone connect; Vehicle & Tool and the relay functions are typed; the NTRIP profile is saved |
| | **J1** Olival from KML: AB line, 16 m headland, U-turns on, job, AutoSteer and sections Auto; AgOpenWeb drives until it says the field is done; the field is closed. Where AgOpenWeb has no U-turn for the end of a pass (none fits inside the boundary), the operator, seeing no turn drawn as the tractor enters the headland, presses the manual U-turn button toward the next pass |
| | eight minutes on the road (no field open), then **J2** Quinta Torta |
| every hour of spraying | **refill**: AutoSteer and sections off, 15 minutes standing in the field |
| 09:00 | **the tablet's Wi-Fi drops without a word** while AutoSteer drives, the phone reading: the tablet's connection stays open, no presence heartbeat. The host's deadman should disengage AutoSteer within 1.5 s. Then the operator stops, the Wi-Fi comes back, the tablet reconnects |
| 10:00 | **the phone goes into a pocket** with the page open: it stops reading, the connection stays open (a phone with its screen off, or one carried out of Wi-Fi range) |
| 10:05 or later | **the tablet's Wi-Fi drops again**, the phone still in the pocket: the same deadman test |
| | before each drop the tablet must hold control (a pause of the process longer than the deadman, such as a heap dump, revokes it, and it claims the seat back); if it does not within 2 s, that test is reported as not tested, neither a pass nor a finding |
| 10:20 or later | the phone is taken out and reads again |
| | **J3** Olival again, opened from the field list for a second job: the same field and work as J1, to compare the morning with the afternoon |
| 12:30 | **lunch**: an hour parked in the field with the job open, GPS and NTRIP on, both pages closed; then the tablet reconnects, and the phone opens the page again on a slower Wi-Fi (20 Mbit/s, out by the sprayer) |
| | **J4** Tapada da Casa with its 82 obstacles, then J5 Baetas and J6 Couto Ribeira while the day lasts |
| 15:00 | end of the day: the field is closed, AgOpenWeb shuts down (exit save) |
| next morning | AgOpenWeb starts again on the day's data and the last job is resumed |

The phone stays in the pocket at least a minute of wall time and the tablet's second Wi-Fi drop
waits until the phone's stall is felt (the tablet waiting 3 s for a map update) or 3 minutes of
wall time after it, whatever the pace: socket buffers fill in wall time. A shorter day
(`AGOPENWEB_DAY_HOURS`) has every event at the same share of the day.

## What is measured

Every five minutes of the day (`Meters.cs`): the managed heap after a full blocking compacting
collection, resident and anonymous memory, OS and thread pool threads, live
`System.Threading` timers, open file descriptors (sockets and files), collections and bytes
allocated, CPU; the GPS cycle (the whole pipeline for one fix, run inline and timed) and the
control tick as percentiles over the five minutes (`Timings`, a log histogram); the host loop's
slowest answer to a probe posted every 10 s; the tablet's Tick frames per second and its
longest gap; the data folder's size; NTRIP's state.

**Baselines** are the same state hours apart: no field open, the tractor parked, the browsers
connected, after a full collection: after the set-up (`B0`), after each field is closed
(`B-J1`, `B-J2`...) and at the end of the day (`B-END`). A leak is a baseline that keeps rising.
**Heap censuses** (`HeapCensus.cs`) after the first field, at the end and after AgOpenWeb has
shut down: the process writes a dump of itself through the runtime's diagnostics port and,
after the day, ClrMD walks the live objects from the GC's roots and counts them by type. The
report lists the types that grew, each with the path from a root to its biggest object (from
the last AgOpenWeb object on it: what holds it in the app), and what is still alive once the
backend is gone, with what holds it. A GPS cycle or control tick that runs long is listed with
the day's last event before it and whether a garbage collection ran in it.

The looks pause the process themselves: two full compacting collections every five minutes of
the day (about every 24 s of wall time at 12.5x), and a heap dump suspends it for seconds. A
slow tick or cycle within 1 s of one is marked as the harness's and not blamed on a connect or
on the app's own collections, a host loop probe that one paused is not counted, and the
tablet's wait for a Tick restarts after it (a wait already running, the map stopped, keeps
counting). A lock contention is the process's count (`Monitor.LockContentionCount`), not one
lock's: a lock the report names is the suspected one.

## Findings

| Finding | Rule |
|---|---|
| `DEADMAN-DID-NOT-STOP-AUTOSTEER` (Safety) | the tablet went silent with AutoSteer on, the phone reading, and AutoSteer was still engaged 3 s later |
| `DEADMAN-BLOCKED-BY-A-PHONE` (Safety) | the same with the phone in the pocket, when the phone's stall had stopped the map (the broadcast loop stuck) and the tablet held control |
| `MAP-FROZEN-BY-A-PHONE` | while the phone was in the pocket the tablet waited more than 3 s for a map update |
| `HEAP-GROWS-OVER-THE-DAY` | every later baseline is more than 8 MB and 15 % above `B-J1` (above the first baseline after the phone read again, when that leaves an hour: what the phone left is judged under `MAP-FROZEN-BY-A-PHONE`) |
| `CLOSED-FIELD-COVERAGE-KEPT` | with no field open at the end of the day, `CoverageMapService` still holds more than 8 MB (heap census, by field) |
| `HEAP-GROWS-WHILE-PARKED` | the heap grew more than 3 MB during lunch (nothing worked) |
| `FILE-GROWS-WHILE-PARKED` | a file in the data folder grew more than 20 KB during lunch |
| `NATIVE-MEMORY-GROWS` | resident memory outside the managed heap grew more than 40 MB from `B-J1` to `B-END` |
| `THREADS-GROW`, `TIMERS-GROW`, `FILES-LEFT-OPEN`, `SOCKETS-LEFT-OPEN` | more than 8 threads, 3 files, 3 sockets at `B-END` than at `B-J1`; more than 10 timers in every baseline after `B-J1` (a pending `Task.Delay` is a timer; the census names the method awaiting it) |
| `GPS-CYCLE-SLOWER-LATER`, `CONTROL-TICK-SLOWER-LATER` | Olival in the afternoon (J3) 1.3x slower than in the morning (J1), p50 or p95 (p99 for the tick) |
| `GPS-CYCLE-OVER-100-MS`, `CONTROL-TICK-OVER-10-MS` | a cycle longer than the time between two fixes, a tick longer than its period |
| `HOST-LOOP-SLOW` | the host loop answered a probe after more than 250 ms, or logged a work item that held it |
| `MAP-PAUSED-WHILE-A-BROWSER-LOADS` | while the phone loaded the page on the slower Wi-Fi the tablet waited more than 3 s for a map update |
| `RECONNECT-NO-MAP` | a browser that connected and got no Tick, or its first after more than 5 s (not while the phone was in the pocket) |
| `NTRIP-DROPPED` | NTRIP not connected at a five-minute look |
| `EXCEPTIONS-IN-THE-DAY`, `ERRORS-LOGGED` | exceptions on the host loop, errors logged |
| `NEXT-START-SLOW` | the next morning's start took over 3 s and twice the fresh start |
| `BACKEND-NOT-FREED-AFTER-EXIT` | more than 30 MB stayed on the heap after the backend shut down in the process; Major when the first backend's `MainViewModel` is still alive after the next morning's start. This is Android's case: Exit stops `BackendService` but the process stays cached, so opening AgOpenWeb again starts a second backend in the same process, until Android reclaims it (iOS starts the backend once per process). A first backend kept alive is not only memory: its handlers still react to what the second one does (NTRIP enabled, for one) |

The test fails only when the harness itself breaks. The findings are the product: the report
lists each once, most serious first, with its kind (app defect, harness artifact, parity with
AgOpenGPS or enhancement), marked New or Already reported (`KnownFindings.txt`), then
the day's timeline, the fields, the baselines, the heap census, the process every half hour,
the browsers, the disk and what AgOpenWeb logged; the codes and the day's events keep their
names, so two nights diff.

## Running

```bash
dotnet test Tests/AgOpenWeb.WorkingDayScenarios.Tests/ -c Release --filter "FullyQualifiedName~WorkingDayScenarioTests"
# a quick look (a 1.5-hour day, 25x):
AGOPENWEB_DAY_HOURS=1.5 AGOPENWEB_DAY_PACE=4 dotnet test Tests/AgOpenWeb.WorkingDayScenarios.Tests/ -c Release
```

| Variable | Effect |
|---|---|
| `AGOPENWEB_DAY_PACE=8` | real ms per simulated 0.1 s fix: 8 is 12.5x, 100 is real time |
| `AGOPENWEB_DAY_HOURS=8` | the length of the day |
| `AGOPENWEB_DAY_REPORT=path.md` | where the report goes (default: the test output folder) |
| `AGOPENWEB_DAY_CSV=path.csv` | every five-minute sample, for a plot |
| `AGOPENWEB_DAY_CENSUS=0` | no heap census (it writes a dump of the process, a few hundred MB, for a few seconds) |
| `AGOPENWEB_DAY_VERBOSE=0` | quiet console |
| `AGOPENWEB_DAY_KEEP=1` | keep the data folder |

Release builds: a Debug build's GPS cycle is several times slower and its numbers are not the
app's. CI only builds the project; `working-day-scenarios.yml` runs it (by hand, or nightly:
the bug report Worker's `NIGHTLY_WORKFLOWS` lists it). Like the other harnesses it binds
UDP 9999: nothing else of AgOpenWeb may run on the machine.

The browsers are on the loopback, where Linux lets the host's socket take up to about 4 MB before
a send waits (`tcp_wmem`'s default): a browser that stops reading was felt only after about 41 s.
A tablet or phone on Wi-Fi that stops answering sends no acknowledgements, so the host's buffers
fill far sooner. The workflow models Wi-Fi's buffers: it caps `net.ipv4.tcp_wmem` and `tcp_rmem`
at 128 KB before the run, so a stalled browser is felt within a few seconds, and a page loading
over the slower Wi-Fi holds the host as long as its seed takes to arrive. A local run keeps the
machine's own buffers, so its timings of a stall differ from the nightly's. With the small buffers the silent tablet can fill
its own buffers before the deadman's 1.5 s: the report then says the phone, reading, stopped
getting map updates too (the broadcast loop stuck on the tablet). While the deadman is
waited for (a wall-clock watch), the day runs in real time, so the tractor goes as far as it
would on the field.

What an accelerated day does not do: the host's wall-clock work (the 10 Hz broadcast, autosaves,
watchdogs, NTRIP) happens 12.5 times less often per hour of the day than on a tractor, so a
leak that grows with those grows 12.5 times slower here; `AGOPENWEB_DAY_PACE=100` runs the day in
real time (past a hosted runner's six hours).
