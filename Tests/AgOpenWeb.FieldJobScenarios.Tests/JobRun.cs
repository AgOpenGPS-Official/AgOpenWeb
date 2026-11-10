// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using AgOpenWeb.Models;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace AgOpenWeb.FieldJobScenarios.Tests;

/// <summary>One job: which field and track, which machine, how the operator set AgOpenWeb up,
/// and what they do while it drives.</summary>
public sealed record JobSpec(
    string Code,
    string Title,
    string Story,
    string Field,
    string Track,
    VehicleSpec V,
    ToolSpec T,
    SystemSpec Sys)
{
    /// <summary>Headland from the whole boundary, m; 0 = none (then AgOpenWeb has no line to turn at).</summary>
    public double HeadlandM { get; init; } = 16;
    /// <summary>Section control in the headland (sections off there) on.</summary>
    public bool HeadlandSections { get; init; } = true;
    /// <summary>"Off outside boundary" on (AgOpenGPS's default; AgOpenWeb's is off).</summary>
    public bool OffWhenOut { get; init; } = true;
    public double UTurnRadius { get; init; } = 8;
    /// <summary>How far inside the boundary the U-turn's outermost point stays (AgOpenWeb default 2 m).</summary>
    public double UTurnDistanceFromBoundary { get; init; } = 2;
    public bool Obstacles { get; init; }
    /// <summary>The real valves' time from relay to nozzles, s: open, shut. AgOpenGPS's default
    /// look-ahead (1.0 s on, 0.5 s off) is what it assumes a sprayer's valves take.</summary>
    public double ValveOnS { get; init; } = 1.0;
    public double ValveOffS { get; init; } = 0.5;
    /// <summary>The operator set look-ahead to the valves' times, as a tuned rig is (careful);
    /// off: look-ahead stays at what the settings say (AgOpenWeb's default 0, or <see cref="Extra"/>).</summary>
    public bool LookAheadMatchesValves { get; init; } = true;
    /// <summary>The outer boundary marked hard (AgOpenWeb then checks the implement's swept path
    /// in every U-turn and refuses a turn that would cross it).</summary>
    public bool HardBoundary { get; init; }
    public double SpeedKmh { get; init; } = 9;
    /// <summary>Extra settings the operator types (config.set key:value), after the machine's.</summary>
    public IReadOnlyList<string> Extra { get; init; } = Array.Empty<string>();
    /// <summary>What a careless operator does while the job runs: called once per fix.</summary>
    public Func<JobRun, Task>? OnFix { get; init; }
    /// <summary>Relay functions left at AgOpenWeb's defaults (1-6) instead of one per section.</summary>
    public bool DefaultRelays { get; init; }
    /// <summary>The operator engages AutoSteer again two seconds after it drops (up to 8 times).</summary>
    public bool Reengage { get; init; } = true;
    /// <summary>Findings this job is expected to produce (the operator's own doing): noted, not counted.</summary>
    public IReadOnlyList<string> Expected { get; init; } = Array.Empty<string>();
    /// <summary>When an <see cref="Expected"/> code is the operator's doing only up to a size (a
    /// 50 cm nudge leaves a 50 cm strip, not a wider one): false = report it as a finding after all.</summary>
    public Func<string, JobAreas, bool>? ExpectedWhen { get; init; }
    /// <summary>How the operator guides the job: the AB line named in <see cref="Track"/> (the
    /// default), or a curve, a closed loop, a water pivot, contour or a recorded path
    /// (<see cref="PathOperator"/>).</summary>
    public GuideKind Guide { get; init; } = GuideKind.AbLine;
    /// <summary>For <see cref="GuideKind.BoundarySegment"/>: the outer ring's vertices (in the
    /// field file's order) the operator taps as A and B, and a vertex on the way round between them.</summary>
    public (int A, int B, int Via) Segment { get; init; }
    /// <summary>U-turns switched on (AgOpenWeb refuses them on a closed track).</summary>
    public bool UTurns { get; init; } = true;
    /// <summary>AgOpenWeb's clock runs on the job's simulated time (<see cref="SimClock"/>), as
    /// it must where AgOpenWeb times guidance itself (contour rebuilds its line every 2 s).</summary>
    public bool SimulatedClock { get; init; }
    /// <summary>On an AB line too: the implement measured against the pass it should be on
    /// (<c>OFF-ITS-PASS</c>), from when it first reaches it, or 30 s after a turn at the latest.</summary>
    public bool PassCheck { get; init; }
    /// <summary>The U-turn style typed in (0 Sagitta, AgOpenWeb's default; 1 K-turn; 2 Keyhole); null = left alone.</summary>
    public int? UTurnStyle { get; init; }
    /// <summary>Start on the first pass whose implement line runs in the cultivated area even if
    /// the tractor's own line (an implement to one side) does not; otherwise both must.</summary>
    public bool EdgePass { get; init; }
    /// <summary>Checks of the job's own, after the common ones (what a careless operator's action must not cost).</summary>
    public Action<Scenario, JobRun, Action<Severity, string, string>>? ExtraJudge { get; init; }
    /// <summary>What the operator does once everything is set up, before the job starts (skip
    /// rows, the units, a setting typed the wrong way).</summary>
    public Func<JobRun, Task>? BeforeStart { get; init; }
    /// <summary>How the operator types the machine's settings (the units pass types those with a
    /// unit into the web client's own boxes); null = each as config.set, as the page sends it.</summary>
    public Func<JobRun, IReadOnlyList<string>, Task<bool>>? TypeSettings { get; init; }
    /// <summary>How the operator makes the headland from the whole boundary; null = headland.wholeBoundary with <see cref="HeadlandM"/>.</summary>
    public Func<JobRun, Task<bool>>? MakeHeadland { get; init; }
}

/// <summary>How the operator guides a job.</summary>
public enum GuideKind
{
    /// <summary>An AB line from the field file (<see cref="JobSpec.Track"/>).</summary>
    AbLine,
    /// <summary>"Bnd. Curve": an open curve along part of the outer boundary, tapped A and B.</summary>
    BoundarySegment,
    /// <summary>"Boundary Curve": the whole outer boundary as a closed loop, worked lap by lap inwards.</summary>
    BoundaryLoop,
    /// <summary>A water pivot from its centre typed in (lat/lon), worked circle by circle inwards.</summary>
    PivotCentre,
    /// <summary>A water pivot recorded by driving part of the way round it.</summary>
    PivotDriven,
    /// <summary>No line: the first lap by hand, then contour guidance follows the strips.</summary>
    Contour,
    /// <summary>A path recorded by driving it, then played back.</summary>
    RecordedPath,
    /// <summary>A curve recorded by driving it by hand (Curve, Record, Finish): the field file's
    /// curve track (<see cref="JobSpec.Track"/>) is where the operator drives; the passes are
    /// the recorded curve offset across the field, with U-turns as on an AB line.</summary>
    DrivenCurve,
}

/// <summary>What one U-turn did.</summary>
public sealed class TurnRecord
{
    public int Index;
    public int PassBefore, PassAfter;
    public double StartSeconds, Seconds;
    public double MaxOutsideM;          // how far the pivot or an implement end went past the outer boundary
    public double MaxPivotOutsideM;     // the pivot alone
    public bool Fallback;               // AgOpenWeb used its simple fallback path for this turn
    public bool Left;
    public double MinSpeedKmh = 99;
    public double MaxHitchDeg;          // trailed / TBT: the largest angle between the tractor and the drawbar
    public double HeadingAtStart, UAtStart, TangentAtStart;
    /// <summary>Seconds into the turn when the tractor was back on a pass line, facing the other
    /// way, inside the cultivated area (the turn done on the ground); -1 if never seen.</summary>
    public double BackOnPassSeconds = -1;
    /// <summary>The pass the real implement was on when the turn began, and 15 s after it ended
    /// (AB lines; int.MinValue = not known).</summary>
    public int TruthPassBefore = int.MinValue, TruthPassAfter = int.MinValue;
    /// <summary>When AgOpenWeb ended the turn: the tractor's heading against the pass direction
    /// (either way), and the implement's distance from the nearest pass.</summary>
    public double EndHeadingOffDeg = double.NaN, EndOffPassM = double.NaN;
    public double EndSeconds = double.NaN;
    /// <summary>The implement centre across the passes (m) when AgOpenWeb ended the turn: against
    /// the pass it is on 15 s later, how far the turn's exit leg put it beside that pass.</summary>
    public double EndU = double.NaN;
    public double ExitOffM => TruthPassAfter == int.MinValue || double.IsNaN(EndU) ? double.NaN : EndU - Spacing * (TruthPassAfter + 0.5);
    public double Spacing;
}

/// <summary>
/// A whole job on one field: AgOpenWeb boots fresh, the operator sets the machine up, imports the
/// field, picks the track, makes the headland, switches U-turns and the sections to automatic and
/// engages AutoSteer at the first pass. From then on AgOpenWeb drives the real machine
/// (<see cref="TruthMachine"/>) by its PGN 254 steer angle, fix by fix at 10 Hz, until it says
/// the field is done. The emulated module opens the valves PGN 239 asks for; what the real
/// implement swept with a valve open is the truth coverage.
/// </summary>
public sealed class JobRun : IAsyncDisposable
{
    public const double FixSeconds = 0.1;
    /// <summary>Real ms per simulated fix (0.1 s): 8 = about 12x real time.</summary>
    public static int PaceMs = int.TryParse(Environment.GetEnvironmentVariable("AGOPENWEB_SCENARIO_PACE"), out var p) ? p : 8;
    public static readonly bool Verbose = Environment.GetEnvironmentVariable("AGOPENWEB_SCENARIO_VERBOSE") != "0";

