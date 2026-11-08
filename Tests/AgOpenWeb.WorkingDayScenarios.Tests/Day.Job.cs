// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using AgOpenWeb.Models;

namespace AgOpenWeb.WorkingDayScenarios.Tests;

/// <summary>The fields, the road between them, and the fix-by-fix drive.</summary>
public sealed partial class Day
{
    // The open field, in its plane (as in the field job harness).
    private LocalPlane? _plane;
    private List<(double E, double N)> _outer = new();
    private List<(double E, double N)>? _work;
    private (double E, double N) _a, _along, _across;
    private double _spacing;
    private JobResult? _job;
    public bool FieldDone, InTurn, ManualTurnFailed;
    private bool _seenWorkThisPass;
    private long _manualAt = -1;
    private double _turnFromU;
    private int _passSide;
    private bool _wasExecuting, _arrowChecked;
    private int _offSince = -1, _reengaged;
    private (double Lat, double Lon) _road;

    public static string TaskName(DayJob j) => "Spraying " + j.Field + (j.Reopen ? " 2" : "");

    // ------------------------------------------------------------------ one field

    private async Task<JobResult?> JobAsync(DayJob job)
    {
        var r = new JobResult { Job = job, Start = Now };
        Jobs.Add(r);
        _job = r;
        Phase = job.Code;
        FieldDone = false; InTurn = false; _wasExecuting = false; _arrowChecked = false; _offSince = -1; _reengaged = 0;
        ManualTurnFailed = false; _seenWorkThisPass = false; _manualAt = -1; _passSide = 0;
        var field = ScenarioFields.Get(job.Field);
        var o = field.InsidePoint();
        for (int i = 0; i < 15; i++) { SendFixAt(o.Lat, o.Lon, 0); await Task.Delay(5); }

        var sw = Stopwatch.StartNew();
        if (!job.Reopen)
        {
            Directory.CreateDirectory(Rig.ImportDir);
            await File.WriteAllTextAsync(Path.Combine(Rig.ImportDir, job.Field + ".kml"), field.ToKml(job.Obstacles, job.Field));
            await SendAsync("import.scan");
            await SendAsync("field.fromKml", job.Field + ".kml\t" + job.Field);
        }
        else
            await SendAsync("field.startJob", job.Field + "\tSpraying\tday\t" + TaskName(job));
        if (!await Rig.WaitUntilAsync(() => Rig.Vm.IsFieldOpen && Rig.Vm.CurrentFieldName == job.Field && !Rig.State.UI.IsBusy, 30000))
        { Harness(job.Code + ": the field did not open"); return null; }
        r.OpenSeconds = sw.Elapsed.TotalSeconds;
        _plane = await Rig.ReadAsync(() => Rig.State.Field.LocalPlane)
            ?? new LocalPlane(new Wgs84(Rig.State.Field.OriginLatitude, Rig.State.Field.OriginLongitude), new SharedFieldProperties());
        _outer = field.Outer.Select(p => Local(p.Lat, p.Lon)).ToList();

        var tr = field.Tracks.FirstOrDefault(t => t.Name == job.Track);
        if (tr.Points == null || tr.Points.Count < 2) { Harness($"{job.Code}: no track '{job.Track}' in '{job.Field}'"); return null; }
        var pa = tr.Points[0]; var pb = tr.Points[^1];
        if (!job.Reopen)
            await SendAsync("track.latLonAB", F($"{pa.Lat:F9},{pa.Lon:F9},{pb.Lat:F9},{pb.Lon:F9}"));
        if (!await Rig.ReadAsync(() => Rig.Vm.IsAutoSteerAvailable)) await SendAsync("track.activate", "0");
        if (!await Rig.ReadAsync(() => Rig.Vm.IsAutoSteerAvailable)) { Harness(job.Code + ": no active AB line"); return null; }
        _a = Local(pa.Lat, pa.Lon);
        var b = Local(pb.Lat, pb.Lon);
        double len = Math.Sqrt((b.E - _a.E) * (b.E - _a.E) + (b.N - _a.N) * (b.N - _a.N));
        _along = ((b.E - _a.E) / len, (b.N - _a.N) / len);
        _across = (_along.N, -_along.E);

        if (!job.Reopen)
        {
            await SendAsync("headland.wholeBoundary", F($"{job.HeadlandM}"));
            if (!await Rig.WaitUntilAsync(() => Rig.State.Field.HeadlandLine is { Count: > 2 }, 15000))
            { Harness(job.Code + ": no headland line was built"); return null; }
        }
        _work = await Rig.ReadAsync(() => Rig.State.Field.HeadlandLine?.Select(p => (p.Easting, p.Northing)).ToList());
        if (!await Rig.ReadAsync(() => Rig.Vm.IsYouTurnEnabled)) await SendAsync("youturn.toggle");
        if (!job.Reopen)
            await SendAsync("field.startJob", job.Field + "\tSpraying\tday\t" + TaskName(job));
        bool jobOn = await Rig.WaitUntilAsync(() => Rig.Vm.IsFieldOpen && !Rig.State.UI.IsBusy, 15000);
        if (!jobOn) { Harness(job.Code + ": the job did not start"); return null; }
        _spacing = Math.Max(0.1, await Rig.ReadAsync(() => Rig.Store.ActualToolWidth - Rig.Store.Tool.Overlap));
        Event(job.Code, F($"{job.Title}: {(job.Reopen ? "opened from the field list" : "imported from KML")}, AB line, headland, U-turns on, job '{TaskName(job)}' started ({r.OpenSeconds:0.0} s)"));
        ResetPace();

        var (pivot, hdg, k0) = StartPose();
        Truth.Place(pivot.E, pivot.N, hdg);
        Truth.SpeedKmh = WorkKmh;
        Parked = false;
        for (int i = 0; i < 30; i++) await StepAsync(false);
        await EngageAsync();
        if (!await Rig.ReadAsync(() => Rig.Vm.IsAutoSteerEngaged)) { Harness(job.Code + ": AutoSteer did not engage at the start"); return null; }

        double workArea = Ring.Area(_work ?? _outer);
        long maxFixes = Fixes + (long)(workArea / (Sprayer.Width * WorkKmh / 3.6) / FixSeconds * 3) + 6000;
        long start = Fixes;
        double leftWorkAt = -1; bool inWorkSinceEnd = false;
        double parkedAtStart = ParkedSeconds;
        while (true)
        {
            await StepAsync(true);
            if (Now >= DayEnd) { r.CutShort = true; break; }
            if (Fixes >= maxFixes) { r.Note = F($"stopped after {(Fixes - start) * FixSeconds / 60:0} minutes without AgOpenWeb reporting the end of the field"); break; }
            if (ManualTurnFailed) { r.Note = F($"at {Now:HH:mm} AgOpenWeb had no U-turn at the end of a pass and the manual U-turn did not start either"); break; }
            if (_reengaged > 12) { r.Note = "AutoSteer kept dropping out; the operator gave up after 12 re-engagements"; break; }
            var tc = Truth.ToolCentre;
            bool inWork = Ring.Inside((tc.E, tc.N), _work ?? _outer);
            if (FieldDone)
            {
                if (inWork) inWorkSinceEnd = true;
                else if (inWorkSinceEnd || (leftWorkAt >= 0 && (Fixes - leftWorkAt) * FixSeconds > 6)) { r.Done = true; break; }
                if (!inWork && leftWorkAt < 0) leftWorkAt = Fixes;
            }
            if (!Ring.Inside((Truth.E, Truth.N), _outer) && Ring.DistToRing((Truth.E, Truth.N), _outer) > 25)
            {
                var lc = Rig.LastCycle;
                r.Note = F($"the tractor ran 25 m out of the field at {Now:HH:mm} (AutoSteer {(lc?.IsAutoSteerEngaged == true ? "engaged" : "off")}, U-turn {(lc?.YouTurn is { IsExecuting: true } ? "executing" : lc?.YouTurn?.TurnPath is { Count: > 1 } ? "planned, not started" : "none planned")}; AgOpenWeb's last status, at {LastStatus.Clock:HH:mm:ss}: \"{LastStatus.Text}\")");
                break;
            }
        }
        await ParkAsync();
        for (int i = 0; i < 20; i++) await StepAsync(false);
        r.AreaHa = Coverage.TotalWorkedArea / 1e4;
        r.ParkedMinutes = (ParkedSeconds - parkedAtStart) / 60;
        r.DrivenMinutes = (Fixes - start) * FixSeconds / 60 - r.ParkedMinutes;
        r.Reengaged = _reengaged;
        Event(job.Code + "-END", F($"{(r.Done ? "AgOpenWeb reported the end of the field" : r.CutShort ? "the day ended in the field" : "stopped: " + r.Note)}; {r.AreaHa:0.00} ha worked, {r.Turns} U-turns ({r.ManualTurns} of them by the operator's button), {r.DrivenMinutes:0} min driving, {r.ParkedMinutes:0} min parked"));

        // Close the field (the job is saved); answer the host's question if it asks one.
        sw.Restart();
        await SendAsync("field.close");
        for (int i = 0; i < 3 && await Rig.ReadAsync(() => Rig.Vm.IsPromptPending); i++)
        {
            var asked = await Rig.AnswerPromptAsync(true);
            Event("PROMPT", "closing the field asked: " + asked + " (answered yes)");
        }
        if (!await Rig.WaitUntilAsync(() => !Rig.Vm.IsFieldOpen && !Rig.State.UI.IsBusy, 30000))
        { Harness(job.Code + ": the field did not close"); return null; }
        Event("CLOSE", F($"{job.Field} closed in {sw.Elapsed.TotalSeconds:0.0} s"));
        r.End = Now;
        var here = Wgs(Truth.E, Truth.N);
        _road = (here.Latitude, here.Longitude);
        _job = null;
        _plane = null;
        Phase = "between fields";
        return r;
    }

