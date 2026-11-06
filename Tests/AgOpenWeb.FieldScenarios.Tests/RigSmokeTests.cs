// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.FieldScenarios.Tests;

[TestFixture, Category("Scenario"), NonParallelizable]
public class RigSmokeTests
{
    [Test]
    public async Task The_backend_boots_and_makes_a_field_with_a_drawn_boundary()
    {
        var root = Path.Combine(Path.GetTempPath(), "aow-scenario-smoke-" + Guid.NewGuid().ToString("N")[..8]);
        await using var rig = await FieldScenarioRig.StartAsync(root);
        var field = ScenarioFields.Get("Olival");
        var inside = field.InsidePoint();
        await rig.PlaceAsync(inside.Lat, inside.Lon, 0);
        await rig.SendAsync("field.new", "Smoke");
        var opened = await rig.WaitUntilAsync(() => rig.Vm.IsFieldOpen && !rig.State.UI.IsBusy);
        TestContext.Out.WriteLine(string.Join("\n", rig.TakeEvents()));
        Assert.That(opened, Is.True);

        var pts = new List<string>();
        foreach (var p in field.Outer)
        {
            var (e, n) = await rig.ToFieldAsync(p.Lat, p.Lon);
            pts.Add(FormattableString.Invariant($"{e:F3},{n:F3}"));
        }
        await rig.SendAsync("boundary.fromMapPoints", string.Join(";", pts));
        var count = await rig.ReadAsync(() => rig.State.Field.CurrentBoundary?.OuterBoundary?.Points.Count ?? 0);
        TestContext.Out.WriteLine(string.Join("\n", rig.TakeEvents()));
        Assert.That(count, Is.EqualTo(field.Outer.Count));
    }
}