    public JobSpec Spec { get; }
    public Scenario S { get; }
    /// <summary>The passes' coordinates (u across, v along) from the operator's reference line.</summary>
    public PassFrame Frame { get; set; } = null!;
    /// <summary>What the operator does on a curve, loop, pivot, contour or recorded path job.</summary>
    public PathOperator? PathOp { get; private set; }
    /// <summary>AgOpenWeb's clock when the job runs on simulated time.</summary>
    public SimClock? SimTime { get; private set; }
    /// <summary>The operator steers by hand (degrees asked of the wheels); null = AgOpenWeb steers.</summary>
    public Func<double>? HandSteer;
    /// <summary>AutoSteer should be on: when it drops, that is reported and the operator engages it again.</summary>
    public bool ExpectEngaged = true;
    /// <summary>Moving over to the next lap: the pass edges are not recorded.</summary>
    public bool InChange;
    /// <summary>A lap's corner (set by the path operator): the pass edges and the pass error
    /// leave it out, as the U-turns.</summary>
    public bool InCorner;
    /// <summary>Unsprayed ground the operator left on purpose (the middle no lap reaches): not a
    /// strip between passes.</summary>
    public Func<(double E, double N), bool>? LeftOnPurpose;
    /// <summary>Unsprayed ground at a lap's corner, where the passes pull apart.</summary>
    public Func<(double E, double N), bool>? AtCorner;
    private bool _passJoined;
    private int _passStartFix;
    private int _passErrorTraced = -1000;
    /// <summary>The operator numbers the passes (contour, recorded path): AgOpenWeb's pass number is not read.</summary>
    public bool PassFromOperator;
    /// <summary>Who ended the job and why (for a job AgOpenWeb does not end itself).</summary>
    public string EndedBy = "";
    /// <summary>The implement's centre against the pass it should be on (curve, loop and pivot jobs):
    /// the worst distance, where, and the mean.</summary>
    public double PassErrorWorst, PassErrorSum; public int PassErrorSamples; public (double E, double N, int Pass, double V) PassErrorWhere;
    public readonly List<(double Err, double E, double N, int Pass, double V)> PassErrorBig = new();
    public FieldJobRig Rig { get; private set; } = null!;
    public EmulatedSprayerModule Module { get; private set; } = null!;
    public SteerModuleStub Steer { get; private set; } = null!;
    public TruthMachine Truth { get; private set; } = null!;
    public ICoverageMapService Coverage { get; private set; } = null!;
    public LocalPlane Plane { get; private set; } = null!;
    public ScenarioField Field { get; private set; } = null!;
    public TruthGrid Grid { get; private set; } = null!;
    /// <summary>What AgOpenWeb's own sections swept, at its own tool position: the screen's
    /// coverage, to tell its map's painting from its section decisions.</summary>
    public TruthGrid ScreenGrid { get; private set; } = null!;
    /// <summary>What AgOpenWeb's mapping swept, read after every 100 Hz tick: the sections whose
    /// IsMappingOn is set, at the tool position service's position. The map's painting input.</summary>
    public TruthGrid MapGrid { get; private set; } = null!;
    private readonly (double E, double N)?[] _prevMapEdge = new (double E, double N)?[65];
    private ISectionControlService? _scs; private IToolPositionService? _tps;

    public List<(double E, double N)> Outer = new();
    public List<(double E, double N)>? Work;   // the headland line
    public List<(bool DriveThrough, IReadOnlyList<(double E, double N)> Ring)> Inner = new();

    // The AB frame: u across the passes (right of A->B), v along.
    public (double E, double N) A, Along, Across;
    public double Spacing;

    // Live state for the operator's hooks.
    public int Fix;
    public double Seconds => Fix * FixSeconds;
    /// <summary>The job's clock in ms, advanced 100 per fix sent (the set-up fixes too): the
    /// emulated module's clock.</summary>
    public long SimMs;
    public bool InTurn, FieldDone, Stopped;
    /// <summary>The operator stopped the job because AgOpenWeb went wrong (a finding says how):
    /// the ground left is its consequence, not judged again.</summary>
    public bool EndedEarly;
    /// <summary>False = the receiver sends nothing (a careless operator's GPS loss).</summary>
    public volatile bool GpsOn = true;
    public int Pass = int.MinValue;
    public int TurnsStarted;
    /// <summary>When AgOpenWeb asked the operator to take over (a refused or a missed U-turn), s; -1 never.</summary>
    public double TookOverAt = -1;
    /// <summary>AgOpenWeb's warning to take over "at the end of the field" (when it was said, its text,
    /// the pass): the operator drives the pass to its end first and takes over there.</summary>
    private (double At, string Text, int Pass)? _takeOverAtEnd;
    bool _arrowChecked;
    /// <summary>Set when AgOpenWeb's first U-turn arrow pointed at the field's edge the operator
    /// started from (a sliver pass): the pass it pointed at and how much of it is inside.</summary>
    public (int Pass, double ChordM)? ArrowAtEdge;
    public readonly List<TurnRecord> Turns = new();
    public readonly List<string> Said = new();
    public readonly List<string> Problems = new();
    public readonly List<string> Notes = new();
    /// <summary>"End of field reached" said by AgOpenWeb, then a U-turn it made after all: when and where.</summary>
    public readonly List<string> EarlyEnds = new();
    private double _endSaidAt = -1;
    private int _endSaidPass;
    public int Reengaged;
    public readonly double[] ScreenOnValveShut = new double[64], ScreenOffValveOpen = new double[64];
    /// <summary>Disagreement that lasted more than a second at a stretch (the switching latency
    /// of a few fixes at each change is not a fault).</summary>
    public readonly double[] PersistentDisagreement = new double[64];
    private readonly int[] _disagreeRun = new int[64];
    private readonly (double E, double N)?[] _prevAppEdge = new (double E, double N)?[65];
    public double SecondsLinkDown, SecondsPaintingLinkDown;
    /// <summary>AgOpenWeb's tool position against the real implement's centre, on the passes
    /// (not in the turns): along the track (+ = AgOpenWeb's is ahead) and across (+ = to the right).</summary>
    public double ToolAlongSum, ToolAcrossSum, ToolAlongMax, ToolAcrossMax; public int ToolSamples;
    /// <summary>Seconds the module was told geo-stop (PGN 239 byte 9) while the job ran.</summary>
    public double SecondsGeoStop;
    /// <summary>Trailed / TBT: the largest angle between the tractor and the drawbar over the whole job, and where.</summary>
    public double MaxHitchDeg;
    public string MaxHitchWhere = "";
    /// <summary>How many fixes behind the receiver AgOpenWeb's last cycle is: for each fix, the
    /// recent truth pivot position nearest AgOpenWeb's own pivot. Histogram by fixes of lag.</summary>
    public readonly int[] LagHistogram = new int[40];
    private readonly (double E, double N)[] _pivotHistory = new (double E, double N)[40];
    public int CyclesAtEnd, FixesAtEnd;
    public double MedianLagFixes
    {
        get
        {
            int total = LagHistogram.Sum(), acc = 0;
            for (int i = 0; i < LagHistogram.Length; i++) { acc += LagHistogram[i]; if (acc * 2 >= total) return i; }
            return 0;
        }
    }
    private readonly Dictionary<int, SortedDictionary<int, (double L, double R)>> _passEdges = new();
    /// <summary>The passes driven: from the lowest to the highest pass number recorded.</summary>
    public int PassesDriven => _passEdges.Count == 0 ? 1 : _passEdges.Keys.Max() - _passEdges.Keys.Min() + 1;
    private readonly (double E, double N)?[] _prevEdge = new (double E, double N)?[65];

    public JobRun(JobSpec spec, Scenario s) { Spec = spec; S = s; }

    public static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    public Task SendAsync(string cmd, string arg = "") => Rig.SendAsync(cmd, arg);

    /// <summary>What the operator did, with the job's clock, in the timeline and the trace.</summary>
    public void Did(string what)
    {
        S.Step(F($"{Seconds:0.0} s: operator: {what}"));
        Journal.Trace(F($"[fj] {Seconds,7:0.0} s   operator: {what}"));
    }

    // ---------------------------------------------------------------- set-up

