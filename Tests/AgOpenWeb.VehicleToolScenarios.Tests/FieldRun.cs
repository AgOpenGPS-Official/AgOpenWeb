// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using AgOpenWeb.Models;
using AgOpenWeb.Models.State;

namespace AgOpenWeb.VehicleToolScenarios.Tests;

/// <summary>What one pass did, measured on the real machine.</summary>
public sealed class PassResult
{
    public int Index;
    public bool Northbound;
    public double TargetX;               // where the operator lined the implement up
    public readonly SortedDictionary<int, double> ToolXByMetre = new();   // 1 m bins along the pass
    public double MeanX, MinX, MaxX;
    public double LineError;             // implement centre vs the nearest pass line AgOpenWeb uses
    public double MaxLineDev;
    public double MaxSteerCmd, SaturatedShare;
    public double MaxSteerWhole;         // largest steer angle from engaging to the end of the pass
    public double AppXte = double.NaN;   // AgOpenWeb's own mean distance from its line while measuring
    public double AppToolSide, AppToolAlong;   // AgOpenWeb's implement vs the real one (worst, m)
    public double AppPivotErr, AppHeadingErr;  // AgOpenWeb's pivot / heading vs the real ones (worst)
    public double SectionsOnShare = double.NaN;
    public int AppPass;
    public bool RanOff;
    public string? Disengaged;
    public int Samples;
}

/// <summary>
/// One field run: a fresh AgOpenWeb (own data folder), a field with an AB line running north,
/// a job with automatic sections, the operator's Vehicle &amp; Tool settings, then four passes
/// side by side, alternately north and south, with AutoSteer on. Between passes the operator
/// puts the tractor at the start of the next pass with the implement next to the last pass
/// (as by eye after a turn), a little off the line, and engages again.
///
/// The field: an AB line along E = 0; passes measured from N = 0 to N = 60 after 40 m to
/// settle. With a slope the field falls to the east, so northbound the tractor leans right.
/// </summary>
public sealed class FieldRun : IAsyncDisposable
{
    public const double MeasureFrom = 0, MeasureTo = 60, RunIn = 40, PreRoll = 15;
    public const double FixSeconds = 0.1;   // 10 Hz receiver

    public VehicleToolRig Rig { get; private set; } = null!;
    public TruthMachine Truth { get; private set; } = null!;
    public LocalPlane Plane { get; private set; } = null!;
    public readonly List<PassResult> Passes = new();
    public readonly List<string> Messages = new();
    public readonly List<string> Problems = new();
    public string DataRoot { get; private set; } = "";

    /// <summary>Real seconds to wait per simulated fix (0.1 s): ~12x real time.</summary>
    public int PaceMs = 8;
    public static readonly bool Verbose = Environment.GetEnvironmentVariable("AGOPENWEB_SCENARIO_VERBOSE") == "1";

    private static readonly (double Lat, double Lon) Origin = (41.5012, -5.4987); // made-up place

    public static async Task<FieldRun> StartAsync(string dataRoot, TruthMachine truth)
    {
        var run = new FieldRun { DataRoot = dataRoot, Truth = truth };
        run.Rig = await VehicleToolRig.StartAsync(dataRoot);
        return run;
    }

