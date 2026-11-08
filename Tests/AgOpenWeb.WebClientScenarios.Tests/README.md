# Web client scenarios: the screens, a second screen, and losing the screen

An end-to-end harness for the web client as farms use it (checklists 12.4, 12.5, 12.8 and
13.11): a 10" and a 7" tablet, a 720p cab PC, a 5" cab screen, a phone in both orientations, a
tablet upright; a phone or an office PC watching beside the cab tablet; and the cab tablet
going away while the tractor works.

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

**S, the screens** (`ScreenScenarios.cs`). The only screen, holding control, a job running:
S1 10" tablet 1280×800, S2 720p cab PC 1280×720, S3 7" tablet 1024×600, S4 5" cab screen 800×480,
S5 phone landscape 844×390, S6 phone portrait 390×844, S7 10" tablet portrait 800×1280, S8 the
5" screen in Portuguese. On each: the page doesn't scroll; every button of the status bar, left
nav, right nav, bottom nav, section bar and camera pad can be tapped (after scrolling its bar if
the bar scrolls); buttons under 40 px are listed; the role, speed, heading, fix, field line and
cross-track are on the screen and not cut; AutoSteer and the section master work by touch; eleven
menus open by their own buttons (File, Screen & Alerts, Tools, Vehicle & Tool, Vehicle settings,
Tool settings, AutoSteer and its full view, Field Operations, Field Tools, Network) and every
control in them can be reached; a phone keeps control when turned around; no script errors.

**O, a second screen** (`ObserverScenarios.cs`). O1 the phone joins as observer and sees what the
operator does; O2 from the phone, every button of its bars tapped and every command id in the
client's scripts sent (plus the most harmful ones with real arguments), then the host's switches,
track, tool, vehicle and files compared; O3 a setting edited on the phone: is it shown as saved?;
O4 the phone asks for control while the operator is live; O5 the operator's tab closes: the
machine stops, the phone takes over (is it told?), the tablet comes back; O6 three screens, the
operator leaves: exactly one takes over.

**C, the operator's screen goes** (`ControlLostScenarios.cs`), working with AutoSteer and sections
on: C1 the tab closes; C2 Wi-Fi out of range 10 s; C3 the same with taps on the frozen screen
(must not run late); C4 the tablet sleeps 8 s; C5 a 0.7 s hiccup (must not stop the work: with
presence every 0.5 s and the deadman at 1.5 s, a longer one may or may not reach it); C6 a
2.5 s hiccup; C7 the access point drops the tablet; C8 the page reloads; C9 Free Drive on and
steered 10° right, then the tab closes; C10 a section switched on by hand, then the tab closes;
C11 the cab tablet's link goes silent with a phone watching; C12 the cab tablet's link silent
for a minute with sections painting and a phone watching, so the host's sends to the dead link
fill its buffers and wait (the deadman runs in the same loop as the sends). Each measures how
long the module kept getting AutoSteer on and sections open, what the screen showed meanwhile,
and what the screen shows when it comes back. C9 and C10 also check that PGN 254 still reaches
the module port at the end, so a module that stopped hearing it is not read as still steering.

Finding codes of the link scenarios:

| Code | Meaning |
|---|---|
| `STEERING-NOT-STOPPED`, `SECTIONS-NOT-CLOSED` | the module still gets AutoSteer on, or sections open |
| `STEERING-STOPS-LATE`, `SECTIONS-CLOSE-LATE` | they stop, but after the deadman (2 s with the sweep and a margin) |
| `SILENT-LINK-LOOKS-LIVE` | the tablet whose link is gone still shows itself as the operator |
| `HICCUP-DROPS-STEERING` | a hiccup shorter than the deadman, less one presence period, stopped the work |
| `DEAD-LINK-STALLS-BROADCAST` | (C12) while one screen's link was dead another screen got no frame for over 2 s: the host's sends to the dead link held up everyone's |
| `TWO-SCREENS-SAY-OPERATOR`, `TWO-OPERATORS` | two screens show, or hold, control |

## Running

The browser is Playwright's Chromium:

```bash
dotnet build Tests/AgOpenWeb.WebClientScenarios.Tests/
pwsh Tests/AgOpenWeb.WebClientScenarios.Tests/bin/Debug/net10.0/playwright.ps1 install --with-deps chromium
dotnet test Tests/AgOpenWeb.WebClientScenarios.Tests/ --logger "console;verbosity=detailed"
```

About 6 minutes. Environment variables:

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
- Sizes, not devices: a phone is Chromium's mobile emulation (viewport, touch, device scale),
  not a phone's browser with its own toolbars and keyboard.
- A background tab's timer throttling (1 Hz, then once a minute after five minutes in Chrome) is
  not emulated; a sleeping tablet is a page whose script is paused.
- Taps land on the centre of a control; the layout check counts a control as reachable when one
  of five points of it can be touched, so a control mostly covered still passes there.
- The relay's buffers are small, but the host's own socket to it keeps the size the operating
  system gives a loopback socket (up to megabytes on Linux), so a dead link takes longer to hold
  up the host's sends than over a real radio; C12 runs a minute for that reason.
- The host's UDP socket listens on every interface (port 9999): a real module on the LAN that
  talks to it would make the host send to that module's subnet. Run it away from a bench board.
