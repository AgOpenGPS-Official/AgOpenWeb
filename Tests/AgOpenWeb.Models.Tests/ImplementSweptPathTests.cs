using System;
using System.Collections.Generic;
using System.Linq;

using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Tool;
using NUnit.Framework;

namespace AgOpenWeb.Models.Tests;

[TestFixture]
public class ImplementSweptPathTests
{
    // ---- helpers --------------------------------------------------------

    private static ToolGeometry Geom(ToolMount mount, double width = 4.0, double offset = 0.0,
        double vehicleHitch = 1.0, double toolHitch = 2.0,
        double trailingHitch = 3.0, double toolToPivot = 0.0, double tankHitch = 2.5,
        double length = 0.0)
        => new(mount, width, offset, vehicleHitch, toolHitch, trailingHitch, toolToPivot, tankHitch, length);

    /// <summary>Straight path heading north (E=0), points 0.5 m apart.</summary>
    private static List<Vec3> StraightNorth(int count = 40, double spacing = 0.5)
    {
        var p = new List<Vec3>();
        for (int i = 0; i < count; i++) p.Add(new Vec3(0, i * spacing, 0));
        return p;
    }

    /// <summary>Arc turning left (CCW) of given radius, centre on the +E side... </summary>
    private static List<Vec3> LeftArc(double radius, double sweep, int count)
    {
        // Start at origin heading north; left turn => centre at (-radius, 0).
        var p = new List<Vec3>();
        double cx = -radius, cy = 0;
        for (int i = 0; i < count; i++)
        {
            double a = sweep * i / (count - 1);            // 0..sweep
            // position rotates CCW about centre starting from origin
            double e = cx + radius * Math.Cos(a);
            double n = cy + radius * Math.Sin(a);
            p.Add(new Vec3(e, n, 0));
        }
        return p;
    }

    /// <summary>Arc turning right (CW) of given radius, centre on the +E side.</summary>
    private static List<Vec3> RightArc(double radius, double sweep, int count)
    {
        // Start at origin heading north; right turn => centre at (+radius, 0).
        var p = new List<Vec3>();
        for (int i = 0; i < count; i++)
        {
            double a = sweep * i / (count - 1);            // 0..sweep
            double e = radius - radius * Math.Cos(a);      // mirror of LeftArc about the N axis
            double n = radius * Math.Sin(a);
            p.Add(new Vec3(e, n, 0));
        }
        return p;
    }

    private static double Dist(Vec3 a, Vec3 b)
        => Math.Sqrt((a.Easting - b.Easting) * (a.Easting - b.Easting) + (a.Northing - b.Northing) * (a.Northing - b.Northing));

    // ---- tests ----------------------------------------------------------

    [Test]
    public void EmptyPath_ReturnsEmptyResult()
    {
        var r = ImplementSweptPath.Compute(new List<Vec3>(), Geom(ToolMount.RearFixed));
        Assert.That(r.ToolCenter, Is.Empty);
        Assert.That(r.LeftEdge, Is.Empty);
        Assert.That(r.RightEdge, Is.Empty);
    }

    [Test]
    public void EdgeSeparation_EqualsWidth()
    {
        var path = StraightNorth();
        var r = ImplementSweptPath.Compute(path, Geom(ToolMount.RearFixed, width: 6.0));
        int mid = r.ToolCenter.Count / 2;
        Assert.That(Dist(r.LeftEdge[mid], r.RightEdge[mid]), Is.EqualTo(6.0).Within(1e-6));
    }

    [Test]
    public void RearFixed_Straight_ToolSitsBehindByHitch_EdgesSquare()
    {
        var path = StraightNorth();
        var r = ImplementSweptPath.Compute(path, Geom(ToolMount.RearFixed, width: 4.0, toolHitch: 2.0));
        int mid = r.ToolCenter.Count / 2;

        // Heading north: tool centre is hitch (behind pivot by 2 m), E stays 0.
        Assert.That(r.ToolCenter[mid].Easting, Is.EqualTo(0.0).Within(1e-6));
        Assert.That(r.ToolCenter[mid].Northing, Is.EqualTo(path[mid].Northing - 2.0).Within(1e-6));
        // Edges straddle in E by ±halfWidth.
        Assert.That(r.LeftEdge[mid].Easting, Is.EqualTo(-2.0).Within(1e-6));
        Assert.That(r.RightEdge[mid].Easting, Is.EqualTo(+2.0).Within(1e-6));
    }

