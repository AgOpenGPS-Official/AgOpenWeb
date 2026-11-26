// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace AgOpenWeb.VehicleToolScenarios.Tests;

[TestFixture, Category("Scenario"), Explicit("debug run")]
public class SmokeTests
{
    [Test]
    public async Task Puma_with_the_8m_sprayer_on_flat_ground()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vt-smoke-" + Guid.NewGuid().ToString("N")[..8]);
        var truth = new TruthMachine(Machines.Puma, Machines.Sprayer8, double.Parse(Environment.GetEnvironmentVariable("VT_SLOPE") ?? "0", CultureInfo.InvariantCulture), new SystemSpec(Environment.GetEnvironmentVariable("VT_GPS") ?? "um982", Environment.GetEnvironmentVariable("VT_IMU") ?? "tm171"));
        await using var run = await FieldRun.StartAsync(dir, truth);
        Assert.That(await run.MakeFieldAsync(), Is.True, string.Join("; ", run.Problems));
        await run.ApplyAsync(Machines.Puma.Settings(truth.Sys).Concat(Machines.Sprayer8.Settings()));
        await run.DrivePassesAsync();
        foreach (var p in run.Passes)
            TestContext.Out.WriteLine(FormattableString.Invariant(
                $"pass {p.Index + 1} {(p.Northbound ? "N" : "S")} target {p.TargetX:F2} mean {p.MeanX:F3} [{p.MinX:F3}..{p.MaxX:F3}] lineErr {p.LineError:F3} maxDev {p.MaxLineDev:F3} steerMax {p.MaxSteerCmd:F1} appTool side {p.AppToolSide:F3} along {p.AppToolAlong:F3} pivot {p.AppPivotErr:F3} hdg {p.AppHeadingErr:F2} on {p.SectionsOnShare:F2} pass# {p.AppPass} n {p.Samples} {p.Disengaged}"));
        var g = run.GapsAndOverlaps();
        TestContext.Out.WriteLine(FormattableString.Invariant($"gap {g.Gap:F3} overlap {g.Overlap:F3} mean {g.Mean:F3}"));
        foreach (var m in run.Messages) TestContext.Out.WriteLine("msg: " + m);
        foreach (var m in run.Problems) TestContext.Out.WriteLine("PROBLEM: " + m);
    }
}
