// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace AgOpenWeb.FieldJobScenarios.Tests;

/// <summary>
/// What the operator does on a job that is not an AB line: makes the line (a curve along the
/// boundary, the boundary as a loop, a water pivot), puts the tractor at the first pass, and,
/// where AgOpenWeb has no U-turn to make (a closed loop, a pivot, contour), moves over to the next
/// lap at the end of each one and decides when the field is done. The main loop of
/// <see cref="JobRun"/> calls <see cref="OnFixAsync"/> after every fix.
/// </summary>
public abstract class PathOperator
{
    public static PathOperator For(GuideKind k) => k switch
    {
        GuideKind.BoundarySegment => new CurveOperator(),
        GuideKind.BoundaryLoop or GuideKind.PivotCentre or GuideKind.PivotDriven => new LapOperator(k),
        GuideKind.Contour => new ContourOperator(),
        GuideKind.RecordedPath => new RecordedPathOperator(),
        _ => throw new ArgumentOutOfRangeException(nameof(k), k, "not a path job"),
    };

    public static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    /// <summary>False when the job measures something other than the work done (a recorded
    /// path driven with the sprayer off): the coverage checks are left out.</summary>
    public virtual bool JudgesCoverage => true;

    /// <summary>After the field opened, before the headland: make the line the job follows and
    /// set <see cref="JobRun.Frame"/>.</summary>
    public abstract Task<bool> CreateTrackAsync(JobRun r);
    /// <summary>Put the tractor at the first pass and get going (AutoSteer, sections).</summary>
    public abstract Task<bool> StartAsync(JobRun r);
    public virtual Task OnFixAsync(JobRun r) => Task.CompletedTask;
    /// <summary>Where pass <paramref name="pass"/> runs, as u in <see cref="JobRun.Frame"/>.</summary>
    public virtual double ExpectedU(JobRun r, int pass) => (pass + 0.5) * r.Spacing;
    public virtual void Metrics(Scenario sc, JobRun r) { }
    public virtual void Judge(Scenario sc, JobRun r, JobAreas a, Action<Severity, string, string> flag) { }

    // ------------------------------------------------------------------ helpers

    /// <summary>The line AgOpenWeb has active, in field E/N.</summary>
    protected static Task<List<(double E, double N)>?> ActiveTrackPointsAsync(JobRun r) =>
        r.Rig.ReadAsync(() => r.Rig.Vm.SelectedTrack?.Points.Select(p => (p.Easting, p.Northing)).ToList());

    /// <summary>The wheel angle a driver gives to reach <paramref name="target"/> (pure pursuit
    /// from the pivot axle), degrees, + = right.</summary>
    public static double PurePursuit(TruthMachine t, (double E, double N) target)
    {
        double dx = target.E - t.E, dy = target.N - t.N;
        double ld2 = dx * dx + dy * dy;
        if (ld2 < 0.01) return 0;
        double lateral = dx * Math.Cos(t.Heading) - dy * Math.Sin(t.Heading);
        double ahead = dx * Math.Sin(t.Heading) + dy * Math.Cos(t.Heading);
        if (ahead < 0.1) return lateral >= 0 ? t.V.MaxSteerAngle : -t.V.MaxSteerAngle;
        double delta = Math.Atan2(2 * t.V.Wheelbase * lateral, ld2) * 180 / Math.PI;
        return Math.Clamp(delta, -t.V.MaxSteerAngle, t.V.MaxSteerAngle);
    }

    /// <summary>The operator's hands on the wheel: keep the tractor on u (in the job's frame),
    /// looking <paramref name="lookM"/> ahead along it.</summary>
    protected static Func<double> Follow(JobRun r, Func<double> u, double lookM = 7) => () =>
    {
        var p = (r.Truth.E, r.Truth.N);
        return PurePursuit(r.Truth, r.Frame.At(u() - r.Spec.T.Offset, r.Frame.V(p) + lookM));
    };

    /// <summary>+1 when the field lies to the right of the reference (passes count up inwards),
    /// -1 when to the left: the side where half a width off the reference is inside the field.</summary>
    protected static int InsideSign(JobRun r)
    {
        double len = r.Frame.Closed ? r.Frame.Length : Math.Max(1, r.Frame.Length);
        int right = 0, left = 0;
        for (double v = 0; v < len; v += Math.Max(1, len / 200))
        {
            if (Ring.Inside(r.Frame.At(+r.Spacing, v), r.Outer)) right++;
            if (Ring.Inside(r.Frame.At(-r.Spacing, v), r.Outer)) left++;
        }
        return right >= left ? 1 : -1;
    }

    protected static async Task<bool> EngageAsync(JobRun r, bool sections = true)
    {
        await r.Rig.WaitForVmAsync();
        if (!await r.Rig.ReadAsync(() => r.Rig.Vm.IsAutoSteerEngaged)) await r.SendAsync("autosteer.toggle");
        if (!await r.Rig.ReadAsync(() => r.Rig.Vm.IsAutoSteerEngaged)) return false;
        if (sections && !await r.Rig.ReadAsync(() => r.Rig.Vm.IsSectionMasterOn)) await r.SendAsync("section.master");
        return true;
    }

    /// <summary>AgOpenWeb's line for pass <paramref name="pass"/> of the active curve, made the way
    /// it makes it (OffsetPassCache: the curve offset, then its bends limited to the U-turn radius;
    /// an open curve's ends then run on straight: GpsPipelineService). <paramref name="limit"/> and
    /// <paramref name="extend"/> false leave those steps out, to tell which one moved the line.</summary>
    protected static async Task<List<(double E, double N)>?> PassLineAsync(JobRun r, int pass, bool limit = true, bool extend = true)
    {
        var track = await r.Rig.ReadAsync(() => r.Rig.Vm.SelectedTrack);
        if (track == null) return null;
        var pts = await r.Rig.ReadAsync(() => track.Points.ToList());
        double dist = AgOpenWeb.Models.Track.PassGeometry.Offset(track, pass, r.Spacing);
        var made = AgOpenWeb.Models.Guidance.CurveProcessing.CreateOffsetCurveWithInfo(pts, dist, track.IsClosed).Points;
        if (limit) made = AgOpenWeb.Models.Guidance.CurveProcessing.LimitTurnRadius(made, r.Rig.Store.Guidance.UTurnRadius, track.IsClosed);
        if (extend && !track.IsClosed) made = AgOpenWeb.Models.Guidance.CurveProcessing.ExtendCurveEnds(made);
        return made.Select(p => (p.Easting, p.Northing)).ToList();
    }

    protected static double DistToLine((double E, double N) q, List<(double E, double N)> line)
    {
        double best = double.MaxValue;
        for (int i = 0; i + 1 < line.Count; i++) best = Math.Min(best, Ring.DistToSegment(q, line[i], line[i + 1]));
        return best;
    }

    /// <summary>How much nearer the reference than <paramref name="want"/> the line runs inside the
    /// cultivated area: the most, and where.</summary>
    protected static (double Short, (double E, double N) At) Shortfall(JobRun r, List<(double E, double N)>? line, double want, Func<(double E, double N), bool>? skip = null)
    {
        double worst = 0; (double E, double N) at = (0, 0);
        if (line == null) return (0, at);
        var work = r.Work ?? r.Outer;
        int segs = r.Frame.Closed ? line.Count : line.Count - 1;
        for (int i = 0; i < segs; i++)
        {
            var a = line[i]; var b = line[(i + 1) % line.Count];
            double l = Math.Sqrt((b.E - a.E) * (b.E - a.E) + (b.N - a.N) * (b.N - a.N));
            int n = Math.Max(1, (int)Math.Ceiling(l));
            for (int k = 0; k < n; k++)
            {
                var p = (a.E + (b.E - a.E) * k / n, a.N + (b.N - a.N) * k / n);
                if (!Ring.Inside(p, work) || skip?.Invoke(p) == true) continue;
                double s = want - Math.Abs(r.Frame.U(p));
                if (s > worst) { worst = s; at = p; }
            }
        }
        return (worst, at);
    }

    /// <summary>The tractor on pass <paramref name="k"/> at <paramref name="v"/>, facing along the line.</summary>
    protected static void PlaceOn(JobRun r, double u, double v)
    {
        var p = r.Frame.At(u - r.Spec.T.Offset, v);
        r.Truth.Place(p.E, p.N, r.Frame.TangentHeading(r.Frame.At(u, v)));
    }
}

