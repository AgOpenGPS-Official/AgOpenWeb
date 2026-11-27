# Settings reader map

Every setting the web client can change (`config.set|key:value`, one `case` of
`RemoteServerWiring.ApplyConfigSet`) and the code that reads it, found by the compiler.
`settings-readers.txt` is the map; CI fails when it is out of date or a setting is read by nothing.

The early settings bugs (#99, #101, #105, #106, #110, #111) were settings that were written and
saved but read by no code that acts: the slider moved, nothing changed. Review sees what a change
adds, not the read it leaves out (or removes in a large commit), and the settings sweep (Vehicle &
Tool harness) finds it only after hours of driving. The map turns it into a line in the diff.

```bash
dotnet run --project Tools/settings-readers               # print the map
dotnet run --project Tools/settings-readers -- --write    # rewrite settings-readers.txt
dotnet run --project Tools/settings-readers -- --check    # CI: up to date, and every setting read
```

It needs the shared projects restored (`dotnet restore` or a build); it loads
`AgOpenWeb.RemoteWiring` and everything it references with Roslyn (`MSBuildWorkspace`) and stops
if they do not compile. About 20 s.

## How it reads the code

1. **What a key writes.** Each `case` of the switch: the store properties and fields it assigns,
   and, through a store method it calls (`ApplySectionCount`, `SetSectionWidth`), what that method
   assigns. The store is the `AgOpenWeb.Models.Configuration` namespace.
2. **Who reads it.** Every reference to that member in the solution (Roslyn's `SymbolFinder`),
   kept when it is a read: not the target of an assignment, an increment or a `ref`/`out`
   argument. A property's backing field is followed too (`CoverageMarginMeters` reads
   `_coverageMargin`, not `CoverageMargin`).
3. **Through the store.** A read inside the store (a method packing bits into a settings byte, a
   property computed from another) is followed to whoever uses that member, until the code leaves
   the store: `autosteer.imuAxisSwap` → `AutoSteerConfig.GetSetting1Byte` → `PgnBuilder.BuildSteerConfigPgn`.
4. **What kind of reader.** By the type it is in:
   - `app`: AgOpenWeb's own code (services, the view model, the wizards, `PgnBuilder`)
   - `page`: `SceneProjector`, with the DTO field the value is put in (`-> LightbarEnabled`)
   - `save`: settings and profile files (`ConfigurationService`, the profile JSON services, ...)
   - `report`: the bug report dump
   - `routing`: `ApplyConfigSet` itself (a store method it calls reading the value back)
5. **On the page.** For a `page` field: its name in `transport.js` (the DTO field in camelCase) and
   the scripts that use it (`.name`). `settings-panels.js`, `config-panels.js`, `cfg-controls.js` and
   `index.html` only show the setting's own value, so they are listed apart ("shown in"). The
   scripts are untyped, so **this one step goes by name**: a field the page decodes under another
   name (the Status frame's) is reported as not decoded, and a name the canvas also uses
   (`ctx.lineWidth`) can match where the setting is not used.

A key **acts** when an `app` reader or a page script (not a panel) reads it. The file's head lists
the keys that do not; `--check` fails on any. `ConfigurationStore.HasUnsavedChanges` (set by
`MarkChanged`) is bookkeeping and left out.

## What it cannot see

- A setting read and then ignored, or used in the wrong units (#112): the read is there. The
  harnesses' settings sweep drives each setting and checks its effect.
- Reads by reflection (JSON serialisation): these are saving, not acting.
- Code outside `AgOpenWeb.RemoteWiring`'s references (the platform heads).

## Checked

With the read of `GoalPointAcquireFactor` in `GpsPipelineService.GoalLookAheadFor` removed,
`autosteer.acquireFactor` is listed as read by nothing (the shape of #101); with `ImuAxisSwap`'s bit
removed from `AutoSteerConfig.GetSetting1Byte`, `autosteer.imuAxisSwap` is (27 Nov 2026).
