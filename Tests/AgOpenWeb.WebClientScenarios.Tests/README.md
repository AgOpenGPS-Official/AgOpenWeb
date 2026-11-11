# Web client scenarios: the screens, a second screen, losing the screen, and the operator's flows

An end-to-end harness for the web client as farms use it (checklists 12.4, 12.5, 12.8 and
13.11) on AgOpenWeb's screen target, a 10" tablet in landscape (the Panasonic FZ-G1: 1280×800
CSS px; other screens are out of scope, `Docs/WEB_CLIENT.md`): the screen itself, in English and
Portuguese; a second 10" tablet watching beside the cab tablet; and the cab tablet going away
while the tractor works. Then the operator's own flows on the cab tablet, done by
taps and typing only (Chris's harness plan, item 3.5): tracks made every way Quick AB offers but
the water pivot, renamed, hidden and deleted; the boundary and the headland; tram lines; fields and jobs opened, closed and resumed;
the section bar; the U-turn and lateral buttons.

The whole backend runs in the test process, as the Desktop head boots it, on a temporary
`AGOPENWEB_DATA` folder, **with its real UDP service** on port 9999 and the embedded web server
on a free port. The screens are **real pages in headless Chromium** (Playwright), each its own
browser context (its own device), talking to that server over real sockets, so the control
authority (`ControlAuthority`, #397), the observer gate (`RemoteServerWiring.ObserverIds`) and
the failsafe when control ends (`MainViewModel.OnBrowserControlEnded`) are the shipped ones.
Taps are real touch taps (or clicks on a mouse screen) at the centre of the control: a button
under another element, or off the screen, is not reached. The layout check tries five points of
each button (the centre and four around it) and looks through what only passes over the screen
for a moment (the toast, the map-tap hint); a covered button is reported with what covers it.

The tractor is AgOpenWeb's own GPS simulator, at 8 km/h on an AB line in a made-up 200 × 600 m
field near 41.5 N 5.5 W, with an 8 m boom in eight 1 m sections. What the machine is told is
read where a module reads it: **PGN 254 on 127.0.0.1:8888** (`ModuleListener.cs`), where
AgOpenWeb's UDP service always sends (the status byte the steer module steers on, the steer
angle, the speed, sections 1-16). The rig gives the UDP service no network interfaces, so its
PGNs, module scans and subnet commands go to the loopback only, never to the LAN's broadcast.
On the simulator AgOpenWeb tells the steer module "don't steer" unless the bench choice is on (no
button for it yet), so the rig sets it at the start (`IsSimulatorDrivingSteering`, and fails
if it was refused): the status byte then says what it says on a receiver's fix.

Each screen that loses its link does so through **its own Wi-Fi** (`LinkRelay.cs`), a TCP relay
between that browser and the server: *silent* (out of range: nothing passes and nobody is told,
as TCP keeps waiting; the relay stops reading both sides and its sockets' buffers are 32 KB, as a
radio link's, so a host that keeps sending soon has its sends wait; the waiting bytes arrive when
it is back), or *cut* (the access point drops the tablet: the browser's connections are reset and
new ones refused, while the host's side of each stays open and unread, so the host learns of it
only by the deadman, as from a tablet that left the network). A tablet that goes to sleep is a page whose script is paused through
the DevTools protocol (`Debugger.pause`: no timers, no messages handled, the socket left open),
as Android and iOS freeze a page that isn't shown (`Page.setWebLifecycleState` freezes only a
hidden page, and a headless page is never hidden). Each scenario starts on ground not yet worked,
so a section in Auto is open until something closes it.

## Scenarios

**S, the screen** (`ScreenScenarios.cs`). The only screen, holding control, a job running: S1 the
10" tablet 1280×800, S2 the same in Portuguese (the longest labels). On each: the page doesn't scroll; every button of the status bar, left
nav, right nav, bottom nav, section bar and camera pad can be tapped (after scrolling its bar if
the bar scrolls); buttons under 40 px are listed; the role, speed, heading, fix, field line and
cross-track are on the screen and not cut; AutoSteer and the section master work by touch; eleven
menus open by their own buttons (File, Screen & Alerts, Tools, Vehicle & Tool, Vehicle settings,
Tool settings, AutoSteer and its full view, Field Operations, Field Tools, Network) and every
control in them can be reached; no script errors.

