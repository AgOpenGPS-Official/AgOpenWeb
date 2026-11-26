# GPS and NTRIP scenarios: the careful and the careless operator

An end-to-end harness for what happens to RTK when the day goes wrong: the internet drops,
the caster restarts or goes quiet, the tablet moves from the tractor's Wi-Fi to the phone's
hotspot, the receiver loses RTK under a hedge, an antenna cable comes off, the position jumps;
and for what an operator gets wrong: a password, a mount point, an address, a port, a VRS
mount point saved before there is a position, a receiver on another box.

The whole backend runs in the test process, as the Desktop head boots it, on a temporary
`AGOPENWEB_DATA` folder (the rig of `AgOpenWeb.VehicleToolScenarios.Tests`), **with its real
UDP and NTRIP services**. Settings go in exactly as the web client sends them
(`ntrip.save`, `ntrip.test`, `ntrip.mountpoints`, `config.set|key:value`, `field.*`,
`track.*`, `section.master`, `autosteer.toggle`).

On the loopback, next to it:

- **a fake caster** (`FakeCaster.cs`): NTRIP 2.0 (HTTP/1.1, chunked) and 1.0 (`ICY 200 OK`),
  401 or `ERROR - Bad Password` for a wrong login, its source table for an unknown mount
  point, a VRS mount point that sends nothing until it has a GGA. Every second it sends an
  epoch of MSM7 observations (GPS, GLONASS, Galileo, BeiDou; about 1.4 KB), every 10 s the
  base position (1005), the antenna (1033) and the GLONASS biases (1230), all with a real
  CRC-24Q and the epoch time in the message. It can stop (connections refused), restart, or
  go quiet with its connections open;
- **the internet** (`Internet.cs`): a TCP relay between AgOpenWeb and the caster. `Drop`
  loses every packet both ways without telling anyone, as a mobile link does: TCP holds the
  data and delivers it at once when the link returns, and a caster drops a client that was
  silent for 60 s. `SwitchNetwork` is the move to the phone's hotspot: the open connections
  are dead for good, new ones work;
- **an emulated receiver** (`EmulatedReceiver.cs`): `$PAOGI` (dual) or `$PANDA` at 10 Hz from
  127.0.0.1 (or another loopback address) to port 9999, and it listens for corrections on
  port 2233. It re-frames what it gets with its own RTCM code (`Rtcm.cs`) and is RTK fixed
  only while it holds a whole observation epoch younger than 2.5 s and the base position; it
  holds the fix 10 s without corrections, float to 30 s, then single, and needs 5 s (from
  float) or 20 s (from single) to fix again. Its position wanders as a receiver's does in
  float, DGPS and single. It counts what a real one would mind: datagrams over 512 bytes,
  datagrams back to back, checksum failures, epochs older than one it already had, bursts;
- **a steer module stub** (`SteerModuleStub.cs`), so AutoSteer can engage.

Every 100 ms the harness records what the receiver does (fix, age of its corrections) next to
what AgOpenWeb shows (fix, age, NTRIP state and message, AutoSteer, sections, steer angle,
heading) and whether the host loop answered. Sounds and toasts are recorded as they come.

Two operators:

- **Careful** (`CarefulOperator.cs`): the right login, Dual GPS on, a pass with sections on
  Auto and AutoSteer on. Then: a normal day (the baseline); the internet down for 10 s, for
  2 minutes, coming and going for 90 s; the caster restarting; the caster quiet for 45 s; the
  move to the phone's hotspot; RTK fixed to float to DGPS under a hedge (with the RTK settings
  as they come, then with "Alarm stops AutoSteer"); one antenna of the dual receiver lost
  (an empty `$PAOGI` heading, then `$PANDA` instead); a 1 m position jump with a dual receiver
  and with a single antenna; the main caster down with a fallback service.
- **Careless** (`CarelessOperator.cs`): the password with one letter in the wrong case (Test
  button first); the mount point in lower case (then the mount point list); a typo in the
  address; the wrong port; NTRIP switched off and on five times in three seconds; a VRS
  mount point saved before the receiver is on; the receiver's corrections input on another
  box than the one sending positions.

## Running

```bash
dotnet test Tests/AgOpenWeb.GpsNtripScenarios.Tests/ --logger "console;verbosity=detailed"
```

About 25 minutes of real time (the outages are real time). Environment variables:

| Variable | Meaning |
|---|---|
| `AGOPENWEB_SCENARIO_REPORT` | where the Markdown report goes (default: the test's work folder) |
| `AGOPENWEB_SCENARIO_OPERATOR` | `careful` or `careless` (default: both) |
| `AGOPENWEB_SCENARIO_ONLY` | scenario codes, comma-separated (`C7,K1`; `C` = every careful one) |
| `AGOPENWEB_SCENARIO_DUMP` | a folder for each scenario's timeline as CSV |
| `AGOPENWEB_SCENARIO_VERBOSE` | `0` to keep the console quiet |

The test fails only when the harness itself breaks (a `HARNESS` finding, or nothing run).
The findings are the product: each kind once in the report, most serious first, with the
scenarios it was seen in, and each scenario's checks, the corrections the receiver got and a
timeline with the caster's, the internet's and the receiver's own logs.

It needs UDP ports 9999 and 2233 free on the machine (stop a running AgOpenWeb and any
module simulator first), and Linux for the second loopback address (127.0.0.2).

## Limits

- The receiver is a model, not a receiver: its timings are typical of a UM982 or an F9P,
  and it does not decode the observations.
- The tractor drives straight and does not answer the steering: the harness judges what
  AgOpenWeb tells the steer module (angle, AutoSteer on or off), not where the tractor goes.
- One AgOpenWeb, one caster (two with the fallback), on the loopback: real Wi-Fi airtime,
  broadcast and the operating system's own network events are not exercised; the network
  change is reported as Android and iOS report it (`INetworkWatcher.NotifyChanged`).
