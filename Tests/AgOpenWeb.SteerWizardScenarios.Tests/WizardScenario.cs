// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using AgOpenWeb.RemoteServer;
using AgOpenWeb.ViewModels.Wizards.SteerWizard;

namespace AgOpenWeb.SteerWizardScenarios.Tests;

/// <summary>
/// One operator working through one story on a fresh tractor. It does what the web client
/// does (the same commands, the same rules about what it sends) and after every step collects
/// what the operator would see (the Wizard frame: step, result, hint, validation; status and
/// failure messages), what the safety watch saw, and the invariants.
/// </summary>
public sealed class WizardScenario : IAsyncDisposable
{
    public SteerWizardRig Rig { get; }
    public SafetyWatch Watch { get; }
    public EmulatedSteerModule Module => Rig.Module;
    private readonly ScenarioJournal _journal;
    private readonly ScenarioRecord _record;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _index;
    private StepRecord? _current;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The browser's own state: overlay open, and holding control.</summary>
    public bool OverlayOpen { get; private set; }
    public bool HoldsControl { get; private set; } = true;
    public WizardDto? Last { get; private set; }

    private WizardScenario(SteerWizardRig rig, ScenarioJournal journal, string name, string op)
    {
        Rig = rig;
        Watch = new SafetyWatch(rig);
        _journal = journal;
        _record = new ScenarioRecord { Name = name, Operator = op };
        journal.Scenarios.Add(_record);
    }

