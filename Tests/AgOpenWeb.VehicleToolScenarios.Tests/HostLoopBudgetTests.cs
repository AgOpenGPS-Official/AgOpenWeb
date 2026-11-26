// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using AgOpenWeb.Models;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace AgOpenWeb.VehicleToolScenarios.Tests;

/// <summary>
/// Work budgets for the host loop and the 10 Hz broadcast, with a field open: the costs the
/// GPS-cycle and control-tick budgets (Services.Tests, HotPathBudgetTests) don't see.
/// #604 was every job file on the disk parsed ten times a second by the broadcast's
/// fingerprints; #169 was screens freezing behind slow work on the host loop.
///
/// The whole backend runs in this process (the rig), with the web server's 10 Hz loop off:
/// the test runs each broadcast itself, on its own thread, right after each fix, as the loop
/// would, and counts what it cost. Every work item and timer tick on the host loop
/// (MainViewModel applying a GPS cycle, the status timer, autosave ...) is counted the same
/// way. Counts (polygon edges, coverage lookups, bytes allocated, file read calls), not time,
/// so CI can hold them to a budget on any machine.
///
/// The field, made through the operator's commands: about 300 x 400 m from a KML file (a
/// boundary point every 2 m), a 24 m headland from the whole boundary, an AB line north, U-turns on,
/// a job started; 40 more fields with a job each on the disk, as on a farm's tablet. Then the
/// tractor stands for 5 s and drives 335 m up a pass at 10 km/h with AutoSteer and the
/// sections on, to the end of the pass in the headland.
///
/// A failure means new work runs on every broadcast or every fix: do it when something
/// changes and keep the result, rather than raising the budget.
/// </summary>
[TestFixture]
[Category("HotPath")] // CI runs these in the hot-path-budgets job, in Release
[NonParallelizable]   // ConfigurationStore is a singleton; the rig listens on UDP 9999 (loopback sends only)
public class HostLoopBudgetTests
{
    // One broadcast (10 Hz), steady state with the field open: nothing changed but the
    // tractor and the coverage. Measured 2026-11-08: 484 KB standing, 501 KB driving (p99
    // 588 KB, one tick 1.4 MB), no polygon edges, up to 2,700 coverage lookups (the delta).
    // 443 KB of that is the Scene fingerprint projecting every tram line to DTOs only to count
    // them, 39 KB the headland segments' edit lines; one tick in five the Status adds 3 KB
    // (52 KB on a host with network interfaces to list).
    // The budget holds that; lower it when the fingerprint stops projecting.
    private const long TickBytes = 768 * 1024;
    private const long TickEdges = 2_000;
    private const long TickCoverageLookups = 20_000;
    // Read calls a tick may make. The Status lists the host's IP addresses (read from /proc and
    // /sys on Linux: 12 read calls on a tractor's host), but the rig reports no network
    // interfaces, so the PGNs never reach the LAN: that listing reads nothing here, and any
    // read is the broadcast's own.
    private const long TickReads = 4;
    // A tick after a change (the Scene or a list rebuilt, the coverage edge) may go over,
    // a few times while driving; never past the peak. Rebuilding the Field Operations lists
    // (41 fields) costs 46 MB and 2,000 reads: once per change, never in a steady tick.
    private const int TicksOverBudgetAllowed = 5;
    private const long TickPeakBytes = 4 * 1024 * 1024;

    // One work item or timer tick on the host loop while driving. Measured: 3.3 KB applying a
    // GPS cycle (max 3.9), under 1 KB everything else, no edges, no reads. A few items may
    // load code the first time it runs (an assembly is read from the disk).
    private const long ItemBytes = 64 * 1024;
    private const long ItemEdges = 5_000;
    private const int ItemsOverBudgetAllowed = 5;
    // All the host loop's work per fix, on average while driving (measured 4.6 KB).
    private const long BytesPerFix = 32 * 1024;

