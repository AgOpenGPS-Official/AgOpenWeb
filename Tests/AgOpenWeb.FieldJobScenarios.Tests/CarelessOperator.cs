// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.FieldJobScenarios.Tests;

/// <summary>
/// The careless operator: the careful operator's Olival job (the Case Puma, the 8 m sprayer,
/// AB line at 159°, headland 16 m) with one thing wrong in the settings, or one thing done at
/// the wrong moment while AgOpenWeb drives. What the operator's own doing must cost is listed
/// in <see cref="JobSpec.Expected"/> and noted, not counted; anything else is AgOpenWeb's.
/// </summary>
public static class CarelessOperator
{
    private static JobSpec Olival(string code, string title, string story) =>
        new(code, title, story, "Olival", "AB 159 deg", Machines.Puma, Machines.Sprayer8, CarefulOperator.Puma);

    /// <summary>Fires <paramref name="act"/> once, <paramref name="secondsAfter"/> seconds after
    /// the end of U-turn number <paramref name="turn"/> (0 = the start of the job).</summary>
    private static Func<JobRun, Task> AfterTurn(int turn, double secondsAfter, Func<JobRun, Task> act)
    {
        int ended = -1; bool fired = false; bool seen = turn == 0;
        if (turn == 0) ended = 0;
        return async run =>
        {
            if (run.InTurn && run.TurnsStarted == turn) seen = true;
            if (seen && ended < 0 && !run.InTurn && run.TurnsStarted == turn) ended = run.Fix;
            if (!fired && ended >= 0 && run.Fix - ended == (int)Math.Round(secondsAfter * 10))
            {
                fired = true;
                await act(run);
            }
        };
    }