    public async Task<bool> SetUpAsync(string dataRoot)
    {
        var T = Spec.T;
        if (Spec.SimulatedClock)
        {
            SimTime = new SimClock();
            AgOpenWeb.Models.Timing.Clock.Set(SimTime);
        }
        Module = new EmulatedSprayerModule(relays: T.Sections > 16 ? T.Sections : Math.Max(8, T.Sections), directSections: T.Sections > 16)
        { Clock = () => SimMs, ValveOnDelayS = Spec.ValveOnS, ValveOffDelayS = Spec.ValveOffS };
        Module.Start();
        Module.PowerOn();
        Steer = new SteerModuleStub { On = true };
        Truth = new TruthMachine(Spec.V, T, 0, Spec.Sys) { SpeedKmh = Spec.SpeedKmh };
        Rig = await FieldJobRig.StartAsync(dataRoot);
        Coverage = Rig.Services.GetRequiredService<ICoverageMapService>();
        Field = ScenarioFields.Get(Spec.Field);

        S.Step(F($"Vehicle & Tool: {Spec.V.Name}, {T.Name}, {Spec.Sys.Label}"));
        var settings = Spec.V.Settings(Spec.Sys).Concat(T.Settings()).ToList();
        if (!Spec.DefaultRelays)
            for (int s = 0; s < Math.Min(16, T.Sections); s++) settings.Add(F($"machine.pin:{s},{s + 1}"));
        settings.Add("tool.isSectionOffWhenOut:" + (Spec.OffWhenOut ? "1" : "0"));
        settings.Add("tool.isHeadlandSectionControl:" + (Spec.HeadlandSections ? "1" : "0"));
        settings.Add(F($"uturn.radius:{Spec.UTurnRadius}"));
        settings.Add(F($"uturn.distanceFromBoundary:{Spec.UTurnDistanceFromBoundary}"));
        if (Spec.UTurnStyle is int style) settings.Add(F($"uturn.style:{style}"));
        if (Spec.LookAheadMatchesValves)
        {
            settings.Add(F($"tool.lookAheadOn:{Spec.ValveOnS}"));
            settings.Add(F($"tool.lookAheadOff:{Spec.ValveOffS}"));
        }
        settings.AddRange(Spec.Extra);
        if (Spec.TypeSettings != null) { if (!await Spec.TypeSettings(this, settings)) return false; }
        else foreach (var kv in settings) await SendAsync("config.set", kv);
        await SendAsync("profile.save");

        // A few fixes at the field first, as a receiver gives before the field opens.
        var o = Field.InsidePoint();
        for (int i = 0; i < 15; i++) { SendFixAt(o.Lat, o.Lon, 0); await Task.Delay(20); }

        string name = Spec.Field;
        Directory.CreateDirectory(Rig.ImportDir);
        await File.WriteAllTextAsync(Path.Combine(Rig.ImportDir, name + ".kml"), Field.ToKml(Spec.Obstacles, name));
        await SendAsync("import.scan");
        S.Step(F($"Field > From KML: '{name}'{(Spec.Obstacles ? " with its obstacles" : " (outer boundary only)")}"));
        await SendAsync("field.fromKml", name + ".kml\t" + name);
        if (!await Rig.WaitUntilAsync(() => Rig.Vm.IsFieldOpen && Rig.Vm.CurrentFieldName == name && !Rig.State.UI.IsBusy, 20000))
        { Harness("the field did not open from the KML"); return false; }
        Plane = await Rig.ReadAsync(() => Rig.State.Field.LocalPlane)
            ?? new LocalPlane(new Wgs84(Rig.State.Field.OriginLatitude, Rig.State.Field.OriginLongitude), new SharedFieldProperties());
        Outer = Field.Outer.Select(p => Local(p.Lat, p.Lon)).ToList();
        if (Spec.Obstacles)
            foreach (var (dt, ring) in Field.Inner) Inner.Add((dt, ring.Select(p => Local(p.Lat, p.Lon)).ToList()));
        int appInner = await Rig.ReadAsync(() => Rig.State.Field.CurrentBoundary?.InnerBoundaries.Count ?? 0);
        S.Step(F($"field open: {Ring.Area(Outer) / 1e4:0.00} ha, {Inner.Count} obstacles in the file, {appInner} in AgOpenWeb"));

        if (Spec.Guide != GuideKind.AbLine)
        {
            PathOp = PathOperator.For(Spec.Guide);
            if (!await PathOp.CreateTrackAsync(this)) return false;
        }
        else
        {
        // The track, as the operator made it on the map: its first and last point.
        var tr = Field.Tracks.FirstOrDefault(t => t.Name == Spec.Track);
        if (tr.Points == null || tr.Points.Count < 2) { Harness($"no track '{Spec.Track}' in field '{Spec.Field}'"); return false; }
        var pa = tr.Points[0]; var pb = tr.Points[^1];
        await SendAsync("track.latLonAB", F($"{pa.Lat:F9},{pa.Lon:F9},{pb.Lat:F9},{pb.Lon:F9}"));
        if (!await Rig.ReadAsync(() => Rig.Vm.IsAutoSteerAvailable)) await SendAsync("track.activate", "0");
        if (!await Rig.ReadAsync(() => Rig.Vm.IsAutoSteerAvailable)) { Harness("no active AB line"); return false; }
        A = Local(pa.Lat, pa.Lon);
        var b = Local(pb.Lat, pb.Lon);
        double len = Math.Sqrt((b.E - A.E) * (b.E - A.E) + (b.N - A.N) * (b.N - A.N));
        Along = ((b.E - A.E) / len, (b.N - A.N) / len);
        Across = (Along.N, -Along.E);
        double abHeading = Math.Atan2(Along.E, Along.N) * 180 / Math.PI;
        S.Step(F($"AB line '{Spec.Track}' at {(abHeading + 360) % 360:0}°, active"));
        Frame = new StraightFrame(A, Along);
        Truth.FieldAlong = Along;
        }

        if (Spec.HeadlandM > 0)
        {
            if (Spec.MakeHeadland != null) { if (!await Spec.MakeHeadland(this)) return false; }
            else await SendAsync("headland.wholeBoundary", F($"{Spec.HeadlandM}"));
            if (!await Rig.WaitUntilAsync(() => Rig.State.Field.HeadlandLine is { Count: > 2 }, 15000))
            { Harness("no headland line was built"); return false; }
            Work = await Rig.ReadAsync(() => Rig.State.Field.HeadlandLine!.Select(p => (p.Easting, p.Northing)).ToList());
            S.Step(F($"headland {Spec.HeadlandM} m from the whole boundary: {Work.Count} points, {Ring.Area(Work) / 1e4:0.00} ha inside; section control in the headland {(Spec.HeadlandSections ? "on" : "off")}"));
        }
        if (Spec.UTurns)
        {
        if (!await Rig.ReadAsync(() => Rig.Vm.IsYouTurnEnabled)) await SendAsync("youturn.toggle");
        if (!await Rig.ReadAsync(() => Rig.Vm.IsYouTurnEnabled)) { Harness("U-turns did not switch on"); return false; }
        S.Step(F($"U-turns on (radius {Spec.UTurnRadius} m, {(Spec.OffWhenOut ? "off outside boundary" : "sections stay on at the edge")})"));
        }
        if (Spec.HardBoundary)
        {
            // The toggle acts on the selected boundary row (the outer boundary is row 0).
            await SendAsync("boundary.refresh");
            await SendAsync("boundary.select", "0");
            await SendAsync("boundary.hard");
            bool hard = await Rig.ReadAsync(() => Rig.State.Field.CurrentBoundary?.OuterBoundary?.IsHard == true);
            S.Step(hard ? "outer boundary marked hard" : "outer boundary NOT hard after 'boundary.hard' (harness)");
            if (!hard) Harness("boundary.hard did not mark the outer boundary hard");
        }

        if (Spec.BeforeStart != null) await Spec.BeforeStart(this);
        await SendAsync("field.startJob", name + "\tSpraying\tscenario\tSpraying " + name);
        S.Step("job started");

        Spacing = Math.Max(0.1, await Rig.ReadAsync(() => Rig.Store.ActualToolWidth - Rig.Store.Tool.Overlap));
        var box = Outer.Concat(Work ?? new List<(double E, double N)>()).ToList();
        Grid = new TruthGrid(box.Min(p => p.E), box.Max(p => p.E), box.Min(p => p.N), box.Max(p => p.N));
        Grid.Classify(Outer, Work, Inner);
        ScreenGrid = new TruthGrid(box.Min(p => p.E), box.Max(p => p.E), box.Min(p => p.N), box.Max(p => p.N));
        MapGrid = new TruthGrid(box.Min(p => p.E), box.Max(p => p.E), box.Min(p => p.N), box.Max(p => p.N));
        _scs = Rig.Services.GetRequiredService<ISectionControlService>();
        _tps = Rig.Services.GetRequiredService<IToolPositionService>();
        return true;
    }

    public (double E, double N) Local(double lat, double lon)
    {
        var g = Plane.ConvertWgs84ToGeoCoord(new Wgs84(lat, lon));
        return (g.Easting, g.Northing);
    }

    public Wgs84 Wgs(double e, double n) => Plane.ConvertGeoCoordToWgs84(new GeoCoord(n, e));

    public double U((double E, double N) p) => Frame.U(p);
    public double Vv((double E, double N) p) => Frame.V(p);

    private void SendFixAt(double lat, double lon, double headingDeg)
    {
        var sys = Spec.Sys;
        SimMs += 100;
        Module.TickNow(); Module.TickNow();
        Rig.SendSentence(FieldJobRig.Sentence(sys.Dual, sys.HasImu, lat, lon, headingDeg, 0, 0.5));
    }

    /// <summary>The tractor rolls on for <paramref name="fixes"/> fixes with the wheels as
    /// <paramref name="steer"/> asks (straight without), each fix sent and the control loop run;
    /// nothing is measured (the operator getting ready).</summary>
    public async Task RollAsync(int fixes, Func<double>? steer = null)
    {
        for (int i = 0; i < fixes; i++)
        {
            SimMs += 100;
            Truth.Advance(FixSeconds, steer?.Invoke() ?? 0);
            Module.TickNow(); Module.TickNow();
            SendFix();
            for (int t = 0; t < 10; t++) { SimTime?.AdvanceMs(10); Rig.TickLoop(1); }
            await Task.Delay(PaceMs);
        }
    }

    public void SendFix()
    {
        var (ae, an) = Truth.Antenna;
        var w = Wgs(ae, an);
        var sys = Spec.Sys;
        double hdg = Truth.Heading * 180 / Math.PI;
        Rig.SendSentence(FieldJobRig.Sentence(sys.Dual, sys.HasImu, w.Latitude, w.Longitude, hdg, 0, Math.Abs(Truth.SpeedKmh)));
    }

    // ---------------------------------------------------------------- the job