**O, a second screen** (`ObserverScenarios.cs`). O1 a second tablet joins as observer and sees what the
operator does; O2 from the second tablet, every button of its bars tapped and every command id in the
client's scripts sent (plus the most harmful ones with real arguments), then the host's switches,
track, tool, vehicle and files compared; O3 a setting edited on the second tablet: is it shown as saved?;
O4 the second tablet asks for control while the operator is live; O5 the operator's tab closes: the
machine stops, the second tablet takes over (is it told?), the tablet comes back; O6 three screens, the
operator leaves: exactly one takes over.

**C, the operator's screen goes** (`ControlLostScenarios.cs`), working with AutoSteer and sections
on: C1 the tab closes; C2 Wi-Fi out of range 10 s; C3 the same with taps on the frozen screen
(must not run late); C4 the tablet sleeps 8 s; C5 a 0.7 s hiccup (must not stop the work: with
presence every 0.5 s and the deadman at 1.5 s, a longer one may or may not reach it); C6 a
2.5 s hiccup; C7 the access point drops the tablet; C8 the page reloads; C9 Free Drive on and
steered 10° right, then the tab closes; C10 a section switched on by hand, then the tab closes;
C11 the cab tablet's link goes silent with a second tablet watching; C12 the cab tablet's link silent
for a minute with sections painting and a second tablet watching, so the host's sends to the dead link
fill its buffers and wait (the deadman runs in the same loop as the sends). Each measures how
long the module kept getting AutoSteer on and sections open, what the screen showed meanwhile,
and what the screen shows when it comes back. C9 and C10 also check that PGN 254 still reaches
the module port at the end, so a module that stopped hearing it is not read as still steering.