/// <summary>
/// "Bnd. Curve": the operator taps two corners of the outer boundary and AgOpenWeb makes an open
/// curve along the fence between them; the passes are that curve offset across the field, with
/// automatic U-turns at the headland as on an AB line.
/// </summary>
public sealed class CurveOperator : PathOperator
{
    public override async Task<bool> CreateTrackAsync(JobRun r)
    {
        var (ia, ib, iv) = r.Spec.Segment;
        var f = r.Field.Outer;
        var a = r.Local(f[ia].Lat, f[ia].Lon); var b = r.Local(f[ib].Lat, f[ib].Lon); var via = r.Local(f[iv].Lat, f[iv].Lon);
        await r.SendAsync("track.boundaryCurveSeg", F($"{a.E:F2},{a.N:F2},{b.E:F2},{b.N:F2},{via.E:F2},{via.N:F2}"));
        var pts = await ActiveTrackPointsAsync(r);
        bool closed = await r.Rig.ReadAsync(() => r.Rig.Vm.SelectedTrack?.IsClosed == true);
        if (pts == null || pts.Count < 3 || closed) { r.Harness("Bnd. Curve made no open curve: " + string.Join(" | ", r.Collect())); return false; }
        r.Frame = new PolylineFrame(pts, closed: false);
        r.S.Step(F($"Bnd. Curve along the outer boundary from vertex {ia} ({a.E:0},{a.N:0}) to vertex {ib} ({b.E:0},{b.N:0}) by vertex {iv}: an open curve of {pts.Count} points, {r.Frame.Length:0} m with its extensions, active"));
        return true;
    }

    public override async Task<bool> StartAsync(JobRun r)
    {
        // As on an AB line: the first pass along the outside edge with at least 20 m of it in
        // the cultivated area, the tractor 12 m before the boundary facing in.
        var work = r.Work ?? r.Outer;
        double uMin = work.Min(r.U), uMax = work.Max(r.U);
        int kMin = (int)Math.Floor(uMin / r.Spacing) - 1, kMax = (int)Math.Ceiling(uMax / r.Spacing) + 1;
        int k = kMin;
        for (; k <= kMax && r.InsideChordM(k) < 20; k++) { }
        double u = (k + 0.5) * r.Spacing;
        // The longest stretch of the pass inside the cultivated area, and the boundary before it.
        double best = -1, startV = 0, run = 0, runStart = 0;
        for (double v = -200; v <= r.Frame.Length + 200; v += 1)
        {
            if (Ring.Inside(r.Frame.At(u, v), work)) { if (run++ == 0) runStart = v; if (run > best) { best = run; startV = runStart; } }
            else run = 0;
        }
        double vb = startV;
        while (vb > startV - 300 && Ring.Inside(r.Frame.At(u, vb), r.Outer)) vb -= 1;
        PlaceOn(r, u, vb - 12);
        r.S.Step(F($"tractor put at the start of pass {k} ({r.Truth.E:0},{r.Truth.N:0}), 12 m before the boundary, heading {r.Truth.Heading * 180 / Math.PI:0}°"));
        await r.RollAsync(30);
        if (!await EngageAsync(r)) { r.Harness("AutoSteer did not engage at the start: " + string.Join(" | ", r.Collect())); return false; }
        r.S.Step("AutoSteer engaged, sections Auto; AgOpenWeb drives from here");
        return true;
    }

    private int _checkedPass = int.MinValue;
    // The pass whose line strays furthest from the reference offset, in the cultivated area.
    public (int Pass, double Want, double Short, (double E, double N) At, double ShortOffsetOnly, double ShortNoExtend, double Shown)? Worst;

    public override async Task OnFixAsync(JobRun r)
    {
        if (r.Pass == int.MinValue || r.Pass == _checkedPass || r.InTurn) return;
        _checkedPass = r.Pass;
        double want = Math.Abs(ExpectedU(r, r.Pass));
        var full = Shortfall(r, await PassLineAsync(r, r.Pass), want);
        var offsetOnly = Shortfall(r, await PassLineAsync(r, r.Pass, limit: false, extend: false), want);
        var noExtend = Shortfall(r, await PassLineAsync(r, r.Pass, extend: false), want);
        // The copy against the line AgOpenWeb shows.
        var mine = await PassLineAsync(r, r.Pass);
        var shown = await r.Rig.ReadAsync(() => r.Rig.State.Guidance.DisplayLine?.Select(p => (p.Easting, p.Northing)).ToList());
        double apart = 0;
        if (mine is { Count: > 1 } && shown is { Count: > 1 })
            foreach (var q in shown) apart = Math.Max(apart, DistToLine(q, mine));
        Journal.Trace(F($"[fj] {r.Seconds,7:0.0} s   pass {r.Pass}: AgOpenWeb's line comes {full.Short:0.0} m nearer the reference than its {want:0} m at ({full.At.E:0},{full.At.N:0}); offset alone {offsetOnly.Short:0.0} m, without the straight ends {noExtend.Short:0.0} m; the harness's copy {apart:0.00} m from the line shown"));
        if (Worst == null || full.Short > Worst.Value.Short) Worst = (r.Pass, want, full.Short, full.At, offsetOnly.Short, noExtend.Short, apart);
    }

    public override void Judge(Scenario sc, JobRun r, JobAreas a, Action<Severity, string, string> flag)
    {
        if (Worst is { } w)
            sc.Row.Metrics.Add(("AgOpenWeb's line against the reference offset (in the cultivated area): the most it comes nearer, offset alone / with the bends limited / with the straight ends", F($"pass {w.Pass} ({w.Want:0} m): {w.ShortOffsetOnly:0.0} / {w.ShortNoExtend:0.0} / {w.Short:0.0} m at ({w.At.E:0},{w.At.N:0}); the harness's copy {w.Shown:0.00} m from the line shown")));
        if (Worst is { Short: > 1.5 } ww)
        {
            string step = ww.ShortOffsetOnly > 1.5 ? "the offset itself (CurveProcessing.CreateOffsetCurveWithInfo)"
                : ww.ShortNoExtend > 1.5 ? F($"limiting its bends to the U-turn radius ({r.Rig.Store.Guidance.UTurnRadius:0} m, CurveProcessing.LimitTurnRadius)")
                : "running its ends on straight (CurveProcessing.ExtendCurveEnds)";
            Explained = F($"AgOpenWeb's own line for pass {ww.Pass} comes {ww.Short:0.0} m nearer the reference than the {ww.Want:0} m it should be, at ({ww.At.E:0},{ww.At.N:0}) in the cultivated area; the step that moves it is {step}.");
        }
        LapOperator.JudgePassError(sc, r, flag, "the curve's passes", null, Explained);
    }
    public string? Explained;
}

/// <summary>
/// A closed line worked lap by lap: the whole outer boundary as a loop ("Boundary Curve"), or a
/// water pivot's circle. AgOpenWeb makes no U-turns on a closed track, so the operator drives a
/// lap, taps "move one pass" towards the inside where it closes, lets AutoSteer settle on the
/// next lap, and stops when there is no lap left to drive or nothing left unsprayed.
/// </summary>
public sealed class LapOperator : PathOperator
{
    private readonly GuideKind _kind;
    public LapOperator(GuideKind kind) { _kind = kind; }

    private int _sign = 1;            // pass number step inwards
    private double _cum, _prev, _lapStart;
    private bool _primed, _laps;      // _laps: AutoSteer on the line, laps counting
    private int _target, _changeFix, _settle, _retries;
    private double _speedBefore;
    public int Laps;
    public readonly List<double> ChangeSeconds = new();
    public readonly List<string> ChangeTrouble = new();
    public string? Refused;
    public double MinLapRadius = ScenarioFields.PivotPadRadiusM + 4;
    // The water pivot recorded by driving: how AgOpenWeb's circle came out against the real one.
    public double? FitCentreErrorM, FitRadiusErrorM;
    private double _recordedFrom = double.NaN;
    // The boundary loop: how deep the field is, where the laps turn tighter than the rig likes,
    // and AgOpenWeb's line for the next lap when it runs out of the field.
    private DepthGrid? _depth;
    private readonly LapCorners _corners = new();
    // AgOpenWeb's lap lines that run out of the field (with and without the turn-radius limit),
    // and how far AutoSteer took the tractor past the boundary on the laps.
    private readonly List<(int Lap, int Pass, double OutM, double LenM, (double E, double N) At, double OutNoLimitM)> _lineOut = new();
    private int _linesChecked;
    private (int Lap, int Pass, double OutM, double LenM, (double E, double N) At, double OutNoLimitM)? _splitLine;
    public double TractorOutM; public (double E, double N) TractorOutAt; public int TractorOutLap;
    public string Stop = "";

