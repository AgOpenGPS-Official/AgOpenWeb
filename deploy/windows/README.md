# AgOpenWeb — Windows

App-like launcher for the **headless** AgOpenWeb guidance host. Unlike the Linux daemon
(`deploy/linux`), Windows runs as a normal program you start and stop yourself.

## Run

1. Extract the zip anywhere (e.g. `C:\AgOpenWeb`).
2. Double-click **`AgOpenWeb.Desktop.exe`**.
3. In the launcher window, click **Start**. The browser opens to the web UI automatically
   (uncheck "Open browser when started" to disable). Click **Open in Browser** any time.
4. To connect a tablet/phone in the cab, browse the other device to the **LAN URL** shown
   in the window (e.g. `http://192.168.1.50:5174`) — use the **Copy** button.

It's self-contained — no .NET install needed.

## Notes

- **Closing the window stops the host** and saves your config + field state first.
- **Start with Windows** (checkbox) launches the app on sign-in (per-user; uses the
  `HKCU\…\Run` key). Untick to remove.
- Field data, vehicles, tools and config live under your Documents folder
  (`Documents\AgOpenWeb`), so they survive replacing the program folder on update.
- The launcher hosts the backend **in-process**; there is no separate service. To run it
  display-less as a background daemon instead, start the exe with `--headless`.
- This same exe is the cross-platform app: `--windowed` opens the legacy native UI,
  `--launcher` forces the launcher on any OS.
