# AgOpenWeb with a sprayer readout

This is a fork of [AgOpenGPS-Official/AgOpenWeb](https://github.com/AgOpenGPS-Official/AgOpenWeb) that adds a **sprayer readout**: boom pressure, flow and alarms from the sprayer's machine module, shown in a strip above the section buttons. Everything else is the official AgOpenWeb, brought up to date every day.

The official README follows [below](#agopenweb).

## Sprayer readout

- Shows the pressure (bar), the flow (L/min), the actual rate (L/ha), and one small square per section in the section buttons' colours.
- Raises an alarm, with a sound, for low or high pressure, for a flow that doesn't match the open nozzles, and for flow with every section closed.
- Warns when the module doesn't open or close a section that AgOpenWeb asked for.
- With a target rate set, shows the pressure that gives that rate at the current speed. It only advises: nothing is regulated.
- Settings are in **Tool → Machine → Sprayer**: nozzle spacing, nozzle flow at a reference pressure, minimum and maximum pressure, flow tolerance and target rate.
- The strip only appears while a sprayer module is sending data. Without one, AgOpenWeb works exactly like the official version.

The module sends PGN 160 to port 9999 once a second (see [Docs/PGN.md](Docs/PGN.md#sprayer-readout)). Firmware for an Arduino Nano with a relay board, a pressure sensor and a flow meter is in [renatorgr/Boards](https://github.com/renatorgr/Boards/tree/main/Sprayer/Sprayer_Nano).

The readout was proposed to the official project in [AgOpenGPS-Official/AgOpenWeb#390](https://github.com/AgOpenGPS-Official/AgOpenWeb/issues/390). The maintainers would rather handle sprayer data the ISOBUS way, so it lives in this fork for now.

## Download

Get the [Nightly build of this fork](https://github.com/renatorgr/AgOpenWeb/releases/tag/nightly). It is rebuilt from `pulverizador` every day and has the same files as the official Nightly (see "Download & run" below). Links below that point to AgOpenGPS-Official give you the official build, without the sprayer readout.

## Branches

| Branch | What it holds |
|---|---|
| `pulverizador` (default) | The official `develop` plus the sprayer readout. All work on this fork goes here. |
| `develop` | An exact copy of the official `develop`. Don't commit to it. |

Once a day, a GitHub Action ([`sync-pulverizador.yml`](.github/workflows/sync-pulverizador.yml)) brings `develop` up to date with the official repository. If there is anything new, it opens a pull request, "Update pulverizador with the official AgOpenWeb". Merge it to bring the official changes into `pulverizador`.

---

# AgOpenWeb

[![CI](https://github.com/AgOpenGPS-Official/AgOpenWeb/actions/workflows/build-and-release.yml/badge.svg?branch=develop)](https://github.com/AgOpenGPS-Official/AgOpenWeb/actions/workflows/build-and-release.yml)
[![Translation status](https://hosted.weblate.org/widget/agopengps/agopenweb/svg-badge.svg)](https://hosted.weblate.org/projects/agopengps/agopenweb/)

[![Nightly build](https://img.shields.io/github/release-date-pre/AgOpenGPS-Official/AgOpenWeb?label=nightly%20build&style=for-the-badge&color=brightgreen)](https://github.com/AgOpenGPS-Official/AgOpenWeb/releases/tag/nightly)

> **⬇️ Download the [Nightly build](https://github.com/AgOpenGPS-Official/AgOpenWeb/releases/tag/nightly). It is rebuilt from `develop` every day and has all the latest fixes. Please install it, and check the problem still happens on it, before you open an issue.**

Cross-platform agricultural GPS guidance — a rewrite of [AgOpenGPS](https://github.com/farmerbriantee/AgOpenGPS) using HTML/JS, .NET 10, and C#. The UI is a web page served up by the backend host and shown in each platform's own web view.

## Download & run

Get the [**Nightly**](../../releases/tag/nightly) build. It is rebuilt from `develop` daily and
on demand, and it is the build to use and report issues against. Everything is a self-contained app (no .NET install needed). Pick the set that matches how you'll run it — in
every mode the host also serves `http://<host>:5174` on the LAN, so cab phones/tablets connect
in a browser.

**🖥️ Desktop** — a window with the guidance UI (host runs in-process):

| OS | Download | Run |
|---|---|---|
| Windows x64 | `agopenweb-win-x64.zip` | run `AgOpenWeb.Desktop.exe` |
| macOS (Apple silicon) | `agopenweb-macos-arm64.dmg` | drag to Applications, launch |
| Linux x64 / arm64 | `agopenweb-launcher-linux-<arch>.tar.gz` | `./run.sh` |

**🛰️ Headless** (Linux + Windows) — no window; UI in a browser at `http://<host>:5174`, auto-starts
on boot (for an in-cab mini-PC / SBC):

| OS | Download | Install |
|---|---|---|
| Linux x64 / arm64 | `agopenweb-linux-<arch>.tar.gz` | `sudo ./install.sh --from app` (systemd) |
| Windows x64 | `agopenweb-win-x64.zip` | `install-service.cmd` → Run as administrator (Windows Service) |

**📱 Mobile** — all-in-one (host + UI on the device):

| Device | Download | Install |
|---|---|---|
| Android | `agopenweb-android.apk` | sideload |

Each download's bundled `README` lists the prerequisites (e.g. the WebView2 Runtime on Windows,
WebKitGTK on Linux).

## Platforms

- Windows x64
- macOS x64 / ARM64
- Linux x64 / ARM64
- Android
- iOS (sideload)

## Tech stack

- **UI:** HTML/JS web client with CanvasKit (Skia WebAssembly); shown in WebView2 / WKWebView / WebKitGTK on desktop (Photino.NET), WKWebView on iOS, WebView on Android
- **Control brain:** `MainViewModel` with CommunityToolkit.Mvvm, driven by the web client
- **Runtime:** .NET 10
- **Architecture:** shared backend on every platform; the platform projects are thin web-view shells
- **Testing:** NUnit, NSubstitute

## Building from source

Prerequisites: .NET 10 SDK

```bash
# Desktop (Windows/macOS/Linux)
dotnet build Platforms/AgOpenWeb.Desktop
dotnet run --project Platforms/AgOpenWeb.Desktop

# Android
dotnet build Platforms/AgOpenWeb.Android

# Run tests
dotnet test
```

See [BUILD.md](BUILD.md) for platform-specific setup, and `deploy/{linux,windows,macos}/` for the
packaging scripts that produce the release bundles above.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for the architecture overview, feature list, and how to get
started.

To help translate the UI, no code is needed: join in on [Weblate](https://hosted.weblate.org/projects/agopengps/agopenweb/). See [Docs/TRANSLATIONS.md](Docs/TRANSLATIONS.md).

## License

[Apache License 2.0](LICENSE.md)
