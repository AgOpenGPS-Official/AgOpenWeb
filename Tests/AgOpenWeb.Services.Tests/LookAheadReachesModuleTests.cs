// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Section;
using NSubstitute;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// #707: look-ahead reaches the module. As AgOpenGPS (OpenGL.Designer.cs, Section Control), the
/// section command (PGN 239/229 bits) follows the look-ahead's on/off request at once, and only
/// the map waits the look-ahead time, so a valve that takes that long acts at the edge. Before,
/// the bits followed the map's state and the module was told at the edge whatever the setting.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class LookAheadReachesModuleTests
{
    private const double Speed = 2.0;      // m/s (7.2 km/h)
    private const double TickHz = 100.0;   // the control loop
    private SectionControlService _service = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var config = ConfigurationStore.Instance;
        config.NumSections = 1;
        config.Tool.SetSectionWidth(0, 600);
        config.Tool.Offset = 0;
        config.Tool.IsSectionOffWhenOut = true;

        var poly = new BoundaryPolygon();
        poly.Points.Add(new BoundaryPoint(0, 0, 0));
        poly.Points.Add(new BoundaryPoint(200, 0, 0));
        poly.Points.Add(new BoundaryPoint(200, 200, 0));
        poly.Points.Add(new BoundaryPoint(0, 200, 0));
        poly.UpdateBounds();

        _service = new SectionControlService(Substitute.For<IToolPositionService>(),
            Substitute.For<ICoverageMapService>(), ConfigurationStore.Instance) { TickHz = TickHz };
        _service.SetFieldContext(new AgOpenWeb.Models.Sections.SectionFieldContext(new Boundary { OuterBoundary = poly }, null, true, false));
        _service.SetAllAuto();
        _service.MasterState = SectionMasterState.Auto;
    }

    /// <summary>Drive north at <see cref="Speed"/> from <paramref name="fromN"/> to
    /// <paramref name="toN"/>; the northing where the module bit and the map's IsOn first change.</summary>
    private (double BitAt, double IsOnAt) DriveNorth(double fromN, double toN)
    {
        bool bit0 = (_service.GetSectionBits64() & 1) != 0, on0 = _service.SectionStates[0].IsOn;
        double bitAt = double.NaN, onAt = double.NaN;
        for (double n = fromN; n <= toN; n += Speed / TickHz)
        {
            _service.Update(new Vec3(100, n, 0), 0, 0, Speed);
            if (double.IsNaN(bitAt) && ((_service.GetSectionBits64() & 1) != 0) != bit0) bitAt = n;
            if (double.IsNaN(onAt) && _service.SectionStates[0].IsOn != on0) onAt = n;
        }
        return (bitAt, onAt);
    }

    [Test]
    public void The_off_command_goes_the_look_ahead_ahead_of_the_edge_and_the_map_stops_at_it()
    {
        var tool = ConfigurationStore.Instance.Tool;
        tool.LookAheadOnSetting = 1.0;
        tool.LookAheadOffSetting = 1.0;
        DriveNorth(150, 180); // settle on, well inside
        Assume.That(_service.SectionStates[0].IsOn, Is.True);

        var (bitAt, onAt) = DriveNorth(180, 203);
        // 1 s at 2 m/s: the module is told about 2 m before the map stops.
        Assert.That(onAt - bitAt, Is.EqualTo(Speed * tool.LookAheadOffSetting).Within(0.1),
            $"bit off at N={bitAt:F2}, map off at N={onAt:F2}");
    }

    [Test]
    public void With_look_ahead_off_at_zero_the_turn_off_delay_keeps_the_module_on_too()
    {
        var tool = ConfigurationStore.Instance.Tool;
        tool.LookAheadOnSetting = 1.0;
        tool.LookAheadOffSetting = 0;
        tool.TurnOffDelay = 1.0;
        DriveNorth(150, 180);
        Assume.That(_service.SectionStates[0].IsOn, Is.True);

        var (bitAt, onAt) = DriveNorth(180, 203);
        // AgOpenGPS: turnOffDelay is a plain overrun of isSectionOn; the bit and the map go off together.
        Assert.That(bitAt, Is.EqualTo(onAt).Within(1e-9));
    }

    [Test]
    public void The_on_command_goes_the_look_ahead_ahead_of_the_edge_and_the_map_starts_at_it()
    {
        var tool = ConfigurationStore.Instance.Tool;
        tool.LookAheadOnSetting = 1.0;
        tool.LookAheadOffSetting = 1.0;
        DriveNorth(-30, -15); // outside the field, south of it: off
        Assume.That(_service.SectionStates[0].IsOn, Is.False);

        var (bitAt, onAt) = DriveNorth(-15, 10);
        Assert.That(onAt - bitAt, Is.EqualTo(Speed * tool.LookAheadOnSetting).Within(0.1),
            $"bit on at N={bitAt:F2}, map on at N={onAt:F2}");
    }
}
