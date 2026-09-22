using AgOpenWeb.Models;
using AgOpenWeb.Services.AutoSteer;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// PGN 254 status byte: exactly 1 = steer, 0 = don't, as AgOpenGPS sends it
/// (Position.designer.cs). The firmware steers on bit 0 (AiO v26
/// AutosteerProcessor: status &amp; 0x01; AIO v4: guidanceStatus == 1). It used to
/// carry the module's own echoed steer state in bit 0 and the engage in bit 2,
/// which no firmware reads (#125).
/// </summary>
[TestFixture]
public class PgnBuilderEngageBitTests
{
    private const int STATUS_BYTE_INDEX = 7;

    private static byte Status(bool engaged, bool paused = false, bool steerSwitch = false)
    {
        var state = new VehicleState
        {
            IsAutoSteerEngaged = engaged,
            IsSteerPaused = paused,
            GpsValid = true,
            WorkSwitchActive = true,
            SteerSwitchActive = steerSwitch,
        };
        return PgnBuilder.BuildAutoSteerPgn(ref state)[STATUS_BYTE_INDEX];
    }

    [Test]
    public void Engaged_SendsExactly1() => Assert.That(Status(engaged: true), Is.EqualTo(1));

    [Test]
    public void NotEngaged_Sends0_WhateverTheModuleReports()
        => Assert.That(Status(engaged: false, steerSwitch: true), Is.EqualTo(0),
            "the module's echoed steer state must not turn steering on");

    [Test]
    public void EngagedButPaused_Sends0() // reversing with Steer in reverse off
        => Assert.That(Status(engaged: true, paused: true), Is.EqualTo(0));
}