    public override async Task<bool> CreateTrackAsync(JobRun r)
    {
        if (_kind == GuideKind.BoundaryLoop)
        {
            await r.SendAsync("track.boundaryCurve");
            var pts = await ActiveTrackPointsAsync(r);
            bool closed = await r.Rig.ReadAsync(() => r.Rig.Vm.SelectedTrack?.IsClosed == true);
            if (pts == null || pts.Count < 3 || !closed) { r.Harness("Boundary Curve made no closed loop: " + string.Join(" | ", r.Collect())); return false; }
            r.Frame = new PolylineFrame(pts, closed: true);
            r.S.Step(F($"Boundary Curve: the outer boundary as a closed loop of {pts.Count} points, {r.Frame.Length:0} m round, active"));
            return true;
        }
        // A pivot: the real circle until AgOpenWeb has its own (made at the first lap).
        var c = r.Local(ScenarioFields.PivotCentre.Lat, ScenarioFields.PivotCentre.Lon);
        r.Frame = new CircleFrame(c, ScenarioFields.PivotRadiusM);
        r.S.Step(F($"field of a water pivot: a circle of {ScenarioFields.PivotRadiusM:0} m round the tower, its pad an obstacle of {ScenarioFields.PivotPadRadiusM:0} m"));
        return true;
    }

    public override async Task<bool> StartAsync(JobRun r)
    {
        _sign = InsideSign(r);
        int k0 = _sign > 0 ? 0 : -1;
        double u0 = (k0 + 0.5) * r.Spacing;
        double v0 = 0;
        if (r.Frame is PolylineFrame pf)
        {
            // In the middle of the longest side of the loop.
            var pts = pf.Points; double longest = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                var a = pts[i]; var b = pts[(i + 1) % pts.Count];
                double l = Math.Sqrt((a.E - b.E) * (a.E - b.E) + (a.N - b.N) * (a.N - b.N));
                if (l > longest) { longest = l; v0 = pf.V(((a.E + b.E) / 2, (a.N + b.N) / 2)); }
            }
        }
        PlaceOn(r, u0, v0);
        r.S.Step(F($"tractor put on the first lap, half a width inside the boundary ({r.Truth.E:0},{r.Truth.N:0}), heading {r.Truth.Heading * 180 / Math.PI:0}°"));
        await r.RollAsync(30, Follow(r, () => u0));