    /// <summary>Field, AB line and job, made with the tractor standing at the origin.</summary>
    public async Task<bool> MakeFieldAsync()
    {
        for (int i = 0; i < 20; i++)
        {
            Rig.SendSentence(VehicleToolRig.Sentence(Truth.Sys.Dual, Truth.Sys.HasImu, Origin.Lat + i * 1e-6, Origin.Lon, 0, 0, 3.6));
            await Task.Delay(10);
        }
        await Rig.SendAsync("field.new", "Scenario");
        bool open = await Rig.WaitUntilAsync(() => Rig.Vm.IsFieldOpen && !Rig.State.UI.IsBusy);
        if (!open) { Problems.Add("HARNESS: the field did not open"); return false; }
        Plane = await Rig.ReadAsync(() => Rig.State.Field.LocalPlane)
            ?? new LocalPlane(new Wgs84(Rig.State.Field.OriginLatitude, Rig.State.Field.OriginLongitude), new SharedFieldProperties());
        var a = Wgs(0, -100);
        var b = Wgs(0, 200);
        await Rig.SendAsync("track.latLonAB", F($"{a.Latitude:F9},{a.Longitude:F9},{b.Latitude:F9},{b.Longitude:F9}"));
        if (!await Rig.ReadAsync(() => Rig.Vm.IsAutoSteerAvailable))
            await Rig.SendAsync("track.activate", "0");
        if (!await Rig.ReadAsync(() => Rig.Vm.IsAutoSteerAvailable))
        { Problems.Add("HARNESS: no active AB line"); return false; }
        await Rig.SendAsync("field.startJob", "Scenario\tSpraying\tscenario\tSpraying Scenario");
        return true;
    }

    public async Task ApplyAsync(IEnumerable<string> settings, bool save = true)
    {
        foreach (var kv in settings) await Rig.SendAsync("config.set", kv);
        if (save) await Rig.SendAsync("profile.save");
    }

    public Wgs84 Wgs(double e, double n) => Plane.ConvertGeoCoordToWgs84(new GeoCoord(n, e));

    public (double E, double N) Local(double lat, double lon)
    {
        var g = Plane.ConvertWgs84ToGeoCoord(new Wgs84(lat, lon));
        return (g.Easting, g.Northing);
    }

    private double _cmd;
    private readonly Queue<(double E, double N)> _prevTool = new();

    private void SendFix()
    {
        var (ae, an) = Truth.Antenna;
        var w = Wgs(ae, an);
        var sys = Truth.Sys;
        double roll = Truth.RollDeg * (sys.RollInverted ? -1 : 1) + sys.RollBiasDeg;
        double hdg = Truth.Heading * 180 / Math.PI + sys.HeadingErrorDeg + (sys.Dual ? 0 : sys.ImuHeadingOffsetDeg);
        Rig.SendSentence(VehicleToolRig.Sentence(sys.Dual, sys.HasImu, w.Latitude, w.Longitude, hdg, roll, Truth.SpeedKmh));
    }

    /// <summary>Drive four passes; returns false when the harness itself failed.</summary>
    public async Task<bool> DrivePassesAsync(int passes = 4, Func<int, Task>? midPass = null)
    {
        double w = Truth.T.Width;
        // The AB line is the edge of the first pass (AgOpenWeb's PassGeometry): its implement
        // runs half a width to the right of it.
        double target = w / 2;
        for (int k = 0; k < passes; k++)
        {
            bool north = k % 2 == 0;
            var pr = await DrivePassAsync(k, north, target, k % 2 == 0 ? 0.4 : -0.4, midPass);
            Passes.Add(pr);
            if (pr.RanOff) break;
            double next = (double.IsNaN(pr.MeanX) ? target : pr.MeanX) + w;
            target = next;
        }
        return true;
    }