**The flows** (`TrackFlows.cs`, `BoundaryFlows.cs`, `TramFlows.cs`, `FieldFlows.cs`,
`SectionBarFlows.cs`, `UTurnFlows.cs`, on `FlowKit.cs`). Each group works in its own field, 1 km
east of the last one (`FlowFields`): a 240 m wide field with a slanted north edge (420 m on the
west side, 380 m on the east). T, R, F, X and U make it the farmer's way, from a KML file copied
into the Import folder (Field Operations › Fields and Jobs › From KML), and open it again from
Fields and Jobs when the group needs it; B starts with New Field at the tractor and gets that
outline in B4 (Boundary › Import KML). Everything the operator does is a tap on a control's
centre, a pick in a drop-down, a drag of the map or typing on the cab tablet (10" 1280×800). A
control off the screen or covered is a finding; one missing from the page is a `HARNESS` line.
The harness places the simulated tractor, sets its speed and steering, and reads; between steps
it closes menus and dialogs through the page's own functions, and before placing the tractor and
at a scenario's end it switches AutoSteer, the sections and U-turn off on the host. Each step is read back where the operator reads it (the dialog, the list,
the Scene and Tick the screen draws from, the toast or hint it showed) and where it acts (the
host's tracks, boundary, headland, tram systems, jobs and files, and PGN 254 at the module port).
A careful operator does each flow as meant; a careless one types a comma for a decimal point,
leaves a box empty, types a value out of range, taps a button in the middle of something, closes
the field while working. Each finding says what kind it is: an **app defect**, a **harness**
fault, a **parity** difference from AgOpenGPS, or an **enhancement** (the app does no harm but
could say or do better).

**T, tracks**: T1 A+ while driving at 8 km/h; T2 A+ with a typed heading; T3 Drive AB (heading
359.97°, so the name of a line just under 360° is fixed); T4 a curve recorded by driving with a
60° bend; T5 Lat/Lon + Heading and Lat/Lon A, B; T6 coordinates typed carelessly (a comma, an
empty heading, B = A, latitude and longitude swapped); T7 Longest Edge, All Edges, Bnd. Curve;
T8 a line and a curve drawn on the map, and a tap on a button in the middle of drawing; T9
rename (Enter, a tap away, a comma and accents, an empty name, another track's name); T10 hide
and show, and hiding the line AutoSteer steers on at 8 km/h; T11 swap A/B, delete (Cancel,
Confirm), deleting the line AutoSteer steers on; T12 the tracks after the field is closed and
opened. Each line is checked where it should be: on the pass the tractor drove (half the boom to
its right), through the taps, on the boundary's edge, at the typed point and heading.

**B, boundary and headland**: B1 a New Field at the tractor, and an obstacle asked for before the
boundary (#621); B2 the boundary recorded by driving a 60 × 40 m loop; B3 deleted, then drawn on
the map with five taps; B4 imported from a KML; B5 the headland round the whole boundary (2 tool
widths), then 24 m; B6 a headland line on one side by two taps on the fence; B7 the Headland and
Section-control-in-headland buttons; B8 a headland of 0 m and of 150 m in a 240 m field; B9
boundary and headland after the field is closed and opened.

**R, tram lines**: R1 a tram system on the active line (its width from the tool, #675); R2 24 m,
3 passes, Right, a 2 m offset, Edge, each measured on the lines the screen draws, square to the
line; R3 a width of 0 and 150 m, 1500 and -3 passes; R4 the boundary as the reference, and 0
passes on it (#112); R5 Enabled off and on, the bottom bar's Tram button; R6 the line it follows
renamed; R7 after the field is closed and opened; R8 deleted (Cancel, Confirm), its file removed.

**F, fields and jobs**: F1 a job started (task name, work type) and 100 m worked; F2 closed with
the job: no question, coverage on disk, the job listed; F3 Resume Last Job; F4 Resume Job from
the list; F5 a field worked without a job and closed: Unsaved Coverage Cancel, Save, Discard; F6
the field closed at 10 km/h with AutoSteer and the sections on; F7 another field opened in the
middle of a job at 8 km/h; F8 field names with a slash, accents, a name that exists, no name; F9
Drive In; F10 deleting the open field and job (refused), then a job (Cancel, Confirm) and a
field; F11 another field opened after working without a job (the question before the coverage
is dropped).

**X, the section bar**: X1 one section's button round Off, Auto, On, Off at 8 km/h, read at the
module; X2 the master Auto on fresh ground and back over the strip it covered; X3 Manual over
covered ground, and off; X4 a section On by hand, then the master off.

**U, the U-turn and lateral buttons** (a 16 m headland made in Field Builder): U1 the automatic
U-turn from the right-hand bar, taken at the north headland at 10 km/h; U2 the on-screen manual
U-turn (hidden without AutoSteer), with the lateral and U-turn buttons tapped again during the
turn; U3 Lateral right and left; U4 skip rows 1 and a manual U-turn (two passes over). Where the
tractor lands is read on the host: one pass over (two with skip rows 1), measured square to the
line (Quick AB takes the tractor's heading, so a line can be a hair off north), heading back, and its
cross-track error.

Finding codes of the link scenarios:

| Code | Meaning |
|---|---|
| `STEERING-NOT-STOPPED`, `SECTIONS-NOT-CLOSED` | the module still gets AutoSteer on, or sections open |
| `STEERING-STOPS-LATE`, `SECTIONS-CLOSE-LATE` | they stop, but after the deadman (2 s with the sweep and a margin) |
| `SILENT-LINK-LOOKS-LIVE` | the tablet whose link is gone still shows itself as the operator |
| `HICCUP-DROPS-STEERING` | a hiccup shorter than the deadman, less one presence period, stopped the work |
| `DEAD-LINK-STALLS-BROADCAST` | (C12) while one screen's link was dead another screen got no frame for over 2 s: the host's sends to the dead link held up everyone's |
| `TWO-SCREENS-SAY-OPERATOR`, `TWO-OPERATORS` | two screens show, or hold, control |

Finding codes the flows share (each group adds its own, named for what was wrong):

| Code | Meaning |
|---|---|
| `FLOW-CONTROL-UNREACHABLE` | a control the flow needs is off the screen or under another element |
| `FLOW-MENU-DID-NOT-OPEN`, `FLOW-NO-CONFIRM` | a button didn't open its menu or dialog, or a delete didn't ask |
| `FLOW-MAP-POINT-COVERED` | a point the operator must tap on the map stays under a panel or a bar, after zooming out and dragging the map |
| `FLOW-FIELD-NOT-OPENED`, `FLOW-FIELD-NOT-LISTED`, `FLOW-IMPORT-FILE-NOT-LISTED` | the group's field could not be made or opened the operator's way |
| `…-LIST-STALE`, `…-NOT-SHOWN`, `…-STALE` | the host has it, the screen doesn't show it (or shows the old) |
| `…-SILENT` | the app did something other than what was typed, or nothing, and said nothing |

## Running

The browser is Playwright's Chromium:

```bash
dotnet build Tests/AgOpenWeb.WebClientScenarios.Tests/
pwsh Tests/AgOpenWeb.WebClientScenarios.Tests/bin/Debug/net10.0/playwright.ps1 install --with-deps chromium
dotnet test Tests/AgOpenWeb.WebClientScenarios.Tests/ --logger "console;verbosity=detailed"
```

About 25 minutes (the screens, the second screen and the link scenarios about 7, the flows about 17). Environment variables:

| Variable | Meaning |
|---|---|
| `AGOPENWEB_SCENARIO_REPORT` | where the Markdown report goes (default: the test's work folder); the screenshots go to `web-client-shots/` beside it |
| `AGOPENWEB_SCENARIO_ONLY` | scenario codes, comma-separated (`S4,C9`), or a group letter (`C`) |
| `AGOPENWEB_SCENARIO_VERBOSE` | `1` prints every step |
| `AGOPENWEB_CHROMIUM` | a Chromium or Chrome executable to use instead of Playwright's |

The test fails only when the harness itself breaks (a `HARNESS` finding). The findings are the
product: each code once in the table, most serious first, with the scenarios it came up in, then
each scenario's checks, screenshots and steps. Scenario and finding codes are fixed, and
`KnownFindings.txt` lists the codes already reported, so each night's table says which are new.

It needs UDP ports 9999 and 8888 free (stop a running AgOpenWeb, a module simulator and the
other scenario harnesses first: they share the ports).

## Limits

- Chromium only: Safari on an iPad and the Android and iOS web views are not covered, nor the
  heads' own shells (`MainActivity`, `LauncherViewController`, Photino).
- Sizes, not devices: the tablet is a Chromium viewport with touch, not a tablet's browser with
  its own toolbars and keyboard.
- A background tab's timer throttling (1 Hz, then once a minute after five minutes in Chrome) is
  not emulated; a sleeping tablet is a page whose script is paused.
- Taps land on the centre of a control; the layout check counts a control as reachable when one
  of five points of it can be touched, so a control mostly covered still passes there.
- The relay's buffers are small, but the host's own socket to it keeps the size the operating
  system gives a loopback socket (up to megabytes on Linux), so a dead link takes longer to hold
  up the host's sends than over a real radio; C12 runs a minute for that reason.
- The host's UDP socket listens on every interface (port 9999): a real module on the LAN that
  talks to it would make the host send to that module's subnet. Run it away from a bench board.
- The flows run on the 10" tablet only, in English; the other screens are covered by S.
- Not in the flows yet: flags, hotkeys and recorded paths (named in plan section 2), the water
  pivot, contour and AgShare.
- The tractor is put where a flow starts by moving the simulator. A move keeps the heading
  fusion's IMU offset, so after a turn the heading standing still can be a degree or so off; the
  harness then drives it 1.5 m straight (as the app tells an operator to) and puts it back.
- The simulator drives in real time, so a flow drives short distances (a 60 × 40 m boundary,
  a few hundred metres to a headland); a whole job's worth of passes is the field job harness's.
- Typing is at a quick tablet pace (150 ms between keys). At machine speed (15 ms) the stream of
  key events kept the page's presence timer from running for over 1.5 s in the tram field, with
  no long task; the deadman took control away and the name being typed was dropped. A rename
  that doesn't take records what the page sent, the control changes and the presence gaps.
