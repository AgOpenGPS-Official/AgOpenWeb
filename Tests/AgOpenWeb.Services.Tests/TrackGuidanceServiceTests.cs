using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Track;
using AgOpenWeb.Services.Track;
using TrackModel = AgOpenWeb.Models.Track.Track;

namespace AgOpenWeb.Services.Tests;

[TestFixture]
public class TrackGuidanceServiceTests
{
    private TrackGuidanceService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _service = new TrackGuidanceService();
    }

    #region AB Line Pure Pursuit

    [Test]
    public void ABLine_PurePursuit_VehicleRightOfLine_SteersLeft()
    {
        var track = TrackModel.FromABLine(
            "Test AB",
            new Vec3(0, 0, 0),
            new Vec3(0, 100, 0));

        var input = new TrackGuidanceInput
        {
            Track = track,
            PivotPosition = new Vec3(2, 50, 0),
            SteerPosition = new Vec3(2, 52.5, 0),
            UseStanley = false,
            Wheelbase = 2.5,
            MaxSteerAngle = 35,
            GoalPointDistance = 5,
            FixHeading = 0,
            AvgSpeed = 10,
            IsHeadingSameWay = true,
            FindGlobalNearest = true
        };

        var output = _service.CalculateGuidance(input);

        Assert.That(output.CrossTrackError, Is.EqualTo(2.0).Within(0.1),
            "XTE should be ~2m (vehicle 2m east of line)");
        Assert.That(output.SteerAngle, Is.LessThan(0),
            "Should steer left (negative) to return to line");
        Assert.That(Math.Abs(output.SteerAngle), Is.LessThan(35),
            "Steer angle should be within limits");
    }

    #endregion

    #region Curve Pure Pursuit

    [Test]
    public void Curve_PurePursuit_ReturnsReasonableOutput()
    {
        var points = new List<Vec3>();
        for (int i = 0; i <= 10; i++)
        {
            double angle = i * Math.PI / 20;
            double x = 20 * Math.Sin(angle);
            double y = 20 * (1 - Math.Cos(angle));
            points.Add(new Vec3(x, y, angle));
        }

        var track = TrackModel.FromCurve("Test Curve", points);

        var input = new TrackGuidanceInput
        {
            Track = track,
            PivotPosition = new Vec3(1, 1, 0.1),
            SteerPosition = new Vec3(1, 3.5, 0.1),
            UseStanley = false,
            Wheelbase = 2.5,
            MaxSteerAngle = 35,
            GoalPointDistance = 5,
            FixHeading = 0.1,
            AvgSpeed = 8,
            IsHeadingSameWay = true,
            FindGlobalNearest = true
        };

        var output = _service.CalculateGuidance(input);

        Assert.That(double.IsNaN(output.SteerAngle), Is.False, "Steer angle should not be NaN");
        Assert.That(Math.Abs(output.SteerAngle), Is.LessThanOrEqualTo(35));
        Assert.That(output.GoalPoint.Easting != 0 || output.GoalPoint.Northing != 0, Is.True,
            "Goal point should be non-zero");
    }

    #endregion

    #region Stanley Algorithm

    [Test]
    public void ABLine_Stanley_VehicleLeftOfLine_SteersRight()
    {
        var track = TrackModel.FromABLine(
            "Test AB",
            new Vec3(0, 0, 0),
            new Vec3(0, 100, 0));

        var input = new TrackGuidanceInput
        {
            Track = track,
            PivotPosition = new Vec3(-1.5, 50, 0),
            SteerPosition = new Vec3(-1.5, 52.5, 0),
            UseStanley = true,
            Wheelbase = 2.5,
            MaxSteerAngle = 35,
            GoalPointDistance = 5,
            StanleyHeadingErrorGain = 1.0,
            StanleyDistanceErrorGain = 0.8,
            FixHeading = 0,
            AvgSpeed = 10,
            IsHeadingSameWay = true,
            FindGlobalNearest = true
        };

        var output = _service.CalculateGuidance(input);

        Assert.That(output.CrossTrackError, Is.EqualTo(-1.5).Within(0.1),
            "XTE should be ~-1.5m (vehicle 1.5m west of line)");
        Assert.That(output.SteerAngle, Is.GreaterThan(0),
            "Should steer right (positive) to return to line");
        Assert.That(Math.Abs(output.SteerAngle), Is.LessThan(35));
    }

    #endregion

    #region Vehicle On Line

    [Test]
    public void ABLine_VehicleOnLine_NearZeroXteAndSteering()
    {
        var track = TrackModel.FromABLine(
            "Test AB",
            new Vec3(0, 0, 0),
            new Vec3(0, 100, 0));

        var input = new TrackGuidanceInput
        {
            Track = track,
            PivotPosition = new Vec3(0, 50, 0),
            SteerPosition = new Vec3(0, 52.5, 0),
            UseStanley = false,
            Wheelbase = 2.5,
            MaxSteerAngle = 35,
            GoalPointDistance = 5,
            FixHeading = 0,
            AvgSpeed = 10,
            IsHeadingSameWay = true,
            FindGlobalNearest = true
        };

        var output = _service.CalculateGuidance(input);

        Assert.That(Math.Abs(output.CrossTrackError), Is.LessThan(0.01),
            "XTE should be near zero when on line");
        Assert.That(Math.Abs(output.SteerAngle), Is.LessThan(1.0),
            "Steer angle should be near zero when on line");
    }

    #endregion

    #region Track Property Verification

    [Test]
    public void ABLine_TrackProperties_ComputedCorrectly()
    {
        var track = TrackModel.FromABLine(
            "Property Test",
            new Vec3(10, 20, 0.5),
            new Vec3(30, 40, 0.5));
        track.NudgeDistance = 1.5;
        track.IsVisible = true;

        Assert.That(track.Name, Is.EqualTo("Property Test"));
        Assert.That(track.Points, Has.Count.EqualTo(2));
        Assert.That(track.IsABLine, Is.True);
        Assert.That(track.IsCurve, Is.False);
        Assert.That(track.NudgeDistance, Is.EqualTo(1.5).Within(0.001));
        Assert.That(track.IsVisible, Is.True);
        Assert.That(track.Points[0].Easting, Is.EqualTo(10).Within(0.001));
        Assert.That(track.Points[0].Northing, Is.EqualTo(20).Within(0.001));
    }

    #endregion

    #region Edge Cases - Degenerate Input

    [Test]
    public void SinglePointTrack_ReturnsErrorSentinel()
    {
        var track = new TrackModel
        {
            Name = "Bad",
            Points = new List<Vec3> { new(0, 0, 0) }
        };

        var input = CreateDefaultInput(track);
        var output = _service.CalculateGuidance(input);

        Assert.That(output.DistanceFromLinePivot, Is.EqualTo(32000));
    }

    [Test]
    public void NullTrack_ReturnsErrorSentinel()
    {
        var input = CreateDefaultInput(null!);
        input.Track = null!;
        var output = _service.CalculateGuidance(input);

        Assert.That(output.DistanceFromLinePivot, Is.EqualTo(32000));
    }

    #endregion

    #region Edge Cases - Heading Direction

    [Test]
    public void ABLine_HeadingOpposite_FlipsXTESign()
    {
        var track = TrackModel.FromABLine("Test", new Vec3(0, 0, 0), new Vec3(0, 100, 0));

        // Vehicle heading south (PI radians) - opposite to track heading north (0)
        var input = CreateDefaultInput(track);
        input.PivotPosition = new Vec3(2, 50, Math.PI);
        input.SteerPosition = new Vec3(2, 47.5, Math.PI);
        input.FixHeading = Math.PI;
        input.IsHeadingSameWay = false;

        var output = _service.CalculateGuidance(input);

        // XTE sign should be flipped when heading opposite
        Assert.That(output.CrossTrackError, Is.EqualTo(-2.0).Within(0.2));
    }

    [Test]
    public void ABLine_VehicleFarFromLine_ReturnsLargeXTE()
    {
        var track = TrackModel.FromABLine("Test", new Vec3(0, 0, 0), new Vec3(0, 100, 0));

        var input = CreateDefaultInput(track);
        input.PivotPosition = new Vec3(50, 50, 0); // 50m off the line
        input.SteerPosition = new Vec3(50, 52.5, 0);

        var output = _service.CalculateGuidance(input);

        Assert.That(Math.Abs(output.CrossTrackError), Is.GreaterThan(40));
        Assert.That(double.IsNaN(output.SteerAngle), Is.False);
    }

    #endregion

    #region Edge Cases - Stanley Algorithm

    [Test]
    public void Stanley_VehicleOnLine_NearZeroSteering()
    {
        var track = TrackModel.FromABLine("Test", new Vec3(0, 0, 0), new Vec3(0, 100, 0));

        var input = CreateDefaultInput(track);
        input.UseStanley = true;
        input.PivotPosition = new Vec3(0, 50, 0);
        input.SteerPosition = new Vec3(0, 52.5, 0);
        input.StanleyHeadingErrorGain = 1.0;
        input.StanleyDistanceErrorGain = 0.8;

        var output = _service.CalculateGuidance(input);

        Assert.That(Math.Abs(output.SteerAngle), Is.LessThan(1.0));
        Assert.That(Math.Abs(output.CrossTrackError), Is.LessThan(0.01));
    }

    [Test]
    public void Stanley_LowSpeed_StillWorks()
    {
        var track = TrackModel.FromABLine("Test", new Vec3(0, 0, 0), new Vec3(0, 100, 0));

        var input = CreateDefaultInput(track);
        input.UseStanley = true;
        input.PivotPosition = new Vec3(1, 50, 0);
        input.SteerPosition = new Vec3(1, 52.5, 0);
        input.StanleyHeadingErrorGain = 1.0;
        input.StanleyDistanceErrorGain = 0.8;
        input.AvgSpeed = 0.5; // Very slow

        var output = _service.CalculateGuidance(input);

        Assert.That(double.IsNaN(output.SteerAngle), Is.False);
        Assert.That(double.IsInfinity(output.SteerAngle), Is.False);
    }

    #endregion

    #region Multi-Frame State Continuity

    [Test]
    public void PurePursuit_StateCarriesOverBetweenCalls()
    {
        var track = TrackModel.FromABLine("Test", new Vec3(0, 0, 0), new Vec3(0, 100, 0));

        var input = CreateDefaultInput(track);
        input.PivotPosition = new Vec3(1, 10, 0);
        input.SteerPosition = new Vec3(1, 12.5, 0);
        input.IsAutoSteerOn = true;

        var output1 = _service.CalculateGuidance(input);
        Assert.That(output1.State, Is.Not.Null);

        // Second call uses previous state
        input.PreviousState = output1.State;
        input.CurrentLocationIndex = output1.CurrentLocationIndex;
        input.FindGlobalNearest = false;
        input.PivotPosition = new Vec3(0.8, 15, 0);
        input.SteerPosition = new Vec3(0.8, 17.5, 0);

        var output2 = _service.CalculateGuidance(input);

        Assert.That(output2.State, Is.Not.Null);
        Assert.That(double.IsNaN(output2.SteerAngle), Is.False);
    }

    #endregion

    #region Curve Edge Cases

    [Test]
    public void Curve_VehicleAtEnd_DoesNotCrash()
    {
        var points = new List<Vec3>();
        for (int i = 0; i <= 5; i++)
            points.Add(new Vec3(0, i * 10.0, 0));

        var track = TrackModel.FromCurve("Short", points);

        var input = CreateDefaultInput(track);
        input.PivotPosition = new Vec3(0, 55, 0); // Past end
        input.SteerPosition = new Vec3(0, 57.5, 0);

        var output = _service.CalculateGuidance(input);

        Assert.That(double.IsNaN(output.SteerAngle), Is.False);
    }

    [Test]
    public void Curve_ThreePoints_MinimumViable()
    {
        var track = TrackModel.FromCurve("Tiny", new List<Vec3>
        {
            new(0, 0, 0),
            new(5, 10, 0.5),
            new(10, 20, 0.5)
        });

        var input = CreateDefaultInput(track);
        input.PivotPosition = new Vec3(2, 5, 0.3);
        input.SteerPosition = new Vec3(2, 7.5, 0.3);
        input.FixHeading = 0.3;

        var output = _service.CalculateGuidance(input);

        Assert.That(double.IsNaN(output.SteerAngle), Is.False);
        Assert.That(Math.Abs(output.SteerAngle), Is.LessThanOrEqualTo(35));
    }

    #endregion

    #region Curve direction stability (#422)

    // Build a gentle arc curve (radius ~200 m) with proper per-point headings,
    // travelling roughly +North then curving east.
    private static TrackModel BuildArcCurve()
    {
        var pts = new System.Collections.Generic.List<Vec3>();
        double r = 200;
        for (int i = 0; i <= 60; i++)
        {
            double a = i * (Math.PI / 180.0); // 0..60 degrees around the arc
            pts.Add(new Vec3(r - r * Math.Cos(a), r * Math.Sin(a), 0));
        }
        var withHeadings = AgOpenWeb.Models.Guidance.CurveProcessing.CalculateHeadings(pts);
        return new TrackModel { Name = "Arc", Points = withHeadings, Type = TrackType.Curve };
    }

    [Test]
    public void Curve_DirectionFrozenWhileSteering_DoesNotFlipOnHeadingSpike()
    {
        var track = BuildArcCurve();

        // Frame 1: acquire globally, travelling along the curve (same way).
        var p0 = track.Points[10];
        var input1 = new TrackGuidanceInput
        {
            Track = track,
            PivotPosition = new Vec3(p0.Easting, p0.Northing, p0.Heading),
            SteerPosition = new Vec3(p0.Easting, p0.Northing, p0.Heading),
            UseStanley = false, Wheelbase = 2.5, MaxSteerAngle = 35, GoalPointDistance = 5,
            FixHeading = p0.Heading, AvgSpeed = 10, IsAutoSteerOn = true, FindGlobalNearest = true
        };
        var out1 = _service.CalculateGuidance(input1);
        Assert.That(out1.State.IsHeadingSameWay, Is.True, "should acquire travelling same-way");

        // Frame 2: a momentary heading spike ~120 deg off (noise / sharp transient),
        // still steering, local search. Direction must stay frozen, not flip.
        var p1 = track.Points[11];
        double spiked = p1.Heading + 120 * Math.PI / 180.0;
        var input2 = new TrackGuidanceInput
        {
            Track = track,
            PivotPosition = new Vec3(p1.Easting, p1.Northing, spiked),
            SteerPosition = new Vec3(p1.Easting, p1.Northing, spiked),
            UseStanley = false, Wheelbase = 2.5, MaxSteerAngle = 35, GoalPointDistance = 5,
            FixHeading = spiked, AvgSpeed = 10, IsAutoSteerOn = true,
            FindGlobalNearest = false,
            PreviousState = out1.State,
            CurrentLocationIndex = out1.CurrentLocationIndex
        };
        var out2 = _service.CalculateGuidance(input2);

        Assert.That(out2.State.IsHeadingSameWay, Is.True,
            "direction must stay frozen while steering despite a heading spike (#422)");
    }

    [Test]
    public void Curve_DirectionReevaluatedOnGlobalReacquire()
    {
        var track = BuildArcCurve();
        var p = track.Points[10];

        // Genuinely travelling the opposite way (heading reversed ~180 deg) with a
        // global re-acquire requested: the frozen value must be overridden.
        double reversed = p.Heading + Math.PI;
        var input = new TrackGuidanceInput
        {
            Track = track,
            PivotPosition = new Vec3(p.Easting, p.Northing, reversed),
            SteerPosition = new Vec3(p.Easting, p.Northing, reversed),
            UseStanley = false, Wheelbase = 2.5, MaxSteerAngle = 35, GoalPointDistance = 5,
            FixHeading = reversed, AvgSpeed = 10, IsAutoSteerOn = true,
            FindGlobalNearest = true,
            PreviousState = new TrackGuidanceState { IsHeadingSameWay = true }
        };
        var output = _service.CalculateGuidance(input);

        Assert.That(output.State.IsHeadingSameWay, Is.False,
            "a global re-acquire must re-evaluate direction (here: reversed)");
    }

    #endregion

    #region Helpers

    private static TrackGuidanceInput CreateDefaultInput(TrackModel track)
    {
        return new TrackGuidanceInput
        {
            Track = track,
            PivotPosition = new Vec3(0, 50, 0),
            SteerPosition = new Vec3(0, 52.5, 0),
            UseStanley = false,
            Wheelbase = 2.5,
            MaxSteerAngle = 35,
            GoalPointDistance = 5,
            FixHeading = 0,
            AvgSpeed = 10,
            IsHeadingSameWay = true,
            FindGlobalNearest = true,
            IsAutoSteerOn = true
        };
    }

    #endregion
}