    // ------------------------------------------------------------------ the road

    /// <summary>To the next field: eight minutes on the road with no field open (the GPS cycle
    /// without a field), then the tractor is at the next field's gate.</summary>
    private async Task RoadAsync(DayJob next)
    {
        Phase = "road";
        Event("ROAD", "on the road to " + next.Field + ", no field open");
        Parked = false;
        for (int i = 0; i < 4800 && Now < DayEnd; i++)
        {
            _road = Geo.Offset(_road, 0, RoadKmh / 3.6 * FixSeconds);
            await StepAsync(true, road: true);
        }
        Parked = true;
    }

    // ------------------------------------------------------------------ one fix

    /// <summary>One fix of the day: the tractor moves 0.1 s, the receiver sends its sentence
    /// (the GPS cycle runs inline and is timed), the 100 Hz loop runs ten ticks (timed), the
    /// operator does what the day's clock says, the process is looked at every five minutes.</summary>
    public async Task StepAsync(bool events, bool road = false)
    {
        Fixes++;
        Module.TickNow(); Module.TickNow();
        double lat, lon, hdg, kmh;
        if (road)
        {
            (lat, lon, hdg, kmh) = (_road.Lat, _road.Lon, 0, RoadKmh);
        }
        else if (_plane != null)
        {
            Truth.Advance(FixSeconds, Parked ? 0 : _steerCmd);
            var (ae, an) = Truth.Antenna;
            var w = Wgs(ae, an);
            (lat, lon, hdg, kmh) = (w.Latitude, w.Longitude, Truth.Heading * 180 / Math.PI, Math.Abs(Truth.SpeedKmh));
        }
        else
        {
            (lat, lon, hdg, kmh) = (_road.Lat, _road.Lon, 0, 0);
        }
        if (Parked) ParkedSeconds += FixSeconds;

        string sentence = Sentence(lat, lon, hdg, kmh);
        var gc = GcMark.Now();
        long t0 = Stopwatch.GetTimestamp();
        Rig.SendSentence(sentence);
        double cms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        _cycleWin.Add(cms); CycleDay.Add(cms); _job?.Cycle.Add(cms);
        if (cms > 20) Slow("GPS cycle", cms, gc.Since());
        var loop = Rig.Loop;
        for (int t = 0; t < 10; t++)
        {
            gc = GcMark.Now();
            long ts = Stopwatch.GetTimestamp();
            loop.Tick(ts);
            double tms = Stopwatch.GetElapsedTime(ts).TotalMilliseconds;
            _tickWin.Add(tms); TickDay.Add(tms); _job?.Tick.Add(tms);
            if (tms > 10) Slow("control tick", tms, gc.Since());
        }

        if (!road && _plane != null) await FollowAsync();
        if (Fixes % 10 == 0) Collect();
        if (Fixes % 100 == 0) Rig.ProbeHostLoop(ms => { lock (_hostLock) _hostLoopMax = Math.Max(_hostLoopMax, ms); });
        WatchPhoneStall();
        WatchPhoneLoading();
        if (events && !_inEvent) await DayEventsAsync();
        if (Fixes >= _nextSampleFix) await SampleAsync();
        if (Fixes % 4 == 0) await PaceAsync();
    }