    /// <summary>Where the operator starts: the pass nearest the field's left edge (looking along
    /// A->B), the tractor 12 m before the boundary on that pass line, facing in.</summary>
    /// <summary>How long pass k's line runs inside the cultivated area (the longest stretch), m.</summary>
    public double InsideChordM(int k) => InsideChordAtU((k + 0.5) * Spacing);

    /// <summary>How long the line at <paramref name="u"/> runs inside the cultivated area (the longest stretch), m.</summary>
    public double InsideChordAtU(double u)
    {
        var work = Work ?? Outer;
        if (Frame is not StraightFrame)
        {
            // Walk pass k's line in 1 m steps (past the ends of an open curve too).
            double len = Frame.Closed ? Frame.Length : Frame.Length + 400;
            double v0 = Frame.Closed ? 0 : -200, run = 0, longest = 0;
            for (double v = v0; v <= v0 + len; v += 1)
            {
                if (Ring.Inside(Frame.At(u, v), work) && !Inner.Any(i => !i.DriveThrough && Ring.Inside(Frame.At(u, v), i.Ring))) longest = Math.Max(longest, ++run);
                else run = 0;
            }
            return longest;
        }
        var onLine = (A.E + Across.E * u, A.N + Across.N * u);
        var ts = Ring.LineCrossings(onLine, Along, work);
        ts.Sort();
        double best = 0;
        for (int i = 0; i + 1 < ts.Count; i += 2) best = Math.Max(best, ts[i + 1] - ts[i]);
        return best;
    }

    /// <summary>Passes AgOpenWeb would count as inside the field (any point of the line in the
    /// cultivated area) that a person would not drive: shorter than 20 m inside, at either edge.</summary>
    public List<(int Pass, double ChordM)> SliverPasses = new();

    public ((double E, double N) Pivot, double HeadingRad, int Pass) StartPose()
    {
        var work = Work ?? Outer;
        double uMin = work.Min(U), uMax = work.Max(U);
        // Pass k sits at (k + 1/2) x spacing to the right of the AB line (AgOpenWeb's PassGeometry).
        // The operator starts at the first pass along the edge that is worth driving: at least
        // 20 m of it inside the cultivated area.
        int kMin = (int)Math.Floor(uMin / Spacing) - 1, kMax = (int)Math.Ceiling(uMax / Spacing) + 1;
        int k = kMin;
        for (int i = kMin; i <= kMax; i++)
        {
            double chord = InsideChordM(i);
            if (chord > 0 && chord < 20) SliverPasses.Add((i, chord));
        }
        // With an implement to one side the operator starts with it on the boundary side (the
        // tractor inwards of it, as a mower is driven along a fence): along A->B for an implement
        // on the left, against A->B for one on the right. The tractor's own line must run in the
        // cultivated area too. A job that starts on the edge pass on purpose (EdgePass) goes along
        // A->B with whatever the tractor's line does.
        int dir = Spec.T.Offset > 0 && !Spec.EdgePass ? -1 : 1;
        double TractorU(int i) => (i + 0.5) * Spacing - Spec.T.Offset * dir;
        for (k = kMin; k <= kMax && (InsideChordM(k) < 20 || (!Spec.EdgePass && Spec.T.Offset != 0 && InsideChordAtU(TractorU(k)) < 20)); k++) { }
        if (k > kMin && Spec.T.Offset != 0)
            for (int i = kMin; i < k; i++)
                if (InsideChordM(i) >= 20) Journal.Trace(F($"[fj] pass {i} skipped at the start: the implement's line has {InsideChordM(i):0} m in the cultivated area, the tractor's {InsideChordAtU(TractorU(i)):0} m"));
        Journal.Trace("[fj] passes (number: metres of line inside the cultivated area): " + string.Join(", ",
            Enumerable.Range(kMin, kMax - kMin + 1).Select(i => F($"{i}: {InsideChordM(i):0}"))));
        double u = (k + 0.5) * Spacing;
        var onLine = (A.E + Across.E * u, A.N + Across.N * u);
        // The pass may cross the field more than once (a spur of the field on this line): the
        // operator starts at the boundary crossing just before its longest stretch inside the
        // cultivated area, as a person would, not at the first crossing along the line.
        var tw = Ring.LineCrossings(onLine, Along, work);
        tw.Sort();
        double chordStart = tw.Count >= 2 ? tw[0] : Outer.Min(Vv), chordEnd = tw.Count >= 2 ? tw[1] : Outer.Max(Vv);
        for (int i = 0, bestLen = -1; i + 1 < tw.Count; i += 2)
            if (tw[i + 1] - tw[i] > bestLen) { bestLen = (int)(tw[i + 1] - tw[i]); chordStart = tw[i]; chordEnd = tw[i + 1]; }
        var outer = Ring.LineCrossings(onLine, Along, Outer);
        double vPivot;
        if (dir > 0)
        {
            var ts = outer.Where(t => t <= chordStart + 0.01).ToList();
            vPivot = (ts.Count > 0 ? ts.Max() : chordStart) - 12;
        }
        else
        {
            var ts = outer.Where(t => t >= chordEnd - 0.01).ToList();
            vPivot = (ts.Count > 0 ? ts.Min() : chordEnd) + 12;
        }
        double uPivot = TractorU(k);   // the implement sits Offset to the tractor's right
        return ((A.E + Across.E * uPivot + Along.E * vPivot, A.N + Across.N * uPivot + Along.N * vPivot),
                Math.Atan2(Along.E * dir, Along.N * dir), k);
    }

