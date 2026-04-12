using AgValoniaGPS.Models;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.Track;
using AgValoniaGPS.Services.Interfaces;
using AgValoniaGPS.Services.Track;

namespace AgValoniaGPS.Services.Tests;

[TestFixture]
public class SwathGenerationTests
{
    private SwathGenerationService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _service = new SwathGenerationService();
    }

    /// <summary>
    /// Create a rectangular boundary polygon.
    /// </summary>
    private static BoundaryPolygon MakeRectBoundary(double minE, double maxE, double minN, double maxN)
    {
        var bp = new BoundaryPolygon();
        bp.Points.Add(new BoundaryPoint { Easting = minE, Northing = minN });
        bp.Points.Add(new BoundaryPoint { Easting = maxE, Northing = minN });
        bp.Points.Add(new BoundaryPoint { Easting = maxE, Northing = maxN });
        bp.Points.Add(new BoundaryPoint { Easting = minE, Northing = maxN });
        bp.UpdateBounds();
        return bp;
    }

    /// <summary>
    /// Create an AB line track heading north (0 radians).
    /// </summary>
    private static Models.Track.Track MakeNorthABLine(double easting = 0, double northingA = -100, double northingB = 100)
    {
        return Models.Track.Track.FromABLine("Test AB",
            new Vec3(easting, northingA, 0),
            new Vec3(easting, northingB, 0));
    }

    [Test]
    public void RectangularField_NorthHeading_GeneratesCorrectTrackCount()
    {
        // 100m wide field, 10m tool width, heading north
        var input = new SwathPlanInput
        {
            ReferenceTrack = MakeNorthABLine(),
            ClipBoundary = MakeRectBoundary(-50, 50, -200, 200),
            ToolWidth = 10.0,
            Overlap = 0,
            Pattern = SwathPattern.Boustrophedon
        };

        var result = _service.GenerateSwaths(input);

        // 100m / 10m = 10 tracks should fit (indices -5 to 4, or similar)
        Assert.That(result.TotalPossibleTracks, Is.EqualTo(11)); // -5..5 inclusive
        Assert.That(result.Swaths.Count, Is.EqualTo(11));
        Assert.That(result.TotalWorkingDistance, Is.GreaterThan(0));
    }

    [Test]
    public void RectangularField_TracksHaveFiniteEndpoints()
    {
        var input = new SwathPlanInput
        {
            ReferenceTrack = MakeNorthABLine(),
            ClipBoundary = MakeRectBoundary(-50, 50, -200, 200),
            ToolWidth = 10.0,
            Overlap = 0,
        };

        var result = _service.GenerateSwaths(input);

        foreach (var swath in result.Swaths)
        {
            Assert.That(swath.Points.Count, Is.EqualTo(2), $"Swath '{swath.Name}' should have 2 endpoints");
            // Endpoints should be on or near the boundary (northing ≈ ±200)
            var p0 = swath.Points[0];
            var p1 = swath.Points[1];
            Assert.That(Math.Abs(Math.Abs(p0.Northing) - 200), Is.LessThan(1.0),
                $"Start point northing {p0.Northing} should be near boundary");
            Assert.That(Math.Abs(Math.Abs(p1.Northing) - 200), Is.LessThan(1.0),
                $"End point northing {p1.Northing} should be near boundary");
        }
    }

    [Test]
    public void MaxTracks_LimitsOutput()
    {
        var input = new SwathPlanInput
        {
            ReferenceTrack = MakeNorthABLine(),
            ClipBoundary = MakeRectBoundary(-50, 50, -200, 200),
            ToolWidth = 10.0,
            Overlap = 0,
            MaxTracks = 3
        };

        var result = _service.GenerateSwaths(input);

        Assert.That(result.Swaths.Count, Is.EqualTo(3));
        Assert.That(result.TotalPossibleTracks, Is.EqualTo(11)); // Total unchanged
    }

    [Test]
    public void NextNFromVehicle_StartsFromNearestTrack()
    {
        var input = new SwathPlanInput
        {
            ReferenceTrack = MakeNorthABLine(),
            ClipBoundary = MakeRectBoundary(-50, 50, -200, 200),
            ToolWidth = 10.0,
            Overlap = 0,
            MaxTracks = 3,
            VehiclePosition = new Vec3(25, 0, 0), // Near track index 2 or 3
            Pattern = SwathPattern.Boustrophedon
        };

        var result = _service.GenerateSwaths(input);

        Assert.That(result.Swaths.Count, Is.EqualTo(3));
        // First swath should be near easting 20 or 30 (nearest to vehicle at 25)
        var firstSwath = result.Swaths[0];
        double firstEasting = firstSwath.Points[0].Easting;
        Assert.That(Math.Abs(firstEasting - 25), Is.LessThan(10),
            $"First swath easting {firstEasting} should be near vehicle at 25");
    }

    [Test]
    public void SkipWidth_SkipsTracks()
    {
        var input = new SwathPlanInput
        {
            ReferenceTrack = MakeNorthABLine(),
            ClipBoundary = MakeRectBoundary(-50, 50, -200, 200),
            ToolWidth = 10.0,
            Overlap = 0,
            SkipWidth = 2, // Every other track
            Pattern = SwathPattern.Boustrophedon
        };

        var result = _service.GenerateSwaths(input);

        // 11 total tracks, skip every other = 6 tracks
        Assert.That(result.Swaths.Count, Is.EqualTo(6));
    }

    [Test]
    public void SnakeOrdering_ProducesValidSequence()
    {
        var input = new SwathPlanInput
        {
            ReferenceTrack = MakeNorthABLine(),
            ClipBoundary = MakeRectBoundary(-50, 50, -200, 200),
            ToolWidth = 10.0,
            Overlap = 0,
            Pattern = SwathPattern.Snake
        };

        var result = _service.GenerateSwaths(input);

        Assert.That(result.Swaths.Count, Is.EqualTo(11));
        // All swaths should have unique positions (no duplicates)
        var eastings = result.Swaths.Select(s => Math.Round(s.Points[0].Easting, 1)).ToList();
        Assert.That(eastings.Distinct().Count(), Is.EqualTo(eastings.Count),
            "All swaths should have unique easting positions");
    }

    [Test]
    public void Overlap_ReducesSwathSpacing()
    {
        var noOverlap = new SwathPlanInput
        {
            ReferenceTrack = MakeNorthABLine(),
            ClipBoundary = MakeRectBoundary(-50, 50, -200, 200),
            ToolWidth = 10.0,
            Overlap = 0,
        };

        var withOverlap = new SwathPlanInput
        {
            ReferenceTrack = MakeNorthABLine(),
            ClipBoundary = MakeRectBoundary(-50, 50, -200, 200),
            ToolWidth = 10.0,
            Overlap = 2.0, // 2m overlap → 8m effective width
        };

        var resultNoOverlap = _service.GenerateSwaths(noOverlap);
        var resultWithOverlap = _service.GenerateSwaths(withOverlap);

        Assert.That(resultWithOverlap.TotalPossibleTracks,
            Is.GreaterThan(resultNoOverlap.TotalPossibleTracks),
            "More tracks should fit with overlap");
    }

    [Test]
    public void ToolWiderThanField_ReturnsOneOrZeroTracks()
    {
        var input = new SwathPlanInput
        {
            ReferenceTrack = MakeNorthABLine(),
            ClipBoundary = MakeRectBoundary(-5, 5, -200, 200), // 10m wide field
            ToolWidth = 20.0, // 20m tool
            Overlap = 0,
        };

        var result = _service.GenerateSwaths(input);

        Assert.That(result.Swaths.Count, Is.LessThanOrEqualTo(1));
    }

    [Test]
    public void ConcaveBoundary_ProducesMultipleSegments()
    {
        // L-shaped field: a notch cuts across the middle
        var bp = new BoundaryPolygon();
        bp.Points.Add(new BoundaryPoint { Easting = -50, Northing = -100 });
        bp.Points.Add(new BoundaryPoint { Easting = 50, Northing = -100 });
        bp.Points.Add(new BoundaryPoint { Easting = 50, Northing = 0 });
        bp.Points.Add(new BoundaryPoint { Easting = 10, Northing = 0 });    // Notch right edge
        bp.Points.Add(new BoundaryPoint { Easting = 10, Northing = 50 });   // Notch top
        bp.Points.Add(new BoundaryPoint { Easting = 50, Northing = 50 });
        bp.Points.Add(new BoundaryPoint { Easting = 50, Northing = 100 });
        bp.Points.Add(new BoundaryPoint { Easting = -50, Northing = 100 });
        bp.UpdateBounds();

        var input = new SwathPlanInput
        {
            ReferenceTrack = MakeNorthABLine(easting: 30), // Passes through the notch
            ClipBoundary = bp,
            ToolWidth = 10.0,
            Overlap = 0,
        };

        var result = _service.GenerateSwaths(input);

        // Track at easting=30 should be split into two segments by the notch
        // (one below the notch, one above)
        var tracksAt30 = result.Swaths
            .Where(s => Math.Abs(s.Points[0].Easting - 30) < 1.0)
            .ToList();

        Assert.That(tracksAt30.Count, Is.EqualTo(2),
            $"Track at easting 30 should be split into 2 segments by the notch, got {tracksAt30.Count}");
    }

    [Test]
    public void EmptyBoundary_ReturnsEmptyPlan()
    {
        var bp = new BoundaryPolygon();
        bp.UpdateBounds();

        var input = new SwathPlanInput
        {
            ReferenceTrack = MakeNorthABLine(),
            ClipBoundary = bp,
            ToolWidth = 10.0,
        };

        var result = _service.GenerateSwaths(input);

        Assert.That(result.Swaths, Is.Empty);
    }

    [Test]
    public void SwathPlan_DoesNotContainTurnPaths()
    {
        // Turn generation moved to TurnPathService / RouteStitchingService in Phase 2.
        // SwathGenerationService only generates swaths now.
        var input = new SwathPlanInput
        {
            ReferenceTrack = MakeNorthABLine(),
            ClipBoundary = MakeRectBoundary(-50, 50, -200, 200),
            ToolWidth = 10.0,
            MaxTracks = 5,
            Pattern = SwathPattern.Boustrophedon
        };

        var result = _service.GenerateSwaths(input);

        Assert.That(result.Swaths.Count, Is.EqualTo(5));
        Assert.That(result.TotalWorkingDistance, Is.GreaterThan(0));
    }
}