    public double ParkedSeconds;

    /// <summary>The slowest GPS cycles and control ticks of the day, with what had just happened.</summary>
    public readonly List<(DateTime Clock, string What, double Ms, string Before, string Gc)> SlowMoments = new();
    /// <summary>Control ticks over 10 ms and GPS cycles over 100 ms that a garbage collection ran in.</summary>
    public long SlowTicksWithGc, SlowCyclesWithGc;

    private void Slow(string what, double ms, string gc)
    {
        var last = Events.LastOrDefault();
        string before = last == null ? "" : F($"{last.Code} {(Now - last.Clock).TotalSeconds:0} s before ({WallSeconds - last.WallSeconds:0.0} s of wall time)");
        SlowMoments.Add((Now, what, ms, before, gc));
        if (gc.Contains("collection", StringComparison.Ordinal) && what == "control tick" && ms > 10) SlowTicksWithGc++;
        if (gc.Contains("collection", StringComparison.Ordinal) && what == "GPS cycle" && ms > 100) SlowCyclesWithGc++;
        if (SlowMoments.Count > 200)
        {
            // Keep the slowest 100.
            var keep = SlowMoments.OrderByDescending(m => m.Ms).Take(100).OrderBy(m => m.Clock).ToList();
            SlowMoments.Clear(); SlowMoments.AddRange(keep);
        }
    }

