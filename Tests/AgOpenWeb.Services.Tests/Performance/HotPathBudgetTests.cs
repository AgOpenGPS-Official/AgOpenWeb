// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Services.Tests.Performance;

/// <summary>
/// Work budgets for the two paths that run all the time on the tractor: the GPS cycle (10 Hz)
/// and the control tick (100 Hz). Each scenario drives <see cref="HotPathHarness"/> up a pass
/// of a large field into the headland and holds every cycle to a budget of
/// <see cref="WorkCounters"/>: polygon edges visited, coverage lookups, points offset, bytes
/// allocated. Counts are the same on every machine, so this never flaps in CI the way timing
/// would. A pass scan that ran every cycle on a finished field (#423) cost 209 ms per cycle
/// while all other tests passed; it fails here.
///
/// A budget failure means new work runs per cycle. Do it once (when a turn is planned, when
/// the field or coverage changes) and keep the result, rather than raising the budget.
/// <see cref="Report"/> prints each scenario's counts.
/// </summary>
[TestFixture]
[Category("HotPath")] // CI runs these in their own job, in Release (hot-path-budgets)
[NonParallelizable] // ConfigurationStore is a singleton.
public class HotPathBudgetTests
{
    // GPS cycle. Measured 2026-10-25 (#430): 1,444 edges with a headland, nearly all of them
    // the headland distance's raycast over the 1,400-point headland line (the boundary, the
    // U-turn zone, the hydraulic lift and the headland distance's inside test go through strip
    // indexes; before them it was 2,828 plus the raycast, uncounted then), 14 with the boundary
    // only; 2,002 offset points on the 1,000-point curve; 93 KB allocated there, 5 KB on a
    // line (the headland line is no longer copied every cycle). One full walk of the headland
    // line goes over.
    private const long FixEdges = 2_000;
    private const long FixCoverageLookups = 2_000;
    private const long FixOffsetCurvePoints = 5_000;
    private const long FixPolygonOffsetPoints = 1_000;
    private const long FixAllocatedBytes = 128 * 1024;

    // Planning a turn is a one-off: up to 47,000 edges (the curve; 2,700 on a line) and 3.6 MB
    // (alternate skip). Before the turn line, headland and pass areas had strip indexes it was
    // up to 1.9 M edges. A few cycles may go over the steady budget, none past the peak.
    private const int FixesOverBudgetAllowed = 5;
    private const long FixPeakEdges = 100_000;
    private const long FixPeakAllocatedBytes = 16 * 1024 * 1024;

    // Control tick (tool position + section control). Measured 2026-10-25: 216 edges a tick
    // mid-field, 6,582 near the field's edge (the boundary and headland tests go through strip
    // indexes; before them it was 25,344 and 117,262), 360 coverage lookups.
    private const long TickEdges = 20_000;
    private const long TickCoverageLookups = 2_000;
    private const long TickAllocatedBytes = 512 * 1024;
    // The first paint of an empty job allocates the coverage bitmaps (48 MB over a few ticks).
    private const int TicksOverAllocBudgetAllowed = 5;

    private static IEnumerable<TestCaseData> Scenarios()
    {
        foreach (var track in Enum.GetValues<PassTrack>())
        foreach (var skip in Enum.GetValues<Skip>())
        foreach (var worked in new[] { false, true })
        foreach (var headland in new[] { true, false })
            yield return new TestCaseData(track, skip, worked, headland)
                .SetArgDisplayNames(track.ToString(), skip.ToString(), worked ? "finished field" : "empty field",
                    headland ? "headland" : "boundary only");
    }

    // 100 m before the headland line (226 m), through the turn's planning and trigger into
    // the headland, then 50 fixes at the end of the pass.
    private static HotPathHarness Drive(PassTrack track, Skip skip, bool worked, bool headland)
    {
        var h = new HotPathHarness(track, skip, worked, headland);
        h.Drive(126, 240, waitFixes: 50);
        return h;
    }

    [TestCaseSource(nameof(Scenarios))]
    public void Cycles_stay_within_budget(PassTrack track, Skip skip, bool worked, bool headland)
    {
        using var h = Drive(track, skip, worked, headland);
        CheckGpsCycles(h);
        CheckControlTicks(h);
    }

