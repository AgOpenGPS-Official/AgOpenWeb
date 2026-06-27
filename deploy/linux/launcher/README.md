# AgOpenWeb — Linux desktop launcher

The desktop twin of the Windows/macOS launcher: one window that starts the AgOpenWeb
guidance host **and** shows the guidance UI on this PC in an embedded WebView. This is the
Linux *desktop* app. For a headless in-cab appliance (mini-PC / SBC) use the systemd daemon
in `deploy/linux` instead.

It's self-contained — no .NET install needed — but the embedded WebView renders through a
**system WebKit**, so that (and a few GUI libraries) must be present. Pulling GUI runtime
deps in is normal for a desktop app.

## Run

```bash
tar xzf agopenweb-launcher-linux-x64.tar.gz && cd agopenweb-launcher-linux-x64
./run.sh           # run in place
./install.sh       # or: add an application-menu entry (per-user, no sudo)
```

A maximized window opens, starts the in-process host, and fills itself with the web UI once
the host is up. To connect a tablet/phone in the cab, browse it to `http://<this-pc-ip>:5174`
— the host binds all interfaces, so LAN clients connect alongside the on-screen UI.

`./install.sh --uninstall` removes the menu entry and the installed copy (your field data
under `~/Documents/AgOpenWeb` is left untouched).

## Requirements

Install these with your package manager (names vary by distro). On Debian/Ubuntu:

```bash
sudo apt-get install \
  libwebkit2gtk-4.1-0 libsoup-3.0-0 \   # embedded WebView (WebKitGTK backend)
  libicu-dev \                          # .NET globalization (ICU) — host won't start without it
  libfontconfig1 libgl1                 # SkiaSharp (boundary-imagery compositing)
```

- **WebView backend.** Avalonia's Linux WebView uses **WebKitGTK** (`libwebkit2gtk-4.1`) or
  **WPE** (`libwpewebkit-2.0` + `libwpe-1.0` + `libwpebackend-fdo-1.0`); both pull
  `libsoup-3.0`. WebKitGTK is the more widely packaged of the two — install it first. If the
  window opens but stays blank, the WebKit runtime is missing or the wrong flavor; install the
  other backend.
- **ICU** is a hard `.NET` dependency: without `libicu` the host won't start at all. The
  versioned package name differs by release (`libicu76`, `libicu72`, …); `libicu-dev` pulls
  the current one.
- **libfontconfig1 + libgl1** back SkiaSharp's boundary-imagery compositing. Missing them
  leaves the aerial background blank but the host still runs.

A start failure (missing WebKit, port in use, …) is written to
`/tmp/agopenweb-launcher-error.log`.

## Notes

- **Closing the window stops the host** and saves your config + field state first.
- Field data, vehicles, tools and config live under `~/Documents/AgOpenWeb`, so they survive
  replacing the program folder on update.
- The window hosts the backend **in-process**; there is no separate service. The same exe is
  the cross-platform app: `app/AgOpenWeb.Desktop --headless` runs it as a display-less daemon,
  `--launcher` forces the in-window WebView launcher. The UI is always the web app — there is
  no native UI.
