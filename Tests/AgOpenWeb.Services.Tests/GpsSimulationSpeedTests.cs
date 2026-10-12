// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using AgOpenWeb.Models;
using AgOpenWeb.Models.GPS;

namespace AgOpenWeb.Services.Tests;

/// <summary>The simulator travels at the speed it reports (#328).</summary>
[TestFixture]
public class GpsSimulationSpeedTests
{
    [TestCase(1.0 / 30.0)]   // the simulator timer
    [TestCase(0.093)]        // AgOpenGPS's simulator timer
    [TestCase(0.011)]        // a jittery host
    public void Distance_travelled_matches_the_reported_speed(double tickSeconds)
    {
        var sim = new GpsSimulationService();
        var start = new Wgs84(40.7128, -74.006);
        sim.Initialize(start);
        sim.StepDistance = 10.0 / 40.0; // 10 km/h, as MainViewModel.UpdateSimulatorSpeed sets it

        SimulatedGpsData? last = null;
        sim.GpsDataUpdated += (_, e) => last = e.Data;

        const double seconds = 60;
        int ticks = (int)Math.Round(seconds / tickSeconds);
        for (int i = 0; i < ticks; i++)
            sim.Tick(0, tickSeconds);

        Assert.That(last!.SpeedKmh, Is.EqualTo(10.0).Within(1e-9));
        double metres = (sim.CurrentPosition.Latitude - start.Latitude) * 111_132.0; // heading north
        double expected = 10.0 / 3.6 * ticks * tickSeconds;
        Assert.That(metres, Is.EqualTo(expected).Within(expected * 0.005));
    }
}