    public async Task<bool> DriveAsync()
    {
        if (PathOp != null)
        {
            SimTime?.Step();
            if (!await PathOp.StartAsync(this)) return false;
        }
        else
        {
        var (pivot, hdg, k0) = StartPose();
        Truth.Place(pivot.E, pivot.N, hdg);
        S.Step(F($"tractor put at the start of pass {k0} ({pivot.E:0},{pivot.N:0}), 12 m before the boundary, heading {(hdg * 180 / Math.PI + 360) % 360:0}°"));
        for (int i = 0; i < 30; i++) { Truth.Advance(FixSeconds, 0); SendFix(); await Task.Delay(PaceMs); }
        await Rig.WaitForVmAsync();
        await SendAsync("autosteer.toggle");
        if (!await Rig.ReadAsync(() => Rig.Vm.IsAutoSteerEngaged)) { Harness("AutoSteer did not engage at the start: " + string.Join(" | ", Collect())); return false; }
        await SendAsync("section.master");
        if (!await Rig.ReadAsync(() => Rig.Vm.IsSectionMasterOn)) { Harness("the section master did not switch to Auto"); return false; }
        S.Step("AutoSteer engaged, sections Auto; AgOpenWeb drives from here");
        }

        double workArea = Ring.Area(Work ?? Outer);
        int maxFixes = (int)(workArea / (Spec.T.Width * Spec.SpeedKmh / 3.6) / FixSeconds * 3) + 6000;
        double cmd = 0;
        bool wasExecuting = false;
        int afterTurnUntil = -1;
        int saidAtTurnStart = 0;
        TurnRecord? turn = null;
        int offSince = -1, lastPassSeen = int.MinValue;
        double leftWorkAt = -1;
        bool inWorkSinceEnd = false;
        int lastEventCheck = 0;
        var prevOpen = 0UL;
        while (Fix < maxFixes && !Stopped)
        {
            Fix++;
            SimMs += 100;
            Truth.Advance(FixSeconds, cmd);
            Module.TickNow(); Module.TickNow();   // the module's 50 ms loop, twice per 100 ms fix
            _pivotHistory[Fix % _pivotHistory.Length] = (Truth.E, Truth.N);
            if (GpsOn) SendFix();
            for (int t = 0; t < 10; t++) { SimTime?.AdvanceMs(10); Rig.TickLoop(1); SweepMapping(); }   // the 100 Hz section/steer loop, in simulated time
            var c = Rig.LastCycle;
            if (c != null && Fix > _pivotHistory.Length && !InTurn)
            {
                int best = 0; double bestD = double.MaxValue;
                for (int k = 0; k < _pivotHistory.Length; k++)
                {
                    var h = _pivotHistory[(Fix - k) % _pivotHistory.Length];
                    double d = (h.E - c.Easting) * (h.E - c.Easting) + (h.N - c.Northing) * (h.N - c.Northing);
                    if (d < bestD) { bestD = d; best = k; }
                }
                LagHistogram[best]++;
            }
            bool engaged = false;
            if (c != null)
            {
                engaged = c.IsAutoSteerEngaged;
                bool steers = engaged && !(c.IsReverse && !Rig.Store.AutoSteer.SteerInReverse);
                cmd = steers && c.Guidance is { HasGuidance: true } g ? g.SteerAngle : 0;
                if (!double.IsFinite(cmd)) { Problems.Add(F($"fix {Fix}: steer angle {cmd} sent to the module")); cmd = 0; }
                if (HandSteer != null) cmd = HandSteer();
                var yt = c.YouTurn;
                bool executing = yt?.IsExecuting == true;
                if (executing && !wasExecuting)
                {
                    // AgOpenWeb said the field was done, then turned onto another pass after all (the
                    // message came at a bite in the boundary, the turn at the real end): the operator,
                    // driving the pass out, lets it turn, and the job goes on.
                    if (FieldDone && _endSaidAt >= 0 && !Stopped)
                    {
                        EarlyEnds.Add(F($"\"End of field reached\" at {_endSaidAt:0} s on pass {_endSaidPass}, then U-turn {TurnsStarted + 1} at {Seconds:0} s"));
                        FieldDone = false; _endSaidAt = -1; leftWorkAt = -1; inWorkSinceEnd = false;
                    }
                    turn = new TurnRecord { Index = ++TurnsStarted, PassBefore = lastPassSeen, StartSeconds = Seconds, Left = yt!.IsTurnLeft, HeadingAtStart = Truth.Heading, UAtStart = U((Truth.E, Truth.N)), TangentAtStart = Frame.TangentHeading((Truth.E, Truth.N)), TruthPassBefore = TruthPass() };
                    saidAtTurnStart = Math.Max(0, Said.Count - 6);
                    Turns.Add(turn);
                    InTurn = true;
                }
                if (executing && turn != null)
                {
                    if (!turn.Fallback && Said.Skip(saidAtTurnStart).Any(l => l.Contains("using simple fallback", StringComparison.Ordinal))) turn.Fallback = true;
                    var (outside, pivotOut) = OutsideBoundaryM();
                    turn.MaxOutsideM = Math.Max(turn.MaxOutsideM, outside);
                    turn.MaxPivotOutsideM = Math.Max(turn.MaxPivotOutsideM, pivotOut);
                    turn.MinSpeedKmh = Math.Min(turn.MinSpeedKmh, Truth.SpeedKmh);
                    turn.MaxHitchDeg = Math.Max(turn.MaxHitchDeg, Math.Abs(Truth.HitchAngleDeg));
                    if (turn.BackOnPassSeconds < 0)
                    {
                        double dh = Math.Abs(Math.IEEERemainder((Truth.Heading - Frame.TangentHeading((Truth.E, Truth.N))) - (turn.HeadingAtStart - turn.TangentAtStart) - Math.PI, 2 * Math.PI));
                        double du = Math.Abs(U((Truth.E, Truth.N)) - turn.UAtStart);
                        double onPass = Math.Abs(du / Spacing - Math.Round(du / Spacing)) * Spacing;
                        if (dh < 5 * Math.PI / 180 && du > Spacing / 2 && onPass < 0.3 && Work != null && Ring.Inside((Truth.E, Truth.N), Work))
                            turn.BackOnPassSeconds = Seconds - turn.StartSeconds;
                    }
                }
                // Before the first turn the operator looks at the U-turn arrow: a turn toward the
                // edge they started from lands on a pass with less than 20 m inside the field (the
                // start pass was the first one worth driving), so they tap the arrow the other way.
                if (TurnsStarted == 0 && !executing && yt?.TurnPath is { Count: > 1 } path && !_arrowChecked)
                {
                    _arrowChecked = true;
                    // Where the implement lands: the path ends at the tractor; an implement to one
                    // side sits Offset to its right, the right of the path's last heading.
                    var end = path[^1]; var pre = path[^2];
                    double he = Math.Atan2(end.Easting - pre.Easting, end.Northing - pre.Northing);
                    double rightU = Math.Cos(he) * Across.E - Math.Sin(he) * Across.N;
                    var tcNow = Truth.ToolCentre;
                    double uEnd = U((end.Easting, end.Northing)) + Spec.T.Offset * rightU, uNow = U((tcNow.E, tcNow.N));
                    int target = (int)Math.Floor(uEnd / Spacing);
                    if (uEnd < uNow - Spacing / 2)
                    {
                        ArrowAtEdge = (target, InsideChordM(target));
                        Did(F($"the U-turn arrow pointed at the field's edge (pass {target}, {ArrowAtEdge.Value.ChordM:0} m of it inside the cultivated area); the operator tapped it the other way"));
                        await SendAsync("youturn.direction");
                    }
                    else Journal.Trace(F($"[fj] {Seconds,7:0.0} s   the U-turn arrow points into the field (pass {target})"));
                }
                if (!executing && wasExecuting && turn != null)
                {
                    if (!turn.Fallback && Said.Skip(saidAtTurnStart).Any(l => l.Contains("using simple fallback", StringComparison.Ordinal))) turn.Fallback = true;
                    turn.Seconds = Seconds - turn.StartSeconds;
                    turn.EndSeconds = Seconds;
                    {
                        var tce = Truth.ToolCentre;
                        double dh = Math.Abs(Math.IEEERemainder(Truth.Heading - Frame.TangentHeading((tce.E, tce.N)), Math.PI));
                        turn.EndHeadingOffDeg = dh * 180 / Math.PI;
                        double uq = U((tce.E, tce.N)) / Spacing - 0.5;
                        turn.EndOffPassM = Math.Abs(uq - Math.Round(uq)) * Spacing;
                        turn.EndU = U((tce.E, tce.N));
                        turn.Spacing = Spacing;
                    }
                    InTurn = false;
                    afterTurnUntil = Fix + 200;
                    _passJoined = false;
                    _passStartFix = Fix;
                }
                // The 20 s after a turn, once a second: where AgOpenWeb thinks the tractor is
                // (zone and distance to the headland line), to see why a turn is or is not made.
                if (Fix < afterTurnUntil && Fix % 10 == 0 && yt != null)
                {
                    var tcc = ToolCentre();
                    string where = Ring.Inside((Truth.E, Truth.N), Work ?? Outer) ? "cultivated" : Ring.Inside((Truth.E, Truth.N), Outer) ? "headland" : "outside";
                    Journal.Trace(F($"[fj] {Seconds,7:0.0} s   after turn {TurnsStarted}: AgOpenWeb zone {yt.CurrentZone}, headland {(yt.DistanceToHeadland > 1e6 ? "none" : F($"{yt.DistanceToHeadland:0.0} m"))} ahead, executing {yt.IsExecuting}, path {(yt.TurnPath == null ? "none" : "ready")}; really {where}, u {U((tcc.E, tcc.N)):0.0}, steer {c.IsAutoSteerEngaged}"));
                }
                if (!executing && turn is { TruthPassAfter: int.MinValue } && !double.IsNaN(turn.EndSeconds) && Seconds - turn.EndSeconds >= 15)
                    turn.TruthPassAfter = TruthPass();
                wasExecuting = executing;
                if (!executing && !PassFromOperator && c.Guidance is { HasGuidance: true } gg)
                {
                    if (gg.HowManyPathsAway != lastPassSeen)
                    {
                        lastPassSeen = gg.HowManyPathsAway;
                        Pass = lastPassSeen;
                        if (turn != null && turn.PassAfter == 0 && turn.Seconds > 0) turn.PassAfter = lastPassSeen;
                        { var tcc = ToolCentre(); Journal.Trace(F($"[fj] {Seconds,7:0.0} s pass {Pass} (truth u {U((tcc.E, tcc.N)):0.00}, {Turns.Count} turns)")); }
                    }
                }
                // Curve, loop and pivot jobs: the implement against the pass it should be on.
                // Not before the implement first reaches its pass (the start, after a U-turn).
                if ((PathOp != null || Spec.PassCheck) && engaged && HandSteer == null && !executing && !InChange && !InCorner && Pass != int.MinValue)
                {
                    var tcc = Truth.ToolCentre;
                    double want = PathOp?.ExpectedU(this, Pass) ?? (Pass + 0.5) * Spacing;
                    double err = double.IsFinite(want) ? U((tcc.E, tcc.N)) - want : double.NaN;
                    if (!_passJoined && Math.Abs(err) < 0.3) _passJoined = true;
                    // An implement that never reaches its pass is off it too: measured from 30 s
                    // after the turn (the jobs that ask for the check on an AB line).
                    if (!_passJoined && Spec.PassCheck && Fix - _passStartFix > 300) _passJoined = true;
                    if (_passJoined && double.IsFinite(err) && PathOp?.PassErrorJudgedElsewhere(this, err) != true)
                    {
                        if (Math.Abs(err) > 1.0 && Fix - _passErrorTraced >= 50)
                        {
                            _passErrorTraced = Fix;
                            Journal.Trace(F($"[fj] {Seconds,7:0.0} s   off its pass: pass {Pass}, implement at ({tcc.E:0.0},{tcc.N:0.0}) u {err + want:0.00} for {want:0.00}, AgOpenWeb's cross-track {(c.Guidance?.CrossTrackError ?? double.NaN):0.00} m"));
                        }
                        PassErrorSum += Math.Abs(err); PassErrorSamples++;
                        if (Math.Abs(err) > 1.5 && PassErrorBig.Count < 5000) PassErrorBig.Add((err, tcc.E, tcc.N, Pass, Vv((tcc.E, tcc.N))));
                        if (Math.Abs(err) > Math.Abs(PassErrorWorst)) { PassErrorWorst = err; PassErrorWhere = (tcc.E, tcc.N, Pass, Vv((tcc.E, tcc.N))); }
                    }
                }
                // Screen vs valves, and the truth sweep.
                Sweep(c, prevOpen);
                if (!executing && Pass != int.MinValue && (c.ToolEasting != 0 || c.ToolNorthing != 0))
                {
                    var tcc = Truth.ToolCentre;
                    // AgOpenWeb's tool position is the implement's reference point; its sections
                    // sit Offset to the right of it (SectionControlService), as the real one's.
                    double off = Spec.T.Offset, th = c.ToolHeadingRadians;
                    double de = c.ToolEasting + Math.Cos(th) * off - tcc.E, dn = c.ToolNorthing - Math.Sin(th) * off - tcc.N;
                    double along = de * Math.Sin(tcc.Heading) + dn * Math.Cos(tcc.Heading);
                    double across = de * Math.Cos(tcc.Heading) - dn * Math.Sin(tcc.Heading);
                    ToolAlongSum += along; ToolAcrossSum += across; ToolSamples++;
                    if (Math.Abs(along) > Math.Abs(ToolAlongMax)) ToolAlongMax = along;
                    if (Math.Abs(across) > Math.Abs(ToolAcrossMax)) ToolAcrossMax = across;
                }
                if (Module.GeoStop != 0) SecondsGeoStop += FixSeconds;
                // The first minute, every 2 s: where the tractor is and what AgOpenWeb makes of it.
                if (Fix <= 600 && Fix % 20 == 0)
                {
                    var (outw, outp) = OutsideBoundaryM();
                    Journal.Trace(F($"[fj] {Seconds,7:0.0} s   start: pivot ({Truth.E:0.0},{Truth.N:0.0}) hdg {Truth.Heading * 180 / Math.PI:0.0} u {U((Truth.E, Truth.N)):0.0} v {Vv((Truth.E, Truth.N)):0.0} outside {outp:0.0}/{outw:0.0} m; app engaged {engaged}, guidance {c.Guidance?.HasGuidance}, pass {c.Guidance?.HowManyPathsAway}, steer {cmd:0.0}, reverse {c.IsReverse}, zone {c.YouTurn?.CurrentZone}"));
                }
            }
            prevOpen = Module.SectionsOpen;

            if (!engaged && c != null && ExpectEngaged)
            {
                if (offSince < 0) { offSince = Fix; Said.Add(F($"{Seconds:0.0} s: AutoSteer off: {c.DisengageReason ?? "no reason given"}")); }
                // The careful operator looks up, and after two seconds engages again.
                else if (Fix - offSince == 20 && Spec.Reengage)
                {
                    await SendAsync("autosteer.toggle");
                    Reengaged++;
                    if (Reengaged > 8) { Problems.Add("AutoSteer kept going off; gave up after 8 re-engagements"); break; }
                }
            }
            else offSince = -1;

            double hitch = Math.Abs(Truth.HitchAngleDeg);
            if (hitch > MaxHitchDeg)
            {
                MaxHitchDeg = hitch;
                MaxHitchWhere = F($"at {Seconds:0} s, ({Truth.E:0},{Truth.N:0}){(InTurn ? F($", U-turn {TurnsStarted}") : "")}{(Truth.SpeedKmh < 0 ? ", reversing" : "")}");
            }
            if (Spec.OnFix != null) await Spec.OnFix(this);
            if (PathOp != null) await PathOp.OnFixAsync(this);

            if (Fix - lastEventCheck >= 10)
            {
                lastEventCheck = Fix;
                foreach (var e in Collect())
                {
                    Said.Add(F($"{Seconds:0.0} s: {e}"));
                    // AgOpenWeb says it only while it plans a turn, never during one: read once a second,
                    // a message from just before a turn started is stale and is not the end.
                    if ((e.Contains("End of field reached", StringComparison.Ordinal) || e.Contains("Field complete", StringComparison.Ordinal)) && !wasExecuting)
                    {
                        if (!FieldDone) { _endSaidAt = Seconds; _endSaidPass = Pass; }
                        FieldDone = true;
                    }
                    // AgOpenWeb refused the turn (#698, #716: "No U-turn fits", "U-turn blocked") or
                    // missed it ("U-turn missed: ...", or "No U-turn can be made from the headland on
                    // this pass", #761: AutoSteer came on again in the headland past where a turn could
                    // start, #740 T10, or the pass runs along the headland strip) and said so: the
                    // operator takes over and the job stops, as a farmer would, instead of riding on with
                    // AutoSteer out of the field. A warning for "the end of the field" comes mid-pass:
                    // the operator drives the pass to its end and takes over there. Driving on after the
                    // warning is AgOpenGPS behaviour too (leaving the field does not switch AutoSteer
                    // off, #494); RAN-OFF is for a run-off with no such warning. The ground left from
                    // there is the consequence (EndedEarly), reported once through the warning's own finding.
                    if (TookOverAt < 0 && _takeOverAtEnd == null && e.Contains("take over manually", StringComparison.OrdinalIgnoreCase))
                    {
                        if (e.Contains("at the end of the field", StringComparison.OrdinalIgnoreCase))
                        {
                            _takeOverAtEnd = (Seconds, e, Pass);
                            Did(F($"AgOpenWeb asked the operator to take over at the end of the field (\"{e}\"): the operator drives pass {Pass} to its end"));
                        }
                        else TakeOver(e, Seconds, Pass, atEnd: false);
                    }
                }
            }
            var tc = ToolCentre();
            bool inWork = Ring.Inside((tc.E, tc.N), Work ?? Outer);
            // The end of the field the warning asked for: the implement out of the cultivated area
            // with none of it ahead (as the end of the job is judged), or the tractor itself at the
            // boundary, whichever comes first.
            if (_takeOverAtEnd is { } w && TookOverAt < 0 && ((!inWork && !WorkAhead(tc, 40)) || OutsideBoundaryM().Pivot > 0.5))
                TakeOver(w.Text, w.At, w.Pass, atEnd: true);
            if (FieldDone)
            {
                // The operator drives the last pass to its end: AgOpenWeb says it as the last turn ends,
                // the implement still in the headland (a front boom with a tight turn, a drill behind its
                // tank take a while to come back in), and the pass may cross a bit of headland (round a
                // bite in the boundary) and go on. Done when the implement is out of the cultivated area
                // with none of it ahead (40 m), or after 45 s out of it.
                if (inWork) { inWorkSinceEnd = true; leftWorkAt = -1; }
                else
                {
                    if (leftWorkAt < 0) leftWorkAt = Seconds;
                    if (Seconds - leftWorkAt > 45) break;
                    if (!WorkAhead(tc, 40) && (inWorkSinceEnd || Seconds - leftWorkAt > 6)) break;
                }
            }
            if (TookOverAt >= 0) break;
            double far = OutsideBoundaryM().Worst;
            if (far > 25)
            {
                // The run-off is the finding (RAN-OFF); the ground left from here is its consequence.
                Problems.Add(F($"{Seconds:0.0} s: the tractor ran {far:0} m out of the field on pass {Pass} with no warning from AgOpenWeb to take over"));
                EndedBy = F($"the tractor ran {far:0} m out of the field at {Seconds:0} s and the job stopped");
                EndedEarly = true;
                break;
            }
            if (Fix % 10 == 0) await Task.Delay(PaceMs * 10);
        }
        CyclesAtEnd = Rig.CycleCount; FixesAtEnd = Fix;
        if (Fix >= maxFixes) Problems.Add(F($"stopped after {Seconds / 60:0} simulated minutes without AgOpenWeb reporting the end of the field"));
        S.Step(F($"drove {Seconds / 60:0.0} simulated minutes, {Turns.Count} U-turns, passes {_passEdges.Keys.DefaultIfEmpty(0).Min()}..{_passEdges.Keys.DefaultIfEmpty(0).Max()}; {(FieldDone ? (EndedBy.Length > 0 ? EndedBy : "AgOpenWeb reported the end of the field") : "the field was not reported done")}"));
        if (await Rig.ReadAsync(() => Rig.Vm.IsSectionMasterOn)) await SendAsync("section.master");
        if (await Rig.ReadAsync(() => Rig.Vm.IsAutoSteerEngaged)) await SendAsync("autosteer.toggle");
        for (int i = 0; i < 10; i++) { Truth.Advance(FixSeconds, 0); SendFix(); await Task.Delay(PaceMs); }
        foreach (var e in Collect()) Said.Add(F($"{Seconds:0.0} s: {e}"));
        return true;
    }

