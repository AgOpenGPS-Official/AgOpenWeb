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
sudo ./uninstall.sh --purge         # remove everything incl. /home/agopenweb
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
| `/opt/agopenweb` | published program (self-contained) — REPLACED on every update |
| `/home/agopenweb/AgValoniaGPS` | field data + profiles + config (`AGOPENWEB_DATA`) — kept across updates |
| `/etc/systemd/system/agopenweb.service` | the unit |

Updates only ever touch `/opt/agopenweb` (with a `/opt/agopenweb.old` backup). All
operator data lives under `/home/agopenweb` (the `agopenweb` service user's home,
via the `AGOPENWEB_DATA` env var the unit sets), so fields/tools/vehicles/config survive
every `install.sh --from app`. The data home is **world-readable but daemon-write-only**
(`0755` + `UMask=0022`): any login user can `cd /home/agopenweb/AgValoniaGPS` to
browse or back up the fields, but only the service can modify them (no accidental
corruption while it runs). To run the host by hand outside systemd against the same data,
set `AGOPENWEB_DATA=/home/agopenweb`. To put data elsewhere (home dir, USB/SSD, NFS),
point `AGOPENWEB_DATA` at it.

## Notes

- **ICU (libicu) is required.** .NET's globalization needs it and it is NOT bundled in
  the self-contained publish, nor shipped by default on minimal images (Armbian / Debian
  Trixie, etc.). Without it the host won't start at all (`System.Globalization` throws on
  load). `install.sh` apt-installs the right `libicuNN` (resolved per Debian release, e.g.
  `libicu76` on Trixie), falling back to `libicu-dev`. On a non-apt distro install libicu
  manually (or `libicu-dev` / `icu-devtools` both pull it in).
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
