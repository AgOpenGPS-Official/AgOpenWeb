# IMU Heading Wiring Fix

Branch: `fix/imu-heading-wiring`

## Problem

The IMU/GPS heading-fusion slider in `GpsSubTab.axaml` is user-visible and persists `Connections.HeadingFusionWeight`, but the runtime fusion is unreachable. Investigation also surfaced a separate parser bug for PANDA-format heading.

Three concrete defects:

1. **PANDA heading is parsed at 10× the real value.** The AiO firmware (`AiO_New_Dawn/lib/aio_navigation/NAVProcessor.cpp:157`) emits PANDA field 12 as `(int)(heading * 10.0)` — e.g. 90.5° → `"905"`. AgOpenGPS divides by 0.1 on receive (`UDPComm.Designer.cs:137`). `NmeaParserServiceFast.ParsePandaFieldsIntoState` parses field 12 as a raw double with no scaling, so `_state.Heading = 905.0` for a real heading of 90.5°. PAOGI sends field 12 as a float without scaling, so PAOGI is correct.

2. **No "no IMU" sentinel detection.** AiO sends the literal string `"65535"` in PANDA field 12 when no IMU is present. AgValoniaGPS parses that as `_state.Heading = 65535.0`.

3. **The fusion slider does nothing.** `GpsHeadingFusionService.FuseHeading` gates the IMU blend on `SensorState.Instance.HasValidImu`, which compares `ImuHeading != 99999`. Nothing in production code writes `SensorState.Instance.ImuHeading` (verified across all branches via `git log -S`). So `HasValidImu` is always false and the blend never runs.

The 10× heading bug is masked at speed because `FuseHeading` overrides the value with fix-to-fix above `MinGpsStep`. It only manifests at low speed / standstill — exactly when IMU heading would matter.

None of these are Phase B regressions: the deleted `NmeaParserService` had the same no-scaling, no-sentinel parse, and `SensorState.ImuHeading` has never had a production writer.

## Wire-format reference

From `Firmware_Teensy_AiO_26/lib/aio_navigation/NAVProcessor.cpp`, `IMUProcessor.cpp`, and AgOpenGPS `UDPComm.Designer.cs:133-160`:

| Field | PANDA emits | PAOGI emits | Receive convention |
|---|---|---|---|
| 12 (heading) | `(int)(heading*10)`, sentinel `"65535"` | `dualHeading` float, 1 decimal | PANDA: `* 0.1`. PAOGI: as-is. |
| 13 (roll) | `(int)round(currentData.roll)`, source pre-multiplied — see below | `dualRoll` float, 2 decimals | PANDA: `* 0.1`. PAOGI: as-is. |
| 14 (pitch) | `(int)round(pitch)` | int via `round(insPitch)` or `round(imuPitch)` | as-is |
| 15 (yawRate) | `%.2f` float | `yawRate` float, 2 decimals | as-is |

**Roll wire format clarified.** It looked at first like PANDA roll was raw integer degrees because `NAVProcessor.cpp:158` does `(int)round(imuData.roll)` — no ×10. But `IMUProcessor.cpp:255-303` populates `currentData.roll = 10.0f * bnoParser->getRoll()` (and similar for TM171), so the value sitting in `currentData.roll` is already `degrees * 10` despite the misleading `// degrees` comment in `IMUProcessor.h:27`. Net wire format for PANDA roll: same ×10 integer convention as heading. AgOpenGPS's `* 0.1` decode is correct. PR #294's `data.ImuRoll = _state.Roll` is operating on values 10× too large against real firmware — its test passes only because `VirtualGpsReceiver.cs:138` emits roll as float `"5.70"`, not the canonical scaled-int. Both heading and roll need `* 0.1` in the parser; both fixture encoders need to scale.

PGN 211 (binary IMU PGN, `IMUProcessor.cpp:464-498`) over-scales roll by another factor of 10 (`(int16_t)(currentData.roll * 10)` on top of the already-pre-multiplied source). That's a separate firmware bug; AgValoniaGPS doesn't consume PGN 211, so out of scope.

## Goals

