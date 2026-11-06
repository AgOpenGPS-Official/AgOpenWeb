// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Globalization;

namespace AgOpenWeb.SteerWizardScenarios.Tests;

/// <summary>
/// Watches the wheels and the host 50 times a second for the whole scenario, independent of
/// what the operator is doing, and raises a Safety finding when:
/// <list type="bullet">
/// <item>the motor moves the wheels with neither Free Drive nor AutoSteer on (module steering
/// uncommanded);</item>
/// <item>Free Drive stays on with the wizard closed or with no browser holding control — the
/// wheels can move and nobody has a screen or a Stop button;</item>
/// <item>Free Drive stays on after the step that started it was left;</item>
/// <item>AutoSteer is engaged while the wizard is open (the wizard owns the steering);</item>
/// <item>the motor pushes the wheels against a mechanical lock for more than 2.5 s;</item>
/// <item>a runaway: the wheels drive away from the commanded angle (positive feedback, the
/// motor direction is wrong) by more than 6° within a second;</item>
/// <item>the motor drives against the operator's hands.</item>
/// </list>
/// Each episode is reported once, when it crosses its limit, then once more when it ends with
/// how long it lasted and how far the wheels went.
/// </summary>
public sealed class SafetyWatch : IDisposable
{
    private readonly SteerWizardRig _rig;
    private readonly ConcurrentQueue<Finding> _found = new();
    private readonly ConcurrentQueue<string> _notes = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _task;

    /// <summary>Whether a browser holds control (the scenario sets it on a drop).</summary>
    public volatile bool ControlHeld = true;

    public SafetyWatch(SteerWizardRig rig)
    {
        _rig = rig;
        _task = Task.Run(() => LoopAsync(_cts.Token));
    }

    public List<Finding> TakeFindings()
    {
        var l = new List<Finding>();
        while (_found.TryDequeue(out var f)) l.Add(f);
        return l;
    }

    public List<string> TakeNotes()
    {
        var l = new List<string>();
        while (_notes.TryDequeue(out var n)) l.Add(n);
        return l;
    }

    private sealed class Episode
    {
        public long Since = -1;
        public bool Reported;
        public double Min = double.MaxValue, Max = double.MinValue;
        public string Where = "";
    }

    private readonly Dictionary<string, Episode> _ep = new();
    private object? _freeDriveStep;
    private readonly Queue<(long Ms, double Err)> _errHistory = new();
    private double _lastSetPoint = double.NaN;

    private static string Kind(object? step) => step?.GetType().Name.Replace("StepViewModel", "", StringComparison.Ordinal) ?? "none";

    private async Task LoopAsync(CancellationToken ct)
    {
        var inv = CultureInfo.InvariantCulture;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(20, ct); } catch (OperationCanceledException) { break; }
            var m = _rig.Module;
            long now = m.NowMs;
            bool fd = _rig.AutoSteer.IsInFreeDriveMode;
            bool eng = _rig.AutoSteer.IsEngaged;
            var w = _rig.Vm.SteerWizardViewModel; // read off the loop, as the projector does
            object? step = w?.CurrentStep;
            double angle = m.WheelAngleDeg;
            int pwm = m.PwmOut;
            bool enabled = m.MotorEnabled;
            bool driving = enabled && Math.Abs(pwm) >= m.BreakawayPwm;
            double? hand = m.HandAngleDeg;
            string where = "step " + Kind(step) + (w == null ? " (wizard closed)" : "");

            Track("uncommanded", driving && hand == null && !fd && !eng, 300, now, angle, where,
                d => new Finding(Severity.Safety, "MODULE_STEERS_UNCOMMANDED",
                    "The motor moved the wheels with neither Free Drive nor AutoSteer on (" + d + ")."));

            Track("noScreen", fd && (w == null || !ControlHeld), 600, now, angle, where,
                d => new Finding(Severity.Safety, w == null ? "FREE_DRIVE_AFTER_WIZARD_CLOSED" : "FREE_DRIVE_WITHOUT_BROWSER",
                    (w == null ? "Free Drive (the wizard's motor test) still on after the wizard closed"
                               : "Free Drive (the wizard's motor test) still on after the browser lost control — no screen, no Stop button")
                    + " (" + d + ")."));

