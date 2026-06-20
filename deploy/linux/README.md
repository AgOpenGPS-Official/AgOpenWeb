# AgOpenWeb — Linux systemd deployment

Run the **headless** AgOpenWeb host as a daemon on a cab-PC / one-box SBC. The host
has no native UI; operators use a browser (remote tablet, or a local chromium-kiosk
pointed at `http://localhost:5174`). Background and rationale:
[`Plans/DEPLOYMENT_PATTERNS.md`](../../Plans/DEPLOYMENT_PATTERNS.md).

## Install

```bash
# On the target box (needs the .NET 10 SDK to publish):
sudo ./install.sh                 # x86-64 box
sudo ./install.sh --arch arm64    # 64-bit ARM SBC (Raspberry Pi, etc.)
```

This publishes a **self-contained** build (no .NET runtime needed on the box),
installs it to `/opt/agopenweb`, creates the `agopenweb` service user (in `dialout`
+ `can` for serial/CAN device access), and installs + enables `agopenweb.service`.
The host auto-starts on boot.

To build elsewhere and copy: run `install.sh` on a build machine of the same arch,
then copy `/opt/agopenweb`, `agopenweb.service`, and re-run with `--no-build` on the
target.

## Operate

```bash
journalctl -u agopenweb -f          # live logs
systemctl status agopenweb          # state + last lines
systemctl restart agopenweb         # restart
sudo ./uninstall.sh                 # remove (keeps field data)
sudo ./uninstall.sh --purge         # remove everything incl. /var/lib/agopenweb
```

Browse to `http://<box-ip>:5174`.

## What the unit gives you

- **`Type=notify` + `WatchdogSec=30`** — the host signals readiness (systemd marks
  the unit started only once the browser endpoint is live) and pets a hardware
  watchdog via `sd_notify` (`SystemdWatchdogService`); a wedged host is killed and
  restarted.
- **`Restart=always`** — the brain comes back after a crash.
- **`After=network-online.target`** — modules/NTRIP sockets have a real network.
- **journald logging**, dedicated unprivileged user, and moderate hardening
  (tighten per site — see comments in `agopenweb.service`).

## Paths

| Path | Contents |
|------|----------|
| `/opt/agopenweb` | published program (self-contained) |
| `/var/lib/agopenweb` | field data + config (`HOME` of the service user) |
| `/etc/systemd/system/agopenweb.service` | the unit |

## Notes

- The build is **not trimmed** — the steer wizard projects step ViewModels by
  reflection, which trimming would break. Self-contained-untrimmed is ~160 MB
  (bundles the .NET runtime; the unused Avalonia desktop natives ride along but are
  never loaded on the headless path).
- A local kiosk display is a separate concern: install a minimal X/Wayland +
  `chromium --kiosk http://localhost:5174` as its own unit. The AgOpenWeb host
  itself needs no display.
- **Boundary imagery** (drawing a field boundary on the satellite map) composites
  Bing tiles with SkiaSharp host-side, whose native lib links `libfontconfig` +
  `libGL`. `install.sh` apt-installs them; on a non-apt distro install them manually.
  If they're missing the host stays up (capture is crash-isolated) but the background
  is blank. Imagery also needs the board to reach `virtualearth.net` (internet).