    private async Task<PassResult> DrivePassAsync(int k, bool north, double targetX, double startError, Func<int, Task>? midPass)
    {
        var pr = new PassResult { Index = k, Northbound = north, TargetX = targetX };
        double hdg = north ? 0 : Math.PI;
        double sgn = north ? 1 : -1;
        // The implement on the target (the operator lines it up), the tractor where that puts it.
        double startN = north ? MeasureFrom - RunIn - PreRoll : MeasureTo + RunIn + PreRoll;
        double toolSide = Truth.T.Offset * sgn;       // right of the tractor = +E northbound
        _cmd = 0;
        if (await Rig.ReadAsync(() => Rig.Vm.IsAutoSteerEngaged)) await Rig.SendAsync("autosteer.toggle");
        if (k == 0) Truth.Place(targetX - toolSide + startError, startN, hdg);
        else await UTurnIntoAsync(targetX - toolSide + startError, startN, hdg);

        // Disengaged pre-roll: the receiver's heading, reverse detection and the pass AgOpenWeb
        // picks settle on the new pass first.
        int preFixes = (int)(PreRoll / (Truth.SpeedKmh / 3.6) / FixSeconds);
        for (int i = 0; i < preFixes; i++)
        {
            Truth.Advance(FixSeconds, 0);
            SendFix();
            await Task.Delay(PaceMs);
        }
        await Rig.WaitForVmAsync();
        await Rig.SendAsync("autosteer.toggle");
        bool engaged = await Rig.ReadAsync(() => Rig.Vm.IsAutoSteerEngaged);
        if (!engaged) Messages.Add(F($"pass {k + 1}: AutoSteer did not engage"));
        if (k == 0) { await Rig.SendAsync("section.master"); }

        double endN = north ? MeasureTo + 5 : MeasureFrom - 5;
        double sat = 0, onSum = 0, xteSum = 0;
        int xteN = 0;
        int onCount = 0, n = 0;
        bool midDone = false;
        var xs = new List<double>();
        int guard = 0;
        while ((north ? Truth.N < endN : Truth.N > endN) && guard++ < 4000)
        {
            Truth.Advance(FixSeconds, _cmd);
            SendFix();
            var c = Rig.LastCycle;
            if (c != null)
            {
                bool on = c.IsAutoSteerEngaged;
                _cmd = on && c.Guidance is { HasGuidance: true } g ? g.SteerAngle : 0;
                if (on && double.IsFinite(_cmd)) pr.MaxSteerWhole = Math.Max(pr.MaxSteerWhole, Math.Abs(_cmd));
                if (!on && engaged && pr.Disengaged == null)
                    pr.Disengaged = F($"at N={Truth.N:F0}: ") + (c.DisengageReason ?? "AutoSteer went off");
                if (double.IsNaN(_cmd) || double.IsInfinity(_cmd))
                {
                    Problems.Add(F($"pass {k + 1}: steer angle {_cmd} sent to the module"));
                    _cmd = 0;
                }
            }
            var tc = Truth.ToolCentre;
            double nAlong = tc.N;
            if (Verbose && guard % 40 == 1)
                Console.WriteLine(F($"[vt] pass {k + 1} truth pivot ({Truth.E:F2},{Truth.N:F2}) h {Truth.Heading * 180 / Math.PI:F1} steer {Truth.Steer:F1} tool ({tc.E:F2},{tc.N:F2}) | app pivot ({c?.Easting:F2},{c?.Northing:F2}) h {c?.Heading:F1} tool ({c?.ToolEasting:F2},{c?.ToolNorthing:F2}) engaged {c?.IsAutoSteerEngaged} guid {c?.Guidance?.HasGuidance} cmd {_cmd:F1} pass# {c?.Guidance?.HowManyPathsAway} cycles {Rig.CycleCount}"));
            bool measuring = nAlong >= MeasureFrom && nAlong <= MeasureTo;
            if (midPass != null && !midDone && Math.Abs(nAlong - (MeasureFrom + MeasureTo) / 2) < 1)
            {
                midDone = true;
                await midPass(k);
            }
            if (measuring && c != null)
            {
                n++;
                xs.Add(tc.E);
                pr.ToolXByMetre[(int)Math.Floor(nAlong)] = tc.E;
                pr.MaxSteerCmd = Math.Max(pr.MaxSteerCmd, Math.Abs(_cmd));
                if (Math.Abs(_cmd) >= 0.95 * Math.Min(Truth.V.MaxSteerAngle, Rig.Store.Vehicle.MaxSteerAngle)) sat++;
                // AgOpenWeb's view of the same fix, in the tool's frame (side = across the pass).
                // The harness runs ~12x real time, so AgOpenWeb's 100 Hz loop may still show
                // the implement where it was a fix or two earlier: compare with those too.
                // Its tool position is the implement's hitch centre line; the sections (and so
                // the coverage) sit Tool.Offset to its right, as in AgOpenGPS.
                double hr = c.Heading * Math.PI / 180, off = Rig.Store.Tool.Offset;
                double appE = c.ToolEasting + Math.Cos(hr) * off, appN = c.ToolNorthing - Math.Sin(hr) * off;
                double dE = Math.Abs(appE - tc.E), dN = Math.Abs(appN - tc.N);
                foreach (var (pe0, pn0) in _prevTool)
                {
                    dE = Math.Min(dE, Math.Abs(appE - pe0));
                    dN = Math.Min(dN, Math.Abs(appN - pn0));
                }
                pr.AppToolSide = Math.Max(pr.AppToolSide, dE);
                pr.AppToolAlong = Math.Max(pr.AppToolAlong, dN);
                double pe = c.Easting - Truth.E, pn = c.Northing - Truth.N;
                pr.AppPivotErr = Math.Max(pr.AppPivotErr, Math.Sqrt(pe * pe + pn * pn));
                double he = Math.Abs(((c.Heading - Truth.Heading * 180 / Math.PI) % 360 + 540) % 360 - 180);
                pr.AppHeadingErr = Math.Max(pr.AppHeadingErr, he);
                if (c.SectionStates is { Length: > 0 } ss)
                {
                    int cnt = Math.Min(ss.Length, Truth.T.Sections);
                    for (int s = 0; s < cnt; s++) { onSum += ss[s] ? 1 : 0; onCount++; }
                }
                if (c.Guidance != null)
                {
                    pr.AppPass = c.Guidance.HowManyPathsAway;
                    xteSum += Math.Abs(c.Guidance.CrossTrackError); xteN++;
                }
            }
            if (Math.Abs(tc.E - targetX) > 8 || Math.Abs(AngleDiff(Truth.Heading, hdg)) > 70)
            {
                pr.RanOff = true;
                Problems.Add(F($"pass {k + 1}: the tractor left the pass (implement {tc.E - targetX:F1} m off, heading {AngleDiff(Truth.Heading, hdg):F0}° off) at N={Truth.N:F0}"));
                break;
            }
            _prevTool.Enqueue((tc.E, tc.N));
            if (_prevTool.Count > 2) _prevTool.Dequeue();
            await Task.Delay(PaceMs);
        }
        if (await Rig.ReadAsync(() => Rig.Vm.IsAutoSteerEngaged)) await Rig.SendAsync("autosteer.toggle");
        pr.Samples = n;
        if (xs.Count > 0)
        {
            pr.MeanX = xs.Average();
            pr.MinX = xs.Min();
            pr.MaxX = xs.Max();
            double spacing = Math.Max(0.1, Rig.Store.ActualToolWidth - Rig.Store.Tool.Overlap);
            double line = (Math.Round(pr.MeanX / spacing - 0.5) + 0.5) * spacing;  // pass n at (n + ½) × spacing
            pr.LineError = pr.MeanX - line;
            pr.MaxLineDev = xs.Max(x => Math.Abs(x - line));
            pr.SaturatedShare = sat / n;
            if (onCount > 0) pr.SectionsOnShare = onSum / onCount;
            if (xteN > 0) pr.AppXte = xteSum / xteN;
        }
        else
        {
            pr.MeanX = pr.MinX = pr.MaxX = double.NaN;
        }
        CollectMessages();
        return pr;
    }

