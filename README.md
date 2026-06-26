# AgOpenWeb

Cross-platform agricultural GPS guidance — a rewrite of [AgOpenGPS](https://github.com/farmerbriantee/AgOpenGPS) using Avalonia, .NET 10, and C#.

## Download & run

Grab the latest build from the [**Releases**](../../releases) page. Every download is **one
self-contained app** (no .NET install needed) that runs **two ways**:

- **Launcher** — open it and a window shows the guidance UI; the host runs inside it.
- **Headless** — no window; the host runs in the background and you use the UI from any browser
  or cab tablet at `http://<host>:5174` (ideal for an in-cab mini-PC / SBC).

| Platform | Download | Launcher | Headless on boot |
|---|---|---|---|
| Windows x64 | `agopenweb-win-x64.zip` | run `AgOpenWeb.Desktop.exe` | `install-service.cmd` → Windows Service |
| macOS (Apple silicon) | `agopenweb-macos-arm64.dmg` | drag to Applications, launch | `--headless` (manual) |
| Linux x64 / arm64 | `agopenweb-launcher-linux-<arch>.tar.gz` | `./run.sh` | — |
| Linux x64 / arm64 (appliance) | `agopenweb-linux-<arch>.tar.gz` | — | `sudo ./install.sh --from app` (systemd) |
| Android | `agopenweb-android.apk` | sideload (host + UI on the tablet) | — |

In **every** mode the host serves `http://<host>:5174` on the LAN, so phones/tablets in the cab
connect in a browser. Each download's bundled `README` lists the prerequisites (e.g. the WebView2
Runtime on Windows, WebKitGTK on Linux).

## Platforms

- Windows x64
- macOS x64 / ARM64
- Linux x64 / ARM64
- Android
- iOS (sideload)

## Tech stack

- **UI:** Avalonia 12
- **MVVM:** CommunityToolkit.Mvvm
- **Runtime:** .NET 10
- **Architecture:** MVVM with dependency injection; ~92% shared cross-platform code
- **Testing:** NUnit, Avalonia.Headless

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

## License

[GNU General Public License v3.0](LICENSE.md)