- PANDA field 12 parses to correct degrees (÷10).
- "No IMU" sentinel (65535) maps to "IMU heading invalid" instead of leaking through as a heading value.
- IMU heading flows through `GpsData` (mirror of PR #294's roll fix), not through `SensorState`.
- `GpsHeadingFusionService` reads IMU heading from the data parameter; the `SensorState` read goes away.
- Slider in `GpsSubTab` becomes functional for single-antenna PANDA + IMU users.
- Existing PAOGI behavior unchanged.

## Non-goals

- Pitch / yaw rate scaling. PANDA pitch is rounded integer degrees (1° resolution baked into wire format); yaw rate is float. Neither needs scaling and neither is currently consumed by guidance — leave alone.
- External IMU module (PGN 211 / 0xD3) handler. AgValoniaGPS doesn't have one and the AiO ecosystem packs IMU into PANDA anyway.
- Firmware patches. PGN 211's roll over-scaling and any related `IMUProcessor` cleanups are upstream concerns.
- Removing `SensorState` entirely. After this PR, `GpsService.cs:133` is the only remaining `SensorState.Instance.ImuRoll` reader, and it lives inside the now-dead `TransformAntennaToPivot` body (already early-returns for the production E/N=0 path). Cleaning that up is a separate trivial PR.

## Architecture

The intent of `HeadingFusionWeight` is to blend two sources for the *single-antenna* case:
- **IMU heading** — what the AiO IMU reports, available in PANDA field 12.
- **Fix-to-fix heading** — computed by `GpsHeadingFusionService` from successive Easting/Northing positions.

Currently both inputs collapse into `_state.Heading` / `pos.Heading`, so the blend has nothing to mix. Fix:

1. **Parser stage** — `NmeaParserServiceFast.ParsePandaFieldsIntoState` distinguishes PANDA from PAOGI. The dispatcher already detects sentence type at line 317; pass it in (or split into two methods):
   - **PANDA**: parse field 12 as int. If `== 65535` → set `state.ImuValid = false`, leave `state.Heading = 0` and `state.ImuHeading = 0`. If valid → `state.ImuHeading = value * 0.1`, `state.ImuValid = true`. For consistency seed `state.Heading = state.ImuHeading` so first-cycle / standstill has a sensible default; pipeline's fix-to-fix overrides at any real speed. Field 13 (roll) `* 0.1` when `ImuValid`, else 0. Field 14 (pitch) raw int. Field 15 (yawRate) raw float.
   - **PAOGI**: field 12 → `state.Heading` directly. Field 13 (roll) → `state.Roll` directly (already float). `state.ImuValid` stays false — dual antenna is ground truth, no fusion needed; `state.ImuHeading` left at 0.

2. **State** — add `double ImuHeading` to `VehicleState` (struct, `Shared/AgValoniaGPS.Models/VehicleState.cs`). Reuse the existing `ImuValid` flag — PANDA parser sets it based on the 65535 sentinel.

3. **DTO** — add `double ImuHeading` and `bool ImuValid` to `GpsData` (parallel to PR #294's `ImuRoll`).

4. **Publish** — `AutoSteerService.PublishGpsData` forwards `_state.ImuHeading` and `_state.ImuValid`.

5. **Fusion** — `GpsHeadingFusionService.FuseHeading` signature changes:
   ```csharp
   double FuseHeading(double rawHeading, double imuHeading, bool imuValid,
                      double speedMs, double easting, double northing);
   ```
   Logic: compute fix-to-fix as before. Then if `imuValid && fusionWeight in (0,1)`, blend `fixToFix` with `imuHeading` using the weight (GPS weight = `fusionWeight`, IMU weight = `1 - fusionWeight`). Drop `SensorState.Instance.HasValidImu` and `.ImuHeading` reads.

6. **Pipeline** — `GpsPipelineService.ProcessCycle` passes `data.ImuHeading` and `data.ImuValid` to `_headingFusion.FuseHeading`.

7. **Tests** — three buckets:
   - `NmeaParserServiceFastTests`: PANDA heading scaling (`"905"` → 90.5° in `state.ImuHeading`), PANDA roll scaling (`"57"` → 5.7° in `state.Roll`), PANDA 65535 sentinel → `state.ImuValid = false`, PAOGI unchanged (`"90.5"` → 90.5° in `state.Heading`).
   - `GpsHeadingFusionServiceTests`: blend at 0/50/100% weight with valid IMU; IMU branch skipped when invalid.
   - `VirtualGpsReceiver`: PANDA emit changes to `(int)(heading * 10)` and `(int)(roll * 10)`. PAOGI emit stays float. The existing `Roll10Deg_ShiftsLaterally_ViaPipeline` test uses PANDA via the fixture; once both ends scale by the same factor, the round-trip math comes out the same as before — assertion target unchanged.

## Phasing

Single PR. Order of edits:

1. Add `ImuHeading` + `ImuHeadingValid` to `VehicleState`, `GpsData`. Build.
2. `AutoSteerService.PublishGpsData` forwards new fields. Build.
3. `NmeaParserServiceFast`: split PANDA/PAOGI parsing of field 12; sentinel + scaling. Add unit tests.
4. `GpsHeadingFusionService.FuseHeading`: new signature; pipeline call site updated. Drop `SensorState` reads. Update tests.
5. Update `VirtualGpsReceiver` PANDA encoder to AiO format. Run integration tests, fix any fallout.
6. Run full `dotnet test Tests/`. Manual smoke test with simulator (PAOGI path).

## Risks

- **Behavior change for PAOGI users.** None — PAOGI gets `ImuValid = false`, fusion blend stays skipped (same as before). Heading still flows through field 12 as float decimal degrees.

- **Behavior change for PANDA + IMU users.** Currently they get a 10× heading and 10× roll at low speed (masked at speed by fix-to-fix override and by guidance algorithms that mostly don't care about roll magnitude beyond sign and ratio). After the fix, both arrive in correct degrees, and the slider actually blends fix-to-fix with IMU heading. If anyone tuned slider position around the broken behavior, their preferred value may shift. Acceptable: the broken state isn't a feature.

- **Fix-to-fix at standstill.** When stationary, fix-to-fix has stale data. Blend should still produce a stable output (= IMU heading when fix-to-fix is unavailable). Add a test for `speedMs < MinGpsStep`.

- **PR #294's roll correction shrinks 10×.** Before this PR, `data.ImuRoll` was 10× the real roll (lossless wrong direction — test passed because the test fixture also didn't scale). After this PR, `data.ImuRoll` is real degrees (correct). Roll-correction lateral shift will be 1/10 of what it was on real hardware — but that 1/10 number is the actually-correct correction. Worth re-running the simulator-with-roll smoke test described in PR #294's test plan.