    /// <summary>What the app said since the last call (status lines, warnings, errors; not
    /// the AutoSteer and section toggles the harness itself makes).</summary>
    public List<string> CollectMessages()
    {
        var said = new List<string>();
        foreach (var e in Rig.TakeEvents())
            if (e.Kind is RigEventKind.Failure or RigEventKind.Exception or RigEventKind.Error or RigEventKind.Hang
                || (e.Kind == RigEventKind.Status && !e.Text.StartsWith("AutoSteer", StringComparison.Ordinal) && !e.Text.Contains("Section", StringComparison.OrdinalIgnoreCase))
                || (e.Kind == RigEventKind.Warning && !e.Text.Contains("No GPS input", StringComparison.Ordinal)))
                said.Add(e.ToString());
        Messages.AddRange(said);
        return said;
    }

    /// <summary>
    /// The headland turn into the next pass, driven by hand (AutoSteer off), so the receiver
    /// sees the machine come round as it really does. A single antenna without an IMU only
    /// knows its heading from the way it moves: put straight onto the next pass facing the
    /// other way, it would rightly read as reversing. The turn is first driven on a copy of
    /// the machine to learn where it ends, so the real one ends at
    /// (<paramref name="e"/>, <paramref name="n"/>) facing <paramref name="heading"/>.
    /// </summary>
    private async Task UTurnIntoAsync(double e, double n, double heading)
    {
        double prev = (heading + Math.PI) % (2 * Math.PI);
        // A right turn when leaving northbound, a left one when leaving southbound: both
        // move the machine east, onto the next pass. Radius about half the working width,
        // never tighter than the tractor can go.
        double side = prev < Math.PI / 2 || prev > 3 * Math.PI / 2 ? 1 : -1;
        double minR = Truth.V.Wheelbase / Math.Tan(0.8 * Truth.V.MaxSteerAngle * Math.PI / 180);
        double r = Math.Max(Truth.T.Width / 2, minR);
        double steer = Math.Atan(Truth.V.Wheelbase / r) * 180 / Math.PI;

        var copy = new TruthMachine(Truth.V, Truth.T, Truth.SlopeDeg, Truth.Sys) { SpeedKmh = Truth.SpeedKmh };
        copy.Place(0, 0, prev);
        for (var p = new TurnPlan(heading, side, steer); !p.Done;) copy.Advance(FixSeconds, p.Next(copy));
        Truth.Place(e - copy.E, n - copy.N, prev);
        for (var p = new TurnPlan(heading, side, steer); !p.Done;)
        {
            Truth.Advance(FixSeconds, p.Next(Truth));
            SendFix();
            await Task.Delay(PaceMs);
        }
    }

