using System.Collections.Generic;

using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.Guidance;
using AgValoniaGPS.Models.Tool;
using NUnit.Framework;

namespace AgValoniaGPS.Models.Tests;

[TestFixture]
public class TurnClearanceTests
{
    // 100 x 100 m square boundary.
    private static List<Vec2> Square() => new()
    {
        new Vec2(0, 0), new Vec2(100, 0), new Vec2(100, 100), new Vec2(0, 100)
    };

    private static IEnumerable<Vec3> P(params (double e, double n)[] pts)
    {
        foreach (var (e, n) in pts) yield return new Vec3(e, n, 0);
    }

    [Test]
    public void Inside_WellClear_IsClear()
    {
        var r = TurnClearance.Evaluate(P((50, 50)), Square(), TurnClearance.KeepSide.Inside, margin: 5);
        Assert.That(r.IsClear, Is.True);
        Assert.That(r.MaxIntrusion, Is.LessThan(0));
    }

    [Test]
    public void Inside_PointOutsideBoundary_IsNotClear()
    {
        // 1 m outside the right edge, margin 5 -> intrusion = 5 + 1 = 6.
        var r = TurnClearance.Evaluate(P((101, 50)), Square(), TurnClearance.KeepSide.Inside, margin: 5);
        Assert.That(r.IsClear, Is.False);
        Assert.That(r.MaxIntrusion, Is.EqualTo(6.0).Within(1e-6));
    }

    [Test]
    public void Inside_WithinMarginOfEdge_IsNotClear()
    {
        // 2 m inside the top edge, margin 5 -> intrusion = 5 - 2 = 3.
        var r = TurnClearance.Evaluate(P((50, 98)), Square(), TurnClearance.KeepSide.Inside, margin: 5);
        Assert.That(r.IsClear, Is.False);
        Assert.That(r.MaxIntrusion, Is.EqualTo(3.0).Within(1e-6));
    }

    [Test]
    public void Outside_ExclusionFarAway_IsClear()
    {
        var r = TurnClearance.Evaluate(P((200, 200)), Square(), TurnClearance.KeepSide.Outside, margin: 5);
        Assert.That(r.IsClear, Is.True);
    }

    [Test]
    public void Outside_PointInsideExclusion_IsNotClear()
    {
        var r = TurnClearance.Evaluate(P((50, 50)), Square(), TurnClearance.KeepSide.Outside, margin: 5);
        Assert.That(r.IsClear, Is.False);
        Assert.That(r.MaxIntrusion, Is.GreaterThan(0));
    }

    [Test]
    public void LargerMargin_ReducesClearance()
    {
        // 4 m inside the edge: clear at margin 2, not clear at margin 6.
        var pts = P((50, 96));
        var ok = TurnClearance.Evaluate(P((50, 96)), Square(), TurnClearance.KeepSide.Inside, margin: 2);
        var bad = TurnClearance.Evaluate(P((50, 96)), Square(), TurnClearance.KeepSide.Inside, margin: 6);
        Assert.That(ok.IsClear, Is.True);
        Assert.That(bad.IsClear, Is.False);
    }

    [Test]
    public void SweptFootprint_RearCornerPokingOut_IsNotClear()
    {
        // A short north path near the top edge; a long rear-fixed tool whose rear
        // corners extend past the top boundary should be flagged.
        var path = new List<Vec3>();
        for (int i = 0; i < 10; i++) path.Add(new Vec3(50, 95 + i * 0.2, 0)); // ends ~96.8, heading north
        var geom = new ToolGeometry(ToolMount.RearFixed, Width: 4, Offset: 0,
            VehicleHitchLength: 0, ToolHitchLength: 0, TrailingHitchLength: 0,
            TrailingToolToPivotLength: 0, TankTrailingHitchLength: 0, Length: 0);

        // Front-fixed so the body extends NORTH (toward the top edge) past 100.
        var frontGeom = geom with { Mount = ToolMount.FrontFixed, ToolHitchLength = 2, Length = 6 };
        var swept = ImplementSweptPath.Compute(path, frontGeom);

        var r = TurnClearance.Evaluate(swept, Square(), TurnClearance.KeepSide.Inside, margin: 0.5);
        Assert.That(r.IsClear, Is.False, "rear/far corners extending past the boundary should not be clear");
    }

    [Test]
    public void DegeneratePolygon_IsTreatedAsClear()
    {
        var r = TurnClearance.Evaluate(P((50, 50)), new List<Vec2> { new(0, 0), new(1, 1) },
            TurnClearance.KeepSide.Inside, margin: 5);
        Assert.That(r.IsClear, Is.True);
    }
}