    [Test]
    public void FrontFixed_Straight_ToolSitsAheadByHitch()
    {
        var path = StraightNorth();
        var r = ImplementSweptPath.Compute(path, Geom(ToolMount.FrontFixed, toolHitch: 2.0));
        int mid = r.ToolCenter.Count / 2;
        Assert.That(r.ToolCenter[mid].Northing, Is.EqualTo(path[mid].Northing + 2.0).Within(1e-6));
    }

    [Test]
    public void Trailing_Straight_NoOffTracking_EdgesSquare()
    {
        var path = StraightNorth();
        var r = ImplementSweptPath.Compute(path, Geom(ToolMount.Trailing, width: 4.0));
        int last = r.ToolCenter.Count - 1;

        // On a straight line the trailed tool aligns directly behind: E ≈ 0.
        Assert.That(r.ToolCenter[last].Easting, Is.EqualTo(0.0).Within(1e-6));
        Assert.That(r.LeftEdge[last].Easting, Is.EqualTo(-2.0).Within(1e-6));
        Assert.That(r.RightEdge[last].Easting, Is.EqualTo(+2.0).Within(1e-6));
    }

    [Test]
    public void Offset_ShiftsToolCentreRight()
    {
        var path = StraightNorth();
        var r = ImplementSweptPath.Compute(path, Geom(ToolMount.RearFixed, width: 4.0, offset: 1.0));
        int mid = r.ToolCenter.Count / 2;
        Assert.That(r.ToolCenter[mid].Easting, Is.EqualTo(1.0).Within(1e-6));
        Assert.That(r.LeftEdge[mid].Easting, Is.EqualTo(-1.0).Within(1e-6));
        Assert.That(r.RightEdge[mid].Easting, Is.EqualTo(3.0).Within(1e-6));
    }

    [Test]
    public void LeftOffset_ShiftsToolCentreLeft()
    {
        // Mirror of the right-offset case: a negative offset shifts the tool left (-E heading north).
        var path = StraightNorth();
        var r = ImplementSweptPath.Compute(path, Geom(ToolMount.RearFixed, width: 4.0, offset: -1.0));
        int mid = r.ToolCenter.Count / 2;
        Assert.That(r.ToolCenter[mid].Easting, Is.EqualTo(-1.0).Within(1e-6));
        Assert.That(r.LeftEdge[mid].Easting, Is.EqualTo(-3.0).Within(1e-6));
        Assert.That(r.RightEdge[mid].Easting, Is.EqualTo(1.0).Within(1e-6));
    }

    [Test]
    public void Offset_OnTurn_ChangesOuterEdgeReach()
    {
        // On a left turn the right edge is the outer one. An offset toward the
        // outside (right, +) must make that outer edge reach further; an offset
        // toward the inside (left, -) must pull it in. This is what the clearance
        // engine relies on for offset implements near a hard boundary.
        double radius = 10.0;
        var path = LeftArc(radius, Math.PI / 2.0, 120);
        var centre = new Vec3(-radius, 0, 0);

        double OuterReach(double offset)
        {
            var r = ImplementSweptPath.Compute(path, Geom(ToolMount.RearFixed, width: 4.0, offset: offset));
            int last = r.RightEdge.Count - 1;        // right = outer on a left turn
            return Dist(r.RightEdge[last], centre);
        }

        double outward = OuterReach(1.0);
        double none = OuterReach(0.0);
        double inward = OuterReach(-1.0);

        Assert.That(outward, Is.GreaterThan(none), "offset toward outside should widen the outer reach");
        Assert.That(none, Is.GreaterThan(inward), "offset toward inside should reduce the outer reach");
        // The shift should be ~the offset magnitude (radial on a circular arc).
        Assert.That(outward - none, Is.EqualTo(1.0).Within(0.05));
    }