    /// <summary>The operator's hands in a headland turn: a moment straight, the wheel over
    /// to <c>maxDeg</c> the way of the turn, then easing it back onto the new heading.</summary>
    private sealed class TurnPlan(double target, double side, double maxDeg)
    {
        private int _i, _settled;
        public bool Done { get; private set; }

        public double Next(TruthMachine m)
        {
            _i++;
            if (_i <= 10) return 0;
            // Heading still to turn, the way of the turn (0..360°).
            double rem = (((target - m.Heading) * 180 / Math.PI * side) % 360 + 360) % 360;
            if (rem > 30 && rem < 330) return side * maxDeg;
            double err = AngleDiff(target, m.Heading);
            if (Math.Abs(err) < 0.3 && Math.Abs(m.Steer) < 0.5 && ++_settled > 10) Done = true;
            if (_i > 2000) Done = true;
            return Math.Clamp(2.0 * err, -maxDeg, maxDeg);
        }
    }

    /// <summary>Gaps (+) and overlaps (−) between neighbouring passes, from the real implement's
    /// ends, metre by metre along the pass: (worst gap, worst overlap, mean).</summary>
    public (double Gap, double Overlap, double Mean) GapsAndOverlaps()
    {
        double w = Truth.T.Width;
        double gap = 0, ov = 0, sum = 0;
        int cnt = 0;
        for (int k = 0; k + 1 < Passes.Count; k++)
        {
            foreach (var (m, x) in Passes[k].ToolXByMetre)
            {
                if (!Passes[k + 1].ToolXByMetre.TryGetValue(m, out var x2)) continue;
                double g = (x2 - w / 2) - (x + w / 2);
                gap = Math.Max(gap, g);
                ov = Math.Min(ov, g);
                sum += g;
                cnt++;
            }
        }
        return (gap, ov, cnt > 0 ? sum / cnt : double.NaN);
    }

    private static double AngleDiff(double a, double b)
    {
        double d = (a - b) * 180 / Math.PI;
        return ((d % 360) + 540) % 360 - 180;
    }

    private static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        try { await Rig.DisposeAsync(); } catch { }
    }
}