        if (_kind == GuideKind.PivotCentre)
        {
            var c = ScenarioFields.PivotCentre;
            await r.SendAsync("track.latLonPivot", F($"{c.Lat:F9},{c.Lon:F9}"));
            if (!await UsePivotTrackAsync(r, "the centre typed in")) return false;
        }
        if (_kind == GuideKind.PivotDriven)
        {
            // Spraying from the start; the pivot is recorded while the first lap is driven by hand.
            await r.SendAsync("section.master");
            await r.SendAsync("track.recordPivot");
            r.HandSteer = Follow(r, () => u0);
            r.ExpectEngaged = false;
            r.PassFromOperator = true;
            r.Pass = k0;
            _recordedFrom = r.Frame.V((r.Truth.E, r.Truth.N));
            r.S.Step("Pivot: drive part of the way round it (track.recordPivot), sprayer on, by hand");
            return true;
        }
        if (!await EngageAsync(r)) { r.Harness("AutoSteer did not engage on the first lap: " + string.Join(" | ", r.Collect())); return false; }
        _laps = true;
        r.S.Step("AutoSteer engaged, sections Auto; the operator moves one pass in at the end of each lap");
        if (r.Frame is PolylineFrame)
        {
            var first = await CheckLineAsync(r, k0, 1);
            if (first.OutM > 1.0) _lineOut.Add(first);
            await CompareLineAsync(r, k0);
            Journal.Trace(F($"[fj] {r.Seconds,7:0.0} s   lap 1 (pass {k0}): AgOpenWeb's line {(first.OutM > 0 ? F($"{first.OutM:0.0} m out of the field at ({first.At.E:0},{first.At.N:0}), {first.OutNoLimitM:0.0} m without the turn-radius limit") : "inside the field")}"));
        }
        return true;
    }

    private async Task<bool> UsePivotTrackAsync(JobRun r, string how)
    {
        var circle = await r.Rig.ReadAsync(() => r.Rig.Vm.SelectedTrack is { IsPivot: true } t ? t.PivotCircle() : ((AgOpenWeb.Models.Base.Vec3, double)?)null);
        if (circle is not { } cc) { r.Harness("no water pivot track after " + how + ": " + string.Join(" | ", r.Collect())); return false; }
        var real = r.Local(ScenarioFields.PivotCentre.Lat, ScenarioFields.PivotCentre.Lon);
        double de = cc.Item1.Easting - real.E, dn = cc.Item1.Northing - real.N;
        FitCentreErrorM = Math.Sqrt(de * de + dn * dn);
        FitRadiusErrorM = cc.Item2 - ScenarioFields.PivotRadiusM;
        r.Frame = new CircleFrame((cc.Item1.Easting, cc.Item1.Northing), cc.Item2);
        r.S.Step(F($"water pivot from {how}: centre {FitCentreErrorM:0.00} m from the tower, reference circle {cc.Item2:0.00} m (the field's edge is at {ScenarioFields.PivotRadiusM:0} m), active"));
        return true;
    }

    public override async Task OnFixAsync(JobRun r)
    {
        var p = (r.Truth.E, r.Truth.N);
        double v = r.Frame.V(p);
        if (!_primed) { _prev = v; _primed = true; return; }
        _cum += r.Frame.DeltaV(_prev, v);
        _prev = v;

        if (!_laps)
        {
            // Recording the pivot by hand: a third of the way round, then Finish.
            if (_cum < r.Frame.Length / 3) return;
            r.Did("Finish (the pivot recorded over a third of a lap)");
            await r.SendAsync("track.finishCurve");
            if (!await UsePivotTrackAsync(r, "a third of a lap driven")) { r.Stopped = true; return; }
            _prev = r.Frame.V(p);   // the frame changed: v starts again from here
            r.HandSteer = null;
            r.PassFromOperator = false;
            r.ExpectEngaged = true;
            if (!await EngageAsync(r)) { r.Harness("AutoSteer did not engage on the recorded pivot: " + string.Join(" | ", r.Collect())); r.Stopped = true; return; }
            r.Did("AutoSteer engaged on the recorded pivot");
            _laps = true;
            _lapStart = _cum - r.Frame.Length / 3;   // the lap began where the recording did
            return;
        }

        var c = r.Rig.LastCycle;
        if (r.Frame is PolylineFrame)
        {
            _corners.Update(r, _cum);
            var (_, pivotOut) = r.OutsideBoundaryM();
            if (pivotOut > TractorOutM) { TractorOutM = pivotOut; TractorOutAt = p; TractorOutLap = Laps + 1; }
        }
        if (r.InChange)
        {
            if (r.Pass == _target && c?.Guidance is { HasGuidance: true } g && Math.Abs(g.CrossTrackError) < 0.1) _settle++;
            else _settle = 0;
            int since = r.Fix - _changeFix;
            if (since == 30 && r.Pass != _target)
            {
                // AgOpenWeb did not take the move: once more, then give up.
                if (_retries++ < 1) { r.Did("tapped 'move one pass' again"); await r.SendAsync(_sign > 0 ? "track.snapRight" : "track.snapLeft"); _changeFix = r.Fix; return; }
                Refused = F($"AgOpenWeb did not move to pass {_target} (still on {r.Pass}) after two taps: {string.Join(" | ", r.Collect().TakeLast(3))}");
                Finish(r, "the operator stopped: AgOpenWeb would not move to the next lap");
                return;
            }
            if (_settle >= 20 || since > 600)
            {
                if (since > 600) ChangeTrouble.Add(F($"lap {Laps}: not settled on pass {_target} after 60 s (on pass {r.Pass}, {(c?.Guidance?.CrossTrackError ?? double.NaN):0.00} m off)"));
                ChangeSeconds.Add(since / 10.0);
                if (r.Frame is PolylineFrame) await CompareLineAsync(r, _target);
                r.InChange = false;
                _lapStart = _cum;
                r.Truth.SpeedKmh = _speedBefore;
            }
            return;
        }

        if (_cum - _lapStart < r.Frame.Length) return;
        Laps++;
        int next = r.Pass + _sign;
        double uNext = (next + 0.5) * r.Spacing;
        double left = r.UnsprayedShare();
        if (r.Frame is CircleFrame cf)
        {
            bool room = cf.Radius - uNext >= MinLapRadius;
            Journal.Trace(F($"[fj] {r.Seconds,7:0.0} s   lap {Laps} done on pass {r.Pass}; next pass {next}: radius {cf.Radius - uNext:0.0} m; {left * 100:0.0} % of the cultivated area unsprayed"));
            if (left < 0.003) { Finish(r, F($"the operator finished after {Laps} laps: nothing left to spray")); return; }
            if (!room) { Finish(r, F($"the operator finished after {Laps} laps: no room for another lap ({left * 100:0.0} % of the cultivated area unsprayed)")); return; }
        }
        else
        {
            // The ground deep enough for the next lap's line: none, one piece, or pieces a closed
            // line cannot go round. And AgOpenWeb's line for it, as the operator sees it on screen.
            _depth ??= new DepthGrid(r.Outer);
            var pieces = _depth.Pieces(Math.Abs(uNext));
            int big = pieces.Count(a => a >= r.Spacing * r.Spacing);
            var line = await CheckLineAsync(r, next, Laps + 1);
            Journal.Trace(F($"[fj] {r.Seconds,7:0.0} s   lap {Laps} done on pass {r.Pass}; next pass {next}: ground {Math.Abs(uNext):0} m deep in {pieces.Count} piece(s) ({string.Join(", ", pieces.Take(3).Select(a => F($"{a:0} m²")))}); AgOpenWeb's line for it {(line.OutM > 0 ? F($"{line.OutM:0.0} m out of the field at ({line.At.E:0},{line.At.N:0}), {line.OutNoLimitM:0.0} m without the turn-radius limit") : "inside the field")}; {left * 100:0.0} % of the cultivated area unsprayed"));
            if (left < 0.003) { Finish(r, F($"the operator finished after {Laps} laps: nothing left to spray")); return; }
            if (big == 0) { Finish(r, F($"the operator finished after {Laps} laps: no room for another lap ({left * 100:0.0} % of the cultivated area unsprayed)")); return; }
            if (big > 1)
            {
                // The operator sees the line jump across the field and stops; AgOpenWeb draws it anyway.
                if (line.OutM > 1.0) _splitLine = line;
                Finish(r, F($"the operator finished after {Laps} laps: the next lap would have to split in {big} pieces where the field narrows ({left * 100:0.0} % of the cultivated area unsprayed)"));
                return;
            }
            if (line.OutM > 1.0) _lineOut.Add(line);
        }
        // Move one pass inwards; the operator slows down first if AgOpenWeb asks for it.
        _speedBefore = r.Truth.SpeedKmh;
        var a = r.Rig.Store.AutoSteer;
        if (a.ManualTurnsEnabled && r.Truth.SpeedKmh >= a.ManualTurnsSpeed) r.Truth.SpeedKmh = Math.Max(2, a.ManualTurnsSpeed - 1);
        r.Did(F($"lap {Laps} done: 'move one pass' {(_sign > 0 ? "right" : "left")}, to pass {next}"));
        await r.SendAsync(_sign > 0 ? "track.snapRight" : "track.snapLeft");
        r.InChange = true;
        _target = next; _changeFix = r.Fix; _settle = 0; _retries = 0;
    }

    private void Finish(JobRun r, string why)
    {
        // What no lap reached is the operator's to finish (an AB line, or by hand), not a strip
        // between passes.
        double depthDone = Math.Abs((r.Pass + 0.5) * r.Spacing) + r.Spec.T.Width / 2;
        int sign = _sign;
        r.LeftOnPurpose = p => sign * r.Frame.U(p) > depthDone - 0.2;
        Stop = why;
        r.EndedBy = why;
        r.FieldDone = true;
        r.Stopped = true;
        r.Did(why);
    }

    /// <summary>How far a closed line runs out of the field: the furthest, where, and its length outside.</summary>
    private static (double MaxOutM, double LengthOutM, (double E, double N) At) Outside(JobRun r, List<(double E, double N)>? line)
    {
        double max = 0, len = 0; (double E, double N) at = (0, 0);
        if (line == null || line.Count < 2) return (0, 0, at);
        for (int i = 0; i < line.Count; i++)
        {
            var a = line[i]; var b = line[(i + 1) % line.Count];
            double l = Math.Sqrt((b.E - a.E) * (b.E - a.E) + (b.N - a.N) * (b.N - a.N));
            int n = Math.Max(1, (int)Math.Ceiling(l));
            for (int k = 0; k < n; k++)
            {
                var p = (a.E + (b.E - a.E) * k / n, a.N + (b.N - a.N) * k / n);
                if (Ring.Inside(p, r.Outer)) continue;
                len += l / n;
                double d = Ring.DistToRing(p, r.Outer);
                if (d > max) { max = d; at = p; }
            }
        }
        return (max, len, at);
    }

    private async Task<(int Lap, int Pass, double OutM, double LenM, (double E, double N) At, double OutNoLimitM)> CheckLineAsync(JobRun r, int pass, int lap)
    {
        _linesChecked++;
        var with = Outside(r, await PassLineAsync(r, pass));
        var without = Outside(r, await PassLineAsync(r, pass, limit: false));
        return (lap, pass, with.MaxOutM, with.LengthOutM, with.At, without.MaxOutM);
    }

    /// <summary>The harness's copy of AgOpenWeb's line against the line AgOpenWeb shows (its
    /// display line): they must be the same, or the next-lap check looks at the wrong line.</summary>
    private async Task CompareLineAsync(JobRun r, int pass)
    {
        if (pass == int.MinValue) return;
        var mine = await PassLineAsync(r, pass);
        var shown = await r.Rig.ReadAsync(() => r.Rig.State.Guidance.DisplayLine?.Select(p => (p.Easting, p.Northing)).ToList());
        if (mine == null || shown == null || mine.Count < 2 || shown.Count < 2) return;
        double worst = 0;
        foreach (var p in shown) worst = Math.Max(worst, Ring.DistToRing(p, mine));
        double want = Math.Abs(ExpectedU(r, pass));
        // Not where the line runs out of the field: LAP-LINE-LEAVES-FIELD covers that.
        var outAt = _lineOut.Select(l => l.At).ToList();
        Func<(double E, double N), bool> nearOut = q => outAt.Any(o => (o.E - q.E) * (o.E - q.E) + (o.N - q.N) * (o.N - q.N) < 40 * 40);
        var full = Shortfall(r, mine, want, nearOut);
        var offsetOnly = Shortfall(r, await PassLineAsync(r, pass, limit: false), want, nearOut);
        Journal.Trace(F($"[fj] {r.Seconds,7:0.0} s   pass {pass}: AgOpenWeb's line ({shown.Count} points) against the harness's copy ({mine.Count}): {worst:0.00} m apart at most; it comes {full.Short:0.0} m nearer the boundary than its {want:0} m at ({full.At.E:0},{full.At.N:0}), offset alone {offsetOnly.Short:0.0} m"));
        if (worst > 0.5) LineCopyOff = Math.Max(LineCopyOff, worst);
        if (WorstShort == null || full.Short > WorstShort.Value.Short) WorstShort = (pass, want, full.Short, full.At, offsetOnly.Short);
    }
    public double LineCopyOff;
    public (int Pass, double Want, double Short, (double E, double N) At, double ShortOffsetOnly)? WorstShort;

    public override void Metrics(Scenario sc, JobRun r)
    {
        sc.Row.Metrics.Add(("Laps / moves to the next lap: mean and longest time to settle on it", F($"{Laps} / {ChangeSeconds.Count}: {(ChangeSeconds.Count > 0 ? ChangeSeconds.Average() : 0):0.0} s and {(ChangeSeconds.Count > 0 ? ChangeSeconds.Max() : 0):0.0} s")));
        if (FitCentreErrorM is { } ce)
            sc.Row.Metrics.Add(("AgOpenWeb's pivot circle: centre from the tower / reference radius against the field's edge", F($"{ce:0.00} m / {FitRadiusErrorM:+0.00;-0.00} m")));
    }

    public override void Judge(Scenario sc, JobRun r, JobAreas a, Action<Severity, string, string> flag)
    {
        if (Refused != null) flag(Severity.Bug, "LAP-CHANGE-REFUSED", Refused);
        if (_lineOut.Count > 0 || _splitLine != null)
        {
            var parts = new List<string>();
            if (_lineOut.Count > 0)
            {
                var w = _lineOut.OrderByDescending(l => l.OutM).First();
                double noLimit = _lineOut.Max(l => l.OutNoLimitM);
                parts.Add(F($"AgOpenWeb's lap line ran out of the field on {_lineOut.Count} of {_linesChecked} laps, the furthest {w.OutM:0.0} m ({w.LenM:0} m of it outside, lap {w.Lap}, pass {w.Pass}, at ({w.At.E:0},{w.At.N:0})), and AutoSteer took the tractor itself {TractorOutM:0.0} m past the boundary (lap {TractorOutLap}, at ({TractorOutAt.E:0},{TractorOutAt.N:0}))."));
                parts.Add(noLimit <= 0.5
                    ? F($"Offset alone, the lines stay inside (at most {noLimit:0.0} m out): it is the lap's bends limited to the U-turn radius ({r.Rig.Store.Guidance.UTurnRadius:0} m, OffsetPassCache) that cut across a sharp inward corner of the boundary and out of the field.")
                    : F($"The offset itself already runs {noLimit:0.0} m out of the field, before the bends are limited to the U-turn radius."));
            }
            if (_splitLine is { } sl)
                parts.Add(F($"Past the last lap, where the field is narrower than two laps, AgOpenWeb's line for lap {sl.Lap} (pass {sl.Pass}) runs {sl.OutM:0} m out of the field ({sl.LenM:0} m of it outside, at ({sl.At.E:0},{sl.At.N:0})): the offset leaves out the points that come closer to the boundary than the lap's distance and joins what is left with straight lines, across the field and out of it. With AutoSteer engaged, one tap of 'move one pass' sends the tractor along it."));
            flag(Severity.Safety, "LAP-LINE-LEAVES-FIELD", string.Join(" ", parts));
        }
        if (LineCopyOff > 0.5) sc.Harness(F($"the harness's copy of AgOpenWeb's lap line was {LineCopyOff:0.00} m off the line AgOpenWeb shows: the next-lap check is not looking at AgOpenWeb's line"));
        if (ChangeTrouble.Count > 0) flag(Severity.Bug, "LAP-CHANGE-NOT-SETTLED", F($"{ChangeTrouble.Count} of {ChangeSeconds.Count} moves to the next lap never settled: {ChangeTrouble[0]}"));
        if (FitCentreErrorM is { } ce && (ce > 1.0 || Math.Abs(FitRadiusErrorM ?? 0) > 0.5))
            flag(Severity.Bug, "PIVOT-CIRCLE-OFF", F($"AgOpenWeb's water pivot is off the real one: centre {ce:0.00} m from the tower, reference circle {FitRadiusErrorM:+0.00;-0.00} m from the field's edge; every circle is off by as much."));
        // Where the lap line itself left the field, LAP-LINE-LEAVES-FIELD says it already.
        var outAt = _lineOut.Select(l => l.At).ToList();
        string? why = null;
        if (WorstShort is { Short: > 1.5 } ws)
        {
            sc.Row.Metrics.Add(("AgOpenWeb's lap line against the boundary offset by whole widths (in the field): the most it comes nearer, offset alone / with the bends limited", F($"pass {ws.Pass} ({ws.Want:0} m): {ws.ShortOffsetOnly:0.0} / {ws.Short:0.0} m at ({ws.At.E:0},{ws.At.N:0})")));
            why = F($"AgOpenWeb's own line for pass {ws.Pass} comes {ws.Short:0.0} m nearer the boundary than the {ws.Want:0} m it should be, at ({ws.At.E:0},{ws.At.N:0}); {(ws.ShortOffsetOnly > 1.5 ? "the step that moves it is the offset itself (CurveProcessing.CreateOffsetCurveWithInfo)" : F($"the step that moves it is limiting its bends to the U-turn radius ({r.Rig.Store.Guidance.UTurnRadius:0} m, CurveProcessing.LimitTurnRadius)"))}.");
        }
        JudgePassError(sc, r, flag, _kind == GuideKind.BoundaryLoop ? "the laps" : "the circles",
            outAt.Count == 0 ? null : q => outAt.Any(o => (o.E - q.E) * (o.E - q.E) + (o.N - q.N) * (o.N - q.N) < 40 * 40), why);
    }

    /// <summary>How far the implement ran from the pass it was on (the true offset of the
    /// reference line), outside the moves between laps and the U-turns.</summary>
    public static void JudgePassError(Scenario sc, JobRun r, Action<Severity, string, string> flag, string what, Func<(double E, double N), bool>? excused = null, string? why = null)
    {
        if (r.PassErrorSamples == 0) return;
        double mean = r.PassErrorSum / r.PassErrorSamples;
        sc.Row.Metrics.Add(("The implement against its pass (the reference line offset by whole widths): mean / worst", F($"{mean:0.00} m / {r.PassErrorWorst:+0.00;-0.00} m (pass {r.PassErrorWhere.Pass}, at ({r.PassErrorWhere.E:0},{r.PassErrorWhere.N:0}), {r.PassErrorWhere.V:0} m along the line)")));
        // The worst outside the places another finding already explains (the large errors are kept).
        double worst = r.PassErrorWorst; var where = r.PassErrorWhere;
        if (excused != null && excused((where.E, where.N)))
        {
            var rest = r.PassErrorBig.Where(b => !excused((b.E, b.N))).OrderByDescending(b => Math.Abs(b.Err)).FirstOrDefault();
            if (rest.Pass != 0 || rest.Err != 0) { worst = rest.Err; where = (rest.E, rest.N, rest.Pass, rest.V); }
            else worst = 0;
        }
        if (mean > 0.3 || Math.Abs(worst) > 1.5)
            flag(Severity.Inconsistency, "OFF-ITS-PASS", F($"on {what} the implement ran {mean:0.00} m from its pass on average and {Math.Abs(worst):0.00} m at worst (pass {where.Pass} at ({where.E:0},{where.N:0})): {why ?? "the line AgOpenWeb steers is not the reference offset by whole widths there."}"));
        else
            sc.Ok(F($"the implement kept to its pass on {what} ({mean:0.00} m mean, {Math.Abs(r.PassErrorWorst):0.00} m worst{(Math.Abs(r.PassErrorWorst) > 1.5 ? " where the lap line left the field" : "")})"));
    }
}