    private const int OtherFields = 40;
    private const double FieldW = 300, FieldL = 400, Headland = 24;
    private const double Step = 10 / 3.6 * 0.1; // 10 km/h at 10 Hz
    private static readonly (double Lat, double Lon) Origin = (41.5012, -5.4987); // made-up place

    private sealed class Phase(string name)
    {
        public readonly string Name = name;
        public readonly List<Cost> Ticks = new();
        public readonly List<(string Work, Cost Cost)> Items = new();
        public int Fixes;
    }

    [Test]
    public async Task Broadcast_and_host_loop_stay_within_budget()
    {
        var dir = Path.Combine(Drive.DataRootBase, "hostloop-" + Guid.NewGuid().ToString("N")[..8]);
        await using var rig = await VehicleToolRig.StartAsync(dir, broadcastLoop: false);
        var b = rig.Server.Broadcaster ?? throw new InvalidOperationException("no broadcaster");
        var plane = new LocalPlane(new Wgs84(Origin.Lat, Origin.Lon), new SharedFieldProperties());
        var sys = Machines.PumaSys;

        string Fix(double e, double n, double kmh)
        {
            var w = plane.ConvertGeoCoordToWgs84(new GeoCoord(n, e));
            return VehicleToolRig.Sentence(sys.Dual, sys.HasImu, w.Latitude, w.Longitude, 0, 0, kmh);
        }

        // ---- the field, through the operator's commands
        foreach (var kv in Drive.Careful(Machines.Puma, Machines.Sprayer8, sys)) await rig.SendAsync("config.set", kv);
        await rig.SendAsync("profile.save");
        for (int i = 0; i < 15; i++) { rig.SendSentence(Fix(0, -150, 0)); await Task.Delay(10); }

        const string name = "Budget";
        Directory.CreateDirectory(rig.ImportDir);
        await File.WriteAllTextAsync(Path.Combine(rig.ImportDir, name + ".kml"), Kml(plane, name));
        await rig.SendAsync("import.scan");
        await rig.SendAsync("field.fromKml", name + ".kml\t" + name);
        Assert.That(await rig.WaitUntilAsync(() => rig.Vm.IsFieldOpen && rig.Vm.CurrentFieldName == name && !rig.State.UI.IsBusy, 20000),
            Is.True, "the field did not open from the KML");
        var a = plane.ConvertGeoCoordToWgs84(new GeoCoord(-FieldL / 2, 0));
        var z = plane.ConvertGeoCoordToWgs84(new GeoCoord(FieldL / 2, 0));
        await rig.SendAsync("track.latLonAB", string.Create(CultureInfo.InvariantCulture,
            $"{a.Latitude:F9},{a.Longitude:F9},{z.Latitude:F9},{z.Longitude:F9}"));
        if (!await rig.ReadAsync(() => rig.Vm.IsAutoSteerAvailable)) await rig.SendAsync("track.activate", "0");
        await rig.SendAsync("headland.wholeBoundary", Headland.ToString(CultureInfo.InvariantCulture));
        Assert.That(await rig.WaitUntilAsync(() => rig.State.Field.HeadlandLine is { Count: > 2 }, 15000), Is.True, "no headland line");
        if (!await rig.ReadAsync(() => rig.Vm.IsYouTurnEnabled)) await rig.SendAsync("youturn.toggle");
        await rig.SendAsync("field.startJob", name + "\tSpraying\tbudget\tSpraying " + name);
        int boundaryPoints = await rig.ReadAsync(() => rig.State.Field.CurrentBoundary?.OuterBoundary?.Points.Count ?? 0);
        int headlandPoints = await rig.ReadAsync(() => rig.State.Field.HeadlandLine?.Count ?? 0);

        // A farm's other fields, each with a job (a copy of this one, as a backup restores them).
        var src = Path.Combine(rig.FieldsDir, name);
        Assert.That(Directory.Exists(src), Is.True, "the field's folder " + src);
        for (int i = 1; i <= OtherFields; i++)
            CopyDir(src, Path.Combine(rig.FieldsDir, string.Create(CultureInfo.InvariantCulture, $"Farm {i:00}")));
        int jobFiles = Directory.EnumerateFiles(rig.FieldsDir, "job.json", SearchOption.AllDirectories).Count();
        // The Field Operations panel opens (it asks for the lists, as the web client does).
        await rig.SendAsync("lists.refresh");

        var phases = new List<Phase>();
        Phase? current = null;
        double northing = 0;
        var readAt = new List<string>();
        rig.HostLoopMeter = (work, cost) =>
        {
            var p = current;
            if (p == null) return;
            lock (p)
            {
                p.Items.Add((work, cost));
                if (cost.ReadCalls > 0) readAt.Add(string.Create(CultureInfo.InvariantCulture, $"{work} at N {Volatile.Read(ref northing):0.0}: {cost}"));
            }
        };

        // ---- standing: the first broadcasts build everything once, then nothing moves
        var opening = new Phase("field open, first broadcasts");
        var standing = new Phase("standing, field open");
        var driving = new Phase("driving a pass, AutoSteer and sections on");
        phases.AddRange(new[] { opening, standing, driving });

        current = opening;
        for (int i = 0; i < 20; i++)
        {
            rig.SendSentence(Fix(0, -150, 0)); opening.Fixes++;
            await rig.WaitForVmAsync();
            opening.Ticks.Add(Tick(b));
            await Task.Delay(10);
        }
        current = standing;
        for (int i = 0; i < 50; i++)
        {
            rig.SendSentence(Fix(0, -150, 0)); standing.Fixes++;
            await rig.WaitForVmAsync();
            standing.Ticks.Add(Tick(b));
            await Task.Delay(10);
        }

        // ---- driving: 335 m up the pass at 10 km/h into the headland at the far end
        current = null;
        await rig.SendAsync("autosteer.toggle");
        await rig.SendAsync("section.master");
        current = driving;
        for (double n = -150; n <= 185; n += Step)
        {
            Volatile.Write(ref northing, n);
            rig.SendSentence(Fix(0, n, 10)); driving.Fixes++;
            await rig.WaitForVmAsync();
            driving.Ticks.Add(Tick(b));
            await Task.Delay(8);
        }
        current = null;
        rig.HostLoopMeter = null;
        double covered = rig.Services.GetRequiredService<ICoverageMapService>().TotalWorkedArea / 1e4;

        // ---- the report
        var r = new StringBuilder();
        r.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"Field: {boundaryPoints} boundary points, {headlandPoints} headland points; {OtherFields + 1} fields, {jobFiles} job files on the disk; worked {covered:0.00} ha"));
        foreach (var p in phases)
        {
            r.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{p.Name}: {p.Fixes} fixes, {p.Ticks.Count} broadcasts, {p.Items.Count} host-loop items"));
            if (p.Ticks.Count > 0) r.AppendLine("  broadcast  " + Stats(p.Ticks));
            foreach (var g in p.Items.GroupBy(x => x.Work).OrderByDescending(g => g.Sum(x => x.Cost.Bytes)))
                r.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  {g.Key} x{g.Count()}: ") + Stats(g.Select(x => x.Cost).ToList()));
        }
        foreach (var line in readAt.Take(10)) r.AppendLine("Host loop read files: " + line);
        r.AppendLine("Where one broadcast's work goes (each part on its own, after the drive):");
        foreach (var (part, cost) in Breakdown(b)) r.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  {part,-34} {cost}"));
        TestContext.Out.WriteLine(r.ToString());

        // ---- the budgets
        var steady = standing.Ticks.Concat(driving.Ticks).ToList();
        var overTicks = steady.Select((c, i) => (c, i)).Where(x => x.c.Bytes > TickBytes || x.c.Edges > TickEdges
            || x.c.CoverageLookups > TickCoverageLookups).ToList();
        var readTicks = steady.Where(c => c.ReadCalls > TickReads).ToList();
        var items = driving.Items;
        var overItems = items.Where(x => x.Cost.Bytes > ItemBytes || x.Cost.Edges > ItemEdges).ToList();
        var readItems = items.Where(x => x.Cost.ReadCalls > 0).ToList();
        long bytesPerFix = driving.Fixes == 0 ? 0 : items.Sum(x => x.Cost.Bytes) / driving.Fixes;

        Assert.Multiple(() =>
        {
            Assert.That(driving.Fixes, Is.GreaterThan(1000), "the drive");
            Assert.That(covered, Is.GreaterThan(0.1), "the sections painted no coverage: the drive did not happen as planned");
            Assert.That(readTicks, Is.Empty,
                $"{readTicks.Count} broadcasts read files with the field open and nothing changed on the disk (#604): " +
                "a fingerprint or a provider reads the disk every tick. First: " +
                string.Join("; ", readTicks.Take(3)));
            Assert.That(overTicks.Count, Is.LessThanOrEqualTo(TicksOverBudgetAllowed),
                $"{overTicks.Count} of {steady.Count} broadcasts over the per-tick budget; work that should happen " +
                "once runs every tick. First: " + string.Join("; ", overTicks.Take(3).Select(x => $"tick {x.i}: {x.c}")));
            Assert.That(steady.Max(c => c.Bytes), Is.LessThanOrEqualTo(TickPeakBytes), "peak bytes allocated by one broadcast");
            Assert.That(readItems.Count, Is.LessThanOrEqualTo(ItemsOverBudgetAllowed),
                "host-loop work read files while driving (#169: slow work on the host loop freezes every screen): " +
                string.Join("; ", readItems.Take(3).Select(x => x.Work + ": " + x.Cost)));
            Assert.That(overItems.Count, Is.LessThanOrEqualTo(ItemsOverBudgetAllowed),
                $"{overItems.Count} host-loop work items over the per-item budget while driving. First: " +
                string.Join("; ", overItems.Take(3).Select(x => x.Work + ": " + x.Cost)));
            Assert.That(bytesPerFix, Is.LessThanOrEqualTo(BytesPerFix), "bytes allocated on the host loop per fix");
        });
    }

    // One broadcast on this thread, measured. With no client it never awaits.
    private static Cost Tick(AgOpenWeb.RemoteServer.MapBroadcaster b)
    {
        var start = CostMeter.Start();
        var t = b.BroadcastOnceAsync();
        var cost = CostMeter.Stop(start);
        Assert.That(t.IsCompleted, Is.True, "the broadcast awaited: a client is connected, so the counts are not this thread's");
        t.GetAwaiter().GetResult();
        return cost;
    }

    /// <summary>Each part of a broadcast measured on its own (the third call: caches warm),
    /// so a budget failure says where to look.</summary>
    private static List<(string Part, Cost Cost)> Breakdown(AgOpenWeb.RemoteServer.MapBroadcaster b)
    {
        var p = b.Projector;
        var parts = new (string, Action)[]
        {
            ("Scene fingerprint", () => p.SceneFingerprint()),
            ("  of which headland segments", () => p.HeadlandSegsProvider?.Invoke()),
            ("  of which tram lines", () => p.TramLinesProvider?.Invoke()),
            ("Config fingerprint", () => p.ConfigFingerprint()),
            ("Profiles fingerprint", () => p.ProfilesFingerprint()),
            ("NTRIP profiles fingerprint", () => p.NtripProfilesFingerprint()),
            ("Mount points fingerprint", () => p.NtripMountpointsFingerprint()),
            ("Web cards fingerprint", () => p.WebCardsFingerprint()),
            ("Field ops fingerprint", () => p.FieldOpsFingerprint()),
            ("AgShare fingerprint", () => p.AgShareFingerprint()),
            ("App info fingerprint", () => p.AppInfoFingerprint()),
            ("Field tools fingerprint", () => p.FieldToolsFingerprint()),
            ("Recorded path provider", () => b.RecordedPathProvider?.Invoke()),
            ("Boundary provider", () => b.BoundaryProvider?.Invoke()),
            ("Prompt provider", () => b.PromptProvider?.Invoke()),
            ("Tick (built + encoded)", () => AgOpenWeb.RemoteServer.WireCodec.EncodeTick(p.BuildTick(1))),
            ("Status (built + encoded, 2 Hz)", () => AgOpenWeb.RemoteServer.WireCodec.EncodeStatus(p.BuildStatus())),
            ("  of which system data", () => p.SystemDataProvider?.Invoke()),
            ("  of which field data", () => p.FieldDataProvider?.Invoke()),
            ("  of which ISOBUS", () => p.IsobusProvider?.Invoke()),
            ("  of which GPS source", () => p.GpsSourceProvider?.Invoke()),
            ("Scene (built, on a change)", () => p.BuildScene(1)),
            ("Field ops (built, on a change)", () => p.BuildFieldOps()),
        };
        var list = new List<(string, Cost)>();
        foreach (var (name, act) in parts)
        {
            Cost c = default;
            for (int i = 0; i < 3; i++)
            {
                var start = CostMeter.Start();
                act();
                c = CostMeter.Stop(start);
            }
            list.Add((name, c));
        }
        return list;
    }

    private static string Stats(List<Cost> s)
    {
        string P(Func<Cost, long> f, string what, Func<long, string>? fmt = null)
        {
            var v = s.Select(f).OrderBy(x => x).ToList();
            fmt ??= x => x.ToString("N0", CultureInfo.InvariantCulture);
            return $"{what} p50 {fmt(v[v.Count / 2])} p99 {fmt(v[(int)(v.Count * 0.99)])} max {fmt(v[^1])}";
        }
        return string.Join(" | ",
            P(c => c.Bytes, "KB", x => (x / 1024.0).ToString("N1", CultureInfo.InvariantCulture)),
            P(c => c.Edges, "edges"), P(c => c.CoverageLookups, "coverage"), P(c => c.OffsetPoints, "offset pts"),
            P(c => c.ReadCalls, "reads"));
    }

    // About 300 x 400 m around the origin, a point every 2 m, the edges gently waving (a
    // ditch, a hedge) so the import keeps the points: about 1,400.
    private static string Kml(LocalPlane plane, string name)
    {
        var ring = new List<(double E, double N)>();
        double x0 = -FieldW / 2, x1 = FieldW / 2, y0 = -FieldL / 2, y1 = FieldL / 2;
        static double Wave(double t, double amp, double len) => amp * Math.Sin(2 * Math.PI * t / len);
        for (double x = x0; x < x1; x += 2) ring.Add((x, y0 + Wave(x - x0, 4, 50)));
        for (double y = y0; y < y1; y += 2) ring.Add((x1 + Wave(y - y0, 3, 80), y));
        for (double x = x1; x > x0; x -= 2) ring.Add((x, y1 + Wave(x - x0, 4, 50)));
        for (double y = y1; y > y0; y -= 2) ring.Add((x0 + Wave(y - y0, 3, 80), y));
        ring.Add(ring[0]);
        var inv = CultureInfo.InvariantCulture;
        var coords = string.Join(" ", ring.Select(p =>
        {
            var w = plane.ConvertGeoCoordToWgs84(new GeoCoord(p.N, p.E));
            return w.Longitude.ToString("F9", inv) + "," + w.Latitude.ToString("F9", inv) + ",0";
        }));
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<kml xmlns=\"http://www.opengis.net/kml/2.2\"><Document>\n" +
               $"<name>{name}</name>\n<Placemark><name>{name}</name><Polygon><outerBoundaryIs><LinearRing><coordinates>" +
               coords + "</coordinates></LinearRing></outerBoundaryIs></Polygon></Placemark>\n</Document></kml>\n";
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        foreach (var d in Directory.EnumerateDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }
}