    private static void CheckGpsCycles(HotPathHarness h)
    {
        var over = h.Fixes.Select((c, i) => (c, i)).Where(x =>
            x.c.PolygonEdges > FixEdges || x.c.CoverageLookups > FixCoverageLookups
            || x.c.OffsetCurvePoints > FixOffsetCurvePoints || x.c.PolygonOffsetPoints > FixPolygonOffsetPoints
            || x.c.AllocatedBytes > FixAllocatedBytes).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(over.Count, Is.LessThanOrEqualTo(FixesOverBudgetAllowed),
                $"{over.Count} of {h.Fixes.Count} GPS cycles over the per-cycle budget; work that " +
                $"should happen once is running every cycle. First: {Describe(over.Take(3))}");
            Assert.That(h.Fixes.Max(c => c.PolygonEdges), Is.LessThanOrEqualTo(FixPeakEdges), "peak polygon edges in one GPS cycle");
            Assert.That(h.Fixes.Max(c => c.AllocatedBytes), Is.LessThanOrEqualTo(FixPeakAllocatedBytes), "peak bytes allocated in one GPS cycle");
        });
    }

    private static void CheckControlTicks(HotPathHarness h)
    {
        var t = h.ControlTicks;
        Assert.Multiple(() =>
        {
            Assert.That(t.Max(c => c.PolygonEdges), Is.LessThanOrEqualTo(TickEdges), "polygon edges in one control tick");
            Assert.That(t.Max(c => c.CoverageLookups), Is.LessThanOrEqualTo(TickCoverageLookups), "coverage lookups in one control tick");
            Assert.That(t.Max(c => c.OffsetCurvePoints + c.PolygonOffsetPoints), Is.Zero, "the control tick never offsets curves or polygons");
            Assert.That(t.Count(c => c.AllocatedBytes > TickAllocatedBytes), Is.LessThanOrEqualTo(TicksOverAllocBudgetAllowed),
                "control ticks allocating more than the budget");
        });
    }

    private static string Describe(IEnumerable<(WorkCounters.Snapshot c, int i)> cycles) =>
        string.Join("; ", cycles.Select(x =>
            $"cycle {x.i}: {x.c.PolygonEdges:N0} edges, {x.c.CoverageLookups:N0} coverage lookups, " +
            $"{x.c.OffsetCurvePoints + x.c.PolygonOffsetPoints:N0} offset points, {x.c.AllocatedBytes / 1024:N0} KB"));

    /// <summary>Each scenario's counts per cycle: run it to see where a budget stands.</summary>
    [Test, Explicit("prints counts; no assertions")]
    public void Report()
    {
        foreach (var data in Scenarios())
        {
            var (track, skip, worked, headland) = ((PassTrack)data.Arguments[0]!, (Skip)data.Arguments[1]!,
                (bool)data.Arguments[2]!, (bool)data.Arguments[3]!);
            using var h = Drive(track, skip, worked, headland);
            var y = h.Last?.YouTurn;
            TestContext.Out.WriteLine($"{track} {skip} {(worked ? "finished" : "empty")} {(headland ? "headland" : "boundary only")}" +
                $" (turn planned {y?.TurnPath != null}, triggered {y?.IsTriggered})");
            TestContext.Out.WriteLine("  GPS cycle    " + Stats(h.Fixes));
            TestContext.Out.WriteLine("  control tick " + Stats(h.ControlTicks));
        }
    }

    private static string Stats(List<WorkCounters.Snapshot> s)
    {
        string P(Func<WorkCounters.Snapshot, long> f, string name)
        {
            var v = s.Select(f).OrderBy(x => x).ToList();
            return $"{name} p50 {v[v.Count / 2]:N0} max {v[^1]:N0}";
        }
        return string.Join(" | ", P(c => c.PolygonEdges, "edges"), P(c => c.CoverageLookups, "coverage"),
            P(c => c.OffsetCurvePoints + c.PolygonOffsetPoints, "offset pts"), P(c => c.AllocatedBytes, "bytes"));
    }
}
