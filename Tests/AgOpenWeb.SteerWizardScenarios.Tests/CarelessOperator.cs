// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace AgOpenWeb.SteerWizardScenarios.Tests;

/// <summary>
/// The operator who skips steps, types nonsense, holds the wheel, presses buttons twice,
/// goes Back or Cancel in the middle of a test, loses the browser, and drives the circles
/// badly. Each story first does the steps before it the careful way (one line in the log),
/// then does one thing wrong and checks what the wizard did about it: refused and said why
/// (fine), or let it through (a finding), or moved the wheels when nobody asked (Safety).
/// </summary>
public static class CarelessOperator
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static IEnumerable<(string Name, Func<ScenarioJournal, string, Task> Run)> All()
    {
        yield return ("Careless: Next, Next, Next… Finish (sensor mounted the other way)", NextNextFinishAsync);
        yield return ("Careless: nonsense numbers", NonsenseNumbersAsync);
        yield return ("Careless: Zero WAS with the wheels turned, twice", ZeroWasSloppyAsync);
        yield return ("Careless: Zero Roll on a slope, twice", ZeroRollSloppyAsync);
        yield return ("Careless: motor wired backwards (careful otherwise)", MotorBackwardsAsync);
        yield return ("Careless: motor wired backwards, wheel left turned 12° right", MotorBackwardsWheelTurnedAsync);
        yield return ("Careless: holds the wheel during the motor test", HoldsWheelAsync);
        yield return ("Careless: Back in the middle of the motor test", BackMidTestAsync);
        yield return ("Careless: ✕ in the middle of the max-angle test", CancelMidMaxAngleAsync);
        yield return ("Careless: steer switch off in the middle of the motor test", SwitchOffMidTestAsync);
        yield return ("Careless: motor backwards, motor test skipped, max-angle test", MaxAngleUntestedMotorAsync);
        yield return ("Careless: browser lost in the middle of the motor test, page reloaded", BrowserLostMidTestAsync);
        yield return ("Careless: Start pressed three times", SpamStartAsync);
        yield return ("Careless: AutoSteer button while the wizard is open", AutoSteerDuringWizardAsync);
        yield return ("Careless: ✕ while the CPD circle is recording", CancelDuringCpdAsync);
        yield return ("Careless: wizard opened again while the CPD circle is recording", ReopenDuringCpdAsync);
        yield return ("Careless: CPD circle, stops after a quarter", CpdStopsEarlyAsync);
        yield return ("Careless: CPD circle, straightens the wheel at the end", CpdStraightensAsync);
        yield return ("Careless: CPD circle turning left", CpdLeftAsync);
        yield return ("Careless: Ackermann circle turning right", AckermannRightAsync);
        yield return ("Careless: Ackermann circle, straightens the wheel after Record", AckermannStraightensAsync);
        yield return ("Careless: GPS only, then Skip", GpsOnlySkipAsync);
        yield return ("Careless: Cancel after the motor test", CancelAfterMotorTestAsync);
    }

    // ------------------------------------------------------------ the careful part before the mistake

    private static readonly string[] Order =
        { "welcome", "vehicleType", "hardware", "dimensions", "antenna", "hwconfig", "roll", "was", "motor", "maxangle", "cpd", "ackermann", "gains", "speed", "finish" };

    /// <summary>Open the wizard and do every step before <paramref name="kind"/> the careful way,
    /// knowing the tractor's truth (sensor and motor direction). One log line.</summary>
    private static Task PrepareAsync(WizardScenario s, string kind) =>
        s.StepAsync("Careful set-up up to the " + kind + " step", async () =>
        {
            var m = s.Module;
            var rig = s.Rig;
            await s.OpenAsync();
            int stop = Array.IndexOf(Order, kind);
            for (int i = 0; i < stop; i++)
            {
                switch (Order[i])
                {
                    case "vehicleType": await s.SetAsync("vehicle.type", "0"); break;
                    case "hardware": await s.HardwareAsync(1); break;
                    case "dimensions":
                        await s.SetAsync("vehicle.wheelbase", rig.TruthWheelbase);
                        await s.SetAsync("vehicle.trackWidth", rig.TruthTrackWidth);
                        break;
                    case "antenna":
                        await s.SetAsync("vehicle.antennaPivot", rig.TruthAntennaAheadOfRearAxle);
                        await s.SetAsync("vehicle.antennaHeight", 3.2);
                        break;
                    case "hwconfig":
                        await s.SetAsync("autosteer.externalEnable", "1");
                        await s.SetAsync("autosteer.motorDriver", "1");
                        break;
                    case "roll":
                        m.TruthRollDeg = 0;
                        await s.SettleAsync(400);
                        await s.ActAsync("ZeroRoll");
                        await s.SettleAsync(300);
                        break;
                    case "was":
                        if (m.TruthWasReversed) await s.SetAsync("autosteer.invertWas", "1");
                        s.Hands(0);
                        await s.SettleAsync(500);
                        await s.ActAsync("ZeroWas");
                        await s.SettleAsync(500);
                        s.Hands(null);
                        break;
                    case "motor":
                        await s.ActAsync("StartTest");
                        await s.WaitTestEndAsync();
                        break;
                    case "maxangle":
                        await s.ActAsync("StartTest");
                        await s.WaitTestEndAsync();
                        break;
                    case "cpd":
                        await CircleQuietAsync(s, +20);
                        break;
                    case "ackermann":
                        await CircleQuietAsync(s, -20);
                        break;
                }
                if (!await s.NextAsync())
                {
                    s.Rig.Note("set-up: Next did nothing on the " + Order[i] + " step");
                    return;
                }
            }
        });

    private static async Task CircleQuietAsync(WizardScenario s, double wheel)
    {
        s.Hands(wheel);
        s.Drive(5);
        await s.SettleAsync(2500);
        await s.ActAsync("StartRecording");
        await s.WaitResultAsync(90000);
        s.Drive(0);
        await s.SettleAsync(600);
        s.Hands(0);
        await s.SettleAsync(300);
        s.Hands(null);
    }

    private static Task<double> Cpd(WizardScenario s) => s.Rig.ReadAsync(() => s.Rig.Store.AutoSteer.CountsPerDegree);
    private static Task<int> Ack(WizardScenario s) => s.Rig.ReadAsync(() => s.Rig.Store.AutoSteer.Ackermann);
    private static Task<bool> WizardOpen(WizardScenario s) => s.Rig.ReadAsync(() => s.Rig.Vm.IsSteerWizardOpen);
    private static Task<bool> FreeDrive(WizardScenario s) => s.Rig.ReadAsync(() => s.Rig.AutoSteer.IsInFreeDriveMode);

    private static async Task<string> SituationAsync(WizardScenario s)
    {
        bool open = await WizardOpen(s);
        string kind = open ? await s.KindAsync() : "";
        bool fd = await FreeDrive(s);
        return string.Create(Inv, $"wizard {(open ? "open on " + kind : "closed")}, Free Drive {(fd ? "ON" : "off")}, wheels {s.Module.WheelAngleDeg:F1}°");
    }

    /// <summary>The careful finish of a story: the remaining steps with Next, then Finish.</summary>
    private static async Task NextToEndAsync(WizardScenario s)
    {
        for (int i = 0; i < 16 && await WizardOpen(s); i++)
        {
            if (!await s.NextAsync())
            {
                var f = await s.Rig.FrameAsync();
                if (f?.CanSkip == true) await s.SkipAsync(); else break;
            }
        }
    }

    // ------------------------------------------------------------ X1 Next all the way

    private static async Task NextNextFinishAsync(ScenarioJournal j, string name)
    {
        await using var s = await WizardScenario.StartAsync(j, name, "careless", m => m.TruthWasReversed = true);
        await s.StepAsync("Open the wizard", s.OpenAsync);
        await s.StepAsync("Press Next on every step without doing anything, then Finish", async () =>
        {
            int n = 0;
            while (await WizardOpen(s) && n++ < 20)
            {
                var kind = await s.KindAsync();
                if (!await s.NextAsync()) { s.Rig.Note("Next did nothing on " + kind); break; }
            }
            bool stillOpen = await WizardOpen(s);
            s.Rig.Note(string.Create(Inv, $"wizard {(stillOpen ? "still open" : "finished")} after {n} presses"));
        }, Expect.Any);
        await s.StepAsync("What the wizard let through", async () =>
        {
            var a = s.Rig.Store.AutoSteer;
            await s.CheckAsync(!(!await WizardOpen(s) && a.InvertWas != s.Module.TruthWasReversed),
                new Finding(Severity.Improvement, "FINISH_WITH_NO_TEST",
                    "The wizard finishes with no step tested (WAS direction, motor, max angle, CPD, Ackermann all skipped by Next) and says nothing about it: the steer sensor here is mounted the other way and nothing caught it."));
        }, Expect.Any);
        await CarefulOperator.DriveAbLineAsync(s, "after Next-Next-Finish with the sensor mounted the other way");
    }

    // ------------------------------------------------------------ X2 nonsense numbers

    private static async Task NonsenseNumbersAsync(ScenarioJournal j, string name)
    {
        await using var s = await WizardScenario.StartAsync(j, name, "careless");
        await PrepareAsync(s, "dimensions");
        // A nonsense value is refused either way: at the box (config.set says why and keeps the
        // old value: Next then goes on with that one) or at Next (the step's check). The
        // finding is Next going on with the nonsense in the live settings.
        async Task<bool> WentOnWith(Func<bool> nonsense) => await s.NextAsync() && await s.Rig.ReadAsync(nonsense);
        var live = s.Rig.Store;
        await s.StepAsync("Types wheelbase 0 m → Next", async () =>
        {
            await s.SetAsync("vehicle.wheelbase", "0");
            await s.SettleAsync(200);
            if (await WentOnWith(() => live.Vehicle.Wheelbase < 0.5))
                s.Report(new Finding(Severity.Bug, "NONSENSE_ACCEPTED",
                    "The Dimensions step went on with a wheelbase of 0 m: its check (0.5–15 m) reads the step's own copy, taken when the step opened, and the web types into the live settings."));
        }, Expect.Refused);
        await s.StepAsync("Sees 0 m, goes Back, types the right 2.88 m → Next", async () =>
        {
            if ((await s.KindAsync()) != "dimensions") await s.BackAsync();
            await s.SetAsync("vehicle.wheelbase", s.Rig.TruthWheelbase);
            await s.SettleAsync(200);
            if (!await s.NextAsync())
            {
                var f = await s.Rig.FrameAsync();
                s.Report(new Finding(Severity.Bug, "VALIDATION_STALE",
                    "The right wheelbase (2.88 m) is refused with \"" + (f?.Validation ?? "") + "\": the step checks the 0 m it copied when it opened. The operator is stuck until they go Back and Next again (and the web never shows this message either)."));
                await s.BackAsync();
                await s.NextAsync();
                await s.NextAsync();
            }
        });
        await s.StepAsync("Types track width 0 m and wheelbase 99 m → Next", async () =>
        {
            if ((await s.KindAsync()) != "dimensions") await s.BackAsync();
            await s.SetAsync("vehicle.trackWidth", "0");
            await s.SetAsync("vehicle.wheelbase", "99");
            await s.SettleAsync(200);
            if (await WentOnWith(() => live.Vehicle.TrackWidth < 0.2 || live.Vehicle.Wheelbase > 20))
                s.Report(new Finding(Severity.Bug, "NONSENSE_ACCEPTED",
                    "The Dimensions step went on with a track width of 0 m and a wheelbase of 99 m."));
        }, Expect.Refused);
        await s.StepAsync("Fixes the dimensions (Back, type, Next)", async () =>
        {
            if ((await s.KindAsync()) != "dimensions") await s.BackAsync();
            await s.SetAsync("vehicle.wheelbase", s.Rig.TruthWheelbase);
            await s.SetAsync("vehicle.trackWidth", s.Rig.TruthTrackWidth);
            if (!await s.NextAsync()) { await s.BackAsync(); await s.NextAsync(); await s.NextAsync(); }
        });
        await s.StepAsync("Antenna height -1 m → Next", async () =>
        {
            await s.SetAsync("vehicle.antennaHeight", "-1");
            await s.SettleAsync(200);
            if (await WentOnWith(() => live.Vehicle.AntennaHeight < 0))
                s.Report(new Finding(Severity.Inconsistency, "NONSENSE_ACCEPTED_ANTENNA",
                    "The Antenna step went on with an antenna height of -1 m."));
            await s.SetAsync("vehicle.antennaHeight", "3.2");
        }, Expect.Refused);
        await s.StepAsync("Goes on to the gains step (Next past the tests)", async () => await s.GoToAsync("gains"));
        foreach (var (key, val, what, taken) in new (string, string, string, Func<bool>)[]
                 {
                     ("autosteer.proportionalGain", "0", "Kp 0", () => live.AutoSteer.ProportionalGain < 1),
                     ("autosteer.proportionalGain", "999", "Kp 999", () => live.AutoSteer.ProportionalGain > 200),
                     ("autosteer.integralGain", "9", "Ki 9", () => live.Guidance.PurePursuitIntegralGain > 1),
                 })
        {
            await s.StepAsync("Types " + what + " → Next", async () =>
            {
                await s.SetAsync(key, val);
                await s.SettleAsync(250);
                if (await WentOnWith(taken))
                {
                    s.Report(new Finding(Severity.Bug, "NONSENSE_ACCEPTED_GAINS", "The Gains step went on with " + what + "."));
                    await s.BackAsync();
                }
            }, Expect.Refused);
        }
        await s.StepAsync("Fixes Kp 50 / Ki 0 → Next to speed", async () =>
        {
            await s.SetAsync("autosteer.proportionalGain", "50");
            await s.SetAsync("autosteer.integralGain", "0");
            await s.NextAsync();
        });
        await s.StepAsync("Min steer speed 25 km/h, max 5 km/h → Next", async () =>
        {
            await s.SetAsync("autosteer.minSteerSpeed", "25");
            await s.SetAsync("autosteer.maxSteerSpeed", "5");
            await s.SettleAsync(250);
            if (await WentOnWith(() => live.AutoSteer.MinSteerSpeed >= live.AutoSteer.MaxSteerSpeed))
                s.Report(new Finding(Severity.Bug, "NONSENSE_ACCEPTED_SPEED", "The Speed step went on with min speed above max speed."));
        }, Expect.Refused);
        await s.StepAsync("Finish with whatever is there", async () =>
        {
            if ((await s.KindAsync()) != "finish") await s.GoToAsync("finish");
            await s.FinishAsync();
            await s.SettleAsync(300);
            if (await WizardOpen(s))
                s.Report(new Finding(Severity.Bug, "FINISH_REFUSED_OVERLAY_CLOSED",
                    "Finish was refused (a step's check failed) but the web closed the wizard anyway: the host still has it open, nothing shows it, and AutoSteer refuses to engage."));
        }, Expect.Any);
    }

    // ------------------------------------------------------------ X3 Zero WAS sloppy

    private static async Task ZeroWasSloppyAsync(ScenarioJournal j, string name)
    {
        await using var s = await WizardScenario.StartAsync(j, name, "careless");
        await PrepareAsync(s, "was");
        await s.StepAsync("Wheels turned 15° right, presses Zero WAS", async () =>
        {
            s.Hands(15);
            await s.SettleAsync(500);
            await s.ActAsync("ZeroWas");
            await s.SettleAsync(500);
            double straightReads;
            s.Hands(0);
            await s.SettleAsync(400);
            straightReads = s.Module.ReportedAngleDeg;
            s.Rig.Note(string.Create(Inv, $"straight ahead now reads {straightReads:F1}°"));
            if (Math.Abs(straightReads) > 5)
                s.Report(new Finding(Severity.Bug, "ZERO_WAS_ACCEPTS_TURNED_WHEELS",
                    string.Create(Inv, $"Zero WAS took the centre with the wheels turned 15°: straight ahead now reads {straightReads:F1}° and AutoSteer would hold the wheels {Math.Abs(straightReads):F0}° off. No warning (a centre this far from the sensor's middle is suspicious).")));
        }, Expect.Refused);
        await s.StepAsync("Wheels straight, presses Zero WAS twice quickly", async () =>
        {
            s.Hands(0);
            await s.SettleAsync(500);
            await s.ActAsync("ZeroWas");
            await s.ActAsync("ZeroWas");
            await s.SettleAsync(600);
            double reads = s.Module.ReportedAngleDeg;
            s.Rig.Note(string.Create(Inv, $"straight ahead reads {reads:F1}° after the double tap"));
            if (Math.Abs(reads) > 1)
                s.Report(new Finding(Severity.Bug, "ZERO_WAS_DOUBLE_TAP",
                    string.Create(Inv, $"Two quick taps on Zero WAS (the second before the module answered) leave straight ahead reading {reads:F1}°.")));
        });
        await s.StepAsync("Module unplugged, presses Zero WAS", async () =>
        {
            s.Module.Online = false;
            await s.SettleAsync(2500);
            int before = s.Rig.Store.AutoSteer.WasOffset;
            await s.ActAsync("ZeroWas");
            await s.SettleAsync(400);
            int after = s.Rig.Store.AutoSteer.WasOffset;
            var f = await s.Rig.FrameAsync();
            s.Rig.Note(string.Create(Inv, $"offset {before} → {after}"));
            if (after != before)
                s.Report(new Finding(Severity.Bug, "ZERO_WAS_OFFLINE", "Zero WAS changed the offset with the module offline (from a stale reading)."));
            else if ((f?.TestResult ?? "").Length == 0 && (f?.Validation ?? "").Length == 0)
                s.Report(new Finding(Severity.Improvement, "ZERO_WAS_SILENT",
                    "Zero WAS with the module offline does nothing and says nothing: the operator thinks the centre is set."));
            s.Module.Online = true;
            await s.SettleAsync(1500);
        }, Expect.Refused);
        await s.StepAsync("Turns Invert WAS on after zeroing (sensor is normal)", async () =>
        {
            await s.SetAsync("autosteer.invertWas", "1");
            await s.SettleAsync(500);
            s.Hands(10);
            await s.SettleAsync(400);
            double r = s.Module.ReportedAngleDeg;
            s.Hands(0);
            await s.SettleAsync(400);
            double z = s.Module.ReportedAngleDeg;
            s.Hands(null);
            s.Rig.Note(string.Create(Inv, $"10° right reads {r:F1}°, straight reads {z:F1}°"));
            if (Math.Abs(z) > 2)
                s.Report(new Finding(Severity.Inconsistency, "INVERT_AFTER_ZERO_MOVES_CENTRE",
                    string.Create(Inv, $"Turning Invert WAS on after Zero WAS moves the centre: straight ahead reads {z:F1}° (the firmware subtracts the offset when inverted, so the zero must be done again; the step doesn't say so).")));
            await s.SetAsync("autosteer.invertWas", "0");
            await s.SettleAsync(400);
        }, Expect.Any);
        await s.StepAsync("Leaves with the wheels turned 15° zeroed → Next", async () =>
        {
            s.Hands(15);
            await s.SettleAsync(500);
            await s.ActAsync("ZeroWas");
            await s.SettleAsync(500);
            s.Hands(null);
            await s.NextAsync();
        }, Expect.Any);
    }

    // ------------------------------------------------------------ X4 Zero Roll sloppy

    private static async Task ZeroRollSloppyAsync(ScenarioJournal j, string name)
    {
        await using var s = await WizardScenario.StartAsync(j, name, "careless");
        await PrepareAsync(s, "roll");
        await s.StepAsync("Parked on a 9° slope, presses Zero Roll", async () =>
        {
            s.Module.TruthRollDeg = 9;
            await s.SettleAsync(600);
            await s.ActAsync("ZeroRoll");
            await s.SettleAsync(600);
            s.Module.TruthRollDeg = 0;
            await s.SettleAsync(600);
            double level = (await s.Rig.FrameAsync())?.LiveRoll ?? 0;
            s.Rig.Note(string.Create(Inv, $"level ground now reads {level:F1}°"));
            if (Math.Abs(level) > 4)
                s.Report(new Finding(Severity.Bug, "ZERO_ROLL_ACCEPTS_SLOPE",
                    string.Create(Inv, $"Zero Roll on a 9° slope was taken: level ground now reads {level:F1}° and side-hill compensation would steer uphill on the flat. No range check or warning (a roll zero beyond ±5° is almost always a slope).")));
        }, Expect.Refused);
        await s.StepAsync("Level ground, presses Zero Roll twice quickly", async () =>
        {
            s.Module.TruthRollDeg = 0;
            await s.SettleAsync(500);
            await s.ActAsync("ZeroRoll");
            await s.ActAsync("ZeroRoll");
            await s.SettleAsync(700);
            double level = (await s.Rig.FrameAsync())?.LiveRoll ?? 0;
            s.Rig.Note(string.Create(Inv, $"level ground reads {level:F1}° after the double tap"));
            if (Math.Abs(level) > 0.5)
                s.Report(new Finding(Severity.Bug, "ZERO_ROLL_DOUBLE_TAP",
                    string.Create(Inv, $"Two quick taps on Zero Roll leave level ground reading {level:F1}°.")));
        });
    }

    // ------------------------------------------------------------ X5 / X22 motor backwards

    private static async Task MotorBackwardsAsync(ScenarioJournal j, string name)
    {
        await using var s = await WizardScenario.StartAsync(j, name, "careless", m => m.TruthMotorReversed = true);
        await PrepareAsync(s, "motor");
        await MotorTestAsync(s, "Motor test with the motor wired backwards");
        await s.StepAsync("Next, max-angle test", async () =>
        {
            await s.NextAsync();
            await s.ActAsync("StartTest");
            await s.WaitTestEndAsync();
        }, Expect.Any);
        await s.StepAsync("Next to the end, Finish", () => NextToEndAsync(s), Expect.Any);
        await s.StepAsync("Check Invert Motor", async () =>
        {
            var inv = await s.Rig.ReadAsync(() => s.Rig.Store.AutoSteer.InvertMotor);
            await s.CheckAsync(inv == s.Module.TruthMotorReversed,
                new Finding(Severity.Safety, "TRUTH_INVERT_MOTOR", "Invert Motor does not match the motor's wiring after the motor test: AutoSteer would steer away from the line."));
        });
        await CarefulOperator.DriveAbLineAsync(s, "after the motor test found the motor backwards");
    }

    private static async Task MotorBackwardsWheelTurnedAsync(ScenarioJournal j, string name)
    {
        await using var s = await WizardScenario.StartAsync(j, name, "careless", m => m.TruthMotorReversed = true);
        await PrepareAsync(s, "motor");
        await s.StepAsync("Leaves the wheels turned 12° right, hands off", async () =>
        {
            s.Hands(12);
            await s.SettleAsync(400);
            s.Hands(null);
            await s.SettleAsync(200);
        });
        await MotorTestAsync(s, "Motor test, motor backwards, wheels at 12°");
    }

    private static Task MotorTestAsync(WizardScenario s, string what) =>
        s.StepAsync(what + ": Start, wait for the result", async () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            double maxAbs = 0;
            await s.ActAsync("StartTest");
            var waiting = s.WaitTestEndAsync();
            while (!waiting.IsCompleted)
            {
                maxAbs = Math.Max(maxAbs, Math.Abs(s.Module.WheelAngleDeg));
                await Task.Delay(20);
            }
            await waiting;
            s.Rig.Note(string.Create(Inv, $"wheels went up to {maxAbs:F1}° during the test ({sw.ElapsedMilliseconds / 1000.0:F1} s)"));
            if (maxAbs > 25)
                s.Report(new Finding(Severity.Safety, "MOTOR_TEST_SWINGS_TO_LOCK",
                    string.Create(Inv, $"The motor test (a 5° nudge) swung the wheels to {maxAbs:F0}° (the lock is at {s.Module.LockRightDeg:F0}°).")));
        }, Expect.Any);

    // ------------------------------------------------------------ X6 holds the wheel

    private static async Task HoldsWheelAsync(ScenarioJournal j, string name)
    {
        await using var s = await WizardScenario.StartAsync(j, name, "careless");
        await PrepareAsync(s, "motor");
        await s.StepAsync("Holds the steering wheel straight, presses Start", async () =>
        {
            s.Hands(0);
            await s.ActAsync("StartTest");
            await s.WaitTestEndAsync();
        }, Expect.Refused);
        await s.StepAsync("Lets go of the wheel", async () =>
        {
            double before = s.Module.WheelAngleDeg;
            s.Hands(null);
            await s.SettleAsync(1500);
            double after = s.Module.WheelAngleDeg;
            s.Rig.Note(string.Create(Inv, $"wheels {before:F1}° → {after:F1}° after letting go"));
            int kp = await s.Rig.ReadAsync(() => s.Rig.Store.AutoSteer.ProportionalGain);
            await s.CheckAsync(kp == 50, new Finding(Severity.Bug, "KP_NOT_RESTORED", string.Create(Inv, $"Kp is {kp} after the motor test (the operator's was 50).")));
        });
    }

    // ------------------------------------------------------------ X7 Back mid-test

    private static async Task BackMidTestAsync(ScenarioJournal j, string name)
    {
        await using var s = await WizardScenario.StartAsync(j, name, "careless");
        await PrepareAsync(s, "motor");
        await s.StepAsync("Start, then Back after 1.5 s", async () =>
        {
            await s.ActAsync("StartTest");
            await Task.Delay(1500);
            await s.BackAsync();
            await s.SettleAsync(1500);
            int kp = await s.Rig.ReadAsync(() => s.Rig.Store.AutoSteer.ProportionalGain);
            s.Rig.Note(await SituationAsync(s) + string.Create(Inv, $", Kp {kp}"));
            await s.CheckAsync(!await FreeDrive(s), new Finding(Severity.Safety, "BACK_LEAVES_FREE_DRIVE", "Back in the middle of the motor test left Free Drive on."));
            await s.CheckAsync(kp == 50, new Finding(Severity.Bug, "KP_NOT_RESTORED", string.Create(Inv, $"Kp is {kp} after Back in the middle of the motor test (the operator's was 50).")));
        }, Expect.Any);
    }

    // ------------------------------------------------------------ X8 ✕ mid max-angle

    private static async Task CancelMidMaxAngleAsync(ScenarioJournal j, string name)
    {
        await using var s = await WizardScenario.StartAsync(j, name, "careless");
        await PrepareAsync(s, "maxangle");
        await s.StepAsync("Start the max-angle test, ✕ after 1 s", async () =>
        {
            await s.ActAsync("StartTest");
            await Task.Delay(1000);
            await s.CancelAsync();
            await s.SettleAsync(2000);
            s.Rig.Note(await SituationAsync(s));
        }, Expect.Any);
    }

    // ------------------------------------------------------------ X8b switch off

    private static async Task SwitchOffMidTestAsync(ScenarioJournal j, string name)
    {
        await using var s = await WizardScenario.StartAsync(j, name, "careless");
        await PrepareAsync(s, "motor");
        await s.StepAsync("Steer switch off, presses Start", async () =>
        {
            s.Module.SteerSwitchClosed = false;
            await s.SettleAsync(300);
            await s.ActAsync("StartTest");
            await s.WaitTestEndAsync(8000);
        }, Expect.Refused);
        await s.StepAsync("Switch on, Start, switch off after 1.5 s", async () =>
        {
            s.Module.SteerSwitchClosed = true;
            await s.SettleAsync(500);
            await s.ActAsync("StartTest");
            await Task.Delay(1500);
            s.Module.SteerSwitchClosed = false;
            await s.WaitTestEndAsync(15000);
            await s.SettleAsync(300);
            var f = await s.Rig.FrameAsync();
            s.Rig.Note("after the switch went off: " + (f?.TestResult ?? "").Replace("\n", " "));
        }, Expect.Any);
        await s.StepAsync("Switch on again (hands off)", async () =>
        {
            double before = s.Module.WheelAngleDeg;
            s.Module.SteerSwitchClosed = true;
            await s.SettleAsync(1500);
            s.Rig.Note(string.Create(Inv, $"wheels {before:F1}° → {s.Module.WheelAngleDeg:F1}°"));
        }, Expect.Any);
    }

    // ------------------------------------------------------------ X9 max angle untested motor

    private static async Task MaxAngleUntestedMotorAsync(ScenarioJournal j, string name)
    {
        await using var s = await WizardScenario.StartAsync(j, name, "careless", m => m.TruthMotorReversed = true);
        await PrepareAsync(s, "motor");
        await s.StepAsync("Motor step: Next without the test", async () =>
        {
            if (await s.NextAsync())
                s.Report(new Finding(Severity.Improvement, "MOTOR_TEST_SKIPPABLE",
                    "Next leaves the motor step with the test never run: the motor direction is unknown and the next step drives the wheels to full lock."));
        }, Expect.Refused);
        await s.StepAsync("Max-angle test with the motor backwards", async () =>
        {
            await s.ActAsync("StartTest");
            await s.WaitTestEndAsync();
            await s.SettleAsync(500);
        }, Expect.Any);
    }

    // ------------------------------------------------------------ X10 browser lost

    private static async Task BrowserLostMidTestAsync(ScenarioJournal j, string name)
    {
        await using var s = await WizardScenario.StartAsync(j, name, "careless");
        await PrepareAsync(s, "maxangle");
        await s.StepAsync("Start the max-angle test; the tablet's Wi-Fi drops after 0.5 s", async () =>
        {
            await s.ActAsync("StartTest");
            await Task.Delay(500);
            await s.DropControlAsync();
            await s.WaitTestEndAsync(15000);
            s.Rig.Note(await SituationAsync(s));
        }, Expect.Any);
        await s.StepAsync("The page reloads and takes control", async () =>
        {
            await s.ReloadAsync();
            await s.SettleAsync(300);
        }, Expect.Any);
        await s.StepAsync("Operator gives up on the wizard: field, AB line, AutoSteer on", async () =>
        {
            await s.Send("field.new", "AfterDrop");
            await s.SettleAsync(1200);
            await s.Send("track.aPlus", s.Rig.HeadingDeg.ToString("F1", Inv));
            await s.SettleAsync(500);
            s.Drive(6);
            await s.SettleAsync(1200);
            await s.Send("autosteer.toggle");
            await s.SettleAsync(500);
            bool on = await s.Rig.ReadAsync(() => s.Rig.Vm.IsAutoSteerEngaged);
            s.Rig.Note("AutoSteer " + (on ? "engaged" : "did not engage"));
            if (!on && await WizardOpen(s))
                s.Report(new Finding(Severity.Bug, "AUTOSTEER_BLOCKED_BY_LOST_WIZARD",
                    "After the browser was lost in the wizard, AutoSteer refuses to engage (\"off while the steer wizard is open\") and the reloaded page shows no wizard to close."));
            if (on) await s.Send("autosteer.toggle");
            s.Drive(0);
            await s.SettleAsync(400);
        }, Expect.Any);
    }

    // ------------------------------------------------------------ X21 spam start

    private static async Task SpamStartAsync(ScenarioJournal j, string name)
    {
        await using var s = await WizardScenario.StartAsync(j, name, "careless");
        await PrepareAsync(s, "motor");
        await s.StepAsync("Motor step: Start ×3 quickly", async () =>
        {
            await s.ActAsync("StartTest");
            await s.ActAsync("StartTest");
            await s.ActAsync("StartTest");
            await s.WaitTestEndAsync();
            int kp = await s.Rig.ReadAsync(() => s.Rig.Store.AutoSteer.ProportionalGain);
            await s.CheckAsync(kp == 50, new Finding(Severity.Bug, "KP_NOT_RESTORED", string.Create(Inv, $"Kp is {kp} after Start pressed three times (the operator's was 50).")));
        }, Expect.Any);
        await s.StepAsync("Next; max-angle step: Start ×3 quickly", async () =>
        {
            await s.NextAsync();
            await s.ActAsync("StartTest");
            await s.ActAsync("StartTest");
            await s.ActAsync("StartTest");
            await s.WaitTestEndAsync();
        }, Expect.Any);
    }

    // ------------------------------------------------------------ AutoSteer during the wizard

    private static async Task AutoSteerDuringWizardAsync(ScenarioJournal j, string name)
    {
        await using var s = await WizardScenario.StartAsync(j, name, "careless");
        await s.StepAsync("Field and AB line first, then opens the wizard", async () =>
        {
            await s.Send("field.new", "Before");
            await s.SettleAsync(1200);
            await s.Send("track.aPlus", s.Rig.HeadingDeg.ToString("F1", Inv));
            await s.SettleAsync(500);
        });
        await PrepareAsync(s, "motor");
        await s.StepAsync("Drives at 6 km/h, presses the AutoSteer button", async () =>
        {
            s.Drive(6);
            await s.SettleAsync(1200);
            await s.Send("autosteer.toggle");
            await s.SettleAsync(500);
            bool on = await s.Rig.ReadAsync(() => s.Rig.Vm.IsAutoSteerEngaged);
            s.Rig.Note("AutoSteer " + (on ? "ENGAGED" : "refused"));
            if (on) await s.Send("autosteer.toggle");
            s.Drive(0);
            await s.SettleAsync(500);
        }, Expect.Refused);
    }

    // ------------------------------------------------------------ X11 / X19 zombie CPD

    private static async Task CancelDuringCpdAsync(ScenarioJournal j, string name)
    {
        await using var s = await WizardScenario.StartAsync(j, name, "careless");
        await PrepareAsync(s, "cpd");
        await ZombieCpdAsync(s, "✕ (Cancel)", () => s.CancelAsync());
    }

    private static async Task ReopenDuringCpdAsync(ScenarioJournal j, string name)
    {
        await using var s = await WizardScenario.StartAsync(j, name, "careless");
        await PrepareAsync(s, "cpd");
        await ZombieCpdAsync(s, "Steer Wizard opened again (second tablet)", () => s.OpenAsync());
    }

    private static async Task ZombieCpdAsync(WizardScenario s, string what, Func<Task> act)
    {
        double cpdBefore = 0;
        await s.StepAsync("CPD: wheel 20° right, 5 km/h, Record, then " + what + " after 3 s", async () =>
        {
            s.Hands(20);
            s.Drive(5);
            await s.SettleAsync(2500);
            await s.ActAsync("StartRecording");
            await Task.Delay(3000);
            cpdBefore = await Cpd(s);
            await act();
            await s.SettleAsync(300);
        }, Expect.Any);
        await s.StepAsync("Keeps circling for 20 s (finishing the turn to get out of the field)", async () =>
        {
            await s.SettleAsync(20000);
            s.Drive(0);
            await s.SettleAsync(500);
            s.Hands(null);
            double after = await Cpd(s);
            var f = await s.Rig.FrameAsync();
            s.Rig.Note(string.Create(Inv, $"CPD {cpdBefore:F0} → {after:F0}; wizard on step {f?.StepKind ?? "(closed)"}"));
            if (Math.Abs(after - cpdBefore) > 0.5 && (f == null || f.StepKind != "cpd"))
                s.Report(new Finding(Severity.Bug, "ZOMBIE_STEP_WRITES",
                    string.Create(Inv, $"The CPD step kept recording after the wizard was {(f == null ? "closed" : "reopened")} and changed CPD {cpdBefore:F0} → {after:F0} while nobody saw it.")));
        }, Expect.Any);
    }

    // ------------------------------------------------------------ X12 / X13 / X14 CPD driven badly

    private static async Task CpdStopsEarlyAsync(ScenarioJournal j, string name)
    {
        await using var s = await WizardScenario.StartAsync(j, name, "careless");
        await PrepareAsync(s, "cpd");
        double before = 0;
        await s.StepAsync("CPD: wheel 20° right, 5 km/h, Record, stops after a quarter circle", async () =>
        {
            before = await Cpd(s);
            s.Hands(20);
            s.Drive(5);
            await s.SettleAsync(2500);
            s.Rig.ResetTurnCount();
            await s.ActAsync("StartRecording");
            while (s.Rig.TurnedDeg < 90) await Task.Delay(50);
            s.Drive(0);
            var f = await s.WaitResultAsync(8000);
            double now = await Cpd(s);
            s.Rig.Note(string.Create(Inv, $"turned {s.Rig.TurnedDeg:F0}°; CPD {before:F0} → {now:F0}"));
        }, Expect.Refused);
        await CheckCpdKept(s, before, "a quarter circle and a stop");
    }

    private static async Task CpdStraightensAsync(ScenarioJournal j, string name)
    {
        await using var s = await WizardScenario.StartAsync(j, name, "careless");
        await PrepareAsync(s, "cpd");
        double before = 0;
        await s.StepAsync("CPD: wheel 20° right, Record, half a circle, then eases the wheel to 12°", async () =>
        {
            before = await Cpd(s);
            s.Hands(20);
            s.Drive(5);
            await s.SettleAsync(2500);
            s.Rig.ResetTurnCount();
            await s.ActAsync("StartRecording");
            while (s.Rig.TurnedDeg < 170) await Task.Delay(50);
            s.Hands(12);
            await s.WaitResultAsync(90000);
            s.Drive(0);
            await s.SettleAsync(500);
            s.Hands(null);
            double now = await Cpd(s);
            s.Rig.Note(string.Create(Inv, $"CPD {before:F0} → {now:F0} (sensor's real {s.Module.TruthCpd:F0}, the careful run gets 94)"));
        }, Expect.Refused);
        await CheckCpdKept(s, before, "the wheel eased from 20° to 12° half way");
    }

    private static async Task CheckCpdKept(WizardScenario s, double before, string what)
    {
        await s.StepAsync("Check the CPD", async () =>
        {
            double after = await Cpd(s);
            double ratio = after / s.Module.TruthCpd;
            if (Math.Abs(after - before) > 0.5 && ratio is < 0.7 or > 1.3)
                s.Report(new Finding(Severity.Bug, "CPD_FROM_BAD_CIRCLE",
                    string.Create(Inv, $"The CPD step took a circle with {what} and set CPD {after:F0} (the sensor's real {s.Module.TruthCpd:F0}: every angle now reads {s.Module.TruthCpd / after:F2}×).")));
        });
    }

    private static async Task CpdLeftAsync(ScenarioJournal j, string name)
    {
        await using var s = await WizardScenario.StartAsync(j, name, "careless");
        await PrepareAsync(s, "cpd");
        double before = await Cpd(s);
        await CarefulOperator.CircleAsync(s, "CPD", -20, 5);
        await s.StepAsync("Check the CPD (the step asks for a right-hand circle)", async () =>
        {
            double cpd = await Cpd(s);
            s.Rig.Note(string.Create(Inv, $"CPD {before:F0} → {cpd:F0} from a left-hand circle (right-hand gives 94)"));
            // Refused (the CPD kept) is right; a CPD taken from it is the finding.
            if (Math.Abs(cpd - before) > 0.5 && Math.Abs(cpd - 94) > 4)
                s.Report(new Finding(Severity.Inconsistency, "CPD_LEFT_CIRCLE_ACCEPTED",
                    string.Create(Inv, $"The CPD step took a LEFT-hand circle (it asks for right) and set CPD {cpd:F0} instead of ~94: left readings carry the Ackermann factor and the left sensor curve.")));
        });
    }

    private static async Task AckermannRightAsync(ScenarioJournal j, string name)
    {
        // Truth 120 (left reads less than right), so a right-hand circle's ~90 can't pass for it.
        await using var s = await WizardScenario.StartAsync(j, name, "careless", m => m.TruthAckermann = 120);
        await PrepareAsync(s, "ackermann");
        int before = await Ack(s);
        await CarefulOperator.CircleAsync(s, "Ackermann", +20, 5);
        await s.StepAsync("Check Ackermann (the step asks for a left-hand circle)", async () =>
        {
            int ack = await Ack(s);
            s.Rig.Note(string.Create(Inv, $"Ackermann {before} → {ack} from a right-hand circle (a left-hand one gives ~108 here; truth 120)"));
            // Refused (Ackermann kept) is right; a factor taken from it is the finding.
            if (ack != before && Math.Abs(ack - 108) > 8)
                s.Report(new Finding(Severity.Bug, "ACKERMANN_RIGHT_CIRCLE_ACCEPTED",
                    string.Create(Inv, $"The Ackermann step took a RIGHT-hand circle (it asks for left) and set Ackermann {ack} (a left-hand circle gives ~108, the tractor's real 120): it sets the factor for LEFT readings from a right turn, which only measures the CPD step's 0.9.")));
        });
    }

    private static async Task AckermannStraightensAsync(ScenarioJournal j, string name)
    {
        await using var s = await WizardScenario.StartAsync(j, name, "careless");
        await PrepareAsync(s, "ackermann");
        await s.StepAsync("Ackermann: wheel 20° left, Record, then eases the wheel to 10° left", async () =>
        {
            s.Hands(-20);
            s.Drive(5);
            await s.SettleAsync(2500);
            await s.ActAsync("StartRecording");
            await Task.Delay(800);
            s.Hands(-10);
            await s.WaitResultAsync(90000);
            s.Drive(0);
            await s.SettleAsync(500);
            s.Hands(null);
            int ack = await Ack(s);
            s.Rig.Note(string.Create(Inv, $"Ackermann {ack} (careful run ~80, truth 90)"));
            if (ack is < 60 or > 120)
                s.Report(new Finding(Severity.Bug, "ACKERMANN_FROM_START_ANGLE",
                    string.Create(Inv, $"The Ackermann step uses the angle at the moment Record was pressed, not the angle held while circling: the wheel eased from 20° to 10° and Ackermann became {ack}.")));
        }, Expect.Refused);
    }

    // ------------------------------------------------------------ X20 GPS only + Skip

    private static async Task GpsOnlySkipAsync(ScenarioJournal j, string name)
    {
        await using var s = await WizardScenario.StartAsync(j, name, "careless");
        await s.StepAsync("Open, Next, Tractor, Next, 'GPS only', Next", async () =>
        {
            await s.OpenAsync();
            await s.NextAsync();
            await s.SetAsync("vehicle.type", "0");
            await s.NextAsync();
            await s.HardwareAsync(0);
            await s.NextAsync();
        });
        var seen = new List<string>();
        await s.StepAsync("Next / Skip through the rest", async () =>
        {
            for (int i = 0; i < 20 && await WizardOpen(s); i++)
            {
                var f = await s.Rig.FrameAsync();
                if (f == null) break;
                seen.Add(f.StepKind);
                if (f.CanSkip) await s.SkipAsync();
                else if (!await s.NextAsync()) break;
            }
            s.Rig.Note("steps shown: " + string.Join(", ", seen));
            var steer = seen.Where(k => k is "roll" or "was" or "motor" or "maxangle" or "cpd" or "ackermann" or "gains").Distinct().ToList();
            if (steer.Count > 0)
                s.Report(new Finding(Severity.Inconsistency, "GPS_ONLY_SHOWS_STEER_STEPS",
                    "With 'GPS only' chosen, Skip still lands on AutoSteer steps (" + string.Join(", ", steer) + "): Skip moves one step on without leaving out the steps that don't apply, Next does."));
        }, Expect.Any);
    }

    // ------------------------------------------------------------ Cancel after the motor test

    private static async Task CancelAfterMotorTestAsync(ScenarioJournal j, string name)
    {
        await using var s = await WizardScenario.StartAsync(j, name, "careless", m => m.TruthMotorReversed = true);
        await PrepareAsync(s, "motor");
        await s.StepAsync("Motor test (motor backwards): Start, wait", async () =>
        {
            await s.ActAsync("StartTest");
            await s.WaitTestEndAsync();
        }, Expect.Any);
        await s.StepAsync("✕ Cancel: 'I'll do the rest another day'", async () =>
        {
            await s.CancelAsync();
            await s.SettleAsync(500);
            var a = s.Rig.Store.AutoSteer;
            s.Rig.Note(string.Create(Inv, $"live settings after Cancel: Invert Motor {(a.InvertMotor ? "on" : "off")}, WAS offset {a.WasOffset}, roll zero {s.Rig.Store.Ahrs.RollZero:F1}"));
            var prof = Directory.GetFiles(s.Rig.DataRoot, "*.json", SearchOption.AllDirectories)
                .Where(p => p.Contains("Vehicle", StringComparison.OrdinalIgnoreCase)).ToList();
            string text = string.Join("\n", prof.Select(File.ReadAllText));
            bool savedInvert = text.Contains("\"InvertMotor\": true", StringComparison.Ordinal) || text.Contains("\"InvertMotor\":true", StringComparison.Ordinal);
            s.Rig.Note("vehicle profile on disk " + (savedInvert ? "has" : "does NOT have") + " Invert Motor on");
            if (a.InvertMotor && !savedInvert)
                s.Report(new Finding(Severity.Inconsistency, "CANCEL_KEEPS_LIVE_NOT_SAVED",
                    "Cancel keeps what the steps changed in the live settings (Invert Motor on, the WAS and roll zeros) but doesn't save them: the module runs them until the next restart, then they are gone. Cancel should either undo them or say they stay."));
        }, Expect.Any);
    }
}
