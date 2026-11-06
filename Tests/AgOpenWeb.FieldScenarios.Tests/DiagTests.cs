// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace AgOpenWeb.FieldScenarios.Tests;

/// <summary>Scratch experiments used while confirming findings (Explicit: never in a normal run).</summary>
[TestFixture, Explicit, Category("Scenario"), NonParallelizable]
public class DiagTests
{
    [Test]
    public async Task Headland_on_a_field_with_obstacles()
    {
        var name = Environment.GetEnvironmentVariable("DIAG_FIELD") ?? "Baetas";
        bool obstacles = Environment.GetEnvironmentVariable("DIAG_NO_OBSTACLES") != "1";
        var root = Path.Combine(Path.GetTempPath(), "aow-diag-" + Guid.NewGuid().ToString("N")[..8]);
        await using var rig = await FieldScenarioRig.StartAsync(root);
        var j = new ScenarioJournal();
        var s = new Scenario(rig, j, "diag", "careful");
        var op = new DiagOp(s);
        var f = ScenarioFields.Get(name);
        await op.Make(f, obstacles);
        await rig.SendAsync("headland.wholeBoundary", "12");
        var snap = await rig.ReadAsync(() => FieldSnapshot.Take(rig));
        var inv = CultureInfo.InvariantCulture;
        TestContext.Out.WriteLine(string.Join("\n", rig.TakeEvents()));
        TestContext.Out.WriteLine(snap.Summary());
        var outer = snap.Outer!;
        var hl = snap.Headland!;
        TestContext.Out.WriteLine(string.Create(inv, $"outer {outer.Count} pts area {Poly.Area(outer):F0}; headland {hl.Count} pts area {Poly.Area(hl):F0}"));
        for (int i = 0; i < hl.Count; i++)
        {
            double d = Poly.DistToRing(hl[i], outer);
            if (d < 11.5 || i % 25 == 0)
                TestContext.Out.WriteLine(string.Create(inv, $"  hl[{i}] {hl[i].E:F2},{hl[i].N:F2}  dist to outer {d:F2}"));
        }
        if (Poly.SelfCrossing(hl) is { } x)
            TestContext.Out.WriteLine(string.Create(inv, $"self-crossing edges {x.I}/{x.J} at {x.At.E:F1},{x.At.N:F1}"));
        for (int k = 0; k < snap.Inner.Count; k++)
        {
            double d = snap.Inner[k].Min(p => Poly.DistToRing(p, outer));
            TestContext.Out.WriteLine(string.Create(inv, $"inner {k + 1}: {snap.Inner[k].Count} pts, {Poly.Area(snap.Inner[k]):F0} m², nearest to outer {d:F1} m, driveThrough {snap.InnerFlags[k].DriveThrough}"));
        }
        if (Poly.SelfCrossing(outer) is { } ox)
            TestContext.Out.WriteLine(string.Create(inv, $"OUTER self-crossing edges {ox.I}/{ox.J} at {ox.At.E:F1},{ox.At.N:F1}"));
    }

    [Test]
    public async Task Heading_after_rolling_in()
    {
        var root = Path.Combine(Path.GetTempPath(), "aow-diag-" + Guid.NewGuid().ToString("N")[..8]);
        await using var rig = await FieldScenarioRig.StartAsync(root);
        var f = ScenarioFields.Get("Olival");
        var p = f.InsidePoint();
        var inv = CultureInfo.InvariantCulture;
        foreach (var h in new[] { 0.0, 30, 60, 90, 135, 200, 300 })
        {
            await rig.PlaceAsync(p.Lat, p.Lon, h);
            var a = await rig.ReadAsync(() => rig.State.Vehicle.Heading);
            await Task.Delay(500);
            var b = await rig.ReadAsync(() => rig.State.Vehicle.Heading);
            await rig.DriveAsync(new[] { p, Geo.Offset(p, Math.Sin(h * Math.PI / 180) * 30, Math.Cos(h * Math.PI / 180) * 30) });
            var c = await rig.ReadAsync(() => rig.State.Vehicle.Heading);
            TestContext.Out.WriteLine(string.Create(inv, $"want {h:F1}: after roll-in {a:F2}, 0.5 s later {b:F2}, after driving 30 m {c:F2}"));
            p = f.InsidePoint();
        }
    }

    private sealed class DiagOp(Scenario s) : Operator(s)
    {
        public async Task Make(ScenarioField f, bool obstacles)
        {
            await SetUpSprayer();
            await WriteImportFile("diag.kml", f.ToKml(obstacles, "Diag"));
            var p = f.InsidePoint();
            await R.PlaceAsync(p.Lat, p.Lon, 0);
            await Cmd("field.fromKml", "diag.kml\tDiag");
            await R.WaitUntilAsync(() => R.Vm.CurrentFieldName == "Diag" && !R.State.UI.IsBusy);
        }
    }
}
