// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace AgOpenWeb.SteerWizardScenarios.Tests;

/// <summary>
/// The operator who does everything right, in order, reads every result and only goes on
/// when the step says it worked. At the end the settings must match the tractor's truth
/// (the emulated sensor, motor and geometry), the profile must be saved, and a drive on an
/// AB line with AutoSteer must stay on the line.
/// </summary>
public static class CarefulOperator
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static async Task RunAsync(ScenarioJournal journal, string name = "Careful: whole wizard",
        Action<EmulatedSteerModule>? truth = null)
    {
        await using var s = await WizardScenario.StartAsync(journal, name, "careful", truth);
        var m = s.Module;
        var rig = s.Rig;

        await s.StepAsync("Open the steer wizard", s.OpenAsync);
        await s.StepAsync("Welcome → Next", async () => await s.CheckAsync(await s.NextAsync(), Blocked("welcome")));
        await s.StepAsync("Vehicle type: Tractor → Next", async () =>
        {
            await s.SetAsync("vehicle.type", "0");
            await s.CheckAsync(await s.NextAsync(), Blocked("vehicleType"));
        });
        await s.StepAsync("Hardware: AutoSteer → Next", async () =>
        {
            await s.HardwareAsync(1);
            await s.CheckAsync(await s.NextAsync(), Blocked("hardware"));
        });
        await s.StepAsync("Dimensions: wheelbase 2.88 m, track 1.90 m → Next", async () =>
        {
            await s.SetAsync("vehicle.wheelbase", rig.TruthWheelbase);
            await s.SetAsync("vehicle.trackWidth", rig.TruthTrackWidth);
            await s.CheckAsync(await s.NextAsync(), Blocked("dimensions"));
        });
        await s.StepAsync("Antenna: 0.60 m ahead of the rear axle, 3.20 m high, centre → Next", async () =>
        {
            await s.SetAsync("vehicle.antennaPivot", rig.TruthAntennaAheadOfRearAxle);
            await s.SetAsync("vehicle.antennaHeight", 3.2);
            await s.SetAsync("vehicle.antennaSide", "center");
            await s.CheckAsync(await s.NextAsync(), Blocked("antenna"));
        });
        await s.StepAsync("Hardware config: steer switch, Cytron, differential → Next", async () =>
        {
            await s.SetAsync("autosteer.externalEnable", "1");
            await s.SetAsync("autosteer.motorDriver", "1");
            await s.SetAsync("autosteer.adConverter", "0");
            await s.CheckAsync(await s.NextAsync(), Blocked("hwconfig"));
        });

        // Roll: tilt right, the gauge must go right; level ground, Zero Roll.
        await s.StepAsync("Roll: park tilted 4° to the right, read the gauge", async () =>
        {
            m.TruthRollDeg = 4;
            await s.SettleAsync(600);
        });
        double rollTilted = s.Last?.LiveRoll ?? 0;
        await s.StepAsync(string.Create(Inv, $"Roll: gauge shows {rollTilted:F1}° → {(rollTilted < 0 ? "turn Invert Roll on" : "leave Invert Roll off")}"), async () =>
        {
            if (rollTilted < 0) await s.ToggleAsync("roll.isRollInvert", () => rig.Store.Ahrs.IsRollInvert);
            await s.SettleAsync(400);
        });
        await s.StepAsync("Roll: drive onto level ground, press Zero Roll", async () =>
        {
            m.TruthRollDeg = 0;
            await s.SettleAsync(600);
            await s.ActAsync("ZeroRoll");
            await s.SettleAsync(600);
        });
        await s.StepAsync("Roll: check the gauge reads 0 on level ground, tilted right reads +", async () =>
        {
            var f = await rig.FrameAsync();
            double level = f?.LiveRoll ?? 99;
            m.TruthRollDeg = 3;
            await s.SettleAsync(600);
            double right = (await rig.FrameAsync())?.LiveRoll ?? -99;
            m.TruthRollDeg = 0;
            await s.SettleAsync(400);
            if (Math.Abs(level) > 0.3 || right < 2)
                s.Report(new Finding(Severity.Bug, "ROLL_CAL_WRONG",
                    string.Create(Inv, $"After Invert/Zero Roll done right, level ground reads {level:F1}° and 3° right reads {right:F1}°.")));
            await s.CheckAsync(await s.NextAsync(), Blocked("roll"));
        });

        // WAS: direction first, then zero with the wheels straight.
        await s.StepAsync("WAS: turn the wheel 10° right by hand, read the angle", async () =>
        {
            s.Hands(10);
            await s.SettleAsync(500);
        });
        double read10 = s.Last?.LiveAngle ?? 0;
        await s.StepAsync(string.Create(Inv, $"WAS: it reads {read10:F1}° → {(read10 < 0 ? "turn Invert WAS on" : "leave Invert WAS off")}"), async () =>
        {
            if (read10 < 0) await s.ToggleAsync("autosteer.invertWas", () => rig.Store.AutoSteer.InvertWas);
            await s.SettleAsync(500);
        });
        await s.StepAsync("WAS: wheels straight ahead, press Zero WAS once", async () =>
        {
            s.Hands(0);
            await s.SettleAsync(600);
            await s.ActAsync("ZeroWas");
            await s.SettleAsync(700);
        });
        await s.StepAsync("WAS: check 0° straight and positive to the right, hands off → Next", async () =>
        {
            double straight = m.ReportedAngleDeg;
            s.Hands(10);
            await s.SettleAsync(400);
            double right = m.ReportedAngleDeg;
            s.Hands(0);
            await s.SettleAsync(300);
            s.Hands(null);
            if (Math.Abs(straight) > 0.5 || right < 5)
                s.Report(new Finding(Severity.Bug, "WAS_CAL_WRONG",
                    string.Create(Inv, $"After Zero WAS done right, straight reads {straight:F1}° and 10° right reads {right:F1}°.")));
            await s.CheckAsync(await s.NextAsync(), Blocked("was"));
        });

        // Motor: press Start, hands off, wait for the result.
        await s.StepAsync("Motor test: Start, hands clear, wait for the result", async () =>
        {
            await s.ActAsync("StartTest");
            await s.WaitTestEndAsync();
        });
        string motorResult = s.Last?.TestResult ?? "";
        await s.StepAsync("Motor test: read the result → Next", async () =>
        {
            await s.CheckAsync(await s.NextAsync(), Blocked("motor"));
            var mm = System.Text.RegularExpressions.Regex.Match(motorResult, @"Minimum PWM: (\d+)");
            int saved = await rig.ReadAsync(() => rig.Store.AutoSteer.MinPwm);
            if (mm.Success && int.Parse(mm.Groups[1].Value, Inv) != saved)
                s.Report(new Finding(Severity.Bug, "MOTOR_MIN_PWM_NOT_SAVED",
                    string.Create(Inv, $"The motor test found Minimum PWM {mm.Groups[1].Value} but after Next the setting is still {saved}.")));
        });
        await s.StepAsync("Max angle: Start, hands clear, wait", async () =>
        {
            await s.ActAsync("StartTest");
            await s.WaitTestEndAsync();
        });
        string maxResult = s.Last?.TestResult ?? "";
        await s.StepAsync("Max angle: read the result → Next", async () =>
        {
            // The lock as the sensor reads it then (CPD and Ackermann not calibrated yet).
            double leftLockReads = m.LockLeftDeg * 100.0 / m.TruthAckermann * m.TruthCpd / rig.Store.AutoSteer.CountsPerDegree;
            var lm = System.Text.RegularExpressions.Regex.Match(maxResult, @"Left max: ([\d.]+)");
            if (lm.Success && double.Parse(lm.Groups[1].Value, Inv) < leftLockReads - 1.5)
                s.Report(new Finding(Severity.Inconsistency, "MAX_ANGLE_SHORT_OF_LOCK",
                    string.Create(Inv, $"The max-angle test measured the left side at {lm.Groups[1].Value}° but the lock reads {leftLockReads:F1}°: the wheels never reached the lock (free drive was held to ±40° once, below a lock that reads past it).")));
            await s.CheckAsync(await s.NextAsync(), Blocked("maxangle"));
        });

        // CPD circle: wheel 20° right, ~5 km/h, Record, keep the circle.
        await CircleAsync(s, "CPD", +20, 5.0);
        await s.StepAsync("CPD: done → Next", async () =>
        {
            await s.CheckAsync(await s.NextAsync(), Blocked("cpd"));
        });
        await CircleAsync(s, "Ackermann", -20, 5.0);
        await s.StepAsync("Ackermann: done → Next", async () =>
        {
            await s.CheckAsync(await s.NextAsync(), Blocked("ackermann"));
        });
        await s.StepAsync("Gains: keep the defaults → Next", async () =>
        {
            await s.CheckAsync(await s.NextAsync(), Blocked("gains"));
        });
        await s.StepAsync("Speed: min 1.0 km/h, max 18 km/h → Next", async () =>
        {
            await s.SetAsync("autosteer.minSteerSpeed", 1.0);
            await s.SetAsync("autosteer.maxSteerSpeed", 18.0);
            await s.CheckAsync(await s.NextAsync(), Blocked("speed"));
        });
        await s.StepAsync("Finish page: every test ran, so none is listed as not run", async () =>
        {
            string text = (await s.Rig.FrameAsync())?.TestResult ?? "";
            rig.Note("Finish page: " + (text.Length > 0 ? text : "(nothing)"));
            if (text.Contains("Not tested", StringComparison.Ordinal))
                s.Report(new Finding(Severity.Bug, "FINISH_WARNS_ABOUT_A_TEST_THAT_RAN",
                    "The Finish page lists a test as not run after the careful operator ran every one: " + text));
        });
        await s.StepAsync("Finish", s.FinishAsync);

        // What the careful run must have achieved, against the tractor's truth.
        await s.StepAsync("Check the calibration against the tractor", async () =>
        {
            await s.SettleAsync(500);
            var a = rig.Store.AutoSteer;
            if (a.InvertWas != m.TruthWasReversed)
                s.Report(new Finding(Severity.Bug, "TRUTH_INVERT_WAS", "Invert WAS does not match the sensor's mounting."));
            if (a.InvertMotor != m.TruthMotorReversed)
                s.Report(new Finding(Severity.Safety, "TRUTH_INVERT_MOTOR", "Invert Motor does not match the motor's wiring after the motor test: AutoSteer would steer away from the line."));
            double cpdErr = a.CountsPerDegree / m.TruthCpd;
            if (cpdErr is < 0.75 or > 1.25)
                s.Report(new Finding(Severity.Bug, "TRUTH_CPD",
                    string.Create(Inv, $"CPD {a.CountsPerDegree:F0} vs the sensor's real {m.TruthCpd:F0} counts/° ({cpdErr * 100:F0} %).")));
            if (Math.Abs(a.Ackermann - m.TruthAckermann) > 20)
                s.Report(new Finding(Severity.Bug, "TRUTH_ACKERMANN",
                    string.Create(Inv, $"Ackermann {a.Ackermann} vs the real {m.TruthAckermann:F0}.")));
            if (Math.Abs(a.MinPwm - m.BreakawayPwm) > 15)
                s.Report(new Finding(Severity.Inconsistency, "TRUTH_MIN_PWM",
                    string.Create(Inv, $"Min PWM {a.MinPwm} vs the motor's real break-away {m.BreakawayPwm}.")));
            // Max steer angle is in the module's degrees: the lock as the FINAL CPD reads it.
            double lockReads = Math.Min(m.LockLeftDeg, m.LockRightDeg) * m.TruthCpd / Math.Max(1, a.CountsPerDegree);
            double maxA = rig.Store.Vehicle.MaxSteerAngle;
            if (maxA > lockReads + 0.5 || maxA < 0.6 * lockReads)
                s.Report(new Finding(Severity.Inconsistency, "TRUTH_MAX_ANGLE",
                    string.Create(Inv, $"Max steer angle {maxA:F0}° but the locks read {lockReads:F1}° with the final CPD (measured before the CPD step changed the scale).")));
            // Left and right must read alike for the same wheel angle (Ackermann's job).
            s.Hands(20);
            await s.SettleAsync(400);
            double r20 = m.ReportedAngleDeg;
            s.Hands(-20);
            await s.SettleAsync(400);
            double l20 = -m.ReportedAngleDeg;
            s.Hands(0);
            await s.SettleAsync(300);
            s.Hands(null);
            rig.Note(string.Create(Inv, $"wheels 20° right read {r20:F1}°, 20° left read {l20:F1}°"));
            if (Math.Abs(r20 - l20) / 20 > 0.06)
                s.Report(new Finding(Severity.Inconsistency, "LEFT_RIGHT_READ_DIFFERENTLY",
                    string.Create(Inv, $"After CPD and Ackermann, the same 20° reads {r20:F1}° to the right and {l20:F1}° to the left ({Math.Abs(r20 - l20) / 20 * 100:F0} % apart): the CPD step multiplies by 0.9 on purpose, the Ackermann step matches the left side to the circle without it, so the two sides end up {Math.Abs(r20 / l20 - 1) * 100:F0} % apart and AutoSteer turns one way harder than the other.")));
            if (rig.Store.Vehicle.Wheelbase != rig.TruthWheelbase)
                s.Report(new Finding(Severity.Bug, "TRUTH_WHEELBASE", "The wheelbase typed in is not in the settings."));
            // Saved: the profile on disk carries the calibration.
            var prof = Directory.GetFiles(rig.DataRoot, "*.json", SearchOption.AllDirectories)
                .Where(p => p.Contains("Vehicle", StringComparison.OrdinalIgnoreCase)).ToList();
            string text = string.Join("\n", prof.Select(File.ReadAllText));
            if (!text.Contains(a.CountsPerDegree.ToString(Inv), StringComparison.Ordinal) && !text.Contains(((int)a.CountsPerDegree).ToString(Inv), StringComparison.Ordinal))
                s.Report(new Finding(Severity.Bug, "NOT_SAVED", "After Finish, the vehicle profile on disk does not carry the new CPD."));
        });

        await DriveAbLineAsync(s, "after the careful wizard");
    }

    /// <summary>Drive a steady circle with the wheel held at <paramref name="wheelDeg"/>, press
    /// Record once moving, keep going until the step gives its result.</summary>
    public static async Task CircleAsync(WizardScenario s, string what, double wheelDeg, double kmh, int timeoutMs = 90000)
    {
        await s.StepAsync(string.Create(Inv, $"{what}: wheel held {Math.Abs(wheelDeg):F0}° {(wheelDeg > 0 ? "right" : "left")}, drive {kmh:F0} km/h, press Record"), async () =>
        {
            s.Hands(wheelDeg);
            s.Drive(kmh);
            await s.SettleAsync(2500);
            s.Rig.ResetTurnCount();
            await s.ActAsync("StartRecording");
            await s.SettleAsync(300);
        });
        await s.StepAsync($"{what}: keep the circle until the result", async () =>
        {
            await s.WaitResultAsync(timeoutMs);
            s.Drive(0);
            await s.SettleAsync(800);
            s.Hands(0);
            await s.SettleAsync(300);
            s.Hands(null);
        });
    }

    /// <summary>After the wizard: an AB line 4° off the tractor's heading, AutoSteer on, 30 s at
    /// 8 km/h. The calibration is right if the tractor settles on the line and the wheels never
    /// sit at a lock.</summary>
    public static async Task DriveAbLineAsync(WizardScenario s, string when)
    {
        var rig = s.Rig;
        await s.StepAsync("AutoSteer check " + when + ": new field, AB line 4° off the heading", async () =>
        {
            s.Hands(null);
            await s.Send("field.new", "SteerCheck");
            await s.SettleAsync(1500);
            double h = rig.HeadingDeg + 4;
            await s.Send("track.aPlus", h.ToString("F1", Inv));
            await s.SettleAsync(600);
        });
        await s.StepAsync("AutoSteer check: drive 8 km/h, AutoSteer on, 30 s", async () =>
        {
            s.Drive(8);
            await s.SettleAsync(1500);
            await s.Send("autosteer.toggle");
            await s.SettleAsync(300);
            bool on = await rig.ReadAsync(() => rig.Vm.IsAutoSteerEngaged);
            if (!on)
            {
                s.Rig.Note("AutoSteer did not engage");
                return;
            }
            double maxXte = 0, lastXte = 0, maxWheel = 0;
            for (int i = 0; i < 300; i++)
            {
                await Task.Delay(100);
                double xte = await rig.ReadAsync(() => rig.State.Guidance.CrossTrackError);
                if (i > 150) maxXte = Math.Max(maxXte, Math.Abs(xte));
                lastXte = xte;
                maxWheel = Math.Max(maxWheel, Math.Abs(s.Module.WheelAngleDeg));
            }
            rig.Note(string.Create(Inv, $"AutoSteer: max off-line {maxXte * 100:F0} cm in the last 15 s, ended at {lastXte * 100:F0} cm, wheels up to {maxWheel:F0}°"));
            if (maxXte > 0.3)
                s.Report(new Finding(Severity.Safety, "AUTOSTEER_DOES_NOT_HOLD_LINE",
                    string.Create(Inv, $"With the wizard's settings AutoSteer does not hold the line ({when}): up to {maxXte * 100:F0} cm off after 15 s, wheels up to {maxWheel:F0}°.")));
            await s.Send("autosteer.toggle");
            s.Drive(0);
            await s.SettleAsync(500);
        });
    }

    private static Finding Blocked(string kind) =>
        new(Severity.Bug, "CAREFUL_NEXT_BLOCKED", $"Next did nothing on the {kind} step for an operator who did everything right.");
}