    public static async Task<WizardScenario> StartAsync(ScenarioJournal journal, string name, string op,
        Action<EmulatedSteerModule>? truth = null, Action<SteerWizardRig>? setup = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "aow-steerwiz-" + Guid.NewGuid().ToString("N")[..8]);
        var rig = await SteerWizardRig.StartAsync(root, truth);
        setup?.Invoke(rig);
        var s = new WizardScenario(rig, journal, name, op);
        var m = rig.Module;
        s._record.Truth = string.Create(Inv,
            $"sensor {m.TruthCpd:F0} counts/°, centre {m.TruthCentreCounts:+0;-0} counts, sensor {(m.TruthWasReversed ? "reversed" : "normal")}, motor {(m.TruthMotorReversed ? "wired backwards" : "normal")}, Ackermann {m.TruthAckermann:F0}, locks {m.LockLeftDeg:F0}°/{m.LockRightDeg:F0}°, break-away PWM {m.BreakawayPwm}, wheelbase {rig.TruthWheelbase:F2} m");
        return s;
    }

    // ------------------------------------------------------------ the step runner

    public async Task StepAsync(string what, Func<Task> act, Expect expect = Expect.Works)
    {
        var rec = new StepRecord { Index = ++_index, What = what, Expect = expect };
        Rig.TakeEvents();
        var before = Last;
        var sw = Stopwatch.StartNew();
        _current = rec;
        try { await act(); }
        catch (Exception ex)
        {
            rec.Findings.Add(new Finding(Severity.Bug, "HARNESS_STEP_THREW", what + ": " + ex.GetType().Name + ": " + ex.Message));
        }
        _current = null;
        rec.Ms = sw.ElapsedMilliseconds;
        Last = await Rig.FrameAsync();

        foreach (var e in Rig.TakeEvents())
        {
            switch (e.Kind)
            {
                case RigEventKind.Exception:
                    rec.Findings.Add(new Finding(Severity.Bug, "HOST_EXCEPTION", FirstLine(e.Text)));
                    break;
                case RigEventKind.Hang:
                    rec.Findings.Add(new Finding(Severity.Bug, "HOST_HANG", e.Text));
                    break;
                case RigEventKind.Error:
                    rec.Findings.Add(new Finding(Severity.Bug, "HOST_ERROR_LOG", FirstLine(e.Text)));
                    break;
                case RigEventKind.Failure:
                case RigEventKind.Status:
                case RigEventKind.Note:
                case RigEventKind.Warning:
                    if (e.Text.Length > 0) rec.Says.Add(e.Text);
                    break;
            }
        }
        if (Last is { } f)
        {
            if (f.TestResult.Length > 0 && f.TestResult != before?.TestResult) rec.Says.Add("result: " + f.TestResult);
            if (f.RecordHint.Length > 0 && f.RecordHint != before?.RecordHint) rec.Says.Add("hint: " + f.RecordHint);
            if (f.Validation.Length > 0) rec.Says.Add("validation: " + f.Validation);
        }
        rec.Says.AddRange(Watch.TakeNotes());
        rec.Findings.AddRange(Watch.TakeFindings());
        rec.Findings.AddRange(await InvariantsAsync());
        rec.After = await StateAsync();
        foreach (var fi in rec.Findings) _journal.Add(fi, _record.Name + " #" + rec.Index.ToString(Inv) + " (" + what + ")");
        _record.Steps.Add(rec);
        _record.Ms = _clock.ElapsedMilliseconds;
        if (Environment.GetEnvironmentVariable("AGOPENWEB_SCENARIO_VERBOSE") == "1")
            TestContext.Progress.WriteLine($"[{_record.Name}] {rec.Index}. {what} — {string.Join(" / ", rec.Says)} {string.Join("; ", rec.Findings.Select(x => x.Code))} | {rec.After}");
    }

    /// <summary>A finding the scenario itself decides (a check on what the step did).</summary>
    public void Report(Finding f)
    {
        // During a step: that step (StepAsync files it in the journal when the step ends).
        if (_current != null) { _current.Findings.Add(f); return; }
        var st = _record.Steps.Count > 0 ? _record.Steps[^1] : null;
        if (st == null) return;
        st.Findings.Add(f);
        _journal.Add(f, _record.Name + " #" + st.Index.ToString(Inv) + " (" + st.What + ")");
    }

    public async Task CheckAsync(bool ok, Finding f) { if (!ok) Report(f); await Task.CompletedTask; }

    private static string FirstLine(string s) { int i = s.IndexOf('\n'); return i < 0 ? s : s[..i]; }

    // ------------------------------------------------------------ what the web client does

    public Task Send(string cmd, string arg = "") => Rig.SendAsync(cmd, arg);

    /// <summary>Tools → Steer Wizard (openSteerWizard).</summary>
    public async Task OpenAsync()
    {
        OverlayOpen = true;
        await Send("wizard.open");
        await Task.Delay(100);
    }

    public async Task<bool> NextAsync()
    {
        var before = await Rig.FrameAsync();
        if (before?.IsLast == true) { await FinishAsync(); return true; }
        await Send("wizard.next");
        await Task.Delay(80);
        var after = await Rig.FrameAsync();
        return after?.StepIndex != before?.StepIndex;
    }

    public async Task BackAsync() { await Send("wizard.back"); await Task.Delay(80); }
    public async Task SkipAsync() { await Send("wizard.skip"); await Task.Delay(80); }

    /// <summary>Finish: the client sends wizard.finish and closes its overlay at once.</summary>
    public async Task FinishAsync() { await Send("wizard.finish"); OverlayOpen = false; await Task.Delay(100); }

    /// <summary>Cancel (the ✕): wizard.cancel and the overlay closes.</summary>
    public async Task CancelAsync() { await Send("wizard.cancel"); OverlayOpen = false; await Task.Delay(100); }

    /// <summary>A field or a segment button: config.set|key:value (cfgSend).</summary>
    public Task SetAsync(string key, string value) => Send("config.set", key + ":" + value);
    public Task SetAsync(string key, double value) => SetAsync(key, value.ToString("R", Inv));

    /// <summary>A toggle: the client sends the opposite of what its Config frame shows.</summary>
    public async Task ToggleAsync(string key, Func<bool> current) => await SetAsync(key, current() ? "0" : "1");

    public Task HardwareAsync(int level) => Send("wizard.hw", level.ToString(Inv));

    /// <summary>A test/calibration button (rnSend: only sent while holding control).</summary>
    public async Task ActAsync(string cmd)
    {
        if (!HoldsControl) return;
        await Send("wizard.action", cmd);
    }

    public async Task DropControlAsync()
    {
        HoldsControl = false;
        Watch.ControlHeld = false;
        OverlayOpen = false; // the page is gone
        await Rig.DropControlAsync();
    }

    /// <summary>The page reloads and takes the free seat: a fresh client with no wizard open.</summary>
    public async Task ReloadAsync()
    {
        await Rig.TakeControlAsync();
        HoldsControl = true;
        Watch.ControlHeld = true;
        OverlayOpen = false;
    }

    public async Task<string> KindAsync() => (await Rig.FrameAsync())?.StepKind ?? "(none)";

    /// <summary>Next until the step is <paramref name="kind"/> (or the wizard ends).</summary>
    public async Task GoToAsync(string kind, int max = 20)
    {
        for (int i = 0; i < max; i++)
        {
            var f = await Rig.FrameAsync();
            if (f == null || f.StepKind == kind) return;
            if (!await NextAsync())
            {
                // Blocked: skip if the step allows it, else stop trying.
                var g = await Rig.FrameAsync();
                if (g?.CanSkip == true) await SkipAsync(); else return;
            }
        }
    }

    /// <summary>Wait until the step's test is over: no test flag and no Free Drive for 400 ms.</summary>
    public async Task<bool> WaitTestEndAsync(int timeoutMs = 40000)
    {
        var sw = Stopwatch.StartNew();
        int quiet = 0;
        await Task.Delay(300);
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var f = await Rig.FrameAsync();
            bool busy = (f?.TestActive ?? false) || Rig.AutoSteer.IsInFreeDriveMode;
            quiet = busy ? 0 : quiet + 1;
            if (quiet >= 4) return true;
            await Task.Delay(100);
        }
        return false;
    }

    /// <summary>Wait until the frame says something new (result text) or time runs out.</summary>
    public async Task<WizardDto?> WaitResultAsync(int timeoutMs, Func<WizardDto, bool>? done = null)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var f = await Rig.FrameAsync();
            if (f != null && (done?.Invoke(f) ?? (!f.TestActive && f.TestResult.Length > 0))) return f;
            await Task.Delay(100);
        }
        return await Rig.FrameAsync();
    }

    // ------------------------------------------------------------ the operator's body

    public void Hands(double? deg) => Module.HandAngleDeg = deg;
    public void Drive(double kmh) => Rig.SpeedKmh = kmh;

    public async Task SettleAsync(int ms = 400) { await Task.Delay(ms); await Rig.DrainAsync(); }

    // ------------------------------------------------------------ invariants

    private async Task<List<Finding>> InvariantsAsync()
    {
        var list = new List<Finding>();
        var (open, freeDrive, store) = await Rig.ReadAsync(() => (Rig.Vm.IsSteerWizardOpen, Rig.AutoSteer.IsInFreeDriveMode, Rig.Store));
        var a = store.AutoSteer;

        // The host holds a wizard open that no screen shows: AutoSteer then refuses to engage
        // ("AutoSteer is off while the steer wizard is open") with nothing to close.
        if (open && !OverlayOpen)
            list.Add(new Finding(Severity.Bug, "HIDDEN_WIZARD_BLOCKS_AUTOSTEER",
                "The host still has the steer wizard open but no browser shows it (page reloaded / control lost): AutoSteer refuses to engage until the wizard is opened and closed again."));

        // Values that make AutoSteer steer dangerously or not at all.
        if (a.Ackermann is < 50 or > 160)
            list.Add(new Finding(Severity.Safety, "ACKERMANN_EXTREME",
                string.Create(Inv, $"Ackermann is {a.Ackermann} (the module multiplies every LEFT reading by {a.Ackermann / 100.0:F2}): left turns read far too small/large, so AutoSteer drives the wheels toward the left lock before the reading catches up.")));
        if (a.CountsPerDegree is < 20)
            list.Add(new Finding(Severity.Bug, "CPD_EXTREME",
                string.Create(Inv, $"Counts per degree is {a.CountsPerDegree:F0}: every angle reads {(Module.TruthCpd / Math.Max(1, a.CountsPerDegree)):F0}× too large.")));
        if (store.Vehicle.MaxSteerAngle < 10)
            list.Add(new Finding(Severity.Bug, "MAX_STEER_ANGLE_TINY",
                string.Create(Inv, $"Max steer angle is {store.Vehicle.MaxSteerAngle:F0}°: guidance can hardly steer.")));
        if (store.Vehicle.Wheelbase is < 0.5 or > 15 || double.IsNaN(store.Vehicle.Wheelbase))
            list.Add(new Finding(Severity.Bug, "WHEELBASE_INVALID",
                string.Create(Inv, $"Wheelbase is {store.Vehicle.Wheelbase} m in the live settings (the wizard's step allows only 0.5–15 m).")));
        if (store.Vehicle.TrackWidth is < 0.5 or > 10)
            list.Add(new Finding(Severity.Bug, "TRACK_WIDTH_INVALID",
                string.Create(Inv, $"Track width is {store.Vehicle.TrackWidth} m in the live settings (the step allows only 0.5–10 m).")));
        if (a.MinSteerSpeed >= a.MaxSteerSpeed)
            list.Add(new Finding(Severity.Bug, "SPEED_RANGE_EMPTY",
                string.Create(Inv, $"Min steer speed {a.MinSteerSpeed} ≥ max steer speed {a.MaxSteerSpeed} km/h in the live settings: AutoSteer can never steer.")));
        if (a.ProportionalGain is < 1 or > 200)
            list.Add(new Finding(Severity.Bug, "KP_OUT_OF_RANGE",
                string.Create(Inv, $"Proportional gain is {a.ProportionalGain} in the live settings (the step allows only 1–200; the module gets it clamped).")));

        // The module must agree with the store once nothing is changing (the 150 ms debounce).
        if (!freeDrive)
        {
            await Task.Delay(350);
            var m = Module;
            var (kp, cpd, off, ack, iw, im) = await Rig.ReadAsync(() => (a.ProportionalGain, a.CountsPerDegree, a.WasOffset, a.Ackermann, a.InvertWas, a.InvertMotor));
            if (m.Kp != (byte)Math.Clamp(kp, 1, 200) || m.Cpd != (byte)Math.Clamp((int)Math.Round(cpd, MidpointRounding.AwayFromZero), 1, 255)
                || m.WasOffset != (short)off || m.InvertWas != iw || m.MotorDriveDirection != im
                || Math.Abs(m.AckermanFix * 100 - Math.Clamp(ack, 0, 200)) > 0.6)
                list.Add(new Finding(Severity.Bug, "MODULE_OUT_OF_STEP",
                    string.Create(Inv, $"The module runs other settings than AgOpenWeb shows: module Kp {m.Kp} CPD {m.Cpd} offset {m.WasOffset} Ack {m.AckermanFix * 100:F0} invWAS {m.InvertWas} invMotor {m.MotorDriveDirection}; host {kp} {cpd:F0} {off} {ack} {iw} {im}.")));
        }
        return list;
    }

    public async Task<string> StateAsync()
    {
        var (open, kind, fd) = await Rig.ReadAsync(() => (Rig.Vm.IsSteerWizardOpen,
            Rig.Vm.SteerWizardViewModel?.CurrentStep?.GetType().Name.Replace("StepViewModel", "") ?? "-", Rig.AutoSteer.IsInFreeDriveMode));
        var s = Rig.Store;
        var a = s.AutoSteer;
        var m = Module;
        return string.Create(Inv,
            $"wizard {(open ? kind : "closed")}{(fd ? ", FREE DRIVE" : "")}; wheels {m.WheelAngleDeg:F1}° (reads {m.ReportedAngleDeg:F1}°); Kp {a.ProportionalGain}, minPWM {a.MinPwm}, CPD {a.CountsPerDegree:F0}, offset {a.WasOffset}, Ack {a.Ackermann}, invWAS {(a.InvertWas ? 1 : 0)}, invMotor {(a.InvertMotor ? 1 : 0)}, maxAngle {s.Vehicle.MaxSteerAngle:F0}°, wheelbase {s.Vehicle.Wheelbase:F2}, roll zero {s.Ahrs.RollZero:F1} inv {(s.Ahrs.IsRollInvert ? 1 : 0)}, speed {a.MinSteerSpeed:F1}–{a.MaxSteerSpeed:F1}");
    }

    public async ValueTask DisposeAsync()
    {
        Watch.Dispose();
        await Rig.DisposeAsync();
        try { Directory.Delete(Rig.DataRoot, true); } catch { }
    }
}
