// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
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
/// Checklist 6.7: with the tram display Off there are no lights and nothing goes to the machine
/// module, as AgOpenGPS. Its control byte (OpenGL.Designer.cs) is 0 whenever displayMode is 0,
/// whatever is under the wheels and whatever the manual buttons say; its GUI timer clears the
/// manual buttons while the display is off; and PGN 239's tram byte is that control byte
/// (Sections.Designer.cs). Here the real control loop (AutoSteerService with the tram and tool
/// position services) is given a tram line under both wheels of the tool, and the PGN 239 frame
/// it sends is read back and held to AgOpenGPS's frame for the same state.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class TramOffSendsNothingTests
{
    private const double HitchLength = 2.0; // rear-fixed tool, working centre 2 m behind the axle
    private const double SpeedMps = 1.0;

    private ConfigurationStore _config = null!;
    private ToolPositionService _toolPosition = null!;
    private TramLineService _tram = null!;
    private AutoSteerService _autoSteer = null!;
    private readonly List<byte[]> _sent = new();

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
        _config.Tool.Offset = 0;
        _config.Tool.Width = 6;
        _config.NumSections = 1;
        _config.Tool.SetSectionWidth(0, 600);
        _config.Tram.TramWidth = 6; // one tool width: the trams run about the tool's centre
        _config.Tram.IsOuterInverted = false;
        _config.Tram.DisplayMode = TramDisplayMode.All;

        _sent.Clear();
        var udp = Substitute.For<IUdpCommunicationService>();
        udp.When(u => u.SendToModules(Arg.Any<byte[]>())).Do(ci => _sent.Add((byte[])ci.Arg<byte[]>().Clone()));

        _toolPosition = new ToolPositionService(_config);
        _tram = new TramLineService(new TramLineOffsetService(), NullLogger<TramLineService>.Instance, _config);
        _autoSteer = new AutoSteerService(Substitute.For<ITrackGuidanceService>(), udp,
            Substitute.For<IGpsService>(), _config);
        _autoSteer.SetTramLineService(_tram);
        _autoSteer.SetToolPositionService(_toolPosition);
        _autoSteer.Start();

        // The control loop's job: the tractor at the origin heading north, the tool 2 m behind.
        _toolPosition.Update(new Vec3(0, 0, 0), 0);
        Assume.That(_toolPosition.HasPosition, Is.True);
    }

    [TearDown]
    public void TearDown()
    {
        _autoSteer.Stop();
        ConfigurationStore.SetInstance(new ConfigurationStore());
    }

    /// <summary>An east-west tram line under both wheel tracks of the tool.</summary>
    private void TramUnderTheTool()
        => _tram.AddTramLine(new List<Vec2> { new Vec2(-5, -HitchLength), new Vec2(5, -HitchLength) });

    /// <summary>A fix through the control loop, then the control tick's PGN send: the 239 frame.</summary>
    private byte[] MachineFrameAfterAFix()
    {
        _autoSteer.ProcessSimulatedPosition(43.7, -74.0, 0, headingDegrees: 0, speedMps: SpeedMps,
            fixQuality: 4, satellites: 12, hdop: 0.7, easting: 0, northing: 0);
        _sent.Clear();
        _autoSteer.SendPgnsForControlTick();
        var frames = _sent.Where(f => f.Length > 3 && f[3] == PgnBuilder.PGN_MACHINE).ToList();
        Assert.That(frames, Has.Count.EqualTo(1), "one PGN 239 per control tick");
        return frames[0];
    }

    private byte TramState => _autoSteer.LatestSnapshot!.Value.TramState;

    /// <summary>
    /// AgOpenGPS's PGN 239 for our state: nothing else is on (no turn, lift off, in bounds, no
    /// sections), the speed is the fix's, the tram byte is AgOpenGPS's control byte.
    /// </summary>
    private static byte[] AgOpenGpsFrame(byte tramControlByte)
        => AgOpenGpsPgnOracle.MachineData(false, SpeedMps * 3.6, 0, tramControlByte, false, 0, 0);

    /// <summary>
    /// AgOpenGPS OpenGL.Designer.cs, the control byte: the pixel read under each wheel
    /// (grnPixels[...] == 245) is here whether that wheel is on a tram line.
    /// </summary>
    private static int AgOpenGpsControlByte(int displayMode, double toolWidth, double trackWidth,
        bool rightWheelOnTram, bool leftWheelOnTram, bool isRightManualOn, bool isLeftManualOn)
    {
        int controlByte;
        if (displayMode > 0 && toolWidth > trackWidth)
        {
            controlByte = 0;
            if (rightWheelOnTram || isRightManualOn) controlByte += 1;
            if (leftWheelOnTram || isLeftManualOn) controlByte += 2;
        }
        else controlByte = 0;
        return controlByte;
    }

    [Test]
    public void DisplayOn_TramUnderTheTool_BothWheelsGoToTheMachine()
    {
        TramUnderTheTool();

        var frame = MachineFrameAfterAFix();

        int theirs = AgOpenGpsControlByte(1, 6, 1.8, true, true, false, false);
        Assert.That(theirs, Is.EqualTo(3));
        Assert.That(TramState, Is.EqualTo(theirs));
        Assert.That(frame, Is.EqualTo(AgOpenGpsFrame(3)).AsCollection, "the whole 239 frame, AgOpenGPS's");
    }

    [Test]
    public void DisplayOff_TramUnderTheTool_NothingLightsAndTheTramByteIsZero()
    {
        TramUnderTheTool();
        _config.Tram.DisplayMode = TramDisplayMode.Off;

        var frame = MachineFrameAfterAFix();

        int theirs = AgOpenGpsControlByte(0, 6, 1.8, true, true, false, false);
        Assert.That(theirs, Is.Zero);
        Assert.That(TramState, Is.Zero, "no lights");
        Assert.That(frame[8], Is.Zero, "PGN 239 tram byte");
        Assert.That(frame, Is.EqualTo(AgOpenGpsFrame(0)).AsCollection, "the whole 239 frame, AgOpenGPS's");
    }

    [Test]
    public void DisplayOff_TheManualButtonsSendNothingAndAreCleared()
    {
        // Off the tram: only the manual buttons light the wheels.
        _tram.AddTramLine(new List<Vec2> { new Vec2(-5, 50), new Vec2(5, 50) });
        _tram.IsLeftManualOn = true;
        _tram.IsRightManualOn = true;
        Assume.That(MachineFrameAfterAFix()[8], Is.EqualTo(AgOpenGpsControlByte(1, 6, 1.8, false, false, true, true)),
            "with the display on the manual buttons light both wheels (3)");

        _config.Tram.DisplayMode = TramDisplayMode.Off;
        var frame = MachineFrameAfterAFix();

        Assert.That(TramState, Is.Zero);
        Assert.That(frame[8], Is.Zero, "the buttons don't reach the machine with the display off");
        Assert.That(frame, Is.EqualTo(AgOpenGpsFrame(0)).AsCollection);
        // AgOpenGPS GUI.Designer.cs: displayMode == 0 clears isRightManualOn and isLeftManualOn;
        // the lights read those flags, so they go dark with the display.
        Assert.That(_tram.IsLeftManualOn, Is.False, "left manual cleared");
        Assert.That(_tram.IsRightManualOn, Is.False, "right manual cleared");

        // Back on: the buttons were cleared, so nothing lights until a wheel is on a tram.
        _config.Tram.DisplayMode = TramDisplayMode.All;
        Assert.That(MachineFrameAfterAFix()[8], Is.Zero, "the cleared buttons don't come back with the display");
    }

    [Test]
    public void DisplayOffThenOn_TheTramUnderTheToolLightsAgain()
    {
        TramUnderTheTool();
        _config.Tram.DisplayMode = TramDisplayMode.Off;
        Assume.That(MachineFrameAfterAFix()[8], Is.Zero);

        _config.Tram.DisplayMode = TramDisplayMode.All;

        Assert.That(MachineFrameAfterAFix()[8], Is.EqualTo(3), "Off is not sticky");
    }

    [Test]
    public void DisplayOff_ToolNoWiderThanTheTrack_IsZeroEitherWay()
    {
        // AgOpenGPS: tool.width > trackWidth is the other half of the condition.
        _config.Tool.SetSectionWidth(0, 150);
        _config.Vehicle.TrackWidth = 1.8;
        TramUnderTheTool();
        Assert.That(MachineFrameAfterAFix()[8], Is.EqualTo(AgOpenGpsControlByte(1, 1.5, 1.8, true, true, false, false)), "on");
        _config.Tram.DisplayMode = TramDisplayMode.Off;
        Assert.That(MachineFrameAfterAFix()[8], Is.EqualTo(AgOpenGpsControlByte(0, 1.5, 1.8, true, true, false, false)), "off");
    }

    // The ISOBUS task controller reads the sections from PGN 229 only: it goes with every 239,
    // with one section as with 64, as AgOpenGPS sends it on every fix (#696's harness).
    [Test]
    public void Pgn229_goes_with_every_239_whatever_the_section_count()
    {
        _autoSteer.ProcessSimulatedPosition(43.7, -74.0, 0, headingDegrees: 0, speedMps: SpeedMps,
            fixQuality: 4, satellites: 12, hdop: 0.7, easting: 0, northing: 0);
        _sent.Clear();
        _autoSteer.SendPgnsForControlTick();
        Assert.That(_sent.Count(f => f.Length > 3 && f[3] == PgnBuilder.PGN_MACHINE), Is.EqualTo(1));
        Assert.That(_sent.Count(f => f.Length > 3 && f[3] == PgnBuilder.PGN_SECTIONS_64), Is.EqualTo(1),
            "one PGN 229 per control tick with a single section");
    }
}
