// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Sections;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services.AutoSteer;
using AgOpenWeb.Services.Coverage;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Pipeline;
using AgOpenWeb.Services.Section;
using AgOpenWeb.Services.Tool;
using AgOpenWeb.Services.Track;
using AgOpenWeb.Services.YouTurn;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgOpenWeb.Services.Tests.Performance;

public enum PassTrack { ABLine, Curve }
public enum Skip { Normal, SkipWorked, Alternate }

/// <summary>
/// A large field driven through the real GPS cycle (<see cref="GpsPipelineService"/>) and the
/// 100 Hz control tick (tool position + section control, as MainViewModel.OnControlLoopTicked),
/// recording the <see cref="WorkCounters"/> each one costs (one control tick sampled per fix). 300 × 500 m, a boundary point
/// every metre, the headland 24 m in (also a point a metre) or none, a 12 m tool in 6 sections, auto
/// U-turn and auto sections on. The tractor drives north up one pass, from mid-field to the
/// end of the pass in the headland, where it waits.
/// </summary>
public sealed class HotPathHarness : IDisposable
{
    public const double FieldWidth = 300, FieldLength = 500, HeadlandWidth = 24;
    public const double ToolWidth = 12;
    public const double StepPerFix = 0.28; // 10 km/h at 10 Hz
    // The tick runs ten times per fix on the tractor; one sampled per fix holds every position
    // along the drive to the per-tick budget at a tenth of the test time.
    public const int ControlTicksPerFix = 1;

    private readonly GpsService _gps;
    private readonly GpsPipelineService _pipeline;
    private readonly ToolPositionService _tool;
    private readonly SectionControlService _sections;
    private readonly CoverageMapService _coverage;
    private readonly LocalPlane _plane;

    public List<WorkCounters.Snapshot> Fixes { get; } = new();
    public List<WorkCounters.Snapshot> ControlTicks { get; } = new();
    public GpsCycleResult? Last { get; private set; }


    public HotPathHarness(PassTrack trackKind, Skip skip, bool fieldWorked, bool hasHeadland = true)
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var config = ConfigurationStore.Instance;
        config.Vehicle.Wheelbase = 3;
        config.Vehicle.AntennaPivot = 0;
        config.Vehicle.AntennaOffset = 0;
        config.Vehicle.AntennaHeight = 0;
        config.Tool.Width = ToolWidth;
        config.Tool.Overlap = 0;
        config.NumSections = 6;
        for (int i = 0; i < 6; i++) config.Tool.SetSectionWidth(i, (int)(ToolWidth / 6 * 100));
        config.Guidance.UTurnRadius = 8;
        config.Guidance.UTurnDistanceFromBoundary = 2;

        _gps = new GpsService();
        _gps.Start();
        _tool = new ToolPositionService(config);
        _coverage = new CoverageMapService(config);
        _sections = new SectionControlService(_tool, _coverage, config);
        var autoSteer = new AutoSteerService(new TrackGuidanceService(),
            Substitute.For<IUdpCommunicationService>(), _gps, config);
        var headingFusion = Substitute.For<IGpsHeadingFusionService>();
        headingFusion.FuseHeading(Arg.Any<double>(), Arg.Any<double>(), Arg.Any<bool>(),
                Arg.Any<double>(), Arg.Any<double>(), Arg.Any<double>(), Arg.Any<bool>())
            .Returns(ci => ci.ArgAt<double>(0));