    /// <summary>What the tractor does with AgOpenWeb's answer: the steer angle, U-turns, and
    /// the operator's eye on AutoSteer.</summary>
    private async Task FollowAsync()
    {
        var c = Rig.LastCycle;
        if (c == null) return;
        bool engaged = c.IsAutoSteerEngaged;
        bool steers = engaged && !(c.IsReverse && !Rig.Store.AutoSteer.SteerInReverse);
        _steerCmd = steers && c.Guidance is { HasGuidance: true } g ? g.SteerAngle : 0;
        if (!double.IsFinite(_steerCmd)) _steerCmd = 0;
        var yt = c.YouTurn;
        bool executing = yt?.IsExecuting == true;
        if (executing && !_wasExecuting && _job != null)
        {
            _job.Turns++; InTurn = true;
            _turnFromU = U((Truth.E, Truth.N));
            _manualAt = -1;
        }
        if (!executing && _wasExecuting)
        {
            InTurn = false;
            // Which way the passes go: the next pass is on the side this turn went to.
            double du = U((Truth.E, Truth.N)) - _turnFromU;
            if (Math.Abs(du) > _spacing / 2) _passSide = Math.Sign(du);
            _seenWorkThisPass = false;
        }
        _wasExecuting = executing;
        if (!Parked) _drivenSinceRefillS += FixSeconds;

        // AgOpenWeb has no U-turn for the end of this pass (none fits inside the boundary, it
        // says nothing), or none ahead any more (a turn cut short when AutoSteer went off in it):
        // the operator sees no turn drawn as the tractor enters the headland, or the fence 4 m
        // ahead, and presses the manual U-turn button toward the next pass.
        if (_job != null && engaged && !Parked && !executing && !FieldDone)
        {
            bool inWork = Ring.Inside((Truth.E, Truth.N), _work ?? _outer);
            if (inWork) { _seenWorkThisPass = true; _manualAt = -1; }
            else if ((_seenWorkThisPass && yt?.TurnPath is not { Count: > 1 })
                     || (Ring.Inside((Truth.E, Truth.N), _outer) && FenceAheadM() < 4))
            {
                if (_manualAt < 0)
                {
                    _manualAt = Fixes;
                    int side = _passSide != 0 ? _passSide : FieldSide();
                    // The tractor's right, in the field plane (heading clockwise from north).
                    double rightE = Math.Cos(Truth.Heading), rightN = -Math.Sin(Truth.Heading);
                    bool right = (rightE * _across.E + rightN * _across.N) * side > 0;
                    await SendAsync(right ? "youturn.manualRight" : "youturn.manualLeft");
                    _job.ManualTurns++;
                    Count("OPERATOR: no U-turn ahead at the end of a pass: the manual U-turn button pressed");
                }
                else if (Fixes - _manualAt > 30) ManualTurnFailed = true;
            }
        }

        // Before the first turn the careful operator looks at the U-turn arrow (as in the field
        // job harness): a turn toward the edge they started from is tapped the other way.
        if (_job != null && _job.Turns == 0 && !executing && !_arrowChecked && yt?.TurnPath is { Count: > 1 } path)
        {
            _arrowChecked = true;
            var end = path[^1];
            if (U((end.Easting, end.Northing)) < U((Truth.E, Truth.N)) - _spacing / 2)
                await SendAsync("youturn.direction");
        }

        // AutoSteer dropped while driving: the operator looks up and engages it again after 2 s.
        if (!Parked && !engaged)
        {
            if (_offSince < 0)
            {
                _offSince = (int)Fixes;
                Count("AUTOSTEER-OFF: " + (c.DisengageReason ?? "no reason given"));
            }
            else if (Fixes - _offSince == 20 && !_inEvent)
            {
                await SendAsync("autosteer.toggle");
                _reengaged++;
                _offSince = -1;
            }
        }
        else _offSince = -1;
    }

