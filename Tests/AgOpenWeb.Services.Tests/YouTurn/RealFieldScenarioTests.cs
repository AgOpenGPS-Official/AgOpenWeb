// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Services.GeoJson;
using AgOpenWeb.Services.Geometry;
using NUnit.Framework;
using static AgOpenWeb.Services.Tests.YouTurn.ObstacleScenarioTests;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// The obstacle scenarios of <see cref="ObstacleScenarioTests"/> on real fields: every AgOpenWeb
/// field folder (<c>field.geojson</c>) under the folder named by AGOPENWEB_REAL_FIELDS, its own
/// AB lines and the headings listed in a <c>scenario.json</c> beside it
/// (<c>{"headings": [34], "treeMaxSize": 12, "headlandWidth": 18, "implements": ["rear 6", "tbt 12",
/// "trailing 3 -2.5", "trailing 12.5 0 7", "harvester 6 0 4"], "treeImplements": ["rear 3"]}</c>: the
/// implements as tool (rear, front, trailing, tbt, or harvester: a combine, its header a front tool
/// on a machine steering with its rear axle), width, offset to the right and hitch length in metres,
/// for the big obstacles and for the trees; without them the 3 m rigid and
/// trailed tools and the 8 m sprayer), each obstacle from both
/// ends, with the implements of a small farm (a 3 m mounted drill, a 3 m trailed one, an 8 m
/// sprayer), by the operator who does nothing wrong and by the one who does everything wrong.
/// Obstacles up to <c>treeMaxSize</c> across are trees: Soft U-turn obstacles in one run, Hard
/// Go around ones in the other; the larger ones (ditches, patches) are Hard U-turn obstacles.
/// Nothing runs when the variable is unset, since the fields are the farmer's own.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
[Category("Scenario")]
public class RealFieldScenarioTests
{
    private static readonly List<LegResult> Results = new();
    private static readonly Dictionary<string, Loaded> Fields = new();

    /// <summary>One implement of the matrix: the rig's tool type (rear, front, trailing, tbt, harvester), its
    /// width, its lateral offset and its hitch length (m; NaN for the rig's default).</summary>
    private sealed record Implement(string Tool, double Width, double Offset, double Hitch = double.NaN);

    private sealed record Setup(double[] Headings, double TreeMaxSize, double HeadlandWidth,
        Implement[] Implements, Implement[] TreeImplements);

    /// <summary>The implements when the field's <c>scenario.json</c> names none: Renato's 3 m rigid and trailed tools and his 8 m sprayer.</summary>
    private static readonly Implement[] DefaultImplements = { new("rear", 3, 0), new("trailing", 3, 0), new("rear", 8, 0) };

    private sealed class Loaded
    {
        public required string Name { get; init; }
        public required string Directory { get; init; }
        public required Setup Setup { get; init; }
        public required IReadOnlyList<(string Name, Models.Track.Track Track)> Tracks { get; init; }
        public required ObstacleTestField.Built UTurn { get; init; }
        public required ObstacleTestField.Built GoAround { get; init; }
        /// <summary>The field as built for listing the legs (never driven).</summary>
        public ObstacleTestField.Built Field(bool treesGoRound) => treesGoRound ? GoAround : UTurn;
        /// <summary>A fresh copy of the field for one leg: the rig and the bad operator change its obstacles.</summary>
        public ObstacleTestField.Built Fresh(bool treesGoRound) => Build(Directory, Setup, treesGoRound);
    }

    private static string? Root => Environment.GetEnvironmentVariable("AGOPENWEB_REAL_FIELDS");


    private static IEnumerable<Loaded> LoadAll()
    {
        if (string.IsNullOrEmpty(Root) || !Directory.Exists(Root)) yield break;
        foreach (var dir in Directory.GetDirectories(Root).OrderBy(d => d, StringComparer.Ordinal))
        {
            if (!File.Exists(Path.Combine(dir, "field.geojson"))) continue;
            string name = Path.GetFileName(dir);
            if (!Fields.TryGetValue(name, out var loaded))
                Fields[name] = loaded = Load(dir);
            yield return loaded;
        }
    }