    public (double E, double N, double Heading) ToolCentre() => Truth.ToolCentre;

    /// <summary>The operator takes over at AgOpenWeb's warning <paramref name="e"/> (said at <paramref name="warnedAt"/> s
    /// on pass <paramref name="pass"/>), now; <paramref name="atEnd"/> when the warning asked for the end of the field and
    /// the pass was driven to its end first. A refused turn ("No U-turn fits") is a problem the
    /// judge flags; a blocked one ("U-turn blocked": the turn would swing into a hard boundary or
    /// an obstacle, which the app must refuse, #698) and a missed one are what the app said, noted.</summary>
    private void TakeOver(string e, double warnedAt, int pass, bool atEnd)
    {
        TookOverAt = Seconds;
        bool missed = e.Contains("U-turn missed", StringComparison.Ordinal) || e.Contains("No U-turn can be made", StringComparison.Ordinal);
        bool blocked = e.Contains("U-turn blocked", StringComparison.Ordinal);
        string what = missed ? "missed" : blocked ? "blocked" : "refused";
        Problems.Add(F($"{warnedAt:0.0} s: AgOpenWeb {what} the U-turn at the end of pass {pass} and asked the operator to take over{(atEnd ? " at the end of the field" : "")} (\"{e}\")"));
        EndedBy = atEnd && Seconds - warnedAt >= 1
            ? F($"the operator drove pass {pass} to its end and took over there at {Seconds:0} s, as AgOpenWeb's warning at {warnedAt:0} s asked: \"{e}\"")
            : F($"the operator took over at AgOpenWeb's warning at {warnedAt:0} s{(atEnd ? " (the pass was at its end)" : "")}: \"{e}\"");
        EndedEarly = true;
    }

    /// <summary>Is the cultivated area straight ahead of the implement, within <paramref name="m"/> metres?</summary>
    private bool WorkAhead((double E, double N, double Heading) tc, double m)
    {
        double fe = Math.Sin(tc.Heading), fn = Math.Cos(tc.Heading);
        for (double d = 2; d <= m; d += 2)
            if (Ring.Inside((tc.E + fe * d, tc.N + fn * d), Work ?? Outer)) return true;
        return false;
    }