/// <summary>
/// Contour: no line. The operator drives the first lap round the field by hand with the sprayer
/// on (AgOpenWeb records the strip), moves a width inwards by hand, switches Contour on and
/// engages AutoSteer: AgOpenWeb follows the strip beside it. At the end of each lap the
/// operator takes over, moves a width in and engages again (with AutoSteer on AgOpenWeb keeps its
/// reference strip, so it would go round the same lap again).
/// </summary>
public sealed class ContourOperator : PathOperator
{
    private int _sign = 1;
    private double _cum, _prev, _lapStart;
    private bool _primed, _contourOn;
    private int _k, _settle, _changeFix;
    public int Laps, HandLaps = 1;
    public readonly List<string> Trouble = new();
    public int NoLineSeconds;
    private int _noLineRun;
    private DepthGrid? _depth;
    private readonly LapCorners _corners = new();
    // AutoSteer on the contour leaving the lap it should follow (onto another lap, out of the field, or back the way it came).
    private int _offLapFixes;
    private double _cumWindowStart; private int _windowFix;
    public string? LeftLap; public bool LeftLapOutside; public int LeftLapCount;
    /// <summary>Where the implement was each time the contour left its lap.</summary>
    private readonly List<string> _leftAt = new();
    /// <summary>Driving back onto the lap by hand after the contour left it.</summary>
    private bool _recovering;

    public override Task<bool> CreateTrackAsync(JobRun r)
    {
        if (r.Field.Name == ScenarioFields.Pivot.Name)
            r.Frame = new CircleFrame(r.Local(ScenarioFields.PivotCentre.Lat, ScenarioFields.PivotCentre.Lon), ScenarioFields.PivotRadiusM);
        else
            r.Frame = new PolylineFrame(r.Outer, closed: true);
        r.S.Step("no line: the first lap by hand, then Contour");
        return Task.FromResult(true);
    }