    /// <summary>A thing the operator saw, counted (AutoSteer dropping out and why).</summary>
    private void Count(string key)
    {
        if (Said.TryGetValue(key, out var v)) Said[key] = (v.Count + 1, v.First, v.Text);
        else Said[key] = (1, Now, key);
    }

    // ------------------------------------------------------------------ park and go

    public async Task ParkAsync()
    {
        if (await Rig.ReadAsync(() => Rig.Vm.IsSectionMasterOn)) await SendAsync("section.master");
        if (await Rig.ReadAsync(() => Rig.Vm.IsAutoSteerEngaged)) await SendAsync("autosteer.toggle");
        Parked = true;
        Truth.SpeedKmh = 0;
        _steerCmd = 0;
    }

    public async Task EngageAsync()
    {
        Truth.SpeedKmh = WorkKmh;
        Parked = false;
        for (int i = 0; i < 20; i++) await StepAsync(false);
        if (!await Rig.ReadAsync(() => Rig.Vm.IsAutoSteerEngaged)) await SendAsync("autosteer.toggle");
        if (!await Rig.ReadAsync(() => Rig.Vm.IsSectionMasterOn)) await SendAsync("section.master");
        _offSince = -1;
    }

    // ------------------------------------------------------------------ the field plane

    public (double E, double N) Local(double lat, double lon)
    {
        var g = _plane!.ConvertWgs84ToGeoCoord(new Wgs84(lat, lon));
        return (g.Easting, g.Northing);
    }

