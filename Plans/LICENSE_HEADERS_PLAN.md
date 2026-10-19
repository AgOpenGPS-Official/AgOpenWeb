# Licence headers: make the source match LICENSE.md

**Status:** proposed 2026-10-19. One PR. Independent of every other plan.

## Problem

AgOpenWeb is licensed under the Apache License 2.0, but most of its source files say GPL v3.

| What | Says | Where |
|---|---|---|
| `LICENSE.md` | Apache 2.0, "Copyright [2025] [AOG Group]" | since c598c49f (2026-05-17, "Update license to be consistent with AgOpen") |
| About panel in the web client | "Apache License 2.0" | `Shared/AgOpenWeb.RemoteServer/wwwroot/index.html` |
| `README.md`, section License | "GNU General Public License v3.0" | `README.md` |
| Full GPL v3 header block | GPL v3 | 549 `.cs` files, plus `CONTRIBUTING.md`, `BUILD.md`, `Docs/PGN.md` |
| Short header "Licensed under GNU GPL v3. See LICENSE.md." | GPL v3 | 212 of those `.cs` files |

How it got here:

1. 2026-01-21, 26aef156: "Add GPL v3 license headers to all source files" (423 files). `LICENSE.md` was GPL v3 then.
2. 2026-05-17, aedc35b6 and c598c49f: `LICENSE.md` replaced by Apache 2.0, to match AgOpenGPS. The headers were not changed.
3. Since then the drift continues: of the 271 `.cs` files added after 2026-05-17, 108 still got a GPL header, copied from the files around them.

By folder: `Shared` 377, `Tests` 130, `Platforms` 27, `Simulators` 15.

The result is a project that tells its users two different licences. A file with a GPL header that is copied into another project (for example into AgOpenGPS, which is also Apache 2.0) carries that GPL notice with it.

## Decisions

These need the maintainers, not a code change:

- [ ] **The licence is Apache 2.0.** `LICENSE.md`, the About panel and the commit message of c598c49f say so. This plan assumes it.
- [ ] **Contributions made while the project was GPL v3** (until 2026-05-17) need their authors' agreement to be distributed under Apache 2.0. `git shortlog -sne --before=2026-05-17` lists the authors of that period: chriskinal, Xyntexx, PZES, hagre, richardklasens, David Wedel, MalteBahr, plus commits by "Claude" and "BuildTools". Record the agreement, for example as a comment from each in the PR, or in an issue linked from it. This is not legal advice; if in doubt, ask someone who gives it.
- [ ] **Copyright holder name.** `LICENSE.md` says "AOG Group", the headers say "AgOpenWeb Contributors". Pick one and use it in both.
- [ ] **Header form** (see below): a short SPDX header in every file (recommended), or no header at all. Apache 2.0 requires neither; the choice is about clarity when a file travels on its own.

## Change

### New header

```csharp
// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0
```

Three lines instead of fifteen. The SPDX identifier is machine-readable, so tools and reviewers can check it.

### Files

| Files | Change |
|---|---|
| All `.cs` files with the full GPL block or the short GPL line (`Shared`, `Tests`, `Platforms`, `Simulators`) | Replace the header with the new one; keep each file's existing year range |
| `.cs` files without a header (128 today) | Add the new header |
| `README.md`, section License | "Apache License 2.0", linking `LICENSE.md` |
| `CONTRIBUTING.md`, `BUILD.md`, `Docs/PGN.md` | Remove the GPL comment block at the top |
| `CONTRIBUTING.md`, `CLAUDE.md`, `AGENTS.md` | Add the header template and one line: new files use it, never copy a header from a neighbouring file |
| `LICENSE.md` | Copyright line, if the holder name changes (see Decisions) |

### Not changed

- `Plans/Completed/*.md`: historical records; they may mention GPL as it was then.
- `Shared/AgOpenWeb.RemoteServer/wwwroot/vendor/` (CanvasKit): third-party code keeps its own licence.
- `AgOpen_Snapshot/`: a copy of AgOpenGPS v6.4 kept for reference, under the licence AgOpenGPS had then. Either leave it as is with a `README` that says so, or drop it from the repo; it is not built.

### Keeping it fixed

A test in `Tests/AgOpenWeb.Models.Tests` (or a small script in `Tools/` run by CI next to `i18n-extract.py --check`) fails when a `.cs` file:

- contains "GNU General Public License" or "GPL v3", or
- lacks `SPDX-License-Identifier: Apache-2.0` in its first five lines.

Without this check the 108 files in five months show the headers will drift back.

## Steps

1. Maintainers settle the four decisions above.
2. One PR:
   1. A script in `Tools/` rewrites the headers (one commit, generated, no other changes in it).
   2. The docs and templates (second commit).
   3. The CI check (third commit).
3. Reviewers check the diff with `git diff --stat` (only the header lines change in `.cs` files) and run the full test suite.

## Verification

- `grep -rIl "GNU General Public License\|GPL v3" --include=*.cs . --exclude-dir=AgOpen_Snapshot` returns nothing.
- Every `.cs` file outside `AgOpen_Snapshot` and `obj/` has the SPDX line.
- The CI check fails on a test file that has a GPL header, and passes after the header is fixed.
- Build and all test projects pass; no file changes beyond its header.