    public override async Task<bool> StartAsync(JobRun r)
    {
        _sign = InsideSign(r);
        _k = _sign > 0 ? 0 : -1;
        double v0 = 0;
        if (r.Frame is PolylineFrame pf)
        {
            var pts = pf.Points; double longest = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                var a = pts[i]; var b = pts[(i + 1) % pts.Count];
                double l = Math.Sqrt((a.E - b.E) * (a.E - b.E) + (a.N - b.N) * (a.N - b.N));
                if (l > longest) { longest = l; v0 = pf.V(((a.E + b.E) / 2, (a.N + b.N) / 2)); }
            }
        }
        double u0 = ExpectedU(r, _k);
        PlaceOn(r, u0, v0);
        r.S.Step(F($"tractor put half a width inside the boundary ({r.Truth.E:0},{r.Truth.N:0}), heading {r.Truth.Heading * 180 / Math.PI:0}°"));
        await r.RollAsync(30, Follow(r, () => u0));
        await r.SendAsync("section.master");
        r.HandSteer = Follow(r, () => ExpectedU(r, _k));
        r.ExpectEngaged = false;
        r.PassFromOperator = true;
        r.Pass = _k;
        r.S.Step("sections Auto, the first lap by hand along the boundary");
        return true;
    }

    public override async Task OnFixAsync(JobRun r)
    {
        var p = (r.Truth.E, r.Truth.N);
        double v = r.Frame.V(p);
        if (!_primed) { _prev = v; _primed = true; return; }
        _cum += r.Frame.DeltaV(_prev, v);
        _prev = v;
        var c = r.Rig.LastCycle;
        if (r.Frame is PolylineFrame && r.HandSteer == null) _corners.Update(r, _cum);
        // Near the lap's end the contour follows the strip where the operator moved in, and so
        // turns in towards the next lap before the operator takes over: that is the spiral a
        // contour makes, not the tractor leaving its pass.
        bool nearLapEnd = _cum - _lapStart > r.Frame.Length - 3 - 2 * r.Spec.T.Width;
        if (nearLapEnd) r.InCorner = true;
        else if (r.Frame is CircleFrame) r.InCorner = false;

        if (r.InChange)
        {
            // Moving a width in by hand; then Contour (once) and AutoSteer.
            double off = Math.Abs(r.U(p) + r.Spec.T.Offset - ExpectedU(r, _k));
            _settle = off < 0.1 ? _settle + 1 : 0;
            if (_settle < 20 && r.Fix - _changeFix < 600) return;
            if (_settle < 20 && _recovering)
            {
                // Not back on the lap after a minute by hand: engaging here would judge
                // AgOpenWeb on a start the operator made, so the operator stops.
                r.EndedEarly = true;
                Finish(r, F($"the operator stopped: not back on lap {Laps + 1} by hand a minute after the contour left it"));
                return;
            }
            _recovering = false;
            if (!_contourOn)
            {
                await r.SendAsync("contour.toggle");
                _contourOn = await r.Rig.ReadAsync(() => r.Rig.Vm.IsContourModeOn);
                r.Did(_contourOn ? "Contour on" : "Contour did NOT switch on");
                if (!_contourOn) { r.Harness("contour.toggle did not switch Contour on"); r.Stopped = true; return; }
            }
            r.HandSteer = null;
            r.ExpectEngaged = true;
            await r.SendAsync("autosteer.toggle");
            bool on = await r.Rig.ReadAsync(() => r.Rig.Vm.IsAutoSteerEngaged);
            r.Did(on ? F($"AutoSteer engaged on lap {Laps + 1} (pass {_k})") : "AutoSteer did not engage: " + string.Join(" | ", r.Collect().TakeLast(2)));
            if (!on) { Trouble.Add(F($"lap {Laps + 1}: AutoSteer would not engage on the contour")); Finish(r, "the operator stopped: AutoSteer would not engage on the contour"); return; }
            r.InChange = false;
            _offLapFixes = 0; _windowFix = r.Fix; _cumWindowStart = _cum;
            return;
        }

        if (r.HandSteer == null && c != null)
        {
            if (c.Guidance is not { HasGuidance: true }) { if (++_noLineRun % 10 == 0) NoLineSeconds++; }
            else _noLineRun = 0;
        }

        if (r.HandSteer == null && c != null && !r.InChange)
        {
            // Every 10 s on the contour: where AgOpenWeb is taking the tractor.
            var tcc = r.Truth.ToolCentre;
            double u = r.U((tcc.E, tcc.N)), want = ExpectedU(r, _k);
            if (r.Fix % 100 == 0)
                Journal.Trace(F($"[fj] {r.Seconds,7:0.0} s   contour: implement ({tcc.E:0.0},{tcc.N:0.0}) u {u:0.00} for {want:0.00}, heading {Math.IEEERemainder(r.Truth.Heading - r.Frame.TangentHeading(p), 2 * Math.PI) * 180 / Math.PI:0} deg off the lap's way, AgOpenWeb's cross-track {(c.Guidance?.CrossTrackError ?? double.NaN):0.00} m, guidance {c.Guidance?.HasGuidance}"));
            var (_, outNow) = r.OutsideBoundaryM();
            _offLapFixes = Math.Abs(u - want) > r.Spec.T.Width / 2 && (!nearLapEnd || outNow > 0.5) ? _offLapFixes + 1 : 0;
            if (r.Fix - _windowFix >= 30) { _windowFix = r.Fix; _cumWindowStart = _cum; }
            bool back = _cum - _cumWindowStart < -5 && !nearLapEnd;
            if (_offLapFixes >= 30 || back)
            {
                var (_, pivotOut) = r.OutsideBoundaryM();
                bool outside = pivotOut > 0.5 || u * _sign < 0;
                double hdg = Math.Abs(Math.IEEERemainder(r.Truth.Heading - r.Frame.TangentHeading(p), 2 * Math.PI)) * 180 / Math.PI;
                LeftLapCount++;
                Journal.Trace(F($"[fj] {r.Seconds,7:0.0} s   contour left the lap{await LineSideAsync(r, p)}"));
                _leftAt.Add(F($"({tcc.E:0},{tcc.N:0}) lap {Laps + 1}"));
                string what = F($"on lap {Laps + 1} (pass {_k}, {Math.Abs(want):0} m in from the boundary) the implement at ({tcc.E:0},{tcc.N:0}) is {Math.Abs(u):0.0} m {(u * _sign < 0 ? "outside" : "in from")} the boundary{(back ? F($", heading back the way the lap came ({hdg:0}° off it)") : "")}, AgOpenWeb's cross-track {(c.Guidance?.CrossTrackError ?? double.NaN):0.00} m (it steers to a line there){(pivotOut > 0.5 ? F($"; the tractor itself {pivotOut:0.0} m past the boundary") : "")}");
                if (LeftLap == null || (outside && !LeftLapOutside)) { LeftLap = what; LeftLapOutside = outside; }
                r.Did(F($"the contour left lap {Laps + 1}: {what}; the operator takes over and drives back onto the lap"));
                // The operator takes over, drives back onto the lap and engages again.
                await r.SendAsync("autosteer.toggle");
                r.ExpectEngaged = false;
                if (LeftLapCount >= 10)
                {
                    r.EndedEarly = true;
                    Finish(r, F($"the operator stopped: the contour left its lap {LeftLapCount} times"));
                    return;
                }
                r.HandSteer = Follow(r, () => ExpectedU(r, _k), 12);
                r.InChange = true;
                _recovering = true;
                _settle = 0; _changeFix = r.Fix; _offLapFixes = 0;
                return;
            }
        }

        // The lap is done where its strip began (where the operator moved in), a little before.
        if (_cum - _lapStart < r.Frame.Length - 3) return;
        Laps++;
        _lapStart = _cum;
        int next = _k + _sign;
        double uNext = ExpectedU(r, next);
        double left = r.UnsprayedShare();
        int pieces;
        if (r.Frame is CircleFrame cf) pieces = cf.Radius - uNext >= ScenarioFields.PivotPadRadiusM + 4 ? 1 : 0;
        else
        {
            _depth ??= new DepthGrid(r.Outer);
            pieces = _depth.Pieces(Math.Abs(uNext)).Count(a => a >= r.Spacing * r.Spacing);
        }
        Journal.Trace(F($"[fj] {r.Seconds,7:0.0} s   contour lap {Laps} done on pass {_k}; next {next}: the ground {Math.Abs(uNext):0} m deep in {pieces} piece(s); {left * 100:0.0} % unsprayed"));
        if (left < 0.003) { Finish(r, F($"the operator finished after {Laps} laps: nothing left to spray")); return; }
        if (pieces == 0) { Finish(r, F($"the operator finished after {Laps} laps: no room for another lap ({left * 100:0.0} % of the cultivated area unsprayed)")); return; }
        if (pieces > 1) { Finish(r, F($"the operator finished after {Laps} laps: the next lap would have to split in {pieces} pieces where the field narrows ({left * 100:0.0} % of the cultivated area unsprayed)")); return; }
        if (r.HandSteer == null)
        {
            await r.SendAsync("autosteer.toggle");   // take over
            r.ExpectEngaged = false;
        }
        _k = next;
        r.Pass = _k;
        r.HandSteer = Follow(r, () => ExpectedU(r, _k), 12);
        r.InChange = true;
        _settle = 0; _changeFix = r.Fix;
        r.Did(F($"lap {Laps} done: took over, moving a width in to pass {_k} by hand"));
    }

    /// <summary>
    /// For the trace: where AgOpenWeb's contour line runs beside the tractor against the strip
    /// it follows (State.Guidance.DisplayLine and State.Operation.ContourRef), in metres in from
    /// the boundary (the line should be one width inside the strip), and how sharply the strip
    /// turns there.
    /// </summary>
    private async Task<string> LineSideAsync(JobRun r, (double E, double N) p)
    {
        var (line, strip) = await r.Rig.ReadAsync(() => (
            r.Rig.State.Guidance.DisplayLine?.Select(q => (q.Easting, q.Northing)).ToList(),
            r.Rig.State.Operation.ContourRef?.Select(q => (q.Easting, q.Northing, q.Heading)).ToList()));
        if (line is not { Count: > 0 } || strip is not { Count: > 1 }) return "; AgOpenWeb shows no contour line or strip there";
        static double D2((double E, double N) a, (double E, double N) b) => (a.E - b.E) * (a.E - b.E) + (a.N - b.N) * (a.N - b.N);
        var ln = line.MinBy(q => D2(q, p));
        int si = 0; double best = double.MaxValue;
        for (int i = 0; i < strip.Count; i++) { double d = D2((strip[i].Easting, strip[i].Northing), p); if (d < best) { best = d; si = i; } }
        var sp = strip[si];
        double uLine = r.U(ln) * _sign, uStrip = r.U((sp.Easting, sp.Northing)) * _sign;
        // How much the strip turns within 10 m of its point nearest the tractor.
        double turn = 0;
        for (int i = 0; i < strip.Count; i++)
        {
            if (D2((strip[i].Easting, strip[i].Northing), (sp.Easting, sp.Northing)) > 100) continue;
            turn = Math.Max(turn, Math.Abs(Math.IEEERemainder(strip[i].Heading - sp.Heading, 2 * Math.PI)) * 180 / Math.PI);
        }
        bool wrong = uLine < uStrip - r.Spec.T.Width / 2;
        return F($"; AgOpenWeb's contour line beside the tractor is {uLine:0.0} m in from the boundary, the strip it follows {uStrip:0.0} m ({(wrong ? "the line is on the boundary side of the strip" : "the line is inside the strip")}; the strip turns {turn:0}° within 10 m there)");
    }

    private void Finish(JobRun r, string why)
    {
        double depthDone = Math.Abs(ExpectedU(r, _k)) + r.Spec.T.Width / 2;
        int sign = _sign;
        r.LeftOnPurpose = p => sign * r.Frame.U(p) > depthDone - 0.2;
        r.EndedBy = why;
        r.FieldDone = true;
        r.Stopped = true;
        r.Did(why);
    }

    public override void Metrics(Scenario sc, JobRun r) =>
        sc.Row.Metrics.Add(("Laps (the first by hand) / seconds AutoSteer had no contour line to follow", F($"{Laps} / {NoLineSeconds}")));

    public override void Judge(Scenario sc, JobRun r, JobAreas a, Action<Severity, string, string> flag)
    {
        foreach (var t in Trouble) flag(Severity.Bug, "CONTOUR-NOT-ENGAGED", t);
        if (LeftLap != null)
        {
            string why = _leftAt.Count > 1 ? " Where the implement was each time: " + string.Join(", ", _leftAt) + "." : "";
            flag(LeftLapOutside ? Severity.Safety : Severity.Bug, "CONTOUR-LEFT-THE-LAP", F($"AutoSteer on the contour took the tractor off the lap it was following {LeftLapCount} time(s); the operator took over each time and drove back onto it. {(LeftLapCount > 1 ? (LeftLapOutside ? "The first that took the tractor past the boundary" : "The first") : (LeftLapOutside ? "It took the tractor past the boundary" : "It"))}: {LeftLap}.{why}"));
        }
        if (NoLineSeconds > 5)
            flag(Severity.Bug, "CONTOUR-LINE-LOST", F($"with Contour on and AutoSteer engaged, AgOpenWeb had no line to steer for {NoLineSeconds} s of the job."));
        LapOperator.JudgePassError(sc, r, flag, "the contour laps");
    }
}

