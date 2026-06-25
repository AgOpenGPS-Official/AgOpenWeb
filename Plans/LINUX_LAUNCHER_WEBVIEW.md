# Linux Launcher WebView — Findings & Fix (v26.6.27)

Handoff notes for the all-in-one launcher's embedded WebView on **Linux**.
Branch: `fix/linux-launcher-webview-rendering`. Investigated on a Parallels VM
(Apple Silicon, virtio-gpu) — a dev box, **not** a deployment target.

## TL;DR

- The Linux launcher embeds the CanvasKit/WebGL UI via `Avalonia.Controls.NativeWebView`
  (WebKitGTK reparented as an X11 child through `NativeControlHost`).
- It was unusable on Linux for **two GPU-independent bugs** — now fixed:
  1. **Sizing**: the native child window was stuck at 1×1 → page rendered into a 1px corner.
  2. **Splash reveal**: `NavigationCompleted` fires off the UI thread → splash never hid.
- A third issue is environmental, not a code bug: on a **virtualized GPU (virtio-gpu)**
  the reparented child's hardware-GL surface does not present (solid black). This does
  **not** happen on real GPUs. **Decision: the Linux launcher is supported on real
  hardware only (ARM SBC, industrial x86) — not in a VM.**

## What changed

| File | Change |
|---|---|
| `Platforms/AgOpenWeb.Desktop/Launcher/WebViewLauncherWindow.cs` | `SyncNativeWebViewSize()` forces a re-arrange of the native child once the adapter is up (NavigationStarted) + staggered retries; splash reveal marshaled via `Dispatcher.UIThread.Post`. |
| `Platforms/AgOpenWeb.Desktop/Launcher/LauncherEntry.cs` | `WarnIfVirtualMachine()` logs a clear hint (via `systemd-detect-virt`) that the launcher needs real GPU hardware. Hardware GL kept (no forced software rendering). |
| `sys/version.h` | 26.6.26 → 26.6.27 |

Windows/macOS are untouched (WebView2 / WKWebView; both already work).

### Bug 1 — sizing (the big one)
`NativeControlHost` creates the native child window **asynchronously** (after the
WebKitGTK adapter builds), which is **after** the maximized window's first arrange,
and never re-arranges it. The Avalonia-side `Bounds` are already full-size, but the
native X11 window stays at its 1×1 creation size, so the page paints into a 1-pixel
corner and the window looks black. Verified via `xwininfo -tree`: the host window and
the WebKit window were `1x1`, and snapped to full size the instant we called
`InvalidateArrange()`. Fix: re-arrange once the adapter exists, with a couple of
staggered retries to absorb the creation race. Linux-gated (Win/Mac size correctly).

### Bug 2 — splash never hides
The `NavigationCompleted` handler set `_splash.IsVisible = false`, but WebKitGTK can
raise that event off the UI thread, and setting a property off-thread silently no-ops
on the X11 backend — so the "Starting AgOpenWeb…" splash stayed up forever. Fix:
marshal the reveal through `Dispatcher.UIThread.Post`.

## The virtual-GPU wall (environmental, not fixed in code)

Definitive matrix (measured on the Parallels virtio-gpu box):

| Rendering | Embedded WebView (reparented X11 child) | Top-level webkit (Epiphany) |
|---|---|---|
| **Hardware GL** (`virgl`) | ❌ black — Xorg **and** Wayland, even plain HTML | ✅ renders |
| **Software GL** (`llvmpipe`) | ✅ renders full UI | ✅ renders |

The reparented child's GL buffer doesn't present on virtio-gpu. Real GPUs
(Intel/AMD/NVIDIA, ARM SBC GPUs) don't have this limitation — reparented
hardware-GL WebView embedding is a standard, working config there.

Dead ends ruled out for the VM case (don't re-try these):
- **WPE backend** (offscreen, has a software `Shm` mode): WPEWebKit-2.0 isn't packaged
  for Ubuntu 24.04 and can't be installed without building from source.
- **Offscreen cairo path** (`ExperimentalOffscreen`): WebKit 2.52 always uses
  accelerated compositing, so the cairo `draw` snapshot is empty → black.
- **Avalonia Wayland backend**: doesn't exist in Avalonia 12 (X11/Xwayland only).
- **Forcing software GL** (`LIBGL_ALWAYS_SOFTWARE`): works but makes the map sluggish — rejected.
- **System-browser fallback** (`xdg-open`): works and renders the full UI, but the
  two-window UX (status window + browser) was rejected.

## Verification status

- ✅ Sizing fix: verified (native window 1×1 → full size after re-arrange).
- ✅ Full UI renders embedded under **software GL** (proven in a nested Xephyr X server).
- ✅ Full UI renders in a **top-level** WebKitGTK window with hardware GL (Epiphany) on the same box.
- ⚠️ **Embedded WebView with hardware GL on real hardware: NOT verified here** (no real
  GPU available in the VM). This is the maintainer's real-hardware test (ARM SBC + x86).

## How to test on real hardware

```bash
dotnet run --project Platforms/AgOpenWeb.Desktop/AgOpenWeb.Desktop.csproj \
  -c Release -p:DesktopOnly=true -- --launcher
```

Expected: launcher window fills with the live guidance UI (map grid, nav panel, steer
dial), hardware-accelerated. Logs (`[webview] …`, `[launcher] …`) go to stdout.

If it's **black** on real hardware, collect:
- the `[webview]`/`[launcher]` stdout lines,
- `glxinfo -B | grep -i renderer` (confirm a real GPU, not `llvmpipe`/`virgl`),
- `xwininfo -tree` for the AgOpenWeb window (confirm the WebView child is full-size, not 1×1).

That distinguishes "different GL-presentation quirk" from "same vGPU class of issue."

## Notes for capturing/verifying rendering on a Wayland dev box

Direct X grabs (`xwd`) read black for composited/Xwayland windows regardless of content.
What worked: the freedesktop **portal screenshot** (`org.freedesktop.portal.Screenshot`,
`interactive:false`) for the real screen, and Avalonia `RenderTargetBitmap` for
Avalonia-drawn content. A nested **Xephyr** server (no compositor) lets `xwd -root`
capture true pixels but only offers software GL under a Wayland host.
