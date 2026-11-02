// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.AutoSteer;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Tool;
using AgOpenWeb.Services.Tram;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// #541: the tram wheels (PGN 239's tram byte, the tram lights) are checked where the tool is,
/// as AgOpenGPS reads its control byte off the pixels under the tool. The control loop used to
/// project the hitch length ahead of the tractor, so a tram line lit as the tractor reached it
/// and went dark before the implement did; with the implement 10 m back, the lights came on
/// 10 m early.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class TramTriggerAtToolTests
{
    private const double HitchLength = 2.0;   // rear-fixed tool, working centre 2 m behind the axle
    private const double TrailingArm = 2.0;   // set but unused for a rigid tool, as in the report

    private ConfigurationStore _config = null!;
    private ToolPositionService _toolPosition = null!;
    private TramLineService _tram = null!;
    private AutoSteerService _autoSteer = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _config = ConfigurationStore.Instance;
        _config.Vehicle.AntennaPivot = 0;
        _config.Vehicle.AntennaOffset = 0;
        _config.Vehicle.TrackWidth = 1.8;
        _config.Tool.IsToolRearFixed = true;
        _config.Tool.IsToolTrailing = _config.Tool.IsToolTBT = _config.Tool.IsToolFrontFixed = false;
        _config.Tool.HitchLength = HitchLength;
        _config.Tool.TrailingHitchLength = TrailingArm;
        _config.Tool.Offset = 0;
        _config.Tool.Width = 6;
        _config.NumSections = 1;
        _config.Tool.SetSectionWidth(0, 600);
        _config.Tram.TramWidth = 6;             // one tool width: the trams run about the tool's centre
        _config.Tram.IsOuterInverted = false;
        _config.Tram.DisplayMode = TramDisplayMode.All;

        _toolPosition = new ToolPositionService(_config);
        _tram = new TramLineService(new TramLineOffsetService(), NullLogger<TramLineService>.Instance, _config);
        _autoSteer = new AutoSteerService(Substitute.For<ITrackGuidanceService>(),
            Substitute.For<IUdpCommunicationService>(), Substitute.For<IGpsService>(), _config);
        _autoSteer.SetTramLineService(_tram);
        _autoSteer.SetToolPositionService(_toolPosition);
        _autoSteer.Start();
    }

    [TearDown]
    public void TearDown()
    {
        _autoSteer.Stop();
        ConfigurationStore.SetInstance(new ConfigurationStore());
    }

    /// <summary>The control loop's job: the tractor at the origin heading north.</summary>
    private void SettleToolPosition()
    {
        _toolPosition.Update(new Vec3(0, 0, 0), 0);
        Assume.That(_toolPosition.HasPosition, Is.True);
        Assume.That(_toolPosition.ToolPosition.Northing, Is.EqualTo(-HitchLength).Within(1e-6),
            "the rear-fixed tool is the hitch length behind the axle");
    }

    /// <summary>An east-west tram line under both wheel tracks at the given northing.</summary>
    private void TramAcrossAt(double northing)
        => _tram.AddTramLine(new List<Vec2> { new Vec2(-5, northing), new Vec2(5, northing) });

    private byte TramStateAfterAFix()
    {
        _autoSteer.ProcessSimulatedPosition(43.7, -74.0, 0, headingDegrees: 0, speedMps: 1,
            fixQuality: 4, satellites: 12, hdop: 0.7, easting: 0, northing: 0);
        return _autoSteer.LatestSnapshot!.Value.TramState;
    }

    [Test]
    public void TramUnderTheTool_LightsBothWheels()
    {
        SettleToolPosition();
        TramAcrossAt(-HitchLength);

        Assert.That(TramStateAfterAFix(), Is.EqualTo(3));
    }

    [Test]
    public void TramAheadOfTheTractor_DoesNotLight()
    {
        // Where the old projection looked: hitch plus trailing arm, ahead of the tractor.
        SettleToolPosition();
        TramAcrossAt(HitchLength + TrailingArm);

        Assert.That(TramStateAfterAFix(), Is.EqualTo(0));
    }

    [Test]
    public void TramUnderTheTractor_DoesNotLight()
    {
        SettleToolPosition();
        TramAcrossAt(0);

        Assert.That(TramStateAfterAFix(), Is.EqualTo(0), "the pivot is not the tool");
    }

    [Test]
    public void ToolInTheHeadland_NothingLights()
    {
        // AgOpenGPS (OpenGL.Designer.cs): with the tool wholly in the headland, tram.controlByte = 0,
        // so a boundary tram track under it doesn't light during the turn.
        SettleToolPosition();
        TramAcrossAt(-HitchLength);
        _autoSteer.SetMachineState(0, isInUTurn: true, hydLiftState: 2, outOfBounds: false, toolInHeadland: true);
        Assert.That(TramStateAfterAFix(), Is.EqualTo(0), "in the headland");

        _autoSteer.SetMachineState(0, isInUTurn: false, hydLiftState: 1, outOfBounds: false, toolInHeadland: false);
        Assert.That(TramStateAfterAFix(), Is.EqualTo(3), "back in the field");
    }

    [Test]
    public void NoToolPositionYet_NothingLights()
    {
        // The control loop hasn't run yet: there is no tool position to check.
        TramAcrossAt(-HitchLength);

        Assert.That(TramStateAfterAFix(), Is.EqualTo(0));
    }

    [Test]
    public void HiddenLines_DoNotLight()
    {
        SettleToolPosition();
        TramAcrossAt(-HitchLength);
        _config.Tram.DisplayMode = TramDisplayMode.OuterOnly; // the parallel line is not shown

        Assert.That(TramStateAfterAFix(), Is.EqualTo(0));
    }
}