/// <summary>
/// A recorded path: the operator records a route by hand (most of a lap round the field, 6 m
/// inside the boundary, sprayer off), saves it, drives back to its start, picks it, sets
/// "Closest" and presses Play with AutoSteer engaged. Measured: whether AgOpenWeb steers the
/// tractor along the recorded route and says when it is done.
/// </summary>
public sealed class RecordedPathOperator : PathOperator
{
    private const string Name = "Route by the boundary";
    private const double InsideM = 6;
    private int _phase;               // 0 recording, 1 back to the start by hand, 2 playback
    private double _cum, _prev, _phaseStart;
    private bool _primed;
    private readonly List<(double E, double N)> _route = new();
    private PolylineFrame? _routeFrame;
    public double FollowSum, FollowWorst; public int FollowSamples;
    public double PlaybackSeconds, PlaybackStart = -1;
    public bool Engaged, Completed, Started;
    public int SteerSamples, SteerNonZero;
    public string? Trouble;
    public string? Refusal;

    public override bool JudgesCoverage => false;

    public override Task<bool> CreateTrackAsync(JobRun r)
    {
        r.Frame = new PolylineFrame(r.Outer, closed: true);
        r.S.Step("no line: a route recorded by hand, then played back");
        return Task.FromResult(true);
    }

    private double U0(JobRun r) => _sign * InsideM;
    private int _sign = 1;

