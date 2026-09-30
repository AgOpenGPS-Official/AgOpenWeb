// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System.Diagnostics;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services.AutoSteer;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Track;
using NSubstitute;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// #169: PGN 254 goes out at 100 Hz from the control loop whatever the state of guidance,
/// so the firmware's lost-connection watchdog never trips. When AutoSteer is engaged and
/// guidance hasn't updated for <see cref="AutoSteerService.GuidanceStaleLimit"/>, the service
/// must stop steering itself (status 0 in PGN 254) and say so once.
/// </summary>
[TestFixture]
[NonParallelizable]
public class GuidanceStaleWatchdogTests
{
    private AutoSteerService _svc = null!;
    private IUdpCommunicationService _udp = null!;
    private GpsService _gps = null!;
    private int _lostEvents;

    [SetUp]
    public void SetUp()
    {
        var config = new ConfigurationStore();
        ConfigurationStore.SetInstance(config);
        _udp = Substitute.For<IUdpCommunicationService>();
        _gps = new GpsService();
        _svc = new AutoSteerService(new TrackGuidanceService(), _udp, _gps, new ApplicationState(), config);
        _svc.Start();
        _lostEvents = 0;
        _svc.GuidanceLost += (_, _) => _lostEvents++;
    }

    [TearDown]
    public void TearDown() { _svc.Stop(); _gps.Stop(); }

    private static long After(TimeSpan t) =>
        Stopwatch.GetTimestamp() + (long)(t.TotalSeconds * Stopwatch.Frequency);

    /// <summary>Status byte (index 7) of the last PGN 254 sent to the modules.</summary>
    private byte LastSteerStatus()
    {
        var sent = _udp.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IUdpCommunicationService.SendToModules))
            .Select(c => (byte[])c.GetArguments()[0]!)
            .Last(b => b.Length > 7 && b[3] == PgnBuilder.PGN_AUTOSTEER);
        return sent[7];
    }

    [Test]
    public void Engaged_without_guidance_updates_stops_steering_once()
    {
        _svc.Engage();
        _svc.UpdateGuidanceResults(5.0, 0.1);
        _svc.SendPgnsForControlTick();
        Assert.That(LastSteerStatus(), Is.EqualTo(1), "engaged + fresh guidance → steer");

        Assert.That(_svc.CheckGuidanceFreshness(After(TimeSpan.FromSeconds(1.2))), Is.True);
        Assert.That(_svc.IsEngaged, Is.False);
        Assert.That(_lostEvents, Is.EqualTo(1));

        _svc.SendPgnsForControlTick();
        Assert.That(LastSteerStatus(), Is.EqualTo(0), "after the stall PGN 254 must tell the module to stop");

        Assert.That(_svc.CheckGuidanceFreshness(After(TimeSpan.FromSeconds(3))), Is.False, "raised once, not every tick");
        Assert.That(_lostEvents, Is.EqualTo(1));
    }

    [Test]
    public void Fresh_guidance_keeps_steering()
    {
        _svc.Engage();
        _svc.UpdateGuidanceResults(2.0, 0.0);
        Assert.That(_svc.CheckGuidanceFreshness(After(TimeSpan.FromSeconds(0.5))), Is.False);
        Assert.That(_svc.IsEngaged, Is.True);
        Assert.That(_lostEvents, Is.EqualTo(0));
    }

    [Test]
    public void Engaging_starts_the_clock()
    {
        // Engaged long after the last guidance update (e.g. after sitting disengaged): the
        // first check right after engaging must not trip on the old timestamp.
        _svc.UpdateGuidanceResults(0, 0);
        Thread.Sleep(20);
        _svc.Engage();
        Assert.That(_svc.CheckGuidanceFreshness(After(TimeSpan.FromSeconds(0.5))), Is.False);
    }

    [Test]
    public void Not_engaged_or_free_drive_is_never_tripped()
    {
        Assert.That(_svc.CheckGuidanceFreshness(After(TimeSpan.FromSeconds(10))), Is.False, "not engaged");

        _svc.Engage();
        _svc.EnableFreeDrive();
        Assert.That(_svc.CheckGuidanceFreshness(After(TimeSpan.FromSeconds(10))), Is.False,
            "Free Drive has no guidance to go stale");
        Assert.That(_lostEvents, Is.EqualTo(0));
    }
}
