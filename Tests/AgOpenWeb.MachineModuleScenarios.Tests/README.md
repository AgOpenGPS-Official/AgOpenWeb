# Machine module scenarios: the careful and the careless operator

An end-to-end harness for what a machine module gets from AgOpenWeb: the section states
(PGN 239, and 229 above 16 sections), the section widths (PGN 235), the relay functions
(PGN 236) and the machine settings (PGN 238), on a normal day and on a bad one.

The whole backend runs in the test process, as the Desktop head boots it, on a temporary
`AGOPENWEB_DATA` folder (the rig of `AgOpenWeb.VehicleToolScenarios.Tests`), **with its real
UDP service** on port 9999. Settings go in exactly as the web client sends them
(`config.set|key:value`, `profile.*`, `tram.*`, `section.master`).

On the loopback, next to it:

- **an emulated machine module** (`EmulatedSprayerModule.cs`) on 127.0.0.1:8888, which
  handles PGNs as the Sprayer_ESP32 firmware (Boards repository, 0.10.1) does: only packets
  from the program (source 0x7F) with a right checksum; PGN 235/236/238 kept in its flash
  (235 written only when it changed, so a new board has 0 sections); 8 relays driven by
  the relay functions with the official firmware's numbering (1-16 sections, 17 lower,
  18 raise, 19 tram right, 20 tram left, 21 geo stop); the valves shut 2 s after the last
  PGN 239; hellos answered with PGN 123. It can be powered off and on, restarted (flash
  kept), and its Wi-Fi dropped;
- **a steer module stub** (`SteerModuleStub.cs`) that sends PGN 253 ten times a second
  when the scenario's tractor has one, because AgOpenWeb sends its settings again when a
  steer module appears;
- **a tractor** (`MachineSession.cs`) that sends `$PAOGI` fixes at 10 Hz in real time
  (the module's timeouts are real time), driving north at 8 km/h on fresh ground.

Every 100 ms the harness compares the sections on AgOpenWeb's screen with the valves the
module really opened, notes the module status AgOpenWeb shows, and counts the coverage
AgOpenWeb paints while the valves are shut. After each change it compares the module's flash
with AgOpenWeb's settings (section count, widths, relay functions, lift times).

Two operators:

- **Careful** (`CarefulOperator.cs`): modules on first, the 8 m boom typed in (8 × 100 cm,
  relays 7-8 set to sections 7-8), saved, sprayed; widths changed and saved; a second tool
  profile and back; AgOpenWeb restarted with the modules left on.
- **Careless** (`CarelessOperator.cs`): a new sprayer module switched on after AgOpenWeb
  (then Save, a restart, a real width change); a module on before AgOpenWeb on a tractor
  without a steer module; settings changed while the module is unplugged; Wi-Fi drops of
  1, 5 and 30 s; relay functions left at AgOpenWeb's defaults on an 8-section boom; tram
  light relays chosen by AgOpenWeb's names; GPS lost for 6 s; 19 sections.

## Running

```bash
dotnet test Tests/AgOpenWeb.MachineModuleScenarios.Tests/ --logger "console;verbosity=detailed"
```

About 5 minutes (careful about 1, careless about 4). Environment variables:

| Variable | Meaning |
|---|---|
| `AGOPENWEB_SCENARIO_REPORT` | where the Markdown report goes (default: the test's work folder) |
| `AGOPENWEB_SCENARIO_OPERATOR` | `careful` or `careless` (default: both) |
| `AGOPENWEB_SCENARIO_ONLY` | scenario codes, comma-separated (`K1,K4`; `C` = the careful day) |
| `AGOPENWEB_SCENARIO_VERBOSE` | `0` to keep the console quiet |

The test fails only when the harness itself breaks (a `HARNESS` finding, or nothing run).
The findings are the product: each kind once in the report, most serious first, with the
scenarios it was seen in, and each scenario's checks, the PGNs the module received and a
timeline with the module's own log.

It needs UDP ports 9999, 2211, 2222 and 8888 free on the machine (stop a running AgOpenWeb
and any module simulator first).

## Limits

- The module is the sprayer firmware's PGN handling, not the firmware: no rate control, no
  PGN 160/161 (sprayer readout), no web page.
- One machine module, on the loopback: the subnet lock, scans (202) and subnet changes
  (201) are not exercised.
- The tractor drives straight on fresh ground: section switching at boundaries and on
  coverage belongs to the field harness.
