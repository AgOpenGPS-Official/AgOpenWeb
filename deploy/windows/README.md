# AgOpenWeb — Windows

All-in-one launcher for the AgOpenWeb guidance host. One double-click starts the host **and**
shows the guidance UI on this PC — no separate browser, no installer, no .NET install needed.

## Run

1. Extract the zip anywhere (e.g. `C:\AgOpenWeb`).
2. Double-click **`AgOpenWeb.Desktop.exe`**. A maximized window opens, starts the in-process
   host, and fills itself with the web UI once the host is up.
3. To connect a tablet/phone in the cab, browse the other device to
   `http://<this-pc-ip>:5174` — the host binds all interfaces, so LAN clients connect alongside
   the on-screen UI.

It's self-contained — no .NET install needed.

## Requirements

- **WebView2 Evergreen Runtime** — the embedded UI renders through WebView2. It's pre-installed
  on Windows 11 and current Windows 10; if the window stays blank, install the Evergreen Runtime
  from Microsoft (<https://developer.microsoft.com/microsoft-edge/webview2/>). The bundle already
  ships the native `WebView2Loader.dll`; only the Runtime itself must be present on the PC.

## Run as a background service (headless)

For a cab PC that should run the host on boot with **no window** — the UI served to
browsers/tablets at `http://<host>:5174` — install it as a Windows Service instead (the
counterpart to the Linux systemd daemon).

Easiest: **right-click `install-service.cmd` → Run as administrator**. It self-elevates (UAC)
and installs + starts the service. To remove or check it, run from a console:

```bat
install-service.cmd -Action uninstall
install-service.cmd -Action status
```

(The `.cmd` just wraps `install-service.ps1`, handling the UAC elevation and PowerShell's
script-execution policy for you. You can call the `.ps1` directly from an elevated PowerShell
if you prefer — `.\install-service.ps1`, with `-ExecutionPolicy Bypass` if scripts are blocked.)

The service runs the same exe with `--headless` under the Service Control Manager. It
auto-restarts on crash and (unless `-NoFirewall`) opens inbound TCP 5174 for LAN clients.
It runs as LocalSystem, so its data lives under
`C:\Windows\System32\config\systemprofile\Documents\AgOpenWeb`. This is the server/appliance
mode; for a normal desktop, just double-click the exe (above) instead.

## Notes

- **Closing the window stops the host** and saves your config + field state first.
- Field data, vehicles, tools and config live under your Documents folder
  (`Documents\AgOpenWeb`), so they survive replacing the program folder on update.
- The window hosts the backend **in-process**; there is no separate service. To run it
  display-less as a background daemon instead, start the exe with `--headless`.
- This same exe is the cross-platform app. Other launch modes:
  - `--console` — the legacy supervisor control panel (Start / Stop / Open in Browser / LAN URL).
  - `--windowed` — the legacy native Avalonia UI.
  - `--launcher` — force the all-in-one WebView launcher on any OS.
  - `--headless` — force the display-less daemon.
- A start failure (e.g. missing WebView2 Runtime, port in use) is written to
  `%TEMP%\agopenweb-launcher-error.log`.