            if (fd)
            {
                if (_freeDriveStep == null) _freeDriveStep = step ?? new object();
            }
            else _freeDriveStep = null;
            Track("otherStep", fd && step != null && _freeDriveStep != null && !ReferenceEquals(step, _freeDriveStep), 600, now, angle, where,
                d => new Finding(Severity.Safety, "FREE_DRIVE_AFTER_STEP_LEFT",
                    "Free Drive still on after the step that started it was left (" + d + ")."));

            Track("engaged", eng && w != null, 100, now, angle, where,
                d => new Finding(Severity.Safety, "AUTOSTEER_WHILE_WIZARD_OPEN",
                    "AutoSteer engaged while the steer wizard was open (" + d + ")."));

            var sample = m.SamplesSince(now - 40);
            bool atLock = sample.Count > 0 && sample[^1].AtLock;
            Track("lock", atLock, 2500, now, angle, where,
                d => new Finding(Severity.Safety, "PUSHING_AT_LOCK",
                    "The motor kept pushing the wheels against the mechanical lock (" + d + ")."));

            // Runaway: driving, hands off, and the error to a steady set point grows. A new set
            // point (the max-angle test steps 0 -> +40 -> 0 -> -40) makes the error jump without
            // the wheels doing anything wrong, so the history starts again on each change.
            double sp = m.SetPointDeg;
            if (double.IsNaN(_lastSetPoint) || Math.Abs(sp - _lastSetPoint) > 0.5) _errHistory.Clear();
            _lastSetPoint = sp;
            if (driving && hand == null)
            {
                double err = Math.Abs(m.ReportedAngleDeg - sp);
                _errHistory.Enqueue((now, err));
                while (_errHistory.Count > 0 && now - _errHistory.Peek().Ms > 1000) _errHistory.Dequeue();
                double grew = err - _errHistory.Min(x => x.Err);
                Track("runaway", grew > 6 && !atLock, 0, now, angle, where,
                    d => new Finding(Severity.Safety, "RUNAWAY",
                        "The wheels drove AWAY from the commanded angle (wrong motor direction: positive feedback) (" + d + ")."));
            }
            else
            {
                _errHistory.Clear();
                Track("runaway", false, 0, now, angle, where, _ => null!);
            }

            // A test has to try the motor to learn anything, so a short push against the hands
            // is in the nature of it; one that goes on is the test not noticing.
            Track("hands", driving && hand != null, 3000, now, angle, where,
                d => new Finding(Severity.Robustness, "MOTOR_AGAINST_HANDS",
                    "The motor kept driving against the operator's hands on the wheel for over 3 s (" + d + ")."));
        }
    }

    private void Track(string key, bool on, int limitMs, long now, double angle, string where, Func<string, Finding> make)
    {
        if (!_ep.TryGetValue(key, out var e)) _ep[key] = e = new Episode();
        var inv = CultureInfo.InvariantCulture;
        if (on)
        {
            if (e.Since < 0) { e.Since = now; e.Reported = false; e.Min = e.Max = angle; e.Where = where; }
            e.Min = Math.Min(e.Min, angle);
            e.Max = Math.Max(e.Max, angle);
            if (!e.Reported && now - e.Since >= limitMs)
            {
                e.Reported = true;
                _found.Enqueue(make(string.Create(inv, $"{e.Where}, wheels at {angle:F1}°")));
            }
        }
        else if (e.Since >= 0)
        {
            if (e.Reported)
                _notes.Enqueue(string.Create(inv, $"[watch] {key} lasted {(now - e.Since) / 1000.0:F1} s, wheels {e.Min:F1}° … {e.Max:F1}°"));
            e.Since = -1;
            e.Reported = false;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _task.Wait(1000); } catch { }
    }
}