        var offset = new PolygonOffsetService();
        _pipeline = new GpsPipelineService(
            _gps, _tool, new TrackGuidanceService(),
            _sections, _coverage, autoSteer,
            new YouTurnGuidanceService(),
            new YouTurnStateMachine(
                new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance, offset, config),
                new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, config),
                NullLogger<YouTurnStateMachine>.Instance, config),
            Substitute.For<IAudioService>(),
            new PipelineIntents(),
            headingFusion,
            NullLogger<GpsPipelineService>.Instance,
            config,
            new PositionEstimator());

        _plane = new LocalPlane(new Wgs84(43.7128, -74.006), new SharedFieldProperties());
        _pipeline.SetLocalPlane(_plane);
        _pipeline.SynchronousMode = true;
        _pipeline.CycleCompleted += r => Last = r;
        _pipeline.Start();

        var outer = Ring(0);
        var boundary = new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = outer.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
            },
        };
        var headland = Ring(HeadlandWidth).Select(p => new Vec3(p.Easting, p.Northing, 0)).ToList();

        // Passes run north. The track lies inside the cultivated area: the snake plans nothing
        // from a track whose pass 0 is in the headland.
        var track = trackKind == PassTrack.ABLine
            ? Models.Track.Track.FromABLine("AB", new Vec3(TrackEasting, -FieldLength, 0), new Vec3(TrackEasting, FieldLength, 0))
            : Models.Track.Track.FromCurve("Curve", Enumerable.Range(0, 1001)
                .Select(i => new Vec3(TrackEasting + 3 * Math.Sin(i / 80.0), -FieldLength + i, 0)).ToList());

        _pipeline.SetHasActiveField(true);
        _pipeline.SetBoundary(boundary);
        // Many fields have only the outer boundary: the pipeline then turns at a line inset from
        // the fence (its synthetic headland), and section control has no headland to test.
        var headlandLine = hasHeadland ? headland : null;
        _pipeline.SetHeadlandLine(headlandLine);
        _pipeline.SetHeadlandOn(hasHeadland);
        _sections.SetFieldContext(new SectionFieldContext(boundary, headlandLine, true, hasHeadland));
        _sections.SetAllAuto();
        _coverage.SetFieldBounds(-FieldWidth / 2, FieldWidth / 2, -FieldLength / 2, FieldLength / 2);
        if (fieldWorked)
            _coverage.MarkRectangleCovered(-FieldWidth / 2, FieldWidth / 2, -FieldLength / 2, FieldLength / 2);

        _pipeline.SetActiveTrack(track, Pass, 0, false);
        _pipeline.SetYouTurnConfig(
            uTurnSkipRows: skip == Skip.Normal ? 0 : 1,
            isSkipWorkedMode: skip == Skip.SkipWorked,
            headlandCalculatedWidth: hasHeadland ? HeadlandWidth : 0,
            headlandDistance: hasHeadland ? HeadlandWidth : 0,
            isAlternateSkipMode: skip == Skip.Alternate);
        _pipeline.SetYouTurnEnabled(true);
        _pipeline.SetAutoSteerEngaged(true);
    }

    public const double TrackEasting = -100;

    /// <summary>The pass driven: mid-field, with passes either side.</summary>
    public const int Pass = 8;
    public static double PassEasting => TrackEasting + Pass * ToolWidth + ToolWidth / 2;

    // The field's outline inset by `inset`, a point every metre.
    private static List<Vec2> Ring(double inset)
    {
        var r = new List<Vec2>();
        double x0 = -FieldWidth / 2 + inset, x1 = FieldWidth / 2 - inset;
        double y0 = -FieldLength / 2 + inset, y1 = FieldLength / 2 - inset;
        for (double x = x0; x < x1; x++) r.Add(new(x, y0));
        for (double y = y0; y < y1; y++) r.Add(new(x1, y));
        for (double x = x1; x > x0; x--) r.Add(new(x, y1));
        for (double y = y1; y > y0; y--) r.Add(new(x0, y));
        return r;
    }

    /// <summary>
    /// Drive north up the pass from <paramref name="fromNorthing"/> to
    /// <paramref name="toNorthing"/>, then stand there for <paramref name="waitFixes"/> fixes
    /// (still moving slowly, as at the end of a pass waiting for a turn).
    /// </summary>
    public void Drive(double fromNorthing, double toNorthing, int waitFixes = 0)
    {
        double e = PassEasting;
        for (double n = fromNorthing; n <= toNorthing; n += StepPerFix) Fix(e, n);
        for (int i = 0; i < waitFixes; i++) Fix(e, toNorthing + (i % 2) * 0.01);
    }

    private void Fix(double e, double n)
    {
        var ll = _plane.ConvertGeoCoordToWgs84(new GeoCoord(n, e));
        var before = WorkCounters.Take();
        _gps.UpdateGpsData(new GpsData
        {
            CurrentPosition = new Position
            {
                Latitude = ll.Latitude, Longitude = ll.Longitude,
                Heading = 0, Speed = StepPerFix * 10,
            },
            FixQuality = 4,
            SatellitesInUse = 14,
            Hdop = 0.7,
            IsValid = true,
        });
        Fixes.Add(WorkCounters.Take() - before);

        for (int t = 0; t < ControlTicksPerFix; t++)
        {
            double tn = n + StepPerFix * (t + 0.5) / ControlTicksPerFix;
            before = WorkCounters.Take();
            _tool.Update(new Vec3(e, tn, 0), 0);
            _sections.Update(_tool.ToolPosition, _tool.ToolHeading, 0, StepPerFix * 10);
            ControlTicks.Add(WorkCounters.Take() - before);
        }
    }

    public void Dispose()
    {
        _pipeline.Stop();
        _gps.Stop();
    }
}
