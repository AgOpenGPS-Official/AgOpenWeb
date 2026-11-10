// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using AgOpenWeb.Models;

namespace AgOpenWeb.DumpReplayScenarios.Tests;

/// <summary>
/// The take-over: AgOpenWeb boots fresh with the reporter's settings, field, job and track (the
/// replay's set-up), the reporter's GPS log is fed up to a moment on a line shortly before their
/// first headland, and from there a simulated machine with the reporter's geometry (the field job
/// harness's bicycle model, steering motor and implement linkage) carries on, steered by the
/// angle AgOpenWeb sends, with AutoSteer and U-turns on. A replay can only say what AgOpenWeb
/// decided on the reporter's path; the take-over shows where this build would have driven them.
/// </summary>
public sealed class TakeOver : IAsyncDisposable
{
    public const double FixSeconds = 0.1;
    /// <summary>Real ms per simulated fix: 100 is real time (the default: AgOpenWeb's GPS-lost,
    /// guidance and hello watchdogs run on the wall clock).</summary>
    public static readonly int PaceMs = int.TryParse(Environment.GetEnvironmentVariable("AGOPENWEB_SCENARIO_PACE"), out var p) ? p : 100;
    /// <summary>Simulated seconds the take-over drives at most.</summary>
    public static readonly double MaxSeconds = double.TryParse(Environment.GetEnvironmentVariable("AGOPENWEB_TAKEOVER_SECONDS"), NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? m : 240;

    private readonly DumpEntry _entry;
    private readonly BugDump _dump;
    private readonly Scenario _s;
    private DumpReplay _base = null!;
    public TruthMachine Truth { get; private set; } = null!;

    public TakeOver(DumpEntry entry, BugDump dump, Scenario s) { _entry = entry; _dump = dump; _s = s; }

    private static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    public async Task<bool> SetUpAsync(string dataRoot)
    {
        if (!_dump.Rows.Any(r => r.AutoSteer))
        {
            _s.Step("take-over: not run (the reporter never had AutoSteer on in the GPS log)");
            return false;
        }
        // The replay's set-up, quietly (its steps and checks are already in the report).
        _base = new DumpReplay(_entry, _dump, new Scenario(_entry.Id + "-takeover", "", ""));
        if (!await _base.SetUpAsync(dataRoot)) { _s.Step("take-over: not run (the set-up failed, see above)"); return false; }
        return true;
    }

    /// <summary>Where to take over: 25 s before the reporter's first headland approach on a line
    /// with AutoSteer on, else the first row with AutoSteer on and a line.</summary>
    private int StartRow()
    {
        var rows = _dump.Rows;
        bool Good(int i) => rows[i].AutoSteer && rows[i].HasGuidance && !rows[i].YtExecuting && !rows[i].Reverse && rows[i].Speed > 0.5 && Math.Abs(rows[i].Xte) < 0.5;
        var appr = ReplayJudge.Approaches(_base).Select(a => a.Row).Where(i => i < rows.Count && rows[i].AutoSteer).ToList();
        if (appr.Count > 0)
        {
            for (int i = Math.Max(_base.StartRow, appr[0] - 250); i < appr[0]; i++) if (Good(i)) return i;
        }
        for (int i = _base.StartRow; i < rows.Count; i++) if (Good(i)) return i;
        // The reporter stood still on the line the whole log (a simulator left running): the
        // machine sets off from there.
        for (int i = _base.StartRow; i < rows.Count; i++)
            if (rows[i].AutoSteer && rows[i].HasGuidance && !rows[i].YtExecuting && Math.Abs(rows[i].Xte) < 0.5) return i;
        return -1;
    }

    public async Task DriveAsync()
    {
        var rig = _base.Rig;
        var rows = _dump.Rows;
        int start = StartRow();
        if (start < 0) { _s.Step("take-over: not run (no row on a line with AutoSteer on)"); return; }
        var store = rig.Store;
        var v = new VehicleSpec("reporter's vehicle", 0,
            Math.Max(1.0, store.Vehicle.Wheelbase), store.Vehicle.TrackWidth, store.Vehicle.AntennaHeight,
            store.Vehicle.AntennaPivot, store.Vehicle.AntennaOffset, Math.Abs(store.Tool.HitchLength), Math.Max(10, store.Vehicle.MaxSteerAngle));
        string type = store.Tool.IsToolTBT ? "tbt" : store.Tool.IsToolTrailing ? "trailing" : store.Tool.IsToolFrontFixed ? "front" : "rear";
        var t = new ToolSpec("reporter's implement", type, store.ActualToolWidth, Math.Max(1, store.NumSections), store.Tool.Offset,
            type is "front" or "rear" ? Math.Abs(store.Tool.HitchLength) : Math.Abs(store.Tool.TrailingHitchLength),
            store.Tool.TrailingToolToPivotLength, Math.Abs(store.Tool.TankTrailingHitchLength), store.Tool.Overlap);
        Truth = new TruthMachine(v, t, 0);
        double speedKmh = Math.Clamp(Median(rows.Where(r => r.AutoSteer && r.Speed > 0.5).Select(r => r.Speed * 3.6).ToList()), 6, 12);
        Truth.SpeedKmh = speedKmh;
        bool dualLike = store.Connections.IsDualGps || _dump.IsSimulator || rows.Any(r => r.Sentence == "PAOGI");
        bool imuLike = rows.Any(r => r.ImuValid);

        // The reporter's own fixes for the 5 s before, so heading, pass and history are theirs.
        int lead = Math.Max(_base.StartRow, start - 50);
        for (int i = lead; i < start; i++)
        {
            rig.Gps.MarkRealGpsParsed();
            rig.Feed(_base.GpsFor(rows[i]));
            rig.TickLoop(10);
            await Task.Delay(PaceMs);
        }
        // On the reporter's pass (the lateral button, as in the replay), standing at the start.
        for (int k = 0; k < 50 && !_base.AlignDone; k++)
        {
            rig.Gps.MarkRealGpsParsed();
            rig.Feed(_base.GpsFor(rows[start]));
            rig.TickLoop(10);
            await _base.AlignPassStepAsync(rows[start]);
            await Task.Delay(PaceMs);
        }
        var r0 = rows[start];
        // The machine lives in the field's frame (the recorded pivot, after the drift), so the
        // boundary checks hold; its receiver reports the position before the drift.
        Truth.Place(r0.E, r0.N, r0.Heading * Math.PI / 180.0);
        // U-turns on, whatever the reporter had: the take-over drives 4 minutes toward the
        // headland, and with them off its run off the field would be the harness's doing.
        if (!await rig.ReadAsync(() => rig.Vm.IsYouTurnEnabled)) await rig.SendAsync("youturn.toggle");
        if (!await rig.ReadAsync(() => rig.Vm.IsAutoSteerEngaged)) await rig.SendAsync("autosteer.toggle");
        if (_base.SectionsAuto && !await rig.ReadAsync(() => rig.Vm.IsSectionMasterOn)) await rig.SendAsync("section.master");
        bool engaged0 = await rig.ReadAsync(() => rig.Vm.IsAutoSteerEngaged);
        _s.Step(F($"take-over at t {r0.T:0.0} s on pass {r0.PathsAway}: a machine with the reporter's geometry ({type} implement {t.Width:0.##} m, wheelbase {v.Wheelbase:0.##} m) at {speedKmh:0} km/h, AutoSteer {(engaged0 ? "on" : "did NOT engage")}, U-turns {(await rig.ReadAsync(() => rig.Vm.IsYouTurnEnabled) ? "on" : "did NOT switch on")}{(_base.UTurnsUsed ? "" : " (the reporter's were off: switched on for the take-over)")}"));
        if (!engaged0) { _s.Problem(Severity.Bug, Kind.AppDefect, "TAKEOVER-NO-AUTOSTEER", "AutoSteer did not engage at the take-over: " + string.Join(" | ", _base.Collect().Take(3))); return; }

        lock (_base.Sounds) _base.Sounds.Clear();
        _base.Clock = 0;
        var outer = _base.Outer;
        double setDist = store.Guidance.UTurnDistanceFromBoundary;
        int fix = 0, maxFixes = (int)(MaxSeconds / FixSeconds);
        double cmd = 0;
        bool wasExec = false; int turnsDone = 0, offSince = -1, reengaged = 0; double doneAt = -1;
        double turnStart = 0, turnMinPivot = double.MaxValue, turnMinTool = double.MaxValue, turnPivotOut = 0, turnToolOut = 0;
        bool warned = false;
        var turnLines = new List<string>();
        var said = new List<string>();
        // How late each fix goes in against the 100 ms pace (the watchdogs run on the wall clock).
        var pace = System.Diagnostics.Stopwatch.StartNew();
        double maxLate = 0; int lateFixes = 0;
        while (fix < maxFixes)
        {
            fix++;
            double secs = fix * FixSeconds;
            Truth.Advance(FixSeconds, cmd);
            _base.Clock = secs;
            if (PaceMs > 0)
            {
                double late = pace.Elapsed.TotalMilliseconds - PaceMs;
                maxLate = Math.Max(maxLate, late);
                if (late > Scenario.LateMs) { lateFixes++; _s.Late.Add(("take-over", secs, late)); }
                pace.Restart();
            }
            rig.Gps.MarkRealGpsParsed();
            rig.Feed(Fix(rig, dualLike, imuLike));
            rig.TickLoop(10);
            var c = rig.LastCycle;
            bool engaged = c?.IsAutoSteerEngaged == true;
            cmd = engaged && c!.Guidance is { HasGuidance: true } g ? g.SteerAngle : 0;
            if (!double.IsFinite(cmd)) { _s.Problem(Severity.Safety, Kind.AppDefect, "NOT-A-NUMBER", F($"take-over {secs:0.0} s: steer angle {cmd}"), "take-over", secs); cmd = 0; }
            bool exec = c?.YouTurn?.IsExecuting == true;
            var (pivotOut, toolOut, pivotIn, toolIn) = Where(outer);
            if (exec && !wasExec) { turnStart = secs; turnMinPivot = turnMinTool = double.MaxValue; turnPivotOut = turnToolOut = 0; }
            if (exec)
            {
                turnMinPivot = Math.Min(turnMinPivot, pivotIn);
                turnMinTool = Math.Min(turnMinTool, toolIn);
                turnPivotOut = Math.Max(turnPivotOut, pivotOut);
                turnToolOut = Math.Max(turnToolOut, toolOut);
                if (secs - turnStart > 120) { _s.Problem(Severity.Bug, Kind.AppDefect, "TURN-NOT-FINISHED", F($"take-over: a U-turn still running after 120 s"), "take-over", turnStart, secs); break; }
            }
            if (!exec && wasExec)
            {
                turnsDone++;
                turnLines.Add(F($"U-turn {turnsDone}: {secs - turnStart:0} s, nearest the boundary: pivot {turnMinPivot:0.0} m, implement {turnMinTool:0.0} m inside (set: {setDist:0.0} m){(turnPivotOut > 0 ? F($", the pivot {turnPivotOut:0.0} m OUTSIDE") : "")}{(turnToolOut > 0 ? F($", an implement end {turnToolOut:0.0} m outside") : "")}"));
                _s.Step(F($"take-over {secs:0.0} s: ") + turnLines[^1]);
                if (turnPivotOut > 0.3)
                    _s.Problem(Severity.Safety, Kind.AppDefect, "TURN-OUTSIDE-BOUNDARY", F($"take-over U-turn {turnsDone} took the tractor itself {turnPivotOut:0.0} m past the outer boundary (distance from boundary {setDist:0.0} m)"), "take-over", turnStart, secs);
                else if (turnToolOut > 0.3)
                    // As the field job harness: 'distance from boundary' keeps the tractor inside, not
                    // the implement, in AgOpenWeb as in AgOpenGPS, kept on purpose (decided
                    // 2026-11-10): the step line above says how far the implement swung; no finding.
                    _s.Step(F($"take-over U-turn {turnsDone}: as in AgOpenGPS, the implement's end swung {turnToolOut:0.0} m past the boundary (distance from boundary keeps the tractor inside)"));
                if (turnPivotOut <= 0.3 && setDist > 1.5 && turnMinPivot < setDist - 1.0)
                    _s.Problem(Severity.Bug, Kind.AppDefect, "TURN-CLOSER-THAN-SET", F($"take-over U-turn {turnsDone}: the pivot came {turnMinPivot:0.0} m from the boundary, the setting asks {setDist:0.0} m"), "take-over", turnStart, secs);
                if (turnsDone >= 2 && doneAt < 0) doneAt = secs;
            }
            wasExec = exec;
            if (doneAt > 0 && secs - doneAt > 15) break;

            if (fix % 10 == 0)
                foreach (var e in _base.Collect())
                {
                    said.Add(F($"{secs:0.0} s: {e}"));
                    // A refused U-turn (#698/#716): no turn, the operator is warned and takes the wheel.
                    if (e.Contains("No U-turn fits", StringComparison.Ordinal) || e.Contains("take over manually", StringComparison.OrdinalIgnoreCase)) warned = true;
                }
            if (!engaged && c != null)
            {
                if (offSince < 0)
                {
                    offSince = fix;
                    _s.Step(F($"take-over {secs:0.0} s: AutoSteer off: {c.DisengageReason ?? "no reason given"}"));
                    if (warned) break;
                    if (reengaged == 0)
                        _s.Problem(Severity.Bug, Kind.AppDefect, "TAKEOVER-AUTOSTEER-DROPPED", F($"take-over: AutoSteer went off by itself ({c.DisengageReason ?? "no reason given"})"), "take-over", secs);
                }
                else if (fix - offSince == 20 && reengaged < 3)
                {
                    reengaged++;
                    await rig.SendAsync("autosteer.toggle");
                }
            }
            else offSince = -1;
            if (pivotOut > 3 && !exec)
            {
                if (warned) { _s.Note(F($"take-over {secs:0.0} s: no U-turn, the operator was warned (\"No U-turn fits ...\"): the job stops here")); }
                else _s.Problem(Severity.Safety, Kind.AppDefect, "TAKEOVER-RAN-OFF", F($"take-over {secs:0.0} s: the machine left the field ({pivotOut:0} m past the boundary) with AutoSteer on, no U-turn and no warning"), "take-over", secs);
                break;
            }
            // The pace: 100 ms from the start of this fix to the start of the next.
            if (PaceMs > 0) { double rest = PaceMs - pace.Elapsed.TotalMilliseconds; if (rest > 1) await Task.Delay((int)rest); }
        }
        ReplayJudge.Alarms(_s, _base.Sounds, "take-over", 0);
        if (PaceMs > 0) _s.Row.Metrics.Add(("GPS feed lateness (take-over)", F($"max {maxLate:0} ms, {lateFixes} fixes more than {Scenario.LateMs:0} ms late")));
        _s.Row.Metrics.Add(("Take-over", F($"{fix * FixSeconds:0} s driven, {turnsDone} U-turn{(turnsDone == 1 ? "" : "s")}") + (turnLines.Count > 0 ? ": " + string.Join("; ", turnLines) : "")));
        foreach (var l in said.Where(l => !l.Contains("status:", StringComparison.Ordinal)).Take(12)) _s.Step("take-over " + l);
        if (_base.Exceptions.Count > 0) _s.Problem(Severity.Bug, Kind.AppDefect, "EXCEPTION", "an exception on the host loop (take-over): " + _base.Exceptions[0].Split('\n')[0]);
    }

    /// <summary>The fix the machine's receiver gives: the antenna, the true heading as the
    /// reporter's system reports it (a dual heading, or an IMU heading), RTK fixed.</summary>
    private GpsData Fix(ReplayRig rig, bool dual, bool imu)
    {
        var (ae, an) = Truth.Antenna;
        var w = _base.Plane.ConvertGeoCoordToWgs84(new GeoCoord(an - _base.DriftN, ae - _base.DriftE));
        double hdg = (Truth.Heading * 180 / Math.PI + 360) % 360;
        return new GpsData
        {
            CurrentPosition = new Position { Latitude = w.Latitude, Longitude = w.Longitude, Altitude = 100, Heading = dual || imu ? hdg : 0, Speed = Truth.SpeedKmh / 3.6 },
            FixQuality = 4, SatellitesInUse = 20, Hdop = 0.7, DifferentialAge = 1.0,
            ImuRoll = 0, ImuHeading = imu ? hdg : 0, ImuValid = imu,
            HasDualHeading = dual,
            SentenceType = dual ? GpsSentenceType.Paogi : GpsSentenceType.Panda,
            Timestamp = DateTime.UtcNow,
        };
    }

    /// <summary>How far past the outer boundary the pivot and the implement's ends are (0 inside),
    /// and how far inside them the nearest boundary is (0 outside).</summary>
    private (double PivotOut, double ToolOut, double PivotIn, double ToolIn) Where(List<(double E, double N)> outer)
    {
        if (outer.Count < 3) return (0, 0, double.MaxValue, double.MaxValue);
        var pv = (Truth.E, Truth.N);
        bool pIn = Ring.Inside(pv, outer);
        double pd = Ring.DistToRing(pv, outer);
        var tc = Truth.ToolCentre;
        double re = Math.Cos(tc.Heading), rn = -Math.Sin(tc.Heading), hw = Truth.T.Width / 2;
        double toolOut = 0, toolIn = double.MaxValue;
        foreach (var q in new[] { (tc.E + re * hw, tc.N + rn * hw), (tc.E - re * hw, tc.N - rn * hw) })
        {
            double dq = Ring.DistToRing(q, outer);
            if (Ring.Inside(q, outer)) toolIn = Math.Min(toolIn, dq);
            else { toolOut = Math.Max(toolOut, dq); toolIn = 0; }
        }
        return (pIn ? 0 : pd, toolOut, pIn ? pd : 0, toolIn);
    }

    private static double Median(List<double> v) => v.Count == 0 ? 9 : v.OrderBy(x => x).ElementAt(v.Count / 2);

    public async ValueTask DisposeAsync()
    {
        if (_base != null) await _base.DisposeAsync();
    }
}
