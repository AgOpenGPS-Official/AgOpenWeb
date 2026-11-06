// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Coverage;
using AgOpenWeb.Services.GeoJson;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Section;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// Checklist 5.1: recording a boundary driving, with the left, right and centre offset, pause,
/// add a point and delete the last, held to AgOpenGPS's FormBoundaryPlayer and AddBoundaryPoint
/// (<see cref="AgOpenGpsBoundaryRecordOracle"/>). The same fixes go through the real GPS handler
/// (the pivot the cycle wrote, as AgOpenGPS records pivotAxlePos) with the real recording
/// service, the real section service for the Tool option, and the real field service for the
/// file the boundary ends up in.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class BoundaryRecordingMatchesAgOpenGpsTests
{
    private const double ToolWidth = 6; // 3 x 2 m sections, no offset
    private string _root = null!;
    private string _dir = null!;
    private ConfigurationStore _config = null!;
    private MainViewModelBuilder _b = null!;
    private BoundaryRecordingService _rec = null!;
    private SectionControlService _sections = null!;
    private MainViewModel _vm = null!;
    private AgOpenGpsBoundaryRecordOracle _theirs = null!;
    private string? _failure;

    [SetUp]
    public void SetUp()
    {
        _config = new ConfigurationStore();
        ConfigurationStore.SetInstance(_config);
        _config.NumSections = 3;
        for (int i = 0; i < 3; i++) _config.Tool.SetSectionWidth(i, 200);
        _config.Tool.Offset = 0;
        _root = Path.Combine(Path.GetTempPath(), "aow-bndrec-" + Guid.NewGuid().ToString("N"));
        _dir = Path.Combine(_root, "F");
        Directory.CreateDirectory(_dir);
        var field = new Field { Name = "F", DirectoryPath = _dir, Origin = new Position { Latitude = 52, Longitude = 5 } };
        GeoJsonFieldService.Save(field, tracks: null);

        _rec = new BoundaryRecordingService();
        _b = new MainViewModelBuilder { FieldService = new FieldService(), BoundaryRecordingService = _rec };
        _sections = new SectionControlService(_b.ToolPositionService, Substitute.For<ICoverageMapService>(), _config);
        _b.SectionControlService = _sections;
        _b.SettingsService.Settings.Returns(new AppSettings { FieldsDirectory = _root });
        _vm = _b.Build();
        _failure = null; // NUnit keeps one fixture instance across its tests
        _vm.FailureReported += m => _failure = m;
        _vm.ActiveField = field;
        _vm.IsFieldOpen = true;
        _vm.IsBoundarySectionControlOn = false;
        // The side and the antenna/tool choice persist between recordings (AgOpenGPS's CFence
        // flags and setBnd_isDrawPivot): back to its defaults for every test.
        _vm.IsDrawRightSide = true;
        _vm.IsDrawAtPivot = true;

        _theirs = new AgOpenGpsBoundaryRecordOracle();
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, true); } catch { }
        ConfigurationStore.SetInstance(new ConfigurationStore());
    }

    /// <summary>One fix at the pivot, heading in degrees, through our GPS handler and AgOpenGPS's.</summary>
    private void Fix(double e, double n, double headingDeg)
    {
        double rad = headingDeg * Math.PI / 180;
        _vm.State.Vehicle.Easting = e;
        _vm.State.Vehicle.Northing = n;
        _vm.State.Vehicle.Heading = headingDeg;
        _b.ToolPositionService.HasPosition.Returns(true);
        _b.ToolPositionService.ToolPosition.Returns(new Vec3(e, n, rad));
        _b.ToolPositionService.ToolHeading.Returns(rad);
        _b.GpsService.GpsDataUpdated += Raise.Event<EventHandler<GpsData>>(_b.GpsService,
            new GpsData { CurrentPosition = new Position { Easting = e, Northing = n, Heading = headingDeg, Latitude = 52, Longitude = 5 } });
        var (s0Left, lastRight) = AgOpenGpsSectionEdges(e, n, rad);
        _theirs.Fix(new Vec2(e, n), new Vec3(e, n, rad), s0Left, lastRight);
    }

    /// <summary>Position.designer CalculateSectionLookAhead: the first section's left and the last section's right point.</summary>
    private static (Vec2 section0Left, Vec2 lastRight) AgOpenGpsSectionEdges(double easting, double northing, double heading)
    {
        double sinHeading = Math.Sin(-heading), cosHeading = Math.Cos(-heading);
        double positionLeft = -ToolWidth / 2, positionRight = ToolWidth / 2;
        return (new Vec2(cosHeading * positionLeft + easting, sinHeading * positionLeft + northing),
                new Vec2(cosHeading * positionRight + easting, sinHeading * positionRight + northing));
    }

    private (double e, double n, double headingDeg) _at;

    /// <summary>Drive straight from the current place for <paramref name="metres"/> on <paramref name="headingDeg"/>, a fix every 10 cm.</summary>
    private void Drive(double metres, double headingDeg)
    {
        double rad = headingDeg * Math.PI / 180;
        for (int i = 1; i <= (int)Math.Round(metres * 10); i++)
        {
            double d = i * 0.1;
            Fix(_at.e + Math.Sin(rad) * d, _at.n + Math.Cos(rad) * d, headingDeg);
        }
        _at = (_at.e + Math.Sin(rad) * metres, _at.n + Math.Cos(rad) * metres, headingDeg);
    }

    private void StartAt(double e, double n, double headingDeg)
    {
        _at = (e, n, headingDeg);
        Fix(e, n, headingDeg);
    }

    private void OpenThePlayer(bool inner = false)
    {
        (inner ? _vm.DriveAroundInnerBoundaryCommand : _vm.DriveAroundFieldCommand)!.Execute(null); // paused, as AgOpenGPS opens it
        _theirs.FormBoundaryPlayer_Load(ToolWidth);
        Assume.That(_rec.State, Is.EqualTo(BoundaryRecordingState.Paused));
        Assume.That(_theirs.isOkToAddPoints, Is.False);
    }

    private void RecordPause()
    {
        _vm.ToggleRecordingCommand!.Execute(null);
        _theirs.btnPausePlay_Click();
    }

    private static double DistanceToPolyline(Vec3 p, IReadOnlyList<Vec3> line, bool closed)
    {
        double best = double.MaxValue;
        int n = line.Count;
        for (int i = 0; i < (closed ? n : n - 1); i++)
        {
            var a = line[i]; var b = line[(i + 1) % n];
            double dx = b.Easting - a.Easting, dy = b.Northing - a.Northing;
            double len2 = dx * dx + dy * dy;
            double t = len2 < 1e-12 ? 0 : Math.Clamp(((p.Easting - a.Easting) * dx + (p.Northing - a.Northing) * dy) / len2, 0, 1);
            double ex = a.Easting + t * dx - p.Easting, ey = a.Northing + t * dy - p.Northing;
            best = Math.Min(best, Math.Sqrt(ex * ex + ey * ey));
        }
        return best;
    }

    private List<Vec3> Ours() => _rec.RecordedPoints.Select(p => new Vec3(p.Easting, p.Northing, p.Heading)).ToList();

    private static double MeanSpacing(IReadOnlyList<Vec3> pts)
        => pts.Count < 2 ? 0 : pts.Zip(pts.Skip(1), (a, b) => Math.Sqrt((a.Easting - b.Easting) * (a.Easting - b.Easting) + (a.Northing - b.Northing) * (a.Northing - b.Northing))).Average();

    [Test]
    public void DrivenWithARightOffset_RecordsTheSameLine_AndTheSameBoundary()
    {
        OpenThePlayer();
        _vm.BoundaryOffset = 150; // cm, the web's offset box; AgOpenGPS's nudOffset in cm too
        _theirs.nudOffset_Click(1.5);
        Assume.That(_vm.IsDrawRightSide && _vm.IsDrawAtPivot, "AgOpenGPS's defaults: right side, at the pivot");
        StartAt(100, 100, 0);
        RecordPause(); // Record

        // A clockwise 60 m square: the right side is the inside.
        Drive(60, 0); Drive(60, 90); Drive(60, 180); Drive(60, 270);

        var ours = Ours();
        var theirs = _theirs.bndBeingMadePts;
        Assert.That(ours, Has.Count.GreaterThan(200));
        // Along the legs the two lines are one. At a corner the offset point jumps sideways as
        // the heading turns, and where the next point lands depends on the point trigger
        // (AgOpenGPS: a metre of travel since the last point's fix; ours: a metre from the last
        // point), so the corners are cut a little differently: those are left out.
        var corners = new[] { (100.0, 100.0), (100.0, 160.0), (160.0, 160.0), (160.0, 100.0) };
        bool OnALeg(Vec3 p) => corners.All(c => Math.Sqrt((p.Easting - c.Item1) * (p.Easting - c.Item1) + (p.Northing - c.Item2) * (p.Northing - c.Item2)) > 3);
        Assert.That(ours.Where(OnALeg).Max(p => DistanceToPolyline(p, theirs, closed: false)), Is.LessThan(0.02), "every point of ours lies on AgOpenGPS's line");
        Assert.That(theirs.Where(OnALeg).Max(p => DistanceToPolyline(p, ours, closed: false)), Is.LessThan(0.02), "and every point of AgOpenGPS's on ours");
        Assert.That(MeanSpacing(ours), Is.EqualTo(MeanSpacing(theirs)).Within(0.15), "a point about every metre in both");
        // The offset went to the right: the line is 1.5 m inside the drive.
        Assert.That(ours.Where(p => Math.Abs(p.Northing - 130) < 1).Min(p => p.Easting), Is.EqualTo(101.5).Within(0.01), "1.5 m right of the northbound leg");

        // Stop: the boundary is the field's outer boundary; AgOpenGPS fixes its winding and spacing,
        // ours is normalised; the ring is the same.
        _vm.StopBoundaryRecordingCommand!.Execute(null);
        Assert.That(_theirs.btnStop_Click(), Is.True);
        Assert.That(_failure, Is.Null);
        var saved = new FieldService().LoadField(_dir).Boundary!.OuterBoundary!;
        double theirArea = _theirs.bndList[0].area;
        Assert.That(saved.AreaHectares * 10000, Is.EqualTo(theirArea).Within(theirArea * 0.005), "the same area");
        Assert.That(theirArea, Is.EqualTo(57 * 57).Within(57 * 57 * 0.01), "a 57 m square");
        var theirRing = _theirs.bndList[0].fenceLine;
        Assert.That(saved.Points.Select(p => new Vec3(p.Easting, p.Northing, 0)).Where(OnALeg).Max(p => DistanceToPolyline(p, theirRing, closed: true)),
            Is.LessThan(0.05), "the saved ring lies on AgOpenGPS's along the legs");
    }

    [Test]
    public void LeftSide_AndCentre_PutThePointWhereAgOpenGpsDoes()
    {
        OpenThePlayer();
        _vm.BoundaryOffset = 200;
        _theirs.nudOffset_Click(2.0);
        _vm.ToggleBoundaryLeftRightCommand!.Execute(null);
        _theirs.btnLeftRight_Click();
        Assume.That(_vm.IsDrawRightSide, Is.False);
        StartAt(100, 100, 45);
        RecordPause();
        Drive(10, 45);
        var ours = Ours(); var theirs = _theirs.bndBeingMadePts;
        Assert.That(ours, Has.Count.GreaterThan(5));
        for (int i = 0; i < Math.Min(ours.Count, theirs.Count); i++)
        {
            Assert.That(ours[i].Easting, Is.EqualTo(theirs[i].Easting).Within(1e-9), $"point {i} E, 2 m left of the pivot");
            Assert.That(ours[i].Northing, Is.EqualTo(theirs[i].Northing).Within(1e-9), $"point {i} N");
        }

        // Centre: offset 0 records the pivot itself.
        _vm.BoundaryOffset = 0;
        _theirs.nudOffset_Click(0);
        Drive(5, 45);
        // Both record the pivot itself now (where along the drive differs by the point trigger).
        double OffTheDrive(Vec3 p) => Math.Abs((p.Easting - 100) - (p.Northing - 100)) / Math.Sqrt(2); // the 45° drive from (100, 100)
        Assert.That(OffTheDrive(Ours()[^1]), Is.LessThan(1e-9), "ours: on the drive");
        Assert.That(OffTheDrive(_theirs.bndBeingMadePts[^1]), Is.LessThan(1e-9), "AgOpenGPS: on the drive");
        Assert.That(Math.Abs(Ours()[^1].Easting - _at.e), Is.LessThan(1.0), "within the last metre");
    }

    [Test]
    public void ToolOption_RecordsTheToolsEdge_AsAgOpenGps()
    {
        OpenThePlayer();
        _vm.ToggleBoundaryAntennaToolCommand!.Execute(null); // Tool
        _theirs.btnAntennaTool_Click();
        Assume.That(_vm.IsDrawAtPivot, Is.False);
        StartAt(50, 50, 120);
        RecordPause();
        Drive(8, 120);
        var ours = Ours(); var theirs = _theirs.bndBeingMadePts;
        Assert.That(ours, Has.Count.GreaterThan(4));
        for (int i = 0; i < Math.Min(ours.Count, theirs.Count); i++)
        {
            Assert.That(ours[i].Easting, Is.EqualTo(theirs[i].Easting).Within(1e-9), $"point {i}: the last section's right point");
            Assert.That(ours[i].Northing, Is.EqualTo(theirs[i].Northing).Within(1e-9));
        }
        var (_, lastRight) = AgOpenGpsSectionEdges(_at.e, _at.n, 120 * Math.PI / 180);
        Assert.That(ours[^1].Easting, Is.EqualTo(lastRight.Easting).Within(1.0), "3 m right of the pivot, within the last metre of the drive");
    }

    [Test]
    public void Pause_AddAPoint_DeleteTheLast_AsAgOpenGps()
    {
        OpenThePlayer();
        _vm.BoundaryOffset = 150;
        _theirs.nudOffset_Click(1.5);
        StartAt(100, 100, 0);
        RecordPause();
        Drive(10, 0);
        int before = _rec.PointCount;
        Assume.That(before, Is.EqualTo(_theirs.bndBeingMadePts.Count));

        RecordPause(); // Pause
        Drive(10, 0);
        Assert.That(_rec.PointCount, Is.EqualTo(before), "paused: driving adds nothing");
        Assert.That(_theirs.bndBeingMadePts, Has.Count.EqualTo(before));

        // Add a point by hand: where the drive would have put one, at the pivot plus the offset.
        _vm.ToggleBoundaryAntennaToolCommand!.Execute(null); // Tool: the point is the tool's edge
        _theirs.btnAntennaTool_Click();
        _vm.AddBoundaryPointCommand!.Execute(null);
        var (s0Left, lastRight) = AgOpenGpsSectionEdges(_at.e, _at.n, 0);
        _theirs.btnAddPoint_Click(new Vec2(_at.e, _at.n), new Vec3(_at.e, _at.n, 0), s0Left, lastRight);
        Assert.That(_rec.PointCount, Is.EqualTo(before + 1));
        Assert.That(_theirs.bndBeingMadePts, Has.Count.EqualTo(before + 1));
        Assert.That(Ours()[^1].Easting, Is.EqualTo(_theirs.bndBeingMadePts[^1].Easting).Within(1e-9), "the added point, E");
        Assert.That(Ours()[^1].Northing, Is.EqualTo(_theirs.bndBeingMadePts[^1].Northing).Within(1e-9), "the added point, N");
        Assert.That(Ours()[^1].Easting, Is.EqualTo(_at.e + 3).Within(1e-9), "at the tool's right edge, not the pivot's offset");

        // Delete the last
        _vm.UndoBoundaryPointCommand!.Execute(null);
        _theirs.btnDeleteLast_Click();
        Assert.That(_rec.PointCount, Is.EqualTo(before));
        Assert.That(Ours()[^1].Easting, Is.EqualTo(_theirs.bndBeingMadePts[^1].Easting).Within(1e-9));

        // Record again: the drive goes on from here.
        _vm.ToggleBoundaryAntennaToolCommand!.Execute(null);
        _theirs.btnAntennaTool_Click();
        RecordPause();
        Drive(5, 0);
        Assert.That(_rec.PointCount, Is.GreaterThan(before));
        Assert.That(_rec.PointCount, Is.EqualTo(_theirs.bndBeingMadePts.Count).Within(1));
    }

    [Test]
    public void AnInnerBoundary_IsAddedInsideTheOuter_AsAgOpenGps()
    {
        // The field already has its outer boundary.
        var outer = new BoundaryPolygon();
        foreach (var (e, n) in new[] { (0.0, 0.0), (200.0, 0.0), (200.0, 200.0), (0.0, 200.0) }) outer.Points.Add(new BoundaryPoint(e, n, 0));
        outer.UpdateBounds();
        GeoJsonFieldService.Save(new Field { Name = "F", DirectoryPath = _dir, Origin = new Position { Latitude = 52, Longitude = 5 }, Boundary = new Boundary { OuterBoundary = outer } }, tracks: null);
        _theirs.bndList.Add(new AgOpenGpsIsoXmlOracle.CBoundaryList { fenceLine = outer.Points.Select(p => new Vec3(p.Easting, p.Northing, 0)).ToList() });

        OpenThePlayer(inner: true);
        _vm.BoundaryOffset = 0;
        _theirs.nudOffset_Click(0);
        StartAt(50, 50, 0);
        RecordPause();
        Drive(20, 0); Drive(20, 90); Drive(20, 180); Drive(20, 270);
        _vm.StopBoundaryRecordingCommand!.Execute(null);
        Assert.That(_theirs.btnStop_Click(), Is.True);

        Assert.That(_failure, Is.Null);
        var boundary = new FieldService().LoadField(_dir).Boundary!;
        Assert.That(boundary.OuterBoundary!.AreaHectares, Is.EqualTo(4.0).Within(1e-6), "the outer is untouched");
        Assert.That(boundary.InnerBoundaries, Has.Count.EqualTo(1), "the drive became an inner boundary");
        Assert.That(_theirs.bndList, Has.Count.EqualTo(2));
        Assert.That(boundary.InnerBoundaries[0].AreaHectares * 10000, Is.EqualTo(_theirs.bndList[1].area).Within(_theirs.bndList[1].area * 0.005));
        Assert.That(boundary.InnerBoundaries[0].IsDriveThrough, Is.False, "a recorded inner boundary starts as an obstacle");
    }

    [Test]
    public void AnInnerBoundary_WithNoOuter_IsRefused()
    {
        // #621: the obstacle button on a field with no boundary saved an inner boundary only,
        // and the headland builder then found no boundary. AgOpenGPS's first boundary is the outer.
        _vm.DriveAroundInnerBoundaryCommand!.Execute(null);

        Assert.That(_failure, Does.Contain("field boundary first"));
        Assert.That(_rec.State, Is.EqualTo(BoundaryRecordingState.Idle), "no recording started");
        Assert.That(_vm.IsBoundaryPlayerPanelVisible, Is.False);
    }

    [Test]
    public void RecordingAnInner_WithNoOuter_IsRefused()
    {
        _vm.RecordInnerBoundaryCommand!.Execute(null);

        Assert.That(_failure, Does.Contain("field boundary first"));
        Assert.That(_rec.State, Is.EqualTo(BoundaryRecordingState.Idle));
    }

    [Test]
    public void DrawingAnInnerOnTheMap_WithNoOuter_IsRefused()
    {
        _vm.DrawMapInnerBoundaryCommand!.Execute(null);

        Assert.That(_failure, Does.Contain("field boundary first"));
        Assert.That(_vm.IsInnerBoundaryMode, Is.False, "the map draw stays an outer boundary");
    }

    [Test]
    public void FewerThanThreePoints_MakeNoBoundary_AsAgOpenGps()
    {
        OpenThePlayer();
        StartAt(100, 100, 0);
        RecordPause();
        Drive(1.5, 0); // two points
        Assume.That(_rec.PointCount, Is.LessThan(3));
        _vm.StopBoundaryRecordingCommand!.Execute(null);
        Assert.That(_theirs.btnStop_Click(), Is.False, "AgOpenGPS: 'No boundary'");
        Assert.That(_failure, Does.Contain("3 points"));
        Assert.That(new FieldService().LoadField(_dir).Boundary?.OuterBoundary?.IsValid ?? false, Is.False, "nothing saved");
    }
}
