# Menu System Style Standardization

## Context

After the `feature/nav-back-close-chains` rollout, the menu system's **chrome is
consistent** but **buttons and colors have drifted**. An audit (3-way sweep of every
panel/dialog) found the cards/headers are uniform and 100% theme-driven, while the
drift lives in (a) per-dialog button styles that are each re-declared locally, (b)
~280 hardcoded hex colors that should be a handful of named brushes, and (c) a
card/header background **inversion** between the two base controls. Goal: one shared
style vocabulary so every menu panel/dialog reads as the same app, and theme/accent
changes happen in one place.

Do this in its own branch off `develop`. Mechanical but broad (touches most dialogs).

## Current state (from audit)

**Strong commonality (keep as the baseline):**
- All 7 fly-outs use `Controls/FloatingPanel.axaml` — ChromeMedium card / AltHigh
  header / `=` glyph + `X` / CornerRadius 12 / 1px BaseLow border / 8px padding.
- All converted chain dialogs wrap in `Controls/DialogChrome.axaml` — Back · Title ·
  Close header, CornerRadius 12.
- Both base controls are already fully `DynamicResource`-driven.

**Drift to fix (ranked by visibility):**
1. **Card/header inversion:** `FloatingPanel` = ChromeMedium card / AltHigh header;
   `DialogChrome` = the reverse (AltHigh card / ChromeMedium header). Pick ONE
   convention and apply to both.
2. **Primary-button color scatter:** majority use `SystemControlHighlightAccentBrush`,
   but `#27AE60` (imports, Field Builder), `#3498DB` (ImportTracks), `#9B59B6`
   (AgShare Download), multi-color (Log Viewer) all deviate. Cancel/secondary is
   sometimes gray, sometimes `#C0392B`, sometimes no hover.
3. **Per-dialog button styles:** `DialogButton`/`CancelButton`/`OkButton`/`DangerButton`
   /`ToolbarButton`/`TestButton` are redeclared in nearly every dialog's local
   `<UserControl.Styles>` instead of being shared.
4. **~280 hardcoded hex colors** → should be named brushes:
   `#E74C3C` danger (55×), `#4A9A7E` status-green (36×, Screen&Alerts + Network IO),
   `#27AE60` success (29×), `#80000000` modal backdrop (22×), `#1ABC9C` info/teal
   (18×), `#2980B9` primary-blue/hover (14×), plus `#C0392B`/`#2ECC71`/`#E67E22`.
5. **Two structural one-offs:** `RecordedPathDialogPanel` is a hand-built Border
   imitating FloatingPanel (convert to a real `FloatingPanel`); `HelpDialogPanel` and
   `BugReportDialogPanel` reference `ModernButton`, a class defined only inside
   FloatingPanel — likely unresolved in those dialogs (verify + fix).

## Approach

### 1. Add semantic brushes + shared button styles to `SharedResources.axaml`
File: `Shared/AgValoniaGPS.Views/Styles/SharedResources.axaml` (already has the
`ThemeDictionaries` with `ToggleOn/OffBrush`). Add to BOTH Light and Dark variants:
- `PrimaryButtonBrush` (← `SystemControlHighlightAccentBrush` value), `PrimaryButtonHoverBrush` (#2980B9)
- `SuccessButtonBrush` (#27AE60) / `SuccessButtonHoverBrush` (#2ECC71)
- `DangerButtonBrush` (#E74C3C) / `DangerButtonHoverBrush` (#C0392B)
- `StatusOkBrush` (#4A9A7E)  — the Screen&Alerts/Network IO status green
- `InfoAccentBrush` (#1ABC9C)
- `ModalBackdropBrush` (#80000000)  — for any remaining darkening backdrops

Then add **app-wide `Button` style classes** (in a shared styles file included by each
platform's `App.axaml`, or in `SharedResources.axaml` as styles): `Button.DialogButton`
(primary), `Button.CancelButton`, `Button.OkButton` (success), `Button.DangerButton`,
`Button.ToolbarButton` — each bound to the new brushes. Mirror the existing dialog
definitions (CornerRadius 6, Padding 16,10, FontSize 14, SemiBold) so it's a drop-in.

### 2. Reconcile the card/header convention
Decide one: recommend `DialogChrome`'s look (AltHigh card + ChromeMedium header) as the
standard since dialogs hold forms/lists that need card contrast, and update
`FloatingPanel.axaml` to match (swap its card/header brushes) — OR vice-versa. One-line
brush swaps in the two control axaml files; verify both light/dark.

### 3. Strip per-dialog redefinitions + hardcoded hex
For each dialog under `Controls/Dialogs/` that locally defines `DialogButton`/etc.:
delete the local `<Style Selector="Button...">` blocks and the hardcoded button
backgrounds, relying on the shared classes/brushes. Replace primary actions with
`Classes="DialogButton"`, confirms with `OkButton`, deletes with `DangerButton`.
Representative files: NtripProfiles/Editor, NewField, FromExisting, Kml/IsoXml import,
AgShare Upload/Download/Settings, StartWorkSession, ResumeJob, FieldBuilder,
LogViewer, Hotkey, ImportTracks (drop its `#3498DB`).

### 4. Fix the two structural one-offs
- Convert `RecordedPathDialogPanel.axaml` to a real `FloatingPanel` (PanelContent =
  its tab/record/playback body) like the Offset Fix conversion already done.
- In `HelpDialogPanel`/`BugReportDialogPanel`, replace `ModernButton` with the shared
  `DialogButton`/`CancelButton` (confirm whether `ModernButton` resolves there first).

## Files
- `Shared/AgValoniaGPS.Views/Styles/SharedResources.axaml` — new brushes (Light+Dark) + shared button styles.
- `Shared/AgValoniaGPS.Views/Controls/FloatingPanel.axaml` and `DialogChrome.axaml` — reconcile card/header brushes.
- ~20 dialogs under `Shared/AgValoniaGPS.Views/Controls/Dialogs/` — strip local styles/hex, use shared classes.
- `RecordedPathDialogPanel.axaml(.cs)` — convert to FloatingPanel.
- Hardcoded `#4A9A7E`/status colors in `ScreenAlertsPanel.axaml`, `NetworkIoPanel.axaml` → `StatusOkBrush`.

## Verification
1. `dotnet build Platforms/AgValoniaGPS.Desktop/...` clean; `dotnet test Tests/AgValoniaGPS.UI.Tests/` (147 pass).
2. Run Desktop; open every fly-out and a dialog from each chain in BOTH day and night
   mode (toggle theme) — confirm cards/headers/buttons read identically and there's no
   stray accent color. Charts + tool overlays included.
3. Grep that the hardcoded hex set (`#27AE60`, `#E74C3C`, `#3498DB`, `#9B59B6`,
   `#4A9A7E`, `#2980B9`) no longer appears in `Controls/Dialogs` button backgrounds.

## Notes
- Cross-platform: `SharedResources.axaml` is included by each platform `App.axaml` — no
  per-platform edits beyond confirming the include is present.
- Keep gauge/indicator colors (`#E91E90`, `#FFE000`, `#3FD0F0`, roll gauge) as-is —
  those are data-viz, out of scope.
- Audit detail lives in memory `project_nav_back_close_chains.md`.
