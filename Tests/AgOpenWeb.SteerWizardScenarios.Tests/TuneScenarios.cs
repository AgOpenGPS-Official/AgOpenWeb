// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using AgOpenWeb.RemoteServer;

namespace AgOpenWeb.SteerWizardScenarios.Tests;

/// <summary>
/// The wheel tuning step (Tune the Wheel Response): it moves the wheels by itself for a minute
/// or two and writes Minimum To Move, Max Limit and Proportional Gain.
/// <list type="bullet">
/// <item>A careful operator on three steerings the step has never seen: a PWM valve that
/// answers late with one side stiffer than the other (on a tractor set up as a tester's
/// Maxxum 135, with its pressure sensor for the stop check), a Keya motor on the steering
/// wheel with play and a kickout, and a motor on the steering wheel with nothing to stop it
/// but the page. The values are compared with what the steering really does, then measured on the
/// wheels (a turn each way), and the valve drives an AB line with them.</item>
/// <item>A careless one: a hand on the wheel, the steer switch, ✕, Next or a lost browser in
/// the middle, the motor wired backwards, wheels turned or driving at the start, the stop
/// check left out. After each: is the module let go, are the operator's own values back in
/// the settings and in the module, does the page say why.</item>
/// </list>
/// The calibration steps before it are not driven again here (the other stories do that):
/// their results are typed in, as on a tractor calibrated another day.
/// </summary>
public static class TuneScenarios
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static IEnumerable<(string Name, Func<ScenarioJournal, string, Task> Run)> All()
    {
        yield return ("Tune: PWM valve that answers late, pressure sensor (settings as on a Maxxum 135)", (j, n) => WholeAsync(j, n, MaxxumValve, Stop.Pressure, driveLine: true));
        yield return ("Tune: Keya on the steering wheel, kickout sensor", (j, n) => WholeAsync(j, n, Keya, Stop.Sensor, driveLine: false));
        yield return ("Tune: motor on the steering wheel, no stop at the wheel", (j, n) => WholeAsync(j, n, WheelMotor, Stop.None, driveLine: false));
        yield return ("Tune careless: takes the wheel in the middle (kickout sensor)", (j, n) => MidRunAsync(j, n, Keya, Stop.Sensor, "Max Limit", Mistake.Hands, "stopped steering"));
        yield return ("Tune careless: takes the wheel in the first part (no kickout sensor)", (j, n) => MidRunAsync(j, n, WheelMotor, Stop.Switch, "Minimum To Move", Mistake.Hands, "on the wheel"));
        yield return ("Tune careless: takes the wheel in the middle (no kickout sensor)", (j, n) => MidRunAsync(j, n, WheelMotor, Stop.Switch, "Proportional Gain", Mistake.Hands, "hand on the wheel"));
        yield return ("Tune careless: steer switch off in the middle", (j, n) => MidRunAsync(j, n, WheelMotor, Stop.Switch, "Max Limit", Mistake.SwitchOff, "stopped steering"));
        yield return ("Tune careless: ✕ in the middle", (j, n) => MidRunAsync(j, n, WheelMotor, Stop.Switch, "Max Limit", Mistake.Cancel, null));
        yield return ("Tune careless: Next in the middle", (j, n) => MidRunAsync(j, n, WheelMotor, Stop.Switch, "Minimum To Move", Mistake.Next, null));
        yield return ("Tune careless: browser lost in the middle", (j, n) => MidRunAsync(j, n, PwmValve, Stop.Switch, "Max Limit", Mistake.BrowserLost, null));
        yield return ("Tune careless: motor wired backwards, motor test skipped", MotorBackwardsAsync);
        yield return ("Tune careless: motor wired backwards and a Minimum To Move that moves the wheels", MotorBackwardsHoldAsync);
        yield return ("Tune careless: wheels turned, driving, Continue first, stop check left out", RefusalsAsync);
        yield return ("Tune careless: values found, Next without choosing; again, Keep the old values", NotTakenAsync);
    }

    // ------------------------------------------------------------ three steerings

    /// <summary>A PWM valve: opens at 62 to the right and 68 to the left, answers 100 ms late and
    /// builds up its flow, fully open at 40°/s; firmware with LOW_HIGH_DEGREES 1.5.</summary>
    public static void PwmValve(EmulatedSteerModule m)
    {
        m.BreakawayPwm = 62;
        m.BreakawayLeftPwm = 68;
        m.DegPerSecPerPwm = 0.30;
        m.DeadTimeMs = 100;
        m.LagMs = 75;
        m.MaxRateDegPerSec = 40;
        m.LowHighDegrees = 1.5;
        m.WasNoiseCounts = 3;
    }

    /// <summary>
    /// <see cref="PwmValve"/> on a tractor set up as a tester's Case IH Maxxum 135: 120 counts per
    /// degree, Ackermann 69, a pressure sensor that lets go at 35 % and no steer switch. The
    /// valve's own numbers are not that tractor's (nobody has measured them): the step has to
    /// find them.
    /// </summary>
    public static void MaxxumValve(EmulatedSteerModule m)
    {
        PwmValve(m);
        m.TruthCpd = 120;
        m.TruthAckermann = 69;
        m.KickoutFitted = true;
    }

    /// <summary>A Keya motor on the steering wheel: moves from PWM 9, slow for its PWM, 0.4° of
    /// play to the wheels, and a kickout when a hand holds the wheel against it.</summary>
    public static void Keya(EmulatedSteerModule m)
    {
        m.BreakawayPwm = 9;
        m.DegPerSecPerPwm = 0.13;
        m.DeadTimeMs = 50;
        m.LagMs = 50;
        m.BacklashDeg = 0.4;
        m.LowHighDegrees = 1.5;
        m.WasNoiseCounts = 2;
        m.KickoutFitted = true;
    }

    /// <summary>A motor on the steering wheel through a gear: moves from PWM 28, 0.6° of play.</summary>
    public static void WheelMotor(EmulatedSteerModule m)
    {
        m.BreakawayPwm = 28;
        m.DegPerSecPerPwm = 0.25;
        m.DeadTimeMs = 25;
        m.BacklashDeg = 0.6;
        m.WasNoiseCounts = 2;
    }

    /// <summary>How the operator can stop the module at the wheel.</summary>
    public enum Stop { Switch, Sensor, Pressure, None }

    private enum Mistake { Hands, SwitchOff, Cancel, Next, BrowserLost }

    public readonly record struct Values(int Kp, int Max, int Min)
    {
        public override string ToString() => string.Create(Inv, $"Proportional Gain {Kp}, Max Limit {Max}, Minimum To Move {Min}");
    }

    // ------------------------------------------------------------ getting to the step

    /// <summary>Open the wizard and go to the tuning step with the calibration typed in: the
    /// sensor zeroed and scaled, the motor direction set (unless the story is that it is not).</summary>
    public static Task PrepareAsync(WizardScenario s, Stop stop, bool motorDirectionKnown = true) =>
        s.StepAsync("Set-up: calibrated another day (values typed in), Next to the tuning step", async () =>
        {
            var m = s.Module;
            var rig = s.Rig;
            await s.OpenAsync();
            await s.NextAsync();
            await s.SetAsync("vehicle.type", "0");
            await s.NextAsync();
            await s.HardwareAsync(1);
            await s.NextAsync();
            await s.SetAsync("vehicle.wheelbase", rig.TruthWheelbase);
            await s.SetAsync("vehicle.trackWidth", rig.TruthTrackWidth);
            await s.NextAsync();
            await s.SetAsync("vehicle.antennaPivot", rig.TruthAntennaAheadOfRearAxle);
            await s.SetAsync("vehicle.antennaHeight", 3.2);
            await s.NextAsync();
            await s.SetAsync("autosteer.externalEnable", stop == Stop.Switch ? "1" : "0");
            await s.SetAsync("autosteer.motorDriver", "1");
            if (stop == Stop.Sensor)
            {
                await s.SetAsync("autosteer.currentSensorEnabled", "1");
                await s.SetAsync("autosteer.currentTripPoint", "30");
            }
            if (stop == Stop.Pressure)
            {
                // The Maxxum's AutoSteer page: gain 40, Max Limit 180, Minimum To Move 25, max angle 33.
                await s.SetAsync("autosteer.pressureSensorEnabled", "1");
                await s.SetAsync("autosteer.pressureTripPoint", "35");
                await s.SetAsync("autosteer.proportionalGain", "40");
                await s.SetAsync("autosteer.maxPwm", "180");
                await s.SetAsync("autosteer.minPwm", "25");
                await s.SetAsync("autosteer.maxSteerAngle", "33");
            }
            await s.NextAsync();
            m.TruthRollDeg = 0;
            await s.SettleAsync(300);
            await s.ActAsync("ZeroRoll");
            await s.NextAsync();
            if (m.TruthWasReversed) await s.SetAsync("autosteer.invertWas", "1");
            await s.SetAsync("autosteer.countsPerDegree", m.TruthCpd);
            await s.SetAsync("autosteer.ackermann", m.TruthAckermann);
            if (motorDirectionKnown && m.TruthMotorReversed) await s.SetAsync("autosteer.invertMotor", "1");
            s.Hands(0);
            await s.SettleAsync(500);
            await s.ActAsync("ZeroWas");
            await s.SettleAsync(500);
            s.Hands(null);
            await s.GoToAsync("tune");
            string kind = await s.KindAsync();
            if (kind != "tune")
                s.Report(new Finding(Severity.Bug, "HARNESS_TUNE_STEP_NOT_REACHED", "Next from the WAS step did not arrive at the tuning step but at " + kind + "."));
        });

    public static async Task<Values> ValuesAsync(WizardScenario s) =>
        await s.Rig.ReadAsync(() =>
        {
            var a = s.Rig.Store.AutoSteer;
            return new Values(a.ProportionalGain, a.MaxPwm, a.MinPwm);
        });

    private static Values ModuleValues(EmulatedSteerModule m) => new(m.Kp, m.HighPwm, m.MinPwm);

    /// <summary>What the module gets for these settings (the wire's limits).</summary>
    private static Values OnTheWire(Values v) => new(Math.Clamp(v.Kp, 1, 200), Math.Clamp(v.Max, 50, 255), Math.Clamp(v.Min, 1, 200));

    // ------------------------------------------------------------ what the operator does on the page

    /// <summary>
    /// Start, then the stop the page asks for, then what it says after. True when the page
    /// offers Continue. Nothing but the operator's hands may have moved the wheels by then.
    /// </summary>
    public static async Task<bool> StopCheckAsync(WizardScenario s)
    {
        var m = s.Module;
        long t0 = m.NowMs;
        double from = m.WheelAngleDeg;
        await s.ActAsync("StartTest");
        var f = await s.WaitResultAsync(8000, d => !d.TestActive || d.TestPhase.Contains("Stop check: ", StringComparison.Ordinal) && !d.TestPhase.Contains("getting", StringComparison.Ordinal));
        if (f == null || !f.TestActive)
            return false; // refused: the result says why
        string asks = f.TestPhase;
        s.Rig.Note("page: " + asks);
        if (asks.Contains("press Stop now", StringComparison.Ordinal))
            await s.ActAsync("StopTest");
        else if (asks.Contains("by hand", StringComparison.Ordinal))
        {
            // Take the wheel and turn it against the module, a degree at a time.
            for (int i = 1; i <= 8 && (await s.Rig.FrameAsync())?.TestStage == 1; i++)
            {
                s.Hands(from + i);
                await Task.Delay(150);
            }
        }
        else if (asks.Contains("steer switch off", StringComparison.Ordinal))
            m.SteerSwitchClosed = false;

        f = await s.WaitResultAsync(6000, d => d.TestStage != 1);
        string then = f?.TestResult ?? "";
        s.Rig.Note("page: " + then);
        // What moved the wheels before the stop was seen: only a hand may have.
        var driven = m.SamplesSince(t0).Where(x => x.Hand == null).ToList();
        double byItself = driven.Count == 0 ? 0 : driven.Max(x => Math.Abs(x.Angle - from));
        if (asks.Contains("by hand", StringComparison.Ordinal)) byItself = 0; // the hand left them where it let go
        if (byItself > 1.5)
            s.Report(new Finding(Severity.Safety, "TUNE_WHEELS_MOVED_BEFORE_THE_STOP_CHECK",
                string.Create(Inv, $"The wheels moved {byItself:F1}° by themselves before the stop check had passed: the step moves nothing until it has seen the operator stop the module.")));
        s.Hands(null);
        if (then.Contains("back on", StringComparison.Ordinal))
            m.SteerSwitchClosed = true;
        await s.SettleAsync(400);
        return f?.TestStage == 2;
    }

    public sealed record RunOutcome(WizardDto? Frame, double Seconds, double FarthestDeg, int PeakPwm, int SettingsPackets);

    /// <summary>Continue, hands clear, wait until the step is done; what the wheels did meanwhile.</summary>
    public static async Task<RunOutcome> RunAsync(WizardScenario s, int timeoutMs = 300000)
    {
        var m = s.Module;
        double from = m.WheelAngleDeg;
        long t0 = m.NowMs;
        int packets = m.SettingsReceived;
        var sw = Stopwatch.StartNew();
        await s.ActAsync("Continue");
        bool ended = await s.WaitTestEndAsync(timeoutMs);
        var samples = m.SamplesSince(t0);
        double far = samples.Count == 0 ? 0 : samples.Max(x => Math.Abs(x.Angle - from));
        int peak = samples.Count == 0 ? 0 : samples.Max(x => Math.Abs(x.Pwm));
        var frame = await s.Rig.FrameAsync();
        if (!ended)
            s.Report(new Finding(Severity.Bug, "TUNE_NEVER_ENDS",
                string.Create(Inv, $"The tuning was still running after {timeoutMs / 1000} s (page: {frame?.TestPhase}).")));
        return new RunOutcome(frame, sw.Elapsed.TotalSeconds, far, peak, m.SettingsReceived - packets);
    }

    private static Values? Parse(string text)
    {
        var min = Regex.Match(text, @"Minimum To Move: \d+ → (\d+)");
        var max = Regex.Match(text, @"Max Limit: \d+ → (\d+)");
        var kp = Regex.Match(text, @"Proportional Gain: \d+ → (\d+)");
        if (!min.Success || !max.Success || !kp.Success) return null;
        return new Values(int.Parse(kp.Groups[1].Value, Inv), int.Parse(max.Groups[1].Value, Inv), int.Parse(min.Groups[1].Value, Inv));
    }

    /// <summary>Wheel speed the steering really gives at a PWM, the mean of the two sides.</summary>
    private static double TruthSpeed(EmulatedSteerModule m, int pwm)
    {
        double Side(int breakaway)
        {
            if (pwm < breakaway) return 0;
            double v = (pwm - breakaway + 4) * m.DegPerSecPerPwm;
            return m.MaxRateDegPerSec > 0 ? Math.Min(v, m.MaxRateDegPerSec) : v;
        }
        return (Side(m.BreakawayPwm) + Side(m.BreakawayLeftPwm ?? m.BreakawayPwm)) / 2;
    }

    /// <summary>The values on the page against what the steering really does.</summary>
    private static void CheckAgainstSteering(WizardScenario s, Values found, double wantedSpeed)
    {
        var m = s.Module;
        int lowest = m.LowestBreakawayPwm;
        if (found.Min >= lowest)
            s.Report(new Finding(Severity.Bug, "TUNE_MIN_MOVES_THE_WHEELS",
                string.Create(Inv, $"Minimum To Move {found.Min} is at or above the PWM that moves the wheels ({lowest}): they creep whenever the module steers.")));
        else if (found.Min < lowest - Math.Max(5, 0.12 * lowest))
            s.Report(new Finding(Severity.Inconsistency, "TUNE_MIN_FAR_BELOW",
                string.Create(Inv, $"Minimum To Move {found.Min} is well under the PWM that moves the wheels ({lowest}): small corrections do nothing.")));

        double speed = TruthSpeed(m, found.Max);
        double top = TruthSpeed(m, 255);
        s.Rig.Note(string.Create(Inv, $"the steering really turns {speed:F0}°/s at Max Limit {found.Max} ({wantedSpeed:F0}°/s wanted, {top:F0}°/s at full power)"));
        double aim = Math.Min(wantedSpeed, top);
        if (Math.Abs(speed - aim) > 0.3 * aim)
            s.Report(new Finding(Severity.Inconsistency, "TUNE_MAX_LIMIT_OFF",
                string.Create(Inv, $"Max Limit {found.Max} turns the wheels {speed:F0}°/s; {aim:F0}°/s was to be had.")));
    }

    public readonly record struct Turn(double PastDeg, double EndsOffDeg, int Swings, double SettleSeconds);

    /// <summary>
    /// The harness's own measurement, on the wheels themselves: Free Drive holds them, the set
    /// point steps by <paramref name="byDeg"/>, three seconds are recorded. Not what the step
    /// measured: what the steering does with the values now in the module.
    /// </summary>
    public static async Task<Turn> MeasureTurnAsync(WizardScenario s, double byDeg)
    {
        var m = s.Module;
        var rig = s.Rig;
        double from = m.ReportedAngleDeg;
        await rig.RunAsync(() => { rig.AutoSteer.EnableFreeDrive(); rig.AutoSteer.SetFreeDriveAngle(from); });
        await Task.Delay(800);
        double start = m.WheelAngleDeg;
        long t0 = m.NowMs;
        await rig.RunAsync(() => rig.AutoSteer.SetFreeDriveAngle(from + byDeg));
        await Task.Delay(3000);
        var samples = m.SamplesSince(t0);
        await rig.RunAsync(() => { rig.AutoSteer.SetFreeDriveAngle(0); rig.AutoSteer.DisableFreeDrive(); });
        await Task.Delay(300);

        double dir = Math.Sign(byDeg), step = Math.Abs(byDeg);
        var moved = samples.Select(x => (T: (x.Ms - t0) / 1000.0, D: (x.Angle - start) * dir)).ToList();
        if (moved.Count == 0) return new Turn(0, step, 0, 3);
        double past = Math.Max(0, moved.Max(x => x.D) - step);
        double end = moved.Skip(Math.Max(0, moved.Count - 8)).Average(x => x.D);
        int swings = 0;
        bool above = false;
        foreach (var (_, d) in moved)
        {
            if (!above && d > step + 0.3) { above = true; swings++; }
            else if (above && d < step - 0.3) { above = false; swings++; }
        }
        double settle = 0;
        foreach (var (t, d) in moved)
            if (Math.Abs(d - end) > 0.4) settle = t;
        return new Turn(past, Math.Abs(step - end), swings, settle);
    }

    // ------------------------------------------------------------ the careful operator

    /// <summary>The whole step done right, from Start to Use these values; the values found.</summary>
    public static async Task<Values?> CarefulAsync(WizardScenario s)
    {
        var m = s.Module;
        var before = await ValuesAsync(s);
        bool continueOffered = false;
        await s.StepAsync("Tuning: Start, then the stop check as the page asks", async () =>
        {
            continueOffered = await StopCheckAsync(s);
            if (!continueOffered)
                s.Report(new Finding(Severity.Bug, "TUNE_STOP_CHECK_FAILS_FOR_A_CAREFUL_OPERATOR",
                    "The operator stopped the module the way the page asked and the page did not offer Continue: " + ((await s.Rig.FrameAsync())?.TestResult ?? "")));
        });
        if (!continueOffered) return null;

        Values? found = null;
        await s.StepAsync("Tuning: Continue, hands clear, wait for the values", async () =>
        {
            var run = await RunAsync(s);
            string text = run.Frame?.TestResult ?? "";
            s.Rig.Note(string.Create(Inv,
                $"{run.Seconds:F0} s; wheels up to {run.FarthestDeg:F1}° from where they started; highest PWM {run.PeakPwm}; {run.SettingsPackets} settings packets to the module (each one it saves)"));
            found = run.Frame?.TestStage == 3 ? Parse(text) : null;
            if (found == null)
            {
                s.Report(new Finding(Severity.Bug, "TUNE_GIVES_NO_VALUES",
                    "A careful run on a sound steering ended without values: " + text));
                return;
            }
            if (run.FarthestDeg > 25)
                s.Report(new Finding(Severity.Safety, "TUNE_SWINGS_THE_WHEELS_TOO_FAR",
                    string.Create(Inv, $"The tuning took the wheels {run.FarthestDeg:F0}° from where they started (the page says up to 15°, the step's own limit is 22°).")));
            if (run.SettingsPackets > 60)
                s.Report(new Finding(Severity.Robustness, "TUNE_WEARS_THE_MODULE_MEMORY",
                    string.Create(Inv, $"One tuning run sent the module {run.SettingsPackets} settings packets; it saves each one to its EEPROM.")));
            var now = await ValuesAsync(s);
            if (now != before)
                s.Report(new Finding(Severity.Bug, "TUNE_VALUES_IN_USE_BEFORE_THE_OPERATOR_TOOK_THEM",
                    $"The page says the values are not in use yet, but the settings already hold {now} (the operator's own: {before})."));
            CheckAgainstSteering(s, found.Value, (await s.Rig.FrameAsync())?.TestChoice ?? 25);
        });
        if (found == null) return null;

        await s.StepAsync("Tuning: Use these values", async () =>
        {
            await s.ActAsync("Accept");
            await s.SettleAsync(700);
            var now = await ValuesAsync(s);
            if (now != found.Value)
                s.Report(new Finding(Severity.Bug, "TUNE_VALUES_NOT_TAKEN",
                    $"After Use these values the settings hold {now}, the page showed {found.Value}."));
            else if (ModuleValues(m) != OnTheWire(now))
                s.Report(new Finding(Severity.Bug, "TUNE_VALUES_NOT_AT_THE_MODULE",
                    $"After Use these values the module runs {ModuleValues(m)}, the settings hold {now}."));
        });
        return found;
    }

    private static async Task WholeAsync(ScenarioJournal j, string name, Action<EmulatedSteerModule> steering, Stop stop, bool driveLine)
    {
        await using var s = await WizardScenario.StartAsync(j, name, "careful", steering);
        var m = s.Module;
        await PrepareAsync(s, stop);
        var found = await CarefulAsync(s);
        if (found is not { } v) return;

        await s.StepAsync("Next (the gains page); a 6° turn each way, measured on the wheels", async () =>
        {
            await s.CheckAsync(await s.NextAsync(), new Finding(Severity.Bug, "CAREFUL_NEXT_BLOCKED", "Next did nothing on the tuning step after the values were taken."));
            foreach (double by in new[] { +6.0, -6.0 })
            {
                var t = await MeasureTurnAsync(s, by);
                int breakaway = by > 0 ? m.BreakawayPwm : m.BreakawayLeftPwm ?? m.BreakawayPwm;
                // So near the target that the PWM asked for no longer moves this side: the firmware's rule, not the gain's fault.
                double deadBand = Math.Max(0, breakaway - v.Min) / (double)v.Kp;
                s.Rig.Note(string.Create(Inv,
                    $"6° to the {(by > 0 ? "right" : "left")}: {t.PastDeg:F1}° past, ends {t.EndsOffDeg:F1}° off, still after {t.SettleSeconds:F1} s (this side stops moving within {deadBand:F1}° of the target)"));
                if (t.Swings >= 3 || t.PastDeg > 1.5)
                    s.Report(new Finding(Severity.Bug, "TUNE_VALUES_SWING_THE_WHEELS",
                        string.Create(Inv, $"With the values found ({v}) a 6° turn goes {t.PastDeg:F1}° past the target and crosses it {t.Swings} times.")));
                else if (t.EndsOffDeg > deadBand + 0.6 || t.SettleSeconds > 2.5)
                    s.Report(new Finding(Severity.Inconsistency, "TUNE_VALUES_DO_NOT_GET_THERE",
                        string.Create(Inv, $"With the values found ({v}) a 6° turn ends {t.EndsOffDeg:F1}° off after {t.SettleSeconds:F1} s.")));
            }
        });
        await s.StepAsync("Next to the end, Finish; the profile on disk carries the values", async () =>
        {
            for (int i = 0; i < 6 && await s.Rig.ReadAsync(() => s.Rig.Vm.IsSteerWizardOpen); i++)
                await s.NextAsync();
            await s.SettleAsync(600);
            var files = Directory.GetFiles(s.Rig.DataRoot, "*.json", SearchOption.AllDirectories)
                .Where(p => p.Contains("Vehicle", StringComparison.OrdinalIgnoreCase)).ToList();
            string text = string.Join("\n", files.Select(File.ReadAllText));
            bool saved = Regex.IsMatch(text, "\"MinPwm\"\\s*:\\s*" + v.Min.ToString(Inv) + "\\b")
                         && Regex.IsMatch(text, "\"MaxPwm\"\\s*:\\s*" + v.Max.ToString(Inv) + "\\b")
                         && Regex.IsMatch(text, "\"ProportionalGain\"\\s*:\\s*" + v.Kp.ToString(Inv) + "\\b");
            if (!saved)
                s.Report(new Finding(Severity.Bug, "TUNE_VALUES_NOT_SAVED",
                    $"After Finish the vehicle profile on disk does not carry the values found ({v})."));
        });
        if (driveLine)
            await CarefulOperator.DriveAbLineAsync(s, "with the values the tuning found");
    }

    // ------------------------------------------------------------ the careless operator

    /// <summary>Continue, and wait until the page is in <paramref name="part"/>, then a moment more.</summary>
    private static async Task<bool> UntilPartAsync(WizardScenario s, string part, int thenMs)
    {
        await s.ActAsync("Continue");
        await Task.Delay(400);
        var f = await s.WaitResultAsync(150000, d => d.TestStage != 1 || d.TestPhase.StartsWith(part, StringComparison.Ordinal));
        if (f == null || f.TestStage != 1)
            return false;
        await Task.Delay(thenMs);
        return true;
    }

    /// <summary>After a run was cut short: the module let go, the operator's own values back in
    /// the settings and in the module, and (with the page still there) why it stopped.</summary>
    private static async Task AftermathAsync(WizardScenario s, Values before, long sinceMs, string? mustSay)
    {
        var m = s.Module;
        var rig = s.Rig;
        await s.SettleAsync(900);
        var after = m.SamplesSince(sinceMs);
        long lastDrive = after.Where(x => x.Enabled && Math.Abs(x.Pwm) >= m.LowestBreakawayPwm).Select(x => x.Ms).DefaultIfEmpty(sinceMs).Max();
        double wentOn = (lastDrive - sinceMs) / 1000.0;
        bool freeDrive = await rig.ReadAsync(() => rig.AutoSteer.IsInFreeDriveMode);
        var now = await ValuesAsync(s);
        string page = (await rig.FrameAsync())?.TestResult ?? "";
        string says = page.Length > 0 ? "; page: " + FirstLine(page) : "";
        rig.Note(string.Create(Inv,
            $"the motor drove for {wentOn:F2} s after it; Free Drive {(freeDrive ? "ON" : "off")}; settings: {now}; module: {ModuleValues(m)}{says}"));
        if (freeDrive || m.MotorEnabled)
            s.Report(new Finding(Severity.Safety, "TUNE_MODULE_NOT_LET_GO",
                "A second after the tuning was cut short the module is still steering (Free Drive " + (freeDrive ? "on" : "off") + ")."));
        if (now != before)
            s.Report(new Finding(Severity.Safety, "TUNE_TEST_VALUES_LEFT_IN_THE_SETTINGS",
                $"The tuning was cut short and its test values stayed in the settings: {now} (the operator's own: {before}). AutoSteer would steer with values nobody chose."));
        else if (ModuleValues(m) != OnTheWire(before))
            s.Report(new Finding(Severity.Safety, "TUNE_TEST_VALUES_LEFT_IN_THE_MODULE",
                $"The tuning was cut short; the settings show the operator's own values ({before}) but the module still runs {ModuleValues(m)}."));
        if (mustSay != null && !page.Contains(mustSay, StringComparison.OrdinalIgnoreCase))
            s.Report(new Finding(Severity.Inconsistency, "TUNE_DOES_NOT_SAY_WHY_IT_STOPPED",
                "The tuning stopped and the page does not say why (expected \"" + mustSay + "\"): " + FirstLine(page)));
    }

    private static string FirstLine(string s)
    {
        int i = s.IndexOf('\n', StringComparison.Ordinal);
        return i < 0 ? s : s[..i];
    }

    private static async Task MidRunAsync(ScenarioJournal j, string name, Action<EmulatedSteerModule> steering, Stop stop,
        string part, Mistake mistake, string? mustSay)
    {
        await using var s = await WizardScenario.StartAsync(j, name, "careless", steering);
        var m = s.Module;
        await PrepareAsync(s, stop);
        var before = await ValuesAsync(s);
        bool ready = false;
        await s.StepAsync("Start, the stop check as asked, Continue; waits until the page is at " + part, async () =>
        {
            if (!await StopCheckAsync(s)) return;
            ready = await UntilPartAsync(s, part, 1200);
        });
        if (!ready)
        {
            s.Report(new Finding(Severity.Bug, "HARNESS_TUNE_PART_NOT_REACHED", "The tuning never got to " + part + ", so the story did not happen: " + ((await s.Rig.FrameAsync())?.TestResult ?? "")));
            return;
        }
        string what = mistake switch
        {
            Mistake.Hands => "Takes the steering wheel and holds it",
            Mistake.SwitchOff => "Switches the steer switch off",
            Mistake.Cancel => "✕: closes the wizard",
            Mistake.Next => "Presses Next",
            _ => "The browser is gone (tablet asleep)",
        };
        await s.StepAsync(what + " while the wheels are being moved", async () =>
        {
            long at = m.NowMs;
            switch (mistake)
            {
                case Mistake.Hands: s.Hands(m.WheelAngleDeg); break;
                case Mistake.SwitchOff: m.SteerSwitchClosed = false; break;
                case Mistake.Cancel: await s.CancelAsync(); break;
                case Mistake.Next: await s.NextAsync(); break;
                case Mistake.BrowserLost: await s.DropControlAsync(); break;
            }
            // A hand on the wheel with nothing to feel it: the step has to notice by itself.
            await s.WaitTestEndAsync(20000);
            if (mistake == Mistake.Hands)
            {
                var held = m.SamplesSince(at).Where(x => x.Hand != null && x.Enabled && Math.Abs(x.Pwm) >= m.LowestBreakawayPwm).ToList();
                double pushed = held.Count == 0 ? 0 : (held[^1].Ms - at) / 1000.0;
                int hardest = held.Count == 0 ? 0 : held.Max(x => Math.Abs(x.Pwm));
                s.Rig.Note(string.Create(Inv, $"the motor pushed against the hand for {pushed:F1} s, up to PWM {hardest}"));
            }
            await AftermathAsync(s, before, at, mustSay);
            s.Hands(null);
            m.SteerSwitchClosed = true;
        }, Expect.Any);
        if (mistake == Mistake.BrowserLost)
            await s.StepAsync("The page comes back (reload)", async () => { await s.ReloadAsync(); await s.SettleAsync(400); });
    }

    private static async Task MotorBackwardsAsync(ScenarioJournal j, string name)
    {
        await using var s = await WizardScenario.StartAsync(j, name, "careless", m => { WheelMotor(m); m.TruthMotorReversed = true; });
        var m = s.Module;
        await PrepareAsync(s, Stop.Switch, motorDirectionKnown: false);
        var before = await ValuesAsync(s);
        await s.StepAsync("Start, the stop check as asked, Continue", async () =>
        {
            double from = m.WheelAngleDeg;
            long at = m.NowMs;
            if (!await StopCheckAsync(s))
            {
                s.Rig.Note("the stop check did not pass: " + ((await s.Rig.FrameAsync())?.TestResult ?? ""));
                return;
            }
            var run = await RunAsync(s, 60000);
            s.Rig.Note(string.Create(Inv, $"wheels went {run.FarthestDeg:F1}° from where they started; page: {FirstLine(run.Frame?.TestResult ?? "")}"));
            if (run.Frame?.TestStage == 3)
                s.Report(new Finding(Severity.Safety, "TUNE_GIVES_VALUES_WITH_THE_MOTOR_BACKWARDS",
                    "The motor is wired the other way and the tuning still came out with values."));
            if (run.FarthestDeg > 10)
                s.Report(new Finding(Severity.Safety, "TUNE_RUNS_AWAY_WITH_THE_MOTOR_BACKWARDS",
                    string.Create(Inv, $"With the motor wired the other way the tuning let the wheels run {run.FarthestDeg:F0}° before it stopped.")));
            await AftermathAsync(s, before, at, "wrong way");
        }, Expect.Any);
    }

    private static async Task MotorBackwardsHoldAsync(ScenarioJournal j, string name)
    {
        await using var s = await WizardScenario.StartAsync(j, name, "careless", m => { WheelMotor(m); m.TruthMotorReversed = true; });
        var m = s.Module;
        await PrepareAsync(s, Stop.Switch, motorDirectionKnown: false);
        await s.StepAsync("Had typed Minimum To Move 40 on the AutoSteer page (the wheels move from 28)", async () =>
        {
            await s.SetAsync("autosteer.minPwm", "40");
            await s.SettleAsync(500);
        });
        var before = await ValuesAsync(s);
        await s.StepAsync("Start (the module is only asked to hold the wheels for the stop check)", async () =>
        {
            double from = m.WheelAngleDeg;
            long at = m.NowMs;
            await s.ActAsync("StartTest");
            await s.WaitTestEndAsync(40000);
            var samples = m.SamplesSince(at);
            double far = samples.Count == 0 ? 0 : samples.Max(x => Math.Abs(x.Angle - from));
            var f = await s.Rig.FrameAsync();
            s.Rig.Note(string.Create(Inv, $"wheels went {far:F1}° from where they were; page: {FirstLine(f?.TestResult ?? "")}"));
            if (far > 10)
                s.Report(new Finding(Severity.Safety, "TUNE_STOP_CHECK_RUNS_AWAY",
                    string.Create(Inv, $"Holding the wheels for the stop check with the motor wired the other way let them run {far:F0}°.")));
            if (f?.TestStage == 2)
                s.Rig.Note("the stop check waited for the operator (the wheels held still)");
            await s.ActAsync("StopTest");
            await AftermathAsync(s, before, at, far > 1 ? "only asked to hold" : null);
        }, Expect.Any);
    }

    private static async Task RefusalsAsync(ScenarioJournal j, string name)
    {
        await using var s = await WizardScenario.StartAsync(j, name, "careless", WheelMotor);
        var m = s.Module;
        await PrepareAsync(s, Stop.Switch);
        var before = await ValuesAsync(s);

        async Task NothingMovedAsync(long since, double from, string what, string mustSay)
        {
            await s.SettleAsync(600);
            var samples = m.SamplesSince(since);
            bool steered = samples.Any(x => x.Enabled);
            double far = samples.Count == 0 ? 0 : samples.Where(x => x.Hand == null).Select(x => Math.Abs(x.Angle - from)).DefaultIfEmpty(0).Max();
            var f = await s.Rig.FrameAsync();
            string page = f?.TestResult ?? "";
            s.Rig.Note("page: " + (page.Length > 0 ? FirstLine(page) : "(nothing)") + (steered ? "; the module was steering" : "; the module never steered"));
            if (f?.TestStage is 2 or 3)
                s.Report(new Finding(Severity.Bug, "TUNE_GOES_ON_WHEN_IT_SHOULD_REFUSE", what + ": the page goes on (stage " + f.TestStage.ToString(Inv) + ")."));
            if (far > 1.0)
                s.Report(new Finding(Severity.Safety, "TUNE_MOVES_THE_WHEELS_WHEN_IT_SHOULD_REFUSE",
                    string.Create(Inv, $"{what}: the wheels moved {far:F1}°.")));
            if (!page.Contains(mustSay, StringComparison.OrdinalIgnoreCase))
                s.Report(new Finding(Severity.Inconsistency, "TUNE_DOES_NOT_SAY_WHY_IT_REFUSED", what + ": the page does not say why (expected \"" + mustSay + "\"): " + FirstLine(page)));
        }

        await s.StepAsync("Wheels left 15° to the right, Start", async () =>
        {
            s.Hands(15);
            await s.SettleAsync(400);
            s.Hands(null);
            await s.SettleAsync(200);
            long at = m.NowMs;
            await s.ActAsync("StartTest");
            await NothingMovedAsync(at, 15, "Start with the wheels at 15°", "Straighten the wheels");
            s.Hands(0);
            await s.SettleAsync(400);
            s.Hands(null);
        }, Expect.Any);
        await s.StepAsync("Driving 9 km/h, Start", async () =>
        {
            s.Drive(9);
            await s.SettleAsync(2500);
            long at = m.NowMs;
            double from = m.WheelAngleDeg;
            await s.ActAsync("StartTest");
            await NothingMovedAsync(at, from, "Start at 9 km/h", "Too fast");
            s.Drive(0);
            await s.SettleAsync(1500);
        }, Expect.Any);
        await s.StepAsync("Sends Continue without Start (no stop check yet)", async () =>
        {
            long at = m.NowMs;
            double from = m.WheelAngleDeg;
            await s.ActAsync("Continue");
            await s.SettleAsync(1500);
            var samples = m.SamplesSince(at);
            bool steered = samples.Any(x => x.Enabled);
            var f = await s.Rig.FrameAsync();
            s.Rig.Note(steered ? "the module steered" : "nothing happened");
            if (steered || f?.TestActive == true)
                s.Report(new Finding(Severity.Safety, "TUNE_RUNS_WITHOUT_THE_STOP_CHECK",
                    "Continue sent before any stop check started the tuning: the wheels move by themselves and the stop was never tried."));
        }, Expect.Any);
        await s.StepAsync("Start, then does nothing for the stop check (reads the manual instead), 32 s", async () =>
        {
            long at = m.NowMs;
            double from = m.WheelAngleDeg;
            await s.ActAsync("StartTest");
            await Task.Delay(32000);
            await s.WaitTestEndAsync(5000);
            await NothingMovedAsync(at, from, "Stop check not done", "not seen to stop");
            await AftermathAsync(s, before, at, null);
        }, Expect.Any);
    }

    private static async Task NotTakenAsync(ScenarioJournal j, string name)
    {
        await using var s = await WizardScenario.StartAsync(j, name, "careless", WheelMotor);
        var m = s.Module;
        await PrepareAsync(s, Stop.Switch);
        var before = await ValuesAsync(s);

        async Task<bool> ToValuesAsync()
        {
            if (!await StopCheckAsync(s)) return false;
            var run = await RunAsync(s);
            s.Rig.Note("page: " + FirstLine(run.Frame?.TestResult ?? ""));
            return run.Frame?.TestStage == 3;
        }

        async Task OwnValuesAsync(string after)
        {
            await s.SettleAsync(800);
            var now = await ValuesAsync(s);
            s.Rig.Note($"settings: {now}; module: {ModuleValues(m)}");
            if (now != before)
                s.Report(new Finding(Severity.Bug, "TUNE_VALUES_TAKEN_WITHOUT_THE_OPERATOR",
                    $"{after}: the settings hold {now}, the operator's own were {before} and Use these values was never pressed."));
            else if (ModuleValues(m) != OnTheWire(before))
                s.Report(new Finding(Severity.Bug, "TUNE_VALUES_AT_THE_MODULE_WITHOUT_THE_OPERATOR",
                    $"{after}: the module runs {ModuleValues(m)}, the settings show {before}."));
        }

        bool got = false;
        await s.StepAsync("The whole tuning, to its values", async () => got = await ToValuesAsync());
        if (!got)
        {
            s.Report(new Finding(Severity.Bug, "HARNESS_TUNE_GAVE_NO_VALUES", "The tuning gave no values, so the story did not happen."));
            return;
        }
        await s.StepAsync("Presses Next without choosing, then Back", async () =>
        {
            await s.NextAsync();
            await OwnValuesAsync("Next with the values found and not taken");
            await s.BackAsync();
            var f = await s.Rig.FrameAsync();
            s.Rig.Note("back on the tuning page: " + (f?.TestStage == 0 ? "Start offered, the values found are gone" : "stage " + (f?.TestStage ?? -1).ToString(Inv)));
            if (f?.TestStage == 3)
                s.Report(new Finding(Severity.Inconsistency, "TUNE_OLD_RESULT_OFFERED_AGAIN",
                    "After Next and Back the page still offers Use these values for a run from before the step was left."));
        }, Expect.Any);
        await s.StepAsync("The whole tuning again", async () => got = await ToValuesAsync());
        if (!got) return;
        await s.StepAsync("Keep the old values", async () =>
        {
            await s.ActAsync("Discard");
            await OwnValuesAsync("Keep the old values");
            var f = await s.Rig.FrameAsync();
            if (f?.TestStage != 0)
                s.Report(new Finding(Severity.Bug, "TUNE_KEEP_OLD_VALUES_DOES_NOT_END_IT", "After Keep the old values the page is not back at Start."));
        });
    }
}
