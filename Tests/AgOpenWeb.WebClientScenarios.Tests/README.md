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
under another element, or off the screen, is not reached.

The tractor is AgOpenWeb's own GPS simulator, at 8 km/h on an AB line in a made-up 200 × 600 m
field near 41.5 N 5.5 W, with an 8 m boom in eight 1 m sections. What the machine is told is
read where a module reads it: **PGN 254 on 127.0.0.1:8888** (`ModuleListener.cs`), where
AgOpenWeb's UDP service always sends as well (the status byte the steer module steers on, the
steer angle, the speed, sections 1-16).

Each screen that loses its link does so through **its own Wi-Fi** (`LinkRelay.cs`), a TCP relay
between that browser and the server: *silent* (out of range: nothing passes and nobody is told,
as TCP keeps waiting; the waiting bytes arrive when it is back), or *cut* (every connection
reset, new ones refused). A tablet that goes to sleep is a page whose script is paused through
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
(must not run late); C4 the tablet sleeps 8 s; C5 a 1 s hiccup (must not stop the work); C6 a
2.5 s hiccup; C7 the access point drops the tablet; C8 the page reloads; C9 Free Drive on and
steered 10° right, then the tab closes; C10 a section switched on by hand, then the tab closes;
C11 the cab tablet's link goes silent with a phone watching. Each measures how long the module
kept getting AutoSteer on and sections open, what the screen showed meanwhile, and what the
screen shows when it comes back.

## Running

The browser is Playwright's Chromium:

```bash
dotnet build Tests/AgOpenWeb.WebClientScenarios.Tests/
pwsh Tests/AgOpenWeb.WebClientScenarios.Tests/bin/Debug/net10.0/playwright.ps1 install --with-deps chromium
dotnet test Tests/AgOpenWeb.WebClientScenarios.Tests/ --logger "console;verbosity=detailed"
```

About 5 minutes. Environment variables:

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
- Taps check the centre of a control; a control half covered is counted as reachable.