    private static Loaded Load(string dir)
    {
        var setup = ReadSetup(dir);
        var (field, fileTracks) = GeoJsonFieldService.Load(dir);
        var outer = field.Boundary.OuterBoundary ?? throw new InvalidOperationException($"{dir}: no outer boundary");
        double minE = outer.Points.Min(p => p.Easting), maxE = outer.Points.Max(p => p.Easting);
        double minN = outer.Points.Min(p => p.Northing), maxN = outer.Points.Max(p => p.Northing);
        double span = Math.Sqrt((maxE - minE) * (maxE - minE) + (maxN - minN) * (maxN - minN));
        double ce = (minE + maxE) / 2, cn = (minN + maxN) / 2;

        // The field's own AB lines, laid out long enough to cross the whole field (the host extends
        // an AB line without end; the approach planner does not), and the headings the farmer drives.
        var tracks = new List<(string, Models.Track.Track)>();
        foreach (var t in fileTracks.Where(t => t.IsABLine))
        {
            double de = t.Points[1].Easting - t.Points[0].Easting, dn = t.Points[1].Northing - t.Points[0].Northing;
            double heading = Math.Atan2(de, dn);
            double me = (t.Points[0].Easting + t.Points[1].Easting) / 2, mn = (t.Points[0].Northing + t.Points[1].Northing) / 2;
            tracks.Add(($"{t.Name} (planned)", AbLine(t.Name, me, mn, heading, span)));
        }
        foreach (double deg in setup.Headings)
            tracks.Add(($"AB {deg.ToString("0", CultureInfo.InvariantCulture)}° (own)", AbLine($"AB {deg}°", ce, cn, deg * Math.PI / 180, span)));

        return new Loaded
        {
            Name = Path.GetFileName(dir),
            Directory = dir,
            Setup = setup,
            Tracks = tracks,
            UTurn = Build(dir, setup, treesGoRound: false),
            GoAround = Build(dir, setup, treesGoRound: true),
        };
    }