    /// <summary>The pass the real implement is on (the nearest), on an AB line; int.MinValue otherwise.</summary>
    public int TruthPass()
    {
        if (Frame is not StraightFrame || Spacing <= 0) return int.MinValue;
        var t = Truth.ToolCentre;
        return (int)Math.Round(U((t.E, t.N)) / Spacing - 0.5);
    }

    /// <summary>How far the pivot or an implement end is past the outer boundary, 0 inside.</summary>
    /// <summary>How far past the outer boundary the pivot or an implement end is (0 inside), and the pivot alone.</summary>
    public (double Worst, double Pivot) OutsideBoundaryM()
    {
        double worst = 0, pivot = 0;
        var tc = Truth.ToolCentre;
        double re = Math.Cos(tc.Heading), rn = -Math.Sin(tc.Heading), hw = Spec.T.Width / 2;
        if (!Ring.Inside((Truth.E, Truth.N), Outer)) pivot = worst = Ring.DistToRing((Truth.E, Truth.N), Outer);
        foreach (var p in new[] { (tc.E + re * hw, tc.N + rn * hw), (tc.E - re * hw, tc.N - rn * hw) })
            if (!Ring.Inside(p, Outer)) worst = Math.Max(worst, Ring.DistToRing(p, Outer));
        return (worst, pivot);
    }

    private void Sweep(GpsCycleResult c, ulong prevOpen)
    {
        var tc = Truth.ToolCentre;
        double re = Math.Cos(tc.Heading), rn = -Math.Sin(tc.Heading);
        int n = Spec.T.Sections;
        ulong open = Module.SectionsOpen;       // the relays: what the module was told
        ulong spray = Module.SectionsSpraying;  // the nozzles: the relays after the valves' time
        bool link = Module.LinkOk;
        if (!link) SecondsLinkDown += FixSeconds;
        var ss = c.SectionStates;
        Grid.BeginFix();
        double uL = double.MaxValue, uR = double.MinValue;
        bool any = false;
        // The section ends now; the previous fix's ends stay in _prevEdge until every section
        // has swept (neighbouring sections share an end).
        var cur = new (double E, double N)[n + 1];
        for (int s = 0; s <= n; s++)
        {
            double o = Spec.T.Edge(s);
            cur[s] = (tc.E + re * o, tc.N + rn * o);
        }
        for (int s = 0; s < n && s < 64; s++)
        {
            // What must match the screen is the product at the nozzles: the relays rightly lead the
            // screen by the look-ahead (#714), the valves then take that long, and the two meet.
            bool valve = ((spray >> s) & 1) != 0;
            bool screen = ss != null && s < ss.Length && ss[s];
            if (screen && !valve) { ScreenOnValveShut[s] += FixSeconds; if (!link) SecondsPaintingLinkDown += FixSeconds / n; }
            if (!screen && valve) ScreenOffValveOpen[s] += FixSeconds;
            if (screen != valve) { if (++_disagreeRun[s] > 10) PersistentDisagreement[s] += FixSeconds; }
            else _disagreeRun[s] = 0;
            bool flowing = ((spray >> s) & 1) != 0;
            if (flowing && _prevEdge[s] is { } p0 && _prevEdge[s + 1] is { } p1)
                Grid.Sweep(p0, p1, cur[s], cur[s + 1]);
            if (flowing)
            {
                any = true;
                uL = Math.Min(uL, Math.Min(U(cur[s]), U(cur[s + 1])));
                uR = Math.Max(uR, Math.Max(U(cur[s]), U(cur[s + 1])));
            }
        }
        for (int s = 0; s <= n; s++) _prevEdge[s] = cur[s];
        // The same sweep as AgOpenWeb sees it: its tool position and heading, its section states.
        if (ss != null && (c.ToolEasting != 0 || c.ToolNorthing != 0))
        {
            double are = Math.Cos(c.ToolHeadingRadians), arn = -Math.Sin(c.ToolHeadingRadians);
            var acur = new (double E, double N)[n + 1];
            for (int s = 0; s <= n; s++) { double o = Spec.T.Edge(s) + Spec.T.Offset; acur[s] = (c.ToolEasting + are * o, c.ToolNorthing + arn * o); }
            ScreenGrid.BeginFix();
            for (int s = 0; s < n && s < 64; s++)
                if (s < ss.Length && ss[s] && _prevAppEdge[s] is { } q0 && _prevAppEdge[s + 1] is { } q1)
                    ScreenGrid.Sweep(q0, q1, acur[s], acur[s + 1]);
            for (int s = 0; s <= n; s++) _prevAppEdge[s] = acur[s];
        }
        // Curve, loop and pivot jobs: only where the implement is on its pass; where AgOpenWeb's
        // line strays from the reference offset, OFF-ITS-PASS says so and the edges would not compare.
        bool onItsPass = PathOp == null || !(Math.Abs((uL + uR) / 2 - PathOp.ExpectedU(this, Pass)) > 1.0);
        if (any && !InTurn && !InChange && !InCorner && onItsPass && Pass != int.MinValue)
        {
            if (!_passEdges.TryGetValue(Pass, out var d)) _passEdges[Pass] = d = new SortedDictionary<int, (double, double)>();
            int bin = (int)Math.Floor(Vv((tc.E, tc.N)));
            if (d.TryGetValue(bin, out var old)) d[bin] = (Math.Min(old.L, uL), Math.Max(old.R, uR));
            else d[bin] = (uL, uR);
        }
        Track.Add((Truth.E, Truth.N, InTurn || InChange || HandSteer != null, open != 0));
    }

    /// <summary>After a control-loop tick: the strip each mapping section swept since the last tick,
    /// at AgOpenWeb's own tool position (what it hands its coverage map).</summary>
    private void SweepMapping()
    {
        if (_scs == null || _tps == null) return;
        var states = _scs.SectionStates;
        var tp = _tps.ToolPosition; double th = _tps.ToolHeading;
        if (tp.Easting == 0 && tp.Northing == 0) return;
        double re = Math.Cos(th), rn = -Math.Sin(th);
        int n = Math.Min(states.Count, 64);
        MapGrid.BeginFix();
        // Neighbouring sections share an end: all the ends now, then the sweeps, then the store.
        var cur = new (double E, double N)[n + 1];
        for (int i = 0; i < n; i++)
        {
            var st = states[i];
            cur[i] = (tp.Easting + re * st.PositionLeft, tp.Northing + rn * st.PositionLeft);
            cur[i + 1] = (tp.Easting + re * st.PositionRight, tp.Northing + rn * st.PositionRight);
        }
        for (int i = 0; i < n; i++)
            if (states[i].IsMappingOn && _prevMapEdge[i] is { } p0 && _prevMapEdge[i + 1] is { } p1)
                MapGrid.Sweep(p0, p1, cur[i], cur[i + 1]);
        for (int i = 0; i <= n; i++) _prevMapEdge[i] = cur[i];
    }

    /// <summary>The pivot's path, fix by fix: in a turn, any valve open.</summary>
    public readonly List<(double E, double N, bool Turn, bool Spraying)> Track = new();

    /// <summary>A picture of the job for a person: the field (dark), the headland (grey), the
    /// cultivated area (lighter), obstacles (red tint); sprayed once (green), twice or more
    /// (yellow); AgOpenWeb's map where nothing was sprayed (blue: its sections were on there on the screen; cyan: only its 100 Hz mapping was; magenta: neither, the map painted on its own); the pivot's path (white, orange
    /// in the turns). Binary PPM, 1 pixel = 1 cell (0.2 m).</summary>
    public void DumpImage(string path)
    {
        var g = Grid;
        var px = new byte[g.W * g.H * 3];
        void Put(int x, int y, byte r, byte gg, byte b)
        {
            if (x < 0 || y < 0 || x >= g.W || y >= g.H) return;
            int o = ((g.H - 1 - y) * g.W + x) * 3; px[o] = r; px[o + 1] = gg; px[o + 2] = b;
        }
        for (int i = 0; i < g.Count.Length; i++)
        {
            int x = i % g.W, y = i / g.W;
            byte z = g.Zone[i];
            byte r = 20, gg = 20, b = 20;
            if ((z & TruthGrid.InOuter) != 0) { r = gg = b = 70; }
            if ((z & TruthGrid.InWork) != 0) { r = gg = b = 110; }
            if ((z & TruthGrid.InObstacle) != 0) { r = 140; gg = 60; b = 60; }
            if ((z & TruthGrid.InDriveThrough) != 0) { r = 120; gg = 90; b = 60; }
            var cc = g.Centre(i);
            bool app = (z & TruthGrid.InOuter) != 0 && Coverage.IsPointCovered(cc.E, cc.N);
            if (g.Count[i] == 1) { r = 40; gg = 170; b = 60; }
            else if (g.Count[i] >= 2) { r = 220; gg = 200; b = 40; }
            else if (app && MapGrid.Count[i] == 0) { r = 200; gg = 60; b = 200; }
            else if (app && ScreenGrid.Count[i] == 0) { r = 60; gg = 200; b = 200; }
            else if (app) { r = 60; gg = 90; b = 200; }
            Put(x, y, r, gg, b);
        }
        foreach (var t in Track)
        {
            int x = (int)Math.Floor((t.E - g.MinE) / TruthGrid.Cell), y = (int)Math.Floor((t.N - g.MinN) / TruthGrid.Cell);
            if (t.Turn) Put(x, y, 255, 140, 0); else Put(x, y, 255, 255, 255);
        }
        using var f = File.Create(path);
        var hdr = System.Text.Encoding.ASCII.GetBytes($"P6\n{g.W} {g.H}\n255\n");
        f.Write(hdr); f.Write(px);
    }

