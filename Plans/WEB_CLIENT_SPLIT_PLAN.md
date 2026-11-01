# Web client: split app.js and index.html

**Status:** proposed, not started.
**Date:** 2026-11-01
**Purpose:** `app.js` is 8,710 lines (504 KB) and `index.html` 3,412 lines (268 KB: 1,440 of
CSS, the rest the markup of ~85 panels and dialogs). Finding the code for one panel means
searching one file, every PR touches the same two files and conflicts with every other PR,
and a newcomer has no map of where things are. The move to a web client was to widen the
contributor base; the client should be easy to find your way around in. This plan splits
both files by feature **without changing how the client works**: still plain HTML/JS, no
build step, no framework, no modules.

---

## 1. Decisions

| # | Question | Decision |
|---|---|---|
| 1 | Script kind | **Classic scripts**, loaded in order by `index.html`. Top-level `const`/`let`/`function` of classic scripts share one global scope, so code moved to another file reaches and is reached by the rest exactly as today; the console and Playwright (`transport`, `config`, `tick`, `iHoldControl`, `populateAutoSteer`…) keep working. No ES modules: they would hide those names, need an import graph, and change load timing. |
| 2 | Build step | **None.** The browser loads the files as they are in the repo. No bundler, no minifier, no TypeScript. |
| 3 | Layout | `wwwroot/js/` for the client's scripts, `wwwroot/css/` for its styles; `transport.js`, `i18n.js` and `vendor/` stay where they are. |
| 4 | Cut lines | By feature, along the section banners that are already there (`// ---- Network IO ----`). One file owns one panel or one part of the map. Target: no file over ~1,000 lines; the map renderer may be larger if splitting it would cut through one algorithm. |
| 5 | Load order | Today's order in the file, kept: core state first, features next, the frame loop and boot (`skFrame()`, the `wire*()` calls) last, in `js/boot.js`. A file may *call* into any other file at run time, but code that runs while loading uses only files before it. |
| 6 | Markup | Stays in `index.html`, in one place per panel, with a contents comment at the top. Fetching HTML fragments at start would make the start asynchronous and split the i18n scan; moving markup into JS strings makes it harder to read. Revisit only if `index.html` is still hard to work in after the CSS leaves. |
| 7 | Behaviour | **None changes** in the split PRs. Moves only; any fix found on the way is a separate PR. |
| 8 | The one-way guard | `CodePathContinuityTests` pins "the client is `index.html` + `app.js` + `transport.js` + `i18n.js`". It changes to: classic scripts listed in `index.html`, all under `js/` (plus `transport.js`, `i18n.js`), stylesheets under `css/`, no modules, no workers. Every file in those folders must be loaded by `index.html`, and every loaded file must exist. |

## 2. Proposed files

From the section banners in `app.js` today (line ranges approximate; final cuts are made
in the step):

| File | Holds | ~lines |
|---|---|---|
| `js/core.js` | model fed by the transport, render delay, units, camera state, transport wiring | 480 |
| `js/alerts.js` | host prompts and failure notices, alert sounds | 250 |
| `js/map-input.js` | camera controls, map-tap capture, sim drive keys, hotkeys | 450 |
| `js/dialogs.js` | dialog host, unsaved-coverage guard | 150 |
| `js/osk.js` | on-screen keyboard | 300 |
| `js/bars.js` | sim bar, section bar, bottom nav, U-turn / lateral buttons | 250 |
| `js/tracks.js` | flags, Ref Nudge, AB creation, track draw, tracks manager | 700 |
| `js/field-builder.js` | boundary / headland draw, Field Builder tabs (tracks, headland, tram) | 900 |
| `js/left-nav.js` | left nav and its config bridge | 170 |
| `js/config-panels.js` | tool and AutoSteer panels, Smart WAS | 450 |
| `js/network.js` | Network IO, NTRIP settings | 600 |
| `js/fields.js` | Field Operations, AgShare | 300 |
| `js/app-menu.js` | app menu, language, About, bug report | 280 |
| `js/wizards.js` | steer wizard, vehicle & tool picker | 340 |
| `js/control.js` | remote actuation, control authority | 80 |
| `js/projection.js` | screen matrix, screen↔world | 970 |
| `js/hud.js` | lower-right cluster, roll gauge, campad, charts | 400 |
| `js/cards.js` | card pane, web cards | 320 |
| `js/vt.js` | VT painter and cards | 720 |
| `js/map-draw.js` | CanvasKit setup and the Skia render layers | 1,500 |
| `js/boot.js` | frame loop, DOM overlay updates, start | 60 |

CSS, in cascade order: `css/base.css` (tokens, themes, layout), `css/map.css` (overlays on the
map), `css/panels.css` (menu panels and their controls), `css/dialogs.css`, `css/cards.css`
(card pane, VT, web cards), `css/osk.css`.

## 3. What has to follow the files

- **Host:** `RemoteServerHost` serves `/js/<name>.js` and `/css/<name>.css` from the embedded
  `wwwroot` (name only, no path traversal, unknown names 404, `no-store` like `app.js` today).
  The csproj already embeds `wwwroot/**`.
- **CI:** `node --check` runs over every `js/*.js`; the licence-header check covers them.
- **i18n:** `Tools/i18n-extract.py` reads every `js/*.js` with `index.html`; `en.json` must come
  out identical in the split PRs (a check that the scan lost nothing).
- **Docs:** `CLAUDE.md` (key files, "Adding a web command", "Adding UI text"), `CONTRIBUTING.md`,
  and a short `Docs/WEB_CLIENT.md`: the file map, the load order rule, and how to add a panel.
- **Comments:** each file starts with what it owns and what it uses from other files. The
  history in comments goes ("Phase 9", "mirrors native X", ~100 references to the removed
  Avalonia UI): what the code does stays, how it got there is in git and `Plans/Completed/`.

## 4. Steps

Each step is one PR, mechanical, checked the same way: CI green, `en.json` unchanged, the
headless app loads with no page error, every left-nav panel, menu panel and dialog opens, a
field drives in the simulator; then once on the Galaxy Tab and the iPad.

1. **Guard and host first:** the `/js/` and `/css/` routes, the new `CodePathContinuityTests`
   rule, `node --check` and the i18n scan over the folders. No file moves yet.
2. **CSS out of `index.html`** into `css/`, same order. `index.html` drops to ~1,970 lines.
3. **`app.js` into `js/`**, in two or three PRs from the top down (core and input; the panels;
   map, cards and boot), so each diff can be reviewed as "moved, not changed"
   (`git diff --color-moved` shows it).
4. **Comments and `index.html` contents:** file headers, history comments out, a contents
   comment over the markup.
5. **Docs** (§3).

## 5. Open pull requests

A move conflicts with every open branch that touches `app.js` or `index.html`. So:
- Merge or park what's open before step 3 (today: #517 on-screen keyboard; the drawer step of
  `CARD_PANE_SPLIT_PLAN.md` comes after the split, in `js/cards.js`).
- Announce the day; each step 3 PR lands in one go, not left open.
- For a branch that was open across the split: its change is re-applied to the new file by
  hand (git doesn't follow code moved between files); the PR note says which file got what.

## 6. Not in this plan

ES modules, a bundler, a framework, TypeScript, a component system, moving markup out of
`index.html`, any change of behaviour. Each can be its own decision later, with this split
done first.
