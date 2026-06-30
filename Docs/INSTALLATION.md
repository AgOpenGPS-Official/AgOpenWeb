# Installing AgOpenWeb

This guide walks you through installing and running AgOpenWeb on **Windows**, **Linux**, and
**mobile** devices.

AgOpenWeb runs as a small background **host** program that does all the guidance work, and shows
its screen as a **web page**. Whatever device you install it on, the host also publishes that same
screen on your local network at:

```
http://<host-ip>:5174
```

So a mini-PC in the cab can run the host while a phone or tablet just opens a browser to its
address — no extra install on the tablet. Every download is **self-contained**: you do *not* need
to install .NET or any other framework first.

> **Where to download:** All files come from the project's
> [**Releases**](https://github.com/AgOpenGPS-Official/AgOpenWeb/releases) page. Each download
> includes a small `README` listing any prerequisites for that platform.

Pick the platform below.

- [Windows](#windows)
- [Linux](#linux)
- [Mobile (Android / iOS)](#mobile)
- [Connecting from a phone or tablet](#connecting-a-tablet-or-phone-over-wi-fi)
- [Troubleshooting](#troubleshooting)

---

## Windows

There are two ways to run AgOpenWeb on Windows. Both come from the **same download**:
`agopenweb-win-x64.zip`.

| Mode | Best for | What you do |
|------|----------|-------------|
| **Desktop window** | Running guidance directly on this PC | Double-click the app |
| **Windows Service** | An always-on in-cab PC that boots straight into guidance | Run the installer once |

**Before you start:** AgOpenWeb needs the **Microsoft Edge WebView2 Runtime**. This is already
installed on Windows 11 and current Windows 10. If it is missing, download the free "Evergreen"
runtime from Microsoft and install it.

### Option A — Desktop window (simplest)

1. Download `agopenweb-win-x64.zip` from the Releases page.
2. Right-click the zip → **Extract All…** to a folder you'll remember (for example `C:\AgOpenWeb`).
3. Open that folder and double-click **`AgOpenWeb.Desktop.exe`**.
4. A full window opens showing the guidance screen. That's it.

The host is now running. Other devices on the same network can reach it at
`http://<this-pc-ip>:5174` (see [Connecting a tablet](#connecting-a-tablet-or-phone-over-wi-fi)).

### Option B — Windows Service (auto-starts on boot)

Use this when the PC lives in the cab and you want guidance available the moment it powers on,
with no one logged in.

1. Extract `agopenweb-win-x64.zip` as above.
2. In the extracted folder, **right-click `install-service.cmd` → Run as administrator**.
3. The installer registers the service, starts it, and opens the firewall so tablets can connect.

The screen is now available in any browser at `http://localhost:5174` on the PC itself, or
`http://<pc-ip>:5174` from another device.

**To remove the service later**, open an administrator command prompt in the same folder and run:

```
install-service.cmd -Action uninstall
```

---

## Linux

Linux also has two flavors from **two different downloads** — a desktop launcher (opens a window)
and a headless daemon (no window, runs as a system service). Both come in **x64** and **arm64**
builds; arm64 is for SBCs and mini-PCs like Raspberry Pi or the Uno Q.

### Option A — Desktop launcher (opens a window)

Download: `agopenweb-launcher-linux-x64.tar.gz` (or `-arm64`).

```bash
tar xzf agopenweb-launcher-linux-x64.tar.gz
cd agopenweb-launcher-linux-x64
./run.sh
```

A window opens with the guidance screen. To add it to your applications menu so you can launch it
like any other app (per-user, no `sudo` needed):

```bash
./install.sh
```

To remove it again: `./install.sh --uninstall`.

**Prerequisites** (install once via your package manager — they are *not* bundled):

```bash
sudo apt-get install libwebkit2gtk-4.1-0 libsoup-3.0-0 libicu-dev libfontconfig1 libgl1
```

### Option B — Headless daemon (auto-starts on boot)

This is the recommended setup for an in-cab mini-PC or SBC: no window, starts automatically at
boot, restarts itself if it ever crashes, and serves the screen to any browser on the network.

Download: `agopenweb-linux-x64.tar.gz` (or `-arm64`).

```bash
tar xzf agopenweb-linux-x64.tar.gz
cd agopenweb-linux-x64
sudo ./install.sh --from app
```

The installer publishes the app to `/opt/agopenweb`, creates a dedicated `agopenweb` user, installs
a `systemd` service, and starts it. From then on it comes up on every boot.

Open a browser (on the box or another device) to `http://<host-ip>:5174`.

**Managing the daemon:**

```bash
systemctl status agopenweb       # is it running?
journalctl -u agopenweb -f       # watch the live log
systemctl restart agopenweb      # restart it
sudo ./uninstall.sh              # remove it (keeps your field data)
sudo ./uninstall.sh --purge      # remove it AND all field data
```

Your field data, vehicle profiles, and settings live in `/home/agopenweb/AgOpenWeb/` and are
preserved across updates (unless you `--purge`).

---

## Mobile

### Android

AgOpenWeb runs entirely on the tablet — host and screen together — as an all-in-one app.

1. Download `agopenweb-android.apk` from the Releases page to the tablet.
2. Open it. Android will ask you to allow installing apps from this source — approve it (this is
   normal for apps installed outside the Play Store, known as *sideloading*).
3. Launch **AgOpenWeb**. The guidance screen fills the display.
4. On first launch, allow the **notification** permission — Android requires a persistent
   notification while the host runs in the background.

Requirements: **Android 6.0 or newer**. The app keeps running when backgrounded (so connected
cab devices stay live); to fully stop it, force-stop the app from Android settings.

Because the tablet is also the host, other devices on the same Wi-Fi can connect to it at
`http://<tablet-ip>:5174`.

### iOS / iPadOS

Like Android, iOS runs all-in-one on the device — host and screen together. iOS builds are
distributed through Apple's **App Store / TestFlight** (rather than sideloading).

1. Install AgOpenWeb from the App Store, or accept a **TestFlight** invite for a beta build and
   install it from the TestFlight app.
2. Launch **AgOpenWeb**. The guidance screen fills the display.

iOS does not allow true background daemons, so keep the app in the foreground while guiding (use
**Guided Access** to keep it pinned). Because the device is also the host, other devices on the
same Wi-Fi can connect to it at `http://<device-ip>:5174`.

> Any iPhone or iPad can also be used purely **as a client** to a host running elsewhere — just
> open Safari and go to `http://<host-ip>:5174`, no install needed.

---

## Connecting a tablet or phone over Wi-Fi

Any running host — Windows, Linux, or Android — publishes the guidance screen on the local network.
To use a separate phone or tablet as the in-cab display:

1. Make sure the device is on the **same Wi-Fi network** as the host.
2. Find the host's IP address:
   - **Windows:** open Command Prompt, run `ipconfig`, look for the IPv4 address.
   - **Linux:** run `ip addr` (or `hostname -I`).
   - **Android:** Settings → About → Status → IP address.
3. On the phone/tablet, open a web browser and go to `http://<host-ip>:5174`
   (for example `http://192.168.1.50:5174`).

The same host serves every connected device at once, so the cab PC and a tablet can show the
screen simultaneously.

---

## Troubleshooting

**The screen doesn't open on Windows.** Make sure the **WebView2 Runtime** is installed (it ships
with Windows 11). Download the free Evergreen runtime from Microsoft if needed.

**The window won't start on Linux.** You're most likely missing a prerequisite. Re-run the
`apt-get install` line in the Linux section — `libicu-dev` in particular is required for the host
to start at all.

**A tablet can't reach `http://<host-ip>:5174`.** Confirm both devices are on the same network,
double-check the IP address, and make sure the host PC's firewall allows inbound connections on
port **5174**. (The Windows Service installer opens this automatically; the desktop window mode may
prompt you to allow it on first run.)

**Android blocks the install.** Sideloading requires you to allow "install unknown apps" for
whatever app you opened the APK from (your browser or file manager). Grant it, then re-open the
APK.