    /// <summary>What the app said since the last call (status lines, warnings, errors; not the
    /// AutoSteer and section toggles the harness itself makes).</summary>
    public List<string> Collect()
    {
        var said = new List<string>();
        foreach (var e in Rig.TakeEvents())
            if (e.Kind is RigEventKind.Failure or RigEventKind.Exception or RigEventKind.Error or RigEventKind.Hang
                || (e.Kind == RigEventKind.Status && !e.Text.Contains("Section", StringComparison.OrdinalIgnoreCase))
                || (e.Kind == RigEventKind.Warning && !e.Text.Contains("No GPS input", StringComparison.Ordinal)))
                said.Add(e.ToString());
        return said;
    }

    // ---------------------------------------------------------------- the measure

    public JobAreas Measure()
    {
        var a = new JobAreas();
        double cell = TruthGrid.Cell * TruthGrid.Cell;
        var g = Grid;
        int reach = (int)Math.Ceiling(1.5 * Spacing / TruthGrid.Cell);
        // The seams between neighbouring passes where both ran at full width, by 1 m bin along
        // the line: where the gap between them lies across the passes, and how wide it is.
        var seams = new Dictionary<int, List<(double Mid, double Gap)>>();
        {
            var ordered = _passEdges.OrderBy(p => p.Value.Values.Average(x => (x.L + x.R) / 2)).ToList();
            for (int k = 0; k + 1 < ordered.Count; k++)
                foreach (var (bin, x) in ordered[k].Value)
                {
                    if (!ordered[k + 1].Value.TryGetValue(bin, out var x2)) continue;
                    if (x.R - x.L < Spec.T.Width - 0.3 || x2.R - x2.L < Spec.T.Width - 0.3) continue;
                    if (!seams.TryGetValue(bin, out var l)) seams[bin] = l = new();
                    l.Add(((x.R + x2.L) / 2, x2.L - x.R));
                }
        }
        bool OnSeam((double E, double N) c)
        {
            if (seams.Count == 0) return false;
            double u = U(c); int b = (int)Math.Floor(Vv(c));
            for (int db = -1; db <= 1; db++)
                if (seams.TryGetValue(b + db, out var l))
                    foreach (var (mid, gap) in l)
                        if (Math.Abs(u - mid) <= Math.Max(gap, 0) / 2 + TruthGrid.Cell / 2) return true;
            return false;
        }
        for (int i = 0; i < g.Count.Length; i++)
        {
            byte z = g.Zone[i];
            byte t = g.Count[i];
            bool truth = t > 0;
            bool inWork = (z & TruthGrid.InWork) != 0 && (z & TruthGrid.InObstacle) == 0;
            var c = g.Centre(i);
            bool app = (z & TruthGrid.InOuter) != 0 || truth ? Coverage.IsPointCovered(c.E, c.N) : false;
            if (inWork)
            {
                a.Work += cell;
                if (truth) a.Sprayed += cell;
                else
                {
                    a.Missed += cell;
                    if (Work != null && Ring.DistToRing(c, Work) < 3) a.MissedAtEnds += cell;
                    // Within 2 m of an obstacle: the sections closing over it and opening again.
                    else if (g.Near(i, j => (g.Zone[j] & (TruthGrid.InObstacle | TruthGrid.InDriveThrough)) != 0, 10)) a.MissedAtObstacles += cell;
                    // No headland (the lap jobs): the edge of the field along the boundary.
                    else if (Work == null && Ring.DistToRing(c, Outer) < 1.5) a.MissedAlongBoundary += cell;
                    else if (LeftOnPurpose?.Invoke(c) == true) a.MissedLeftInMiddle += cell;
                    else if (AtCorner?.Invoke(c) == true) a.MissedAtCorners += cell;
                    else if (SprayedAcross(i, +1, reach) && SprayedAcross(i, -1, reach)) { a.MissedBetweenPasses += cell; a.MissedBetweenE += c.E * cell; a.MissedBetweenN += c.N * cell; if (OnSeam(c)) a.MissedAtSeams += cell; }
                    // The rest: in a pass that was driven (worked along part of its length, as the
                    // first passes of a field whose corner a U-turn cut off), or in no driven pass
                    // (a side of the field, passes skipped).
                    else if (PassDriven(c)) a.MissedInWorkedPasses += cell;
                }
                if (t >= 2) a.Twice += cell;
                if (app) a.AppPainted += cell;
            }
            if (truth)
            {
                if ((z & TruthGrid.InOuter) == 0) a.OutsideBoundary += cell;
                else if ((z & TruthGrid.InObstacle) != 0) a.InObstacles += cell;
                else if ((z & TruthGrid.InDriveThrough) != 0) a.InDriveThrough += cell;
                else if ((z & TruthGrid.InWork) == 0) a.InHeadland += cell;
                if (!app && !g.Near(i, j => Coverage.IsPointCovered(g.Centre(j).E, g.Centre(j).N), 2)) a.SprayedNotAppPainted += cell;
            }
            if (app && !truth && !g.Near(i, j => g.Count[j] > 0, 2))
            {
                a.AppPaintedNotSprayed += cell;
                if (ScreenGrid.Count[i] > 0 || ScreenGrid.Near(i, j => ScreenGrid.Count[j] > 0, 2)) a.AppPaintedScreenSweptNotSprayed += cell;
            }
            if ((z & TruthGrid.InOuter) != 0)
            {
                bool mapped = MapGrid.Count[i] > 0;
                if (mapped) a.MapSwept += cell;
                if (app && !mapped && !MapGrid.Near(i, j => MapGrid.Count[j] > 0, 2)) a.AppPaintedNotMapped += cell;
                if (mapped && ScreenGrid.Count[i] == 0 && !ScreenGrid.Near(i, j => ScreenGrid.Count[j] > 0, 2)) a.MappedNotScreen += cell;
                bool screen = ScreenGrid.Count[i] > 0;
                if (screen && !app && !g.Near(i, j => Coverage.IsPointCovered(g.Centre(j).E, g.Centre(j).N), 2)) a.ScreenSweptNotPainted += cell;
                if (screen) a.ScreenSwept += cell;
            }
        }
        a.AppTotalWorked = Coverage.TotalWorkedArea;
        a.AppActualCovered = Coverage.ActualAreaCovered;

        // Strips between neighbouring passes, from the real implement's ends (gap +, overlap -).
        var passes = _passEdges.OrderBy(p => p.Value.Values.Average(x => (x.L + x.R) / 2)).ToList();
        for (int k = 0; k + 1 < passes.Count; k++)
        {
            foreach (var (bin, x) in passes[k].Value)
            {
                if (!passes[k + 1].Value.TryGetValue(bin, out var x2)) continue;
                if (x.R - x.L < Spec.T.Width - 0.3 || x2.R - x2.L < Spec.T.Width - 0.3) continue;   // a section still off at the pass's end
                double gap = x2.L - x.R;
                if (gap > a.LargestMissedStripM)
                    a.LargestStripWhere = F($"between passes {passes[k].Key} and {passes[k + 1].Key}, {bin} m along the line from A");
                a.LargestMissedStripM = Math.Max(a.LargestMissedStripM, gap);
                a.LargestOverlapM = Math.Max(a.LargestOverlapM, -gap);
            }
        }
        return a;
    }

    /// <summary>Does the pass this point lies on have the implement's edges recorded somewhere
    /// along it, i.e. was it driven (AgOpenWeb's pass numbering, as <see cref="TruthPass"/>)?</summary>
    private bool PassDriven((double E, double N) c)
    {
        if (Spacing <= 0 || _passEdges.Count == 0) return false;
        return _passEdges.ContainsKey((int)Math.Round(U(c) / Spacing - 0.5));
    }

    /// <summary>The share of the cultivated area (inside the headland line, outside obstacles)
    /// no valve has sprayed yet: what the operator sees left on the map.</summary>
    public double UnsprayedShare()
    {
        var g = Grid;
        long work = 0, left = 0;
        for (int i = 0; i < g.Count.Length; i++)
        {
            byte z = g.Zone[i];
            if ((z & TruthGrid.InWork) == 0 || (z & (TruthGrid.InObstacle | TruthGrid.InDriveThrough)) != 0) continue;
            work++;
            if (g.Count[i] == 0) left++;
        }
        return work == 0 ? 0 : (double)left / work;
    }

    private bool SprayedAcross(int i, int dir, int reach)
    {
        var c = Grid.Centre(i);
        var Across = Frame.AcrossDir(c);
        for (int k = 1; k <= reach; k++)
        {
            int j = Grid.Index(c.E + Across.E * dir * k * TruthGrid.Cell, c.N + Across.N * dir * k * TruthGrid.Cell);
            if (j < 0) return false;
            if (Grid.Count[j] > 0) return true;
        }
        return false;
    }

    public void Harness(string text) { Problems.Add("HARNESS: " + text); S.Harness(text); }

    /// <summary>What the job opened besides the rig (a browser), closed with it.</summary>
    public readonly List<IAsyncDisposable> Owned = new();

    public async ValueTask DisposeAsync()
    {
        try
        {
            foreach (var o in Owned) try { await o.DisposeAsync(); } catch { }
            try { Steer?.Dispose(); } catch { }
            try { Module?.Dispose(); } catch { }
            try { if (Rig != null) await Rig.DisposeAsync(); } catch { }
        }
        finally
        {
            // Only once the backend is gone: it would see its clock jump back while shutting down.
            if (SimTime != null) AgOpenWeb.Models.Timing.Clock.Reset();
        }
    }
}