    [Test]
    public void Trailing_OnLeftTurn_ToolOffTracksToTheInside()
    {
        // Quarter-circle left turn, radius 10. The trailed tool should cut to the
        // inside of the turn: its pivot/centre radius from the turn centre is
        // smaller than the vehicle pivot radius (10).
        double radius = 10.0;
        var path = LeftArc(radius, Math.PI / 2.0, 120);
        var centre = new Vec3(-radius, 0, 0);

        var r = ImplementSweptPath.Compute(path, Geom(ToolMount.Trailing, width: 4.0,
            vehicleHitch: 1.0, trailingHitch: 4.0));

        int last = r.ToolCenter.Count - 1;
        double toolRadius = Dist(r.ToolCenter[last], centre);
        Assert.That(toolRadius, Is.LessThan(radius - 0.5),
            "trailing tool should off-track to the inside of the turn");
    }

    [Test]
    public void Trailing_OnLeftTurn_OuterEdgeSweepsWiderThanInnerEdge()
    {
        // The outer (right, for a left turn) edge should trace a larger radius
        // than the inner (left) edge — this wide swing is what catches a fence.
        double radius = 10.0;
        var path = LeftArc(radius, Math.PI / 2.0, 120);
        var centre = new Vec3(-radius, 0, 0);

        var r = ImplementSweptPath.Compute(path, Geom(ToolMount.Trailing, width: 6.0,
            vehicleHitch: 1.0, trailingHitch: 4.0));
        int last = r.ToolCenter.Count - 1;

        double outerR = Dist(r.RightEdge[last], centre); // right = outside on a left turn
        double innerR = Dist(r.LeftEdge[last], centre);
        Assert.That(outerR, Is.GreaterThan(innerR), "outer edge should sweep wider than inner edge");
    }

    [Test]
    public void Trailing_OnRightTurn_ToolOffTracksToTheInside()
    {
        // Mirror of the left-turn case: right turn => inside is the +E side,
        // centre at (+radius, 0). Trailed tool should cut inside (smaller radius).
        double radius = 10.0;
        var path = RightArc(radius, Math.PI / 2.0, 120);
        var centre = new Vec3(radius, 0, 0);

        var r = ImplementSweptPath.Compute(path, Geom(ToolMount.Trailing, width: 4.0,
            vehicleHitch: 1.0, trailingHitch: 4.0));

        int last = r.ToolCenter.Count - 1;
        double toolRadius = Dist(r.ToolCenter[last], centre);
        Assert.That(toolRadius, Is.LessThan(radius - 0.5),
            "trailing tool should off-track to the inside of a right turn too");
    }

    [Test]
    public void Trailing_OnRightTurn_OuterEdgeSweepsWiderThanInnerEdge()
    {
        // On a right turn the LEFT edge is on the outside, so it should sweep wider.
        double radius = 10.0;
        var path = RightArc(radius, Math.PI / 2.0, 120);
        var centre = new Vec3(radius, 0, 0);

        var r = ImplementSweptPath.Compute(path, Geom(ToolMount.Trailing, width: 6.0,
            vehicleHitch: 1.0, trailingHitch: 4.0));
        int last = r.ToolCenter.Count - 1;

        double outerR = Dist(r.LeftEdge[last], centre);  // left = outside on a right turn
        double innerR = Dist(r.RightEdge[last], centre);
        Assert.That(outerR, Is.GreaterThan(innerR), "outer (left) edge should sweep wider on a right turn");
    }

    [Test]
    public void OffTracking_IsSymmetric_LeftVsRight()
    {
        // The amount the tool cuts inside must be the same magnitude for a left
        // and a right turn of equal radius — no left/right bias in the kinematics.
        double radius = 10.0;
        var left = ImplementSweptPath.Compute(LeftArc(radius, Math.PI / 2.0, 120),
            Geom(ToolMount.Trailing, width: 4.0, vehicleHitch: 1.0, trailingHitch: 4.0));
        var right = ImplementSweptPath.Compute(RightArc(radius, Math.PI / 2.0, 120),
            Geom(ToolMount.Trailing, width: 4.0, vehicleHitch: 1.0, trailingHitch: 4.0));

        int last = left.ToolCenter.Count - 1;
        double leftCut = radius - Dist(left.ToolCenter[last], new Vec3(-radius, 0, 0));
        double rightCut = radius - Dist(right.ToolCenter[last], new Vec3(radius, 0, 0));
        Assert.That(rightCut, Is.EqualTo(leftCut).Within(1e-6), "off-tracking must be left/right symmetric");
    }