    public Wgs84 Wgs(double e, double n) => _plane!.ConvertGeoCoordToWgs84(new GeoCoord(n, e));

    private double U((double E, double N) p) => (p.E - _a.E) * _across.E + (p.N - _a.N) * _across.N;
    private double V((double E, double N) p) => (p.E - _a.E) * _along.E + (p.N - _a.N) * _along.N;

    private double InsideChordM(int k)
    {
        var work = _work ?? _outer;
        double u = (k + 0.5) * _spacing;
        var onLine = (_a.E + _across.E * u, _a.N + _across.N * u);
        var ts = Ring.LineCrossings(onLine, _along, work);
        ts.Sort();
        double best = 0;
        for (int i = 0; i + 1 < ts.Count; i += 2) best = Math.Max(best, ts[i + 1] - ts[i]);
        return best;
    }

    /// <summary>How far the field's outer boundary is straight ahead of the tractor's pivot, m.</summary>
    private double FenceAheadM()
    {
        var ahead = Ring.LineCrossings((Truth.E, Truth.N), (Math.Sin(Truth.Heading), Math.Cos(Truth.Heading)), _outer)
            .Where(t => t > 0).ToList();
        return ahead.Count == 0 ? double.PositiveInfinity : ahead.Min();
    }

    /// <summary>The side (+1 right of the AB line, -1 left) where more of the field lies from the
    /// tractor: where the next pass is before a turn has shown the way.</summary>
    private int FieldSide()
    {
        var work = _work ?? _outer;
        double u = U((Truth.E, Truth.N));
        return work.Max(U) - u >= u - work.Min(U) ? 1 : -1;
    }

    /// <summary>Where the operator starts (as in the field job harness): the first pass along
    /// the field's left edge with at least 20 m inside the cultivated area, 12 m before the
    /// boundary, facing in.</summary>
    private ((double E, double N) Pivot, double HeadingRad, int Pass) StartPose()
    {
        var work = _work ?? _outer;
        double uMin = work.Min(U), uMax = work.Max(U);
        int kMin = (int)Math.Floor(uMin / _spacing) - 1, kMax = (int)Math.Ceiling(uMax / _spacing) + 1;
        int k;
        for (k = kMin; k <= kMax && InsideChordM(k) < 20; k++) { }
        double u = (k + 0.5) * _spacing;
        var onLine = (_a.E + _across.E * u, _a.N + _across.N * u);
        var tw = Ring.LineCrossings(onLine, _along, work);
        tw.Sort();
        double chordStart = tw.Count >= 2 ? tw[0] : _outer.Min(V);
        for (int i = 0, bestLen = -1; i + 1 < tw.Count; i += 2)
            if (tw[i + 1] - tw[i] > bestLen) { bestLen = (int)(tw[i + 1] - tw[i]); chordStart = tw[i]; }
        var ts = Ring.LineCrossings(onLine, _along, _outer).Where(t => t <= chordStart + 0.01).ToList();
        double vStart = ts.Count > 0 ? ts.Max() : chordStart;
        double vPivot = vStart - 12;
        double uPivot = u - Sprayer.Offset;
        return ((_a.E + _across.E * uPivot + _along.E * vPivot, _a.N + _across.N * uPivot + _along.N * vPivot),
                Math.Atan2(_along.E, _along.N), k);
    }
}
