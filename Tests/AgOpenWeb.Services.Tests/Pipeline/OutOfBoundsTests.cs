// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services;
using AgOpenWeb.Services.AutoSteer;
using AgOpenWeb.Services.Coverage;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Pipeline;
using AgOpenWeb.Services.Section;
using AgOpenWeb.Services.Tool;
using AgOpenWeb.Services.Track;
using AgOpenWeb.Services.YouTurn;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgOpenWeb.Services.Tests.Pipeline;

/// <summary>
/// #494: leaving the field never switches autosteer off, as in AgOpenGPS. The pivot outside the
/// outer boundary or inside an inner one is out of bounds: the cycle says so (the web draws the
/// boundary red) and PGN 239 carries geoStop = 1 for the machine module. With automatic U-turns
/// on, inside the turn area (the headland line) is always in bounds.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class OutOfBoundsTests
{
    private const double Lat0 = 43.7128, Lon0 = -74.006;
    private const double MetresPerDegLat = 111_132; // close enough at this latitude for ±100 m

    private GpsService _gpsService = null!;
    private GpsPipelineService _pipeline = null!;
    private AutoSteerService _autoSteer = null!;
    private IUdpCommunicationService _udp = null!;
    private List<GpsCycleResult> _results = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var config = ConfigurationStore.Instance;
        config.Vehicle.AntennaPivot = 0;
        config.Vehicle.AntennaOffset = 0;
        config.Vehicle.AntennaHeight = 0;
        config.Tool.Width = 6;
        config.NumSections = 1;
        config.Tool.SetSectionWidth(0, 600);

        _gpsService = new GpsService();
        _gpsService.Start();

        var toolPosition = new ToolPositionService(config);
        var coverage = new CoverageMapService(config);
        var sectionControl = new SectionControlService(toolPosition, coverage, config);
        _udp = Substitute.For<IUdpCommunicationService>();
        _autoSteer = new AutoSteerService(new TrackGuidanceService(), _udp, _gpsService, config);
        _autoSteer.Start(); // sends PGNs (GeoStop reads PGN 239)

        var headingFusion = Substitute.For<IGpsHeadingFusionService>();
        headingFusion.FuseHeading(Arg.Any<double>(), Arg.Any<double>(), Arg.Any<bool>(),
                Arg.Any<double>(), Arg.Any<double>(), Arg.Any<double>(), Arg.Any<bool>())
            .Returns(ci => ci.ArgAt<double>(0));

        _pipeline = new GpsPipelineService(
            _gpsService, toolPosition, new TrackGuidanceService(),
            sectionControl, coverage, _autoSteer,
            new YouTurnGuidanceService(),
            new YouTurnStateMachine(
                new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance,
                    Substitute.For<AgOpenWeb.Services.Geometry.IPolygonOffsetService>(), config),
                new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, config),
                NullLogger<YouTurnStateMachine>.Instance, config),
            Substitute.For<IAudioService>(),
            new PipelineIntents(),
            headingFusion,
            NullLogger<GpsPipelineService>.Instance,
            config,
            new PositionEstimator());

        _pipeline.SetLocalPlane(new LocalPlane(new Wgs84(Lat0, Lon0), new SharedFieldProperties()));
        _pipeline.SynchronousMode = true;
        _pipeline.Start();

        _results = new List<GpsCycleResult>();
        _pipeline.CycleCompleted += r => _results.Add(r);

        var track = new Models.Track.Track
        {
            Name = "AB", Type = Models.Track.TrackType.ABLine,
            Points = new List<Vec3> { new(0, -200, 0), new(0, 200, 0) },
        };
        _pipeline.SetActiveTrack(track, passNumber: 0, nudgeOffset: 0);
        _pipeline.SetAutoSteerEngaged(true);
    }

    [TearDown]
    public void TearDown()
    {
        _pipeline.Stop();
        _autoSteer.Stop();
        _gpsService.Stop();
    }

    private static BoundaryPolygon Square(double half, bool driveThrough = false) => new()
    {
        Points = new List<BoundaryPoint>
        {
            new(-half, -half, 0), new(half, -half, 0), new(half, half, 0), new(-half, half, 0),
        },
        IsDriveThrough = driveThrough,
    };

    private static List<Vec3> SquareLine(double half) => new()
    {
        new(-half, -half, 0), new(half, -half, 0), new(half, half, 0), new(-half, half, 0),
    };

    // Drive north at (0, northing) for a few cycles; the last cycle's result.
    private GpsCycleResult DriveAt(double northing)
    {
        for (int i = 0; i < 3; i++)
            _gpsService.UpdateGpsData(new GpsData
            {
                CurrentPosition = new Position
                {
                    Latitude = Lat0 + northing / MetresPerDegLat, Longitude = Lon0, Heading = 0, Speed = 3,
                },
                FixQuality = 4,
                IsValid = true,
            });
        return _results[^1];
    }

    // Byte 9 of the PGN 239 the next control tick sends.
    private byte GeoStop()
    {
        var sent = new List<byte[]>();
        _udp.When(u => u.SendToModules(Arg.Any<byte[]>()))
            .Do(c => sent.Add((byte[])c.Arg<byte[]>().Clone()));
        _autoSteer.SendPgnsForControlTick();
        return sent.Last(p => p[3] == 0xEF)[9];
    }

    [Test]
    public void Inside_TheField_IsInBounds()
    {
        _pipeline.SetBoundary(new Boundary { OuterBoundary = Square(50) });

        var r = DriveAt(0);

        Assert.Multiple(() =>
        {
            Assert.That(r.IsOutOfBounds, Is.False);
            Assert.That(GeoStop(), Is.EqualTo(0));
        });
    }

    [Test]
    public void Outside_TheField_SteeringStaysOn_AndGeoStopIsSent()
    {
        _pipeline.SetBoundary(new Boundary { OuterBoundary = Square(50) });

        var r = DriveAt(80);

        Assert.Multiple(() =>
        {
            Assert.That(r.IsOutOfBounds, Is.True);
            Assert.That(r.IsAutoSteerEngaged, Is.True, "AgOpenGPS never switches autosteer off here");
            Assert.That(r.AutoSteerDisengagedThisCycle, Is.False);
            Assert.That(r.DisengageReason, Is.Null);
            Assert.That(GeoStop(), Is.EqualTo(1));
        });

        Assert.That(DriveAt(0).IsOutOfBounds, Is.False, "back in the field");
        Assert.That(GeoStop(), Is.EqualTo(0));
    }

    [Test]
    public void Inside_AnInnerBoundary_IsOutOfBounds_DriveThroughToo()
    {
        var pond = new Boundary { OuterBoundary = Square(50) };
        pond.InnerBoundaries.Add(Square(10));
        _pipeline.SetBoundary(pond);
        Assert.That(DriveAt(0).IsOutOfBounds, Is.True);

        var drive = new Boundary { OuterBoundary = Square(50) };
        drive.InnerBoundaries.Add(Square(10, driveThrough: true));
        _pipeline.SetBoundary(drive);
        Assert.That(DriveAt(0).IsOutOfBounds, Is.True, "an inner boundary is an obstacle (AgOpenGPS IsPointInsideFenceArea)");
    }

    [Test]
    public void NoBoundary_IsNeverOutOfBounds()
    {
        Assert.That(DriveAt(500).IsOutOfBounds, Is.False);
        Assert.That(GeoStop(), Is.EqualTo(0));
    }

    [Test]
    public void WithUTurnsOn_InsideTheTurnArea_IsInBounds_EvenOverAnInnerBoundary()
    {
        // AgOpenGPS: IsPointInsideTurnArea != -1 counts an inner boundary's area too.
        var pond = new Boundary { OuterBoundary = Square(50) };
        pond.InnerBoundaries.Add(Square(10));
        _pipeline.SetBoundary(pond);
        _pipeline.SetHeadlandLine(SquareLine(35));
        _pipeline.SetYouTurnEnabled(true);

        Assert.Multiple(() =>
        {
            Assert.That(DriveAt(0).IsOutOfBounds, Is.False, "in the turn area");
            Assert.That(DriveAt(42).IsOutOfBounds, Is.False, "in the headland, inside the fence");
            Assert.That(DriveAt(80).IsOutOfBounds, Is.True, "outside the fence, no turn under way");
        });
    }
}