    [Test]
    public void Length_Zero_CollapsesFootprintToWorkingLine()
    {
        // Legacy behaviour: with Length 0 the body corners sit on the working edges.
        var path = StraightNorth();
        var r = ImplementSweptPath.Compute(path, Geom(ToolMount.RearFixed, width: 4.0, length: 0.0));
        int mid = r.ToolCenter.Count / 2;
        Assert.That(Dist(r.FrontLeft[mid], r.RearLeft[mid]), Is.EqualTo(0.0).Within(1e-9));
        Assert.That(Dist(r.RearLeft[mid], r.LeftEdge[mid]), Is.EqualTo(0.0).Within(1e-9));
    }

    [Test]
    public void RearFixed_Straight_RearCornersAreLengthBehindFront()
    {
        var path = StraightNorth();
        var r = ImplementSweptPath.Compute(path, Geom(ToolMount.RearFixed, width: 4.0, toolHitch: 2.0, length: 5.0));
        int mid = r.ToolCenter.Count / 2;

        // Heading north: front corners at the hitch (N-2), rear corners 5 m further back.
        Assert.That(r.FrontLeft[mid].Northing, Is.EqualTo(path[mid].Northing - 2.0).Within(1e-6));
        Assert.That(r.RearLeft[mid].Northing, Is.EqualTo(path[mid].Northing - 2.0 - 5.0).Within(1e-6));
        Assert.That(Dist(r.FrontLeft[mid], r.RearLeft[mid]), Is.EqualTo(5.0).Within(1e-6));
        // Rear corners straddle by full width.
        Assert.That(Dist(r.RearLeft[mid], r.RearRight[mid]), Is.EqualTo(4.0).Within(1e-6));
    }

    [Test]
    public void MountedTool_OnTurn_RearOuterCornerSwingsWidestAndGrowsWithLength()
    {
        // The fence-catcher: a rigid rear-mounted implement (bush hog) rotates with
        // the tractor, so its rear-outer corner sweeps the widest arc — and wider
        // the longer the implement. Left turn => right side is outer.
        double radius = 10.0;
        var path = LeftArc(radius, Math.PI / 2.0, 120);
        var centre = new Vec3(-radius, 0, 0);

        double RearOuterReach(double length)
        {
            var r = ImplementSweptPath.Compute(path,
                Geom(ToolMount.RearFixed, width: 4.0, toolHitch: 2.0, length: length));
            int last = r.RearRight.Count - 1;          // rear-right = rear-outer on a left turn
            return Dist(r.RearRight[last], centre);
        }

        var rShort = ImplementSweptPath.Compute(path, Geom(ToolMount.RearFixed, width: 4.0, length: 0.0));
        int last = rShort.RearRight.Count - 1;
        double frontOuter = Dist(rShort.FrontRight[last], centre);

        double reach3 = RearOuterReach(3.0);
        double reach6 = RearOuterReach(6.0);

        Assert.That(reach3, Is.GreaterThan(frontOuter), "rear-outer corner should swing wider than the front");
        Assert.That(reach6, Is.GreaterThan(reach3), "a longer implement should swing its rear corner wider");
    }

    [Test]
    public void FootprintCorners_AlignWithInputPath()
    {
        var path = StraightNorth(25);
        var r = ImplementSweptPath.Compute(path, Geom(ToolMount.RearFixed, length: 5.0));
        Assert.That(r.FrontLeft.Count, Is.EqualTo(path.Count));
        Assert.That(r.FrontRight.Count, Is.EqualTo(path.Count));
        Assert.That(r.RearLeft.Count, Is.EqualTo(path.Count));
        Assert.That(r.RearRight.Count, Is.EqualTo(path.Count));
    }

    [Test]
    public void IndicesAlignWithInputPath()
    {
        var path = StraightNorth(25);
        var r = ImplementSweptPath.Compute(path, Geom(ToolMount.Trailing));
        Assert.That(r.ToolCenter.Count, Is.EqualTo(path.Count));
        Assert.That(r.LeftEdge.Count, Is.EqualTo(path.Count));
        Assert.That(r.RightEdge.Count, Is.EqualTo(path.Count));
    }
}