    private static Setup ReadSetup(string dir)
    {
        string path = Path.Combine(dir, "scenario.json");
        double[] headings = Array.Empty<double>();
        double treeMax = 12, headland = 18;
        Implement[] implements = DefaultImplements, treeImplements = null!;
        if (File.Exists(path))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("headings", out var h))
                headings = h.EnumerateArray().Select(x => x.GetDouble()).ToArray();
            if (doc.RootElement.TryGetProperty("treeMaxSize", out var t)) treeMax = t.GetDouble();
            if (doc.RootElement.TryGetProperty("headlandWidth", out var w)) headland = w.GetDouble();
            if (doc.RootElement.TryGetProperty("implements", out var im)) implements = Implements(im);
            if (doc.RootElement.TryGetProperty("treeImplements", out var ti)) treeImplements = Implements(ti);
        }
        return new Setup(headings, treeMax, headland, implements, treeImplements ?? implements);
    }

    // "rear 6", "trailing 3 -2.5", "trailing 12.5 0 7", "harvester 6 0 4": the tool, its width, its offset
    // to the right, and its hitch length (m).
    private static Implement[] Implements(JsonElement list) => list.EnumerateArray().Select(x =>
    {
        var parts = x.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        double Num(int i, double dflt) => i < parts.Length ? double.Parse(parts[i], CultureInfo.InvariantCulture) : dflt;
        return new Implement(parts[0], Num(1, 0), Num(2, 0), Num(3, double.NaN));
    }).ToArray();

    /// <summary>
    /// The field as the scenarios need it: every obstacle named and sized, trees (up to
    /// <c>treeMaxSize</c> across) as Soft U-turn or Hard Go around obstacles, the rest Hard U-turn
    /// ones; the file's drive-through choices kept. Loaded afresh for each build, since the rig
    /// changes the polygons' choices.
    /// </summary>
    private static ObstacleTestField.Built Build(string dir, Setup setup, bool treesGoRound)
    {
        var (field, _) = GeoJsonFieldService.Load(dir);
        var boundary = field.Boundary;
        boundary.OuterBoundary!.IsHard = true;
        var obstacles = new List<ObstacleTestField.Obstacle>();
        for (int i = 0; i < boundary.InnerBoundaries.Count; i++)
        {
            var p = boundary.InnerBoundaries[i];
            double minE = p.Points.Min(q => q.Easting), maxE = p.Points.Max(q => q.Easting);
            double minN = p.Points.Min(q => q.Northing), maxN = p.Points.Max(q => q.Northing);
            double size = Math.Max(maxE - minE, maxN - minN);
            bool tree = size <= setup.TreeMaxSize;
            if (p.AtObstacle != ObstacleAction.DriveThrough)
            {
                p.AtObstacle = tree && treesGoRound ? ObstacleAction.GoRound : ObstacleAction.Turn;
                p.IsHard = !tree || treesGoRound;
            }
            string kind = tree ? "tree" : "big";
            obstacles.Add(new ObstacleTestField.Obstacle($"Inner {i + 1} ({kind} {size.ToString("0.#", CultureInfo.InvariantCulture)} m)",
                p, (minE + maxE) / 2, (minN + maxN) / 2));
        }
        var headlandPolygon = boundary.HeadlandPolygon;
        if (headlandPolygon == null || !headlandPolygon.IsValid)
        {
            var ring = new PolygonOffsetService().CreateInwardOffset(
                boundary.OuterBoundary.Points.Select(q => new Vec2(q.Easting, q.Northing)).ToList(), setup.HeadlandWidth)
                ?? throw new InvalidOperationException($"{dir}: no headland line");
            headlandPolygon = ObstacleTestField.Polygon(ring.Select(q => (q.Easting, q.Northing)).ToArray());
            boundary.HeadlandPolygon = headlandPolygon;
        }
        return new ObstacleTestField.Built
        {
            Boundary = boundary,
            HeadlandLine = headlandPolygon.Points.Select(q => new Vec3(q.Easting, q.Northing, 0)).ToList(),
            Obstacles = obstacles,
        };
    }

    private static Models.Track.Track AbLine(string name, double e, double n, double heading, double halfLength)
    {
        double se = Math.Sin(heading), ce = Math.Cos(heading);
        return Models.Track.Track.FromABLine(name, new Vec3(e - halfLength * se, n - halfLength * ce, 0),
            new Vec3(e + halfLength * se, n + halfLength * ce, 0));
    }

    private static string LegObstacle(Loaded f, ObstacleTestField.Obstacle o) => $"{f.Name}: {o.Name}";

    public static IEnumerable<TestCaseData> NormalLegs()
    {
        foreach (var f in LoadAll())
            foreach (bool treesGoRound in new[] { false, true })
                foreach (var o in f.Field(treesGoRound).AvoidedObstacles)
                    foreach (var (trackName, _) in f.Tracks)
                        foreach (bool forward in new[] { true, false })
                            foreach (var (tool, width, offset, hitch) in o.Name.Contains("(tree ") ? f.Setup.TreeImplements : f.Setup.Implements)
                            {
                                var leg = new Leg(LegObstacle(f, o), trackName, forward, tool, width, offset, -1, treesGoRound, hitch);
                                if (Wanted(leg)) yield return new TestCaseData(leg).SetName($"Field({leg})");
                            }
    }

    public static IEnumerable<TestCaseData> BadLegs()
    {
        int seed = 1000;
        foreach (var f in LoadAll())
            foreach (bool treesGoRound in new[] { false, true })
                foreach (var o in f.Field(treesGoRound).AvoidedObstacles)
                    foreach (var (trackName, _) in f.Tracks)
                        for (int k = 0; k < 2; k++)
                        {
                            var leg = new Leg(LegObstacle(f, o), trackName, k == 0, "rear", 3, 0, seed++, treesGoRound);
                            if (Wanted(leg)) yield return new TestCaseData(leg).SetName($"FieldBad({leg})");
                        }
    }

    [TestCaseSource(nameof(NormalLegs))]
    public void TheFarmerWhoDoesNothingWrong(Leg leg) => Drive(leg);

    [TestCaseSource(nameof(BadLegs))]
    public void TheFarmerWhoDoesEverythingWrong(Leg leg) => Drive(leg);

    private static void Drive(Leg leg)
    {
        string fieldName = leg.Obstacle[..leg.Obstacle.IndexOf(':')];
        var f = Fields.TryGetValue(fieldName, out var loaded) ? loaded : LoadAll().First(x => x.Name == fieldName);
        var field = f.Fresh(leg.TreesGoRound);
        var shared = f.Tracks.First(t => t.Name == leg.Track).Track;
        // A copy of the line per leg, like the field: nothing one leg does may touch the next one's line.
        var track = shared.IsABLine
            ? Models.Track.Track.FromABLine(shared.Name, shared.Points[0], shared.Points[1])
            : Models.Track.Track.FromCurve(shared.Name, shared.Points.Select(p => new Vec3(p.Easting, p.Northing, p.Heading)).ToList());
        // Run looks the obstacle up by its name on the field.
        var onField = leg with { Obstacle = leg.Obstacle[(fieldName.Length + 2)..] };
        var r = Run(onField, field, track, Results, f.Setup.HeadlandWidth);
        if (leg.Seed < 0)
            Assert.That(r.Outcome, Is.Not.EqualTo("passed it").And.Not.EqualTo("timeout"), "the pass never met the obstacle");
    }

    [Test]
    public void TheFields_AreSane()
    {
        var all = LoadAll().ToList();
        if (all.Count == 0) Assert.Ignore("set AGOPENWEB_REAL_FIELDS to a folder of AgOpenWeb field folders");
        foreach (var f in all)
        {
            var field = f.UTurn;
            Assert.That(field.Outer.IsValid, Is.True, f.Name);
            Assert.That(field.Boundary.HeadlandPolygon, Is.Not.Null, f.Name);
            Assert.That(f.Tracks.Count, Is.GreaterThan(0), f.Name);
            TestContext.Out.WriteLine($"{f.Name}: {field.Outer.AreaHectares:F1} ha, {field.Obstacles.Count} obstacles " +
                $"({field.Obstacles.Count(o => o.Name.Contains("(tree"))} trees), tracks: {string.Join(", ", f.Tracks.Select(t => t.Name))}");
        }
    }

    [OneTimeTearDown]
    public void WriteReport()
    {
        string? path = Environment.GetEnvironmentVariable("AGOPENWEB_SCENARIO_REPORT");
        if (string.IsNullOrEmpty(path) || Results.Count == 0) return;
        File.WriteAllText(path, ScenarioReport.Build(Results, "Obstacle scenarios on real fields"));
    }
}