    public override async Task<bool> StartAsync(JobRun r)
    {
        _sign = InsideSign(r);
        var pf = (PolylineFrame)r.Frame;
        var pts = pf.Points; double longest = 0, v0 = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            var a = pts[i]; var b = pts[(i + 1) % pts.Count];
            double l = Math.Sqrt((a.E - b.E) * (a.E - b.E) + (a.N - b.N) * (a.N - b.N));
            if (l > longest) { longest = l; v0 = pf.V(((a.E + b.E) / 2, (a.N + b.N) / 2)); }
        }
        PlaceOn(r, U0(r), v0);
        await r.RollAsync(30, Follow(r, () => U0(r)));
        r.HandSteer = Follow(r, () => U0(r));
        r.ExpectEngaged = false;
        r.PassFromOperator = true;
        await r.SendAsync("recpath.start");
        bool rec = await r.Rig.ReadAsync(() => r.Rig.Vm.IsRecordingPath);
        r.S.Step(F($"tractor {InsideM:0} m inside the boundary ({r.Truth.E:0},{r.Truth.N:0}); Record path {(rec ? "started" : "did NOT start")}; driving the route by hand, sprayer off"));
        if (!rec) { r.Harness("recpath.start did not start recording: " + string.Join(" | ", r.Collect())); return false; }
        return true;
    }

    public override async Task OnFixAsync(JobRun r)
    {
        var p = (r.Truth.E, r.Truth.N);
        double v = r.Frame.V(p);
        if (!_primed) { _prev = v; _primed = true; return; }
        _cum += r.Frame.DeltaV(_prev, v);
        _prev = v;
        double L = r.Frame.Length;

        if (_phase == 0)
        {
            if (r.Fix % 5 == 0) _route.Add(p);
            if (_cum < 0.85 * L) return;
            _route.Add(p);
            await r.SendAsync("recpath.stop");
            await r.SendAsync("recpath.save", Name);
            var files = await r.Rig.ReadAsync(() => r.Rig.Vm.AvailableRecFiles.ToList());
            _routeFrame = new PolylineFrame(_route, closed: false);
            r.Did(F($"Stop and Save '{Name}' after {_routeFrame.Length:0} m of route; saved paths: {string.Join(", ", files)}"));
            _phase = 1; _phaseStart = _cum;
            return;
        }
        if (_phase == 1)
        {
            // Back round to the start of the route by hand (the rest of the lap).
            if (_cum - _phaseStart < 0.15 * L - 3) return;
            await r.SendAsync("recpath.selectFile", Name);
            // "Closest": start the route at its nearest point, its first one here.
            for (int i = 0; i < 3 && await r.Rig.ReadAsync(() => r.Rig.State.RecordedPath.ResumeState) != 2; i++)
                await r.SendAsync("recpath.cycleResume");
            await r.SendAsync("recpath.play");
            Started = await r.Rig.ReadAsync(() => r.Rig.State.RecordedPath.IsDrivingRecordedPath);
            r.HandSteer = null;
            await r.SendAsync("autosteer.toggle");
            Engaged = await r.Rig.ReadAsync(() => r.Rig.Vm.IsAutoSteerEngaged);
            if (!Engaged) Refusal = string.Join(" | ", r.Collect().Where(s => s.Contains("AutoSteer", StringComparison.Ordinal)).Distinct().TakeLast(2));
            r.Did(F($"picked '{Name}', resume Closest, Play ({(Started ? "playing" : "NOT playing")}), AutoSteer {(Engaged ? "engaged" : "did NOT engage: " + Refusal)}; hands off"));
            r.ExpectEngaged = Engaged;
            _phase = 2; _phaseStart = _cum; PlaybackStart = r.Seconds;
            return;
        }

        // Playback: how far the tractor is from the route, and whether AgOpenWeb says it is done.
        if (_routeFrame != null)
        {
            double d = Math.Abs(_routeFrame.U(p));
            double along = _routeFrame.V(p);
            if (along > 5 && along < _routeFrame.Length - 5)
            {
                FollowSum += d; FollowSamples++;
                FollowWorst = Math.Max(FollowWorst, d);
            }
        }
        var c = r.Rig.LastCycle;
        if (c != null) { SteerSamples++; if (c.IsAutoSteerEngaged && Math.Abs(c.Guidance?.SteerAngle ?? 0) > 0.5) SteerNonZero++; }
        PlaybackSeconds = r.Seconds - PlaybackStart;
        if (r.Said.Any(s => s.Contains("Recorded path complete", StringComparison.Ordinal))) Completed = true;
        bool tooFar = FollowWorst > 15;
        if (Completed || tooFar || _cum - _phaseStart > 1.2 * L)
        {
            if (tooFar && Trouble == null) Trouble = F($"After {PlaybackSeconds:0} s of playback the tractor was {FollowWorst:0} m off the recorded route");
            r.EndedBy = Completed ? "AgOpenWeb said the recorded path is complete" : tooFar ? "the operator stopped: the tractor left the recorded route" : "the operator stopped: the route should have been done";
            r.FieldDone = true;
            r.Stopped = true;
            r.Did(r.EndedBy);
        }
    }

    public override void Metrics(Scenario sc, JobRun r)
    {
        sc.Row.Metrics.Add(("Route recorded / playback: playing, AutoSteer engaged, AgOpenWeb's steer angle not zero", F($"{(_routeFrame?.Length ?? 0):0} m / {(Started ? "yes" : "no")}, {(Engaged ? "yes" : "no")}, {(SteerSamples > 0 ? 100.0 * SteerNonZero / SteerSamples : 0):0} % of fixes")));
        sc.Row.Metrics.Add(("The tractor against the recorded route during playback: mean / worst; playback time; 'complete' said", F($"{(FollowSamples > 0 ? FollowSum / FollowSamples : 0):0.00} m / {FollowWorst:0.00} m; {PlaybackSeconds:0} s; {(Completed ? "yes" : "no")}")));
    }

    public override void Judge(Scenario sc, JobRun r, JobAreas a, Action<Severity, string, string> flag)
    {
        if (!Started) flag(Severity.Bug, "RECORDED-PATH-NOT-PLAYED", "Play did not start the recorded route: " + string.Join(" | ", r.Said.TakeLast(3)));
        else if (FollowWorst > 2 || Trouble != null)
            flag(r.Problems.Any(p => p.Contains("ran", StringComparison.Ordinal)) ? Severity.Safety : Severity.Bug, "RECORDED-PATH-NOT-FOLLOWED",
                F($"with the recorded route playing{(Engaged ? " and AutoSteer engaged" : F($", AutoSteer would not engage ({Refusal})"))}, the tractor did not follow it: {(FollowSamples > 0 ? FollowSum / FollowSamples : 0):0.0} m off on average, {FollowWorst:0.0} m at worst; AgOpenWeb's steer angle to the steer module was not zero in {(SteerSamples > 0 ? 100.0 * SteerNonZero / SteerSamples : 0):0} % of the fixes.{(Trouble != null ? " " + Trouble + "." : "")}{(Engaged ? "" : " Playback (MainViewModel.UpdateRecordedPathPlayback) steers only the built-in simulator's wheel (SimulatorSteerAngle) and gives AutoSteer no line, so on a real machine nothing steers along the route.")}"));
        else
            sc.Ok(F($"the tractor followed the recorded route ({FollowSum / Math.Max(1, FollowSamples):0.00} m mean, {FollowWorst:0.00} m worst)"));
        if (Started && !Completed && FollowWorst <= 2)
            flag(Severity.Inconsistency, "RECORDED-PATH-NOT-DONE", "the route was driven to its end but AgOpenWeb never said 'Recorded path complete'.");
    }
}

/// <summary>
/// How deep each square metre of a field lies: its distance from the outer boundary, on a 1 m
/// grid. The ground at least d deep is where a lap d in from the boundary can run; when it falls
/// into separate pieces the lap would have to split, and one closed line cannot.
/// </summary>
public sealed class DepthGrid
{
    private const double Cell = 1.0;
    private readonly double _e0, _n0;
    private readonly int _w, _h;
    private readonly float[] _d;   // NaN outside the field

    public DepthGrid(IReadOnlyList<(double E, double N)> outer)
    {
        double e0 = outer.Min(p => p.E), e1 = outer.Max(p => p.E), n0 = outer.Min(p => p.N), n1 = outer.Max(p => p.N);
        _e0 = e0; _n0 = n0;
        _w = (int)Math.Ceiling((e1 - e0) / Cell) + 1;
        _h = (int)Math.Ceiling((n1 - n0) / Cell) + 1;
        _d = new float[_w * _h];
        for (int y = 0; y < _h; y++)
            for (int x = 0; x < _w; x++)
            {
                var c = (_e0 + (x + 0.5) * Cell, _n0 + (y + 0.5) * Cell);
                _d[y * _w + x] = Ring.Inside(c, outer) ? (float)Ring.DistToRing(c, outer) : float.NaN;
            }
    }

    /// <summary>The pieces of ground at least <paramref name="depth"/> in from the boundary, m², largest first.</summary>
    public List<double> Pieces(double depth)
    {
        var seen = new bool[_d.Length];
        var sizes = new List<double>();
        var stack = new Stack<int>();
        for (int i = 0; i < _d.Length; i++)
        {
            if (seen[i] || !(_d[i] >= depth)) continue;
            int n = 0;
            seen[i] = true; stack.Push(i);
            while (stack.Count > 0)
            {
                int j = stack.Pop(); n++;
                int x = j % _w, y = j / _w;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx < 0 || yy < 0 || xx >= _w || yy >= _h) continue;
                        int k = yy * _w + xx;
                        if (!seen[k] && _d[k] >= depth) { seen[k] = true; stack.Push(k); }
                    }
            }
            sizes.Add(n * Cell * Cell);
        }
        sizes.Sort((a, b) => b.CompareTo(a));
        return sizes;
    }
}

/// <summary>
/// A lap's corners: where the tractor turns tighter than twice the U-turn radius, and the 10 m
/// after. Laps' corners are rounded, so an inner lap's corner pulls away from the outer lap's:
/// the pass edges and the pass error leave the corners out (<see cref="JobRun.InCorner"/>), and
/// the unsprayed ground there is counted apart (<see cref="JobRun.AtCorner"/>).
/// </summary>
public sealed class LapCorners
{
    private double _cornerCum = double.NegativeInfinity;
    private readonly Dictionary<(int, int), List<(double E, double N)>> _points = new();
    private (double E, double N) _last = (double.NaN, double.NaN);

    public void Update(JobRun r, double cum)
    {
        double steer = Math.Abs(r.Truth.Steer) * Math.PI / 180;
        double radius = steer < 1e-3 ? double.PositiveInfinity : r.Truth.V.Wheelbase / Math.Tan(steer);
        if (radius < 2 * r.Rig.Store.Guidance.UTurnRadius && !r.InChange)
        {
            _cornerCum = cum;
            var t = r.Truth.ToolCentre;
            if (!(Math.Abs(t.E - _last.E) < 1 && Math.Abs(t.N - _last.N) < 1))
            {
                _last = (t.E, t.N);
                var key = ((int)Math.Floor(t.E / 5), (int)Math.Floor(t.N / 5));
                if (!_points.TryGetValue(key, out var l)) _points[key] = l = new();
                l.Add((t.E, t.N));
            }
        }
        r.InCorner = cum - _cornerCum < 10;
        if (r.AtCorner == null)
        {
            double reach = r.Spec.T.Width / 2 + 3;
            r.AtCorner = p =>
            {
                int bx = (int)Math.Floor(p.E / 5), by = (int)Math.Floor(p.N / 5), n = (int)Math.Ceiling(reach / 5);
                for (int x = bx - n; x <= bx + n; x++)
                    for (int y = by - n; y <= by + n; y++)
                        if (_points.TryGetValue((x, y), out var l))
                            foreach (var q in l)
                                if ((q.E - p.E) * (q.E - p.E) + (q.N - p.N) * (q.N - p.N) < reach * reach) return true;
                return false;
            };
        }
    }
}