    public static IEnumerable<JobSpec> Jobs()
    {
        // ---- things done at the wrong moment
        {
            bool off = false, back = false;
            yield return Olival("C1", "Master switch off during the first U-turn, on again after it",
                "The operator knocks the master switch off as the first U-turn starts and puts it back once the tractor is on the next pass; the sections must come back on their own on the new pass.") with
            {
                OnFix = async run =>
                {
                    if (!off && run.InTurn && run.TurnsStarted == 1) { off = true; await run.SendAsync("section.master"); run.Did("master switch OFF (turn 1 starting)"); }
                    if (off && !back && !run.InTurn && run.TurnsStarted == 1) { back = true; await run.SendAsync("section.master"); run.Did("master switch ON again (on the next pass)"); }
                },
            };
        }
        {
            int lostAt = -1; bool masterFixed = false;
            var lose = AfterTurn(1, 30, run => { run.GpsOn = false; lostAt = run.Fix; run.Did("GPS lost for 5 s in the middle of the second pass"); return Task.CompletedTask; });
            yield return Olival("C2", "GPS lost for 5 s in the middle of a pass",
                "The receiver sends nothing for 5 s on the second pass; AgOpenWeb drops AutoSteer and the sections, the operator engages AutoSteer again 2 s after it comes back and sets the master on.") with
            {
                Expected = new[] { "AUTOSTEER-DROPPED", "MISSED-STRIPS" },
                OnFix = async run =>
                {
                    await lose(run);
                    if (lostAt >= 0 && run.Fix - lostAt == 50) { run.GpsOn = true; run.Did("GPS back"); }
                    if (lostAt >= 0 && !masterFixed && run.Fix - lostAt == 90)
                    {
                        masterFixed = true;
                        if (!await run.Rig.ReadAsync(() => run.Rig.Vm.IsSectionMasterOn)) { await run.SendAsync("section.master"); run.Did("master switch ON again"); }
                    }
                },
            };
        }
        {
            int at = -1;
            var rev = AfterTurn(1, 30, run => { run.Truth.SpeedKmh = -3; at = run.Fix; run.Did("reversing 10 m in the middle of the second pass"); return Task.CompletedTask; });
            yield return Olival("C3", "Reversing 10 m in the middle of a pass",
                "On the second pass the operator stops and backs up 10 m at 3 km/h, then drives on; AgOpenWeb must keep the line, not spray the same ground twice, and not drop AutoSteer.") with
            {
                OnFix = async run =>
                {
                    await rev(run);
                    if (at >= 0 && run.Fix - at == 120) { run.Truth.SpeedKmh = run.Spec.SpeedKmh; run.Did("forward again"); }
                },
            };
        }
        yield return Olival("C4", "Track nudged 50 cm right in the middle of a pass",
            "On the second pass the operator nudges the line 50 cm to the right; the rest of that pass and all the following ones shift, so a 50 cm strip must be left on its left side, and nothing else.") with
        {
            Extra = new[] { "autosteer.nudgeDistance:50" },
            Expected = new[] { "MISSED-STRIPS" },
            OnFix = AfterTurn(1, 30, async run => { await run.SendAsync("track.nudgeRight"); run.Did("nudged the track 50 cm right"); }),
        };
        {
            bool on = false, offAgain = false;
            yield return Olival("C5", "Skip rows toggled in the middle of a U-turn",
                "As the second U-turn starts the operator taps 'skip rows' on (1 row), and taps it off again once on the next pass; the turn in progress must not change under the tractor.") with
            {
                OnFix = async run =>
                {
                    if (!on && run.InTurn && run.TurnsStarted == 2) { on = true; await run.SendAsync("youturn.skipToggle"); run.Did("skip rows ON (turn 2 in progress)"); }
                    if (on && !offAgain && !run.InTurn && run.TurnsStarted == 2) { offAgain = true; await run.SendAsync("youturn.skipToggle"); run.Did("skip rows OFF again"); }
                },
            };
        }
        {
            int closedAt = -1;
            var close = AfterTurn(2, 20, async run =>
            {
                run.Truth.SpeedKmh = 0; closedAt = run.Fix;
                await run.SendAsync("field.close"); run.Did("stopped and closed the field in the middle of the third pass");
            });
            yield return Olival("C6", "Field closed in the middle of a pass and resumed",
                "On the third pass the operator stops, closes the field, opens it again with 'resume last job', engages AutoSteer and the master, and drives on; the coverage so far and the pass numbering must survive.") with
            {
                Expected = new[] { "AUTOSTEER-DROPPED" },
                OnFix = async run =>
                {
                    await close(run);
                    if (closedAt >= 0 && run.Fix - closedAt == 30) { await run.SendAsync("field.resumeLast"); run.Did("resume last job"); }
                    if (closedAt >= 0 && run.Fix - closedAt == 60)
                    {
                        run.Truth.SpeedKmh = run.Spec.SpeedKmh;
                        if (!await run.Rig.ReadAsync(() => run.Rig.Vm.IsAutoSteerEngaged)) await run.SendAsync("autosteer.toggle");
                        if (!await run.Rig.ReadAsync(() => run.Rig.Vm.IsSectionMasterOn)) await run.SendAsync("section.master");
                        if (!await run.Rig.ReadAsync(() => run.Rig.Vm.IsYouTurnEnabled)) await run.SendAsync("youturn.toggle");
                        run.Did("AutoSteer and master on again, driving on");
                    }
                },
            };
        }
        {
            int at = -1;
            var drop = AfterTurn(1, 30, run => { run.Module.NetworkUp = false; at = run.Fix; run.Did("module Wi-Fi down for 30 s in the middle of the second pass"); return Task.CompletedTask; });
            yield return Olival("C7", "Module Wi-Fi down for 30 s in the middle of a pass",
                "The machine module loses the network for 30 s on the second pass: its valves shut 2 s later; what does the screen show meanwhile, and what does the map record?") with
            {
                Expected = new[] { "MISSED-STRIPS", "VALVES-NOT-FOLLOWING", "MAP-SHOWS-UNSPRAYED" },
                OnFix = async run =>
                {
                    await drop(run);
                    if (at >= 0 && run.Fix - at == 300) { run.Module.NetworkUp = true; run.Did("module Wi-Fi back"); }
                },
            };
        }

        // ---- things wrong in the settings
        yield return Olival("C8", "Look-ahead 2 s on, 1.5 s off",
            "The operator sets a slow valve's look-ahead: sections open 2 s before the ground and close 1.5 s before the headland; at 9 km/h the valves must now shut ahead of the headland line instead of 1 m past it.") with
        {
            Extra = new[] { "tool.lookAheadOn:2", "tool.lookAheadOff:1.5" },
        };
        yield return Olival("C9", "Relay functions left at the defaults (relays 1-6)",
            "The operator never opened the machine module's pin table: relays 1-6 drive sections 1-6 and sections 7 and 8 have no relay, so the right 2 m of the boom never spray.") with
        {
            DefaultRelays = true,
            Expected = new[] { "SECTION-WITHOUT-RELAY", "MISSED-STRIPS", "VALVES-NOT-FOLLOWING", "MAP-SHOWS-UNSPRAYED", "APP-AREA-OFF" },
        };
        yield return Olival("C10", "No headland section control, 'Off outside boundary' at AgOpenWeb's default (off)",
            "The operator sprays the headland on the way (section control in the headland off) and leaves 'Off outside boundary' off, as AgOpenWeb installs it: how much goes over the fence in the U-turns.") with
        {
            HeadlandSections = false,
            OffWhenOut = false,
            Expected = new[] { "SPRAYED-IN-HEADLAND", "SPRAYED-OUTSIDE-BOUNDARY" },
        };
        yield return Olival("C11", "Headland 8 m, narrower than the 8 m U-turn radius needs",
            "A headland of 8 m with a turn radius of 8 m: the turn's arc needs 16 m plus the 2 m from the boundary, so it must reach into the cultivated area.") with
        {
            HeadlandM = 8,
        };
        yield return Olival("C12", "U-turn radius 4 m, tighter than the tractor turns",
            "The operator sets a 4 m turn radius for a tractor whose tightest turn is about 3.5 m at a standstill; at 9 km/h with the steering's lag it cannot follow the arc.") with
        {
            UTurnRadius = 4,
        };
        yield return Olival("C13", "Sprayer typed in as 6 m (8 x 75 cm) for an 8 m boom",
            "The sections are typed 75 cm wide for a boom whose sections are really 1 m: AgOpenWeb lays the passes 6 m apart and 2 m of every pass is sprayed twice; nothing can tell it, but the map must not hide it either.") with
        {
            Extra = Enumerable.Range(0, 8).Select(s => $"tool.sectionWidth:{s},75").ToArray(),
            Expected = new[] { "DOUBLE-SPRAYED", "APP-AREA-OFF", "SPRAYED-NOT-ON-MAP" },
        };
        yield return Olival("C14", "Skip one row at every U-turn",
            "The operator works the field in the alternate pattern (skip 1 row, come back for the skipped ones): a longer job that must still end with every pass worked and the field reported done.") with
        {
            OnFix = AfterTurn(0, 5, async run => { await run.SendAsync("youturn.skipToggle"); run.Did("skip rows ON (1 row) before the first turn"); }),
        };
        yield return new JobSpec("C15", "Quinta Torta, 24 m sprayer in 48 sections of 50 cm",
            "A wide boom with more than 16 sections (PGN 229 carries 17-48): AgOpenWeb, the module and the valves must agree on all 48, on the biggest field, headland 36 m.",
            "Quinta Torta", "AB 0°", Machines.Puma, Machines.Sprayer24x48, CarefulOperator.Puma) { HeadlandM = 36 };
    }
}
