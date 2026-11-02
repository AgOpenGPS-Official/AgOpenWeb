// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Track;
using AgOpenWeb.Services;
using AgOpenWeb.Services.GeoJson;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// Checklist 1.3 (an AB line from two driven points) and 1.5 (a curve recorded while driving),
/// held to AgOpenGPS's FormQuickAB: A and B at the pivot, the line's heading, the reference
/// line nudged to the tool's side (TrackNudgingService.NudgeABLine); a curve's points as the
/// drive adds them, then MakePointMinimumSpacing(1.6), CalculateHeadings, SmoothAB(4) and
/// NudgeRefCurve. The same fixes go through the real GPS handler here; the oracle is inline.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class DrivenAbAndCurveMatchAgOpenGpsTests
{
    private const double ToolWidth = 6, Overlap = 0.2, ToolOffset = 0.3;
    private string _root = null!, _dir = null!;
    private MainViewModelBuilder _b = null!;
    private MainViewModel _vm = null!;
    private string? _failure;

    [SetUp]
    public void SetUp()
    {
        var config = new ConfigurationStore();
        ConfigurationStore.SetInstance(config);
        config.NumSections = 1;
        config.Tool.SetSectionWidth(0, ToolWidth * 100);
        config.Tool.Overlap = Overlap;
        config.Tool.Offset = ToolOffset;
        _root = Path.Combine(Path.GetTempPath(), "aow-driveab-" + Guid.NewGuid().ToString("N"));
        _dir = Path.Combine(_root, "F");
        Directory.CreateDirectory(_dir);
        var field = new Field { Name = "F", DirectoryPath = _dir, Origin = new Position { Latitude = 52, Longitude = 5 } };
        GeoJsonFieldService.Save(field, tracks: null);
        _b = new MainViewModelBuilder { FieldService = new FieldService() };
        _b.SettingsService.Settings.Returns(new AppSettings { FieldsDirectory = _root });
        _vm = _b.Build();
        _vm.FailureReported += m => _failure = m;
        _vm.ActiveField = field;
        ((FieldService)_b.FieldService).SetActiveField(field); // the tracks file goes to the field service's field
        _vm.IsFieldOpen = true;
        _vm.TrackRefSideRight = true;
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, true); } catch { }
        ConfigurationStore.SetInstance(new ConfigurationStore());
    }

    private (double e, double n, double headingDeg) _at;

    private void Fix(double e, double n, double headingDeg)
    {
        _vm.State.Vehicle.Easting = e;
        _vm.State.Vehicle.Northing = n;
        _vm.State.Vehicle.Heading = headingDeg;
        _b.GpsService.GpsDataUpdated += Raise.Event<EventHandler<GpsData>>(_b.GpsService,
            new GpsData { CurrentPosition = new Position { Easting = e, Northing = n, Heading = headingDeg, Latitude = 52, Longitude = 5 } });
        _at = (e, n, headingDeg);
    }

    // ───────────── AgOpenGPS ─────────────

    /// <summary>dist = (width - overlap) * ±0.5 + offset; NudgeABLine: both points moved along heading + π/2.</summary>
    private static double RefDist(bool isRefRightSide) => (ToolWidth - Overlap) * (isRefRightSide ? 0.5 : -0.5) + ToolOffset;

    private static Vec2 Nudged(Vec2 p, double heading, double dist)
    {
        double perpHeading = heading + Math.PI / 2;
        return new Vec2(p.Easting + Math.Sin(perpHeading) * dist, p.Northing + Math.Cos(perpHeading) * dist);
    }

    private static double DistanceToLine(Vec2 p, Vec3 a, Vec3 b)
    {
        double dx = b.Easting - a.Easting, dy = b.Northing - a.Northing;
        return Math.Abs(dx * (a.Northing - p.Northing) - (a.Easting - p.Easting) * dy) / Math.Sqrt(dx * dx + dy * dy);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void AbFromTwoDrivenPoints_IsTheNudgedLineThroughThem(bool refRight)
    {
        _vm.TrackRefSideRight = refRight;
        // btnALine: desPtA = pivotAxlePos
        Fix(100, 100, 30);
        _vm.StartDriveABCommand!.Execute(null);
        _vm.SetABPointCommand!.Execute(null);
        var desPtA = new Vec2(100, 100);
        // drive on, the tractor wandering a little: the line is A to B, not the drive
        Fix(110, 118, 28); Fix(121, 136, 33);
        // btnBLine: desPtB = pivotAxlePos; desHeading = atan2(B - A)
        Fix(130, 152, 31);
        _vm.SetABPointCommand.Execute(null);
        var desPtB = new Vec2(130, 152);
        double desHeading = Math.Atan2(desPtB.Easting - desPtA.Easting, desPtB.Northing - desPtA.Northing);
        if (desHeading < 0) desHeading += Math.PI * 2;
        // btnEnter_AB: the line, nudged to the reference side
        double dist = RefDist(refRight);
        var theirA = Nudged(desPtA, desHeading, dist);
        var theirB = Nudged(desPtB, desHeading, dist);

        Assert.That(_failure, Is.Null);
        var track = _vm.SelectedTrack!;
        Assert.That(track.IsABLine, Is.True);
        var ourA = track.Points[0]; var ourB = track.Points[1];
        double ourHeading = Math.Atan2(ourB.Easting - ourA.Easting, ourB.Northing - ourA.Northing);
        if (ourHeading < 0) ourHeading += Math.PI * 2;
        Assert.Multiple(() =>
        {
            Assert.That(ourHeading, Is.EqualTo(desHeading).Within(1e-9), "the heading of A to B");
            Assert.That(DistanceToLine(theirA, ourA, ourB), Is.LessThan(1e-6), "AgOpenGPS's nudged A lies on our line");
            Assert.That(DistanceToLine(theirB, ourA, ourB), Is.LessThan(1e-6), "AgOpenGPS's nudged B lies on our line");
            Assert.That(DistanceToLine(desPtA, ourA, ourB), Is.EqualTo(Math.Abs(dist)).Within(1e-6), "the drive is a half tool (less overlap, plus the offset) off the line");
            Assert.That(GeoJsonFieldService.LoadTracks(_dir), Has.Count.EqualTo(1), "saved with the field");
            Assert.That(_vm.CurrentABCreationMode, Is.EqualTo(ABCreationMode.None), "the flow is over");
        });
        // Ours extends the line past the field (AgOpenGPS draws it 1000 m long); the name is ours.
        Assert.That(track.Name, Does.StartWith("AB_"));
    }

    [Test]
    public void TheAPoint_IsWhereTheTractorIs_NotWhereTheStatusBarLastSawIt()
    {
        // The status-bar mirror of the position (Easting/Northing) lags the cycle by up to 200 ms.
        Fix(100, 100, 0);
        _vm.State.Vehicle.Easting = 100.44; // the cycle moved on (8 km/h for 200 ms); the mirror hasn't
        _vm.StartDriveABCommand!.Execute(null);
        _vm.SetABPointCommand!.Execute(null);
        Assert.That(_vm.PendingPointA!.Easting, Is.EqualTo(100.44), "A is the cycle's pivot, as AgOpenGPS's pivotAxlePos");
    }

    // ───────────── curve ─────────────

    /// <summary>A gentle S: 150 m north with a 6 m sway, a fix every 0.5 m.</summary>
    private static (double e, double n, double headingDeg) SAt(double s)
    {
        double e = 100 + 6 * Math.Sin(s / 150 * 2 * Math.PI);
        double n = 100 + s;
        double de = 6 * Math.Cos(s / 150 * 2 * Math.PI) * 2 * Math.PI / 150;
        return (e, n, Math.Atan2(de, 1) * 180 / Math.PI);
    }

    /// <summary>CABCurve.MakePointMinimumSpacing: midpoints inserted until no gap is over minDistance.</summary>
    private static void MakePointMinimumSpacing(List<Vec3> xList, double minDistance)
    {
        int cnt = xList.Count;
        if (cnt > 3)
        {
            for (int i = 0; i < cnt - 1; i++)
            {
                int j = i + 1;
                double distance = Dist(xList[i], xList[j]);
                if (distance > minDistance)
                {
                    Vec3 pointB = new Vec3((xList[i].Easting + xList[j].Easting) / 2.0,
                        (xList[i].Northing + xList[j].Northing) / 2.0,
                        xList[i].Heading);
                    xList.Insert(j, pointB);
                    cnt = xList.Count;
                    i = -1;
                }
            }
        }
    }

    private static double Dist(Vec3 a, Vec3 b) => Math.Sqrt((a.Easting - b.Easting) * (a.Easting - b.Easting) + (a.Northing - b.Northing) * (a.Northing - b.Northing));

    /// <summary>CABCurve.CalculateHeadings (the curve one: ends from their neighbour).</summary>
    private static void CalculateHeadings(List<Vec3> xList)
    {
        int cnt = xList.Count;
        if (cnt > 3)
        {
            Vec3[] arr = new Vec3[cnt];
            cnt--;
            xList.CopyTo(arr);
            xList.Clear();
            Vec3 pt3 = arr[0];
            pt3.Heading = Math.Atan2(arr[1].Easting - arr[0].Easting, arr[1].Northing - arr[0].Northing);
            if (pt3.Heading < 0) pt3.Heading += Math.PI * 2;
            xList.Add(pt3);
            for (int i = 1; i < cnt; i++)
            {
                pt3 = arr[i];
                pt3.Heading = Math.Atan2(arr[i + 1].Easting - arr[i - 1].Easting, arr[i + 1].Northing - arr[i - 1].Northing);
                if (pt3.Heading < 0) pt3.Heading += Math.PI * 2;
                xList.Add(pt3);
            }
            pt3 = arr[arr.Length - 1];
            pt3.Heading = Math.Atan2(arr[arr.Length - 1].Easting - arr[arr.Length - 2].Easting,
                arr[arr.Length - 1].Northing - arr[arr.Length - 2].Northing);
            if (pt3.Heading < 0) pt3.Heading += Math.PI * 2;
            xList.Add(pt3);
        }
    }

    /// <summary>FormQuickAB.SmoothAB(smPts): a window average, the ends left alone.</summary>
    private static void SmoothAB(List<Vec3> desList, int smPts)
    {
        int cnt = desList.Count;
        Vec3[] arr = new Vec3[cnt];
        for (int s = 0; s < smPts / 2; s++) arr[s] = desList[s];
        for (int s = cnt - (smPts / 2); s < cnt; s++) arr[s] = desList[s];
        for (int i = smPts / 2; i < cnt - (smPts / 2); i++)
        {
            double e = 0, n = 0;
            for (int j = -smPts / 2; j < smPts / 2; j++)
            {
                e += desList[j + i].Easting;
                n += desList[j + i].Northing;
            }
            arr[i] = new Vec3(e / smPts, n / smPts, desList[i].Heading);
        }
        desList.Clear();
        desList.AddRange(arr);
    }

    /// <summary>TrackNudgingService.NudgeCurve: each point moved square to its heading, points that
    /// land within the distance of the original dropped, re-spaced at 1.2 m with Catmull-Rom.</summary>
    private static List<Vec3> NudgeCurve(List<Vec3> curvePoints, double distance)
    {
        var result = new List<Vec3>();
        if (curvePoints.Count < 6) return result;
        List<Vec3> curList = new List<Vec3>();
        double distSqAway = (distance * distance) - 0.01;
        for (int i = 0; i < curvePoints.Count; i++)
        {
            Vec3 point = new Vec3(
                curvePoints[i].Easting + (Math.Sin(Math.PI / 2 + curvePoints[i].Heading) * distance),
                curvePoints[i].Northing + (Math.Cos(Math.PI / 2 + curvePoints[i].Heading) * distance),
                curvePoints[i].Heading);
            bool add = true;
            for (int t = 0; t < curvePoints.Count; t++)
            {
                double dist = ((point.Easting - curvePoints[t].Easting) * (point.Easting - curvePoints[t].Easting))
                    + ((point.Northing - curvePoints[t].Northing) * (point.Northing - curvePoints[t].Northing));
                if (dist < distSqAway) { add = false; break; }
            }
            if (add)
            {
                if (curList.Count > 0)
                {
                    double dist = ((point.Easting - curList[^1].Easting) * (point.Easting - curList[^1].Easting))
                        + ((point.Northing - curList[^1].Northing) * (point.Northing - curList[^1].Northing));
                    if (dist > 1.0) curList.Add(point);
                }
                else curList.Add(point);
            }
        }
        int cnt = curList.Count;
        if (cnt < 6) return result;
        Vec3[] arr = curList.ToArray();
        curList.Clear();
        for (int i = 0; i < (arr.Length - 1); i++)
        {
            arr[i].Heading = Math.Atan2(arr[i + 1].Easting - arr[i].Easting, arr[i + 1].Northing - arr[i].Northing);
            if (arr[i].Heading < 0) arr[i].Heading += Math.PI * 2;
        }
        arr[^1].Heading = arr[^2].Heading;
        cnt = arr.Length;
        double spacing = 1.2;
        curList.Add(arr[0]);
        for (int i = 0; i < cnt - 3; i++)
        {
            curList.Add(arr[i + 1]);
            double d = Dist(arr[i + 1], arr[i + 2]);
            if (d > spacing)
            {
                int loopTimes = (int)(d / spacing + 1);
                for (int j = 1; j < loopTimes; j++)
                    curList.Add(GeometryMath.Catmull(j / (double)(loopTimes), arr[i], arr[i + 1], arr[i + 2], arr[i + 3]));
            }
        }
        curList.Add(arr[cnt - 2]);
        curList.Add(arr[cnt - 1]);
        CalculateHeadings(curList);
        return curList;
    }

    private static double DistanceToPolyline(Vec3 p, IReadOnlyList<Vec3> line)
    {
        double best = double.MaxValue;
        for (int i = 0; i + 1 < line.Count; i++)
        {
            var a = line[i]; var b = line[i + 1];
            double dx = b.Easting - a.Easting, dy = b.Northing - a.Northing;
            double len2 = dx * dx + dy * dy;
            double t = len2 < 1e-12 ? 0 : Math.Clamp(((p.Easting - a.Easting) * dx + (p.Northing - a.Northing) * dy) / len2, 0, 1);
            double ex = a.Easting + t * dx - p.Easting, ey = a.Northing + t * dy - p.Northing;
            best = Math.Min(best, Math.Sqrt(ex * ex + ey * ey));
        }
        return best;
    }

    [Test]
    public void CurveRecordedWhileDriving_IsSmooth_AndLiesWhereAgOpenGpsPutsIt()
    {
        // AgOpenGPS adds a point every sectionTriggerStepDistance of travel: 0.75 x the tool
        // width (at most 8 m, at least 1 m), halved while a curve is recorded: 2.25 m for 6 m.
        const double theirStep = 0.75 * ToolWidth * 0.5;
        var desList = new List<Vec3>();
        double sinceTrigger = 0, lastS = 0;

        var (e0, n0, h0) = SAt(0);
        Fix(e0, n0, h0);
        _vm.StartCurveRecordingCommand!.Execute(null); // btnACurve: ptAa = pivot, recording on
        for (double s = 0.5; s <= 150; s += 0.5)
        {
            var (e, n, h) = SAt(s);
            Fix(e, n, h);
            sinceTrigger += s - lastS; lastS = s;
            if (sinceTrigger > theirStep) { desList.Add(new Vec3(e, n, h * Math.PI / 180)); sinceTrigger = 0; }
        }
        _vm.FinishCurveRecordingCommand!.Execute(null); // btnBCurve
        Assert.That(_failure, Is.Null);

        // btnBCurve: spacing, headings, smoothing (the 100 m end extension left out: the drive is compared), the nudge
        MakePointMinimumSpacing(desList, 1.6);
        CalculateHeadings(desList);
        SmoothAB(desList, 4);
        CalculateHeadings(desList);
        var theirs = NudgeCurve(desList, RefDist(true));
        Assume.That(theirs, Has.Count.GreaterThan(100));

        var track = _vm.SelectedTrack!;
        Assert.That(track.IsCurve, Is.True);
        // Our track runs on past the drive's ends (20 m): compare the driven part.
        var ours = track.Points.Where(p => p.Northing >= 102 && p.Northing <= 248).ToList();
        var theirsIn = theirs.Where(p => p.Northing >= 102 && p.Northing <= 248).ToList();
        Assert.That(ours, Has.Count.GreaterThan(50));
        Assert.That(ours.Max(p => DistanceToPolyline(p, theirs)), Is.LessThan(0.05), "our curve lies on AgOpenGPS's");
        Assert.That(theirsIn.Max(p => DistanceToPolyline(p, track.Points)), Is.LessThan(0.05), "and AgOpenGPS's on ours");
        // Smooth: no kink; the S turns at most about 2.9° a metre at its tightest.
        double maxTurnPerMetre = 0;
        for (int i = 1; i + 1 < ours.Count; i++)
        {
            double h1 = Math.Atan2(ours[i].Easting - ours[i - 1].Easting, ours[i].Northing - ours[i - 1].Northing);
            double h2 = Math.Atan2(ours[i + 1].Easting - ours[i].Easting, ours[i + 1].Northing - ours[i].Northing);
            double d = Math.Abs(h2 - h1); if (d > Math.PI) d = 2 * Math.PI - d;
            maxTurnPerMetre = Math.Max(maxTurnPerMetre, d * 180 / Math.PI / Math.Max(0.5, Dist(ours[i], ours[i + 1])));
        }
        Assert.That(maxTurnPerMetre, Is.LessThan(4.0), "smooth: degrees per metre between points");
        // The reference line sits a half tool (less overlap) plus the offset to the right of the drive.
        foreach (double s in new[] { 30.0, 75.0, 120.0 })
        {
            var (de, dn, dh) = SAt(s);
            var expected = Nudged(new Vec2(de, dn), dh * Math.PI / 180, RefDist(true));
            Assert.That(DistanceToPolyline(new Vec3(expected.Easting, expected.Northing, 0), track.Points), Is.LessThan(0.05), $"the nudged drive point at {s} m lies on our line");
        }
    }

    [Test]
    public void TheCurvesFirstPoint_IsTheCyclesPivot()
    {
        Fix(100, 100, 0);
        _vm.State.Vehicle.Easting = 100.44; // the cycle moved on; the status mirror hasn't
        _vm.StartCurveRecordingCommand!.Execute(null);
        Assert.That(_vm.RecordedCurvePointCount, Is.EqualTo(1));
        Fix(100.44, 101.0, 0); Fix(100.44, 102.0, 0);
        _vm.FinishCurveRecordingCommand!.Execute(null);
        // Three points: the first at the cycle's pivot, not at 100.0.
        Assert.That(_failure, Is.Null);
    }
}
