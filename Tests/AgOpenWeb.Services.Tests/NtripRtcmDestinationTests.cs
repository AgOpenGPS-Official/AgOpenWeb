using System;
using System.Net;
using AgOpenWeb.Models.Timing;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Interfaces;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// RTCM forwarding plan, Phase 4: corrections go to the GPS module's own address when it is
/// known, and are broadcast to the modules' subnet otherwise or when the operator asks for it.
/// </summary>
[TestFixture]
public class NtripRtcmDestinationTests
{
    private static NtripConfiguration Config(IPAddress? module, bool broadcastOnly = false) => new()
    {
        SubnetAddress = "192.168.5",
        SubnetProvider = () => "192.168.1",
        GpsModuleAddressProvider = () => module,
        BroadcastOnly = () => broadcastOnly,
    };

    [Test]
    public void AKnownGpsModule_GetsTheCorrectionsDirectly()
    {
        var target = NtripClientService.ResolveRtcmDestination(Config(IPAddress.Parse("192.168.1.126")), out bool unicast);

        Assert.That(target, Is.EqualTo(new IPEndPoint(IPAddress.Parse("192.168.1.126"), 2233)));
        Assert.That(unicast, Is.True);
    }

    [Test]
    public void NoGpsModuleHeard_BroadcastsToTheModulesSubnet()
    {
        var target = NtripClientService.ResolveRtcmDestination(Config(null), out bool unicast);

        Assert.That(target, Is.EqualTo(new IPEndPoint(IPAddress.Parse("192.168.1.255"), 2233)));
        Assert.That(unicast, Is.False);
    }

    [Test]
    public void BroadcastOnly_Broadcasts_EvenWithAKnownModule()
    {
        var target = NtripClientService.ResolveRtcmDestination(
            Config(IPAddress.Parse("192.168.1.126"), broadcastOnly: true), out bool unicast);

        Assert.That(target, Is.EqualTo(new IPEndPoint(IPAddress.Parse("192.168.1.255"), 2233)));
        Assert.That(unicast, Is.False);
    }

    [Test]
    public void WithoutProviders_ItIsTheConfiguredSubnetBroadcast_AsBefore()
    {
        var target = NtripClientService.ResolveRtcmDestination(new NtripConfiguration { SubnetAddress = "10.0.7" }, out bool unicast);

        Assert.That(target, Is.EqualTo(new IPEndPoint(IPAddress.Parse("10.0.7.255"), 2233)));
        Assert.That(unicast, Is.False);
    }

    [Test]
    public void AProviderThatThrows_FallsBackToTheBroadcast()
    {
        var config = new NtripConfiguration
        {
            SubnetAddress = "192.168.5",
            GpsModuleAddressProvider = () => throw new InvalidOperationException(),
            BroadcastOnly = () => throw new InvalidOperationException(),
        };

        var target = NtripClientService.ResolveRtcmDestination(config, out bool unicast);

        Assert.That(target, Is.EqualTo(new IPEndPoint(IPAddress.Parse("192.168.5.255"), 2233)));
        Assert.That(unicast, Is.False);
    }

    [Test]
    public void TheSilenceMonitor_ReportsWhereASourceWasLastHeard_AndHowLongAgo()
    {
        var clock = new TestClock();
        var monitor = new SourceSilenceMonitor(clock);
        Assert.That(monitor.LastSeen("GPS NMEA"), Is.Null, "never heard");

        monitor.MarkSeen("GPS NMEA", "192.168.5.126");
        clock.AdvanceMs(3000);
        monitor.MarkSeen("steer PGN 253", "192.168.5.127");

        var seen = monitor.LastSeen("GPS NMEA");
        Assert.That(seen?.From, Is.EqualTo("192.168.5.126"));
        Assert.That(seen?.AgeMs, Is.EqualTo(3000).Within(1e-6));
    }
}
