using System;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Interfaces;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// RTCM goes to the modules' live /24 (auto-discovered), falling back to the
/// configured subnet, as AgIO sends to its subnet setting.
/// </summary>
[TestFixture]
public class NtripSubnetTests
{
    [Test]
    public void UsesTheDiscoveredSubnet()
    {
        var config = new NtripConfiguration { SubnetAddress = "192.168.5", SubnetProvider = () => "192.168.1" };
        Assert.That(NtripClientService.ResolveRtcmSubnet(config), Is.EqualTo("192.168.1"));
    }

    [TestCase(null)]
    [TestCase("")]
    public void NothingDiscovered_FallsBackToTheSetting(string? live)
    {
        var config = new NtripConfiguration { SubnetAddress = "10.0.7", SubnetProvider = () => live };
        Assert.That(NtripClientService.ResolveRtcmSubnet(config), Is.EqualTo("10.0.7"));
    }

    [Test]
    public void NoProvider_OrOneThatThrows_FallsBackToTheSetting()
    {
        Assert.That(NtripClientService.ResolveRtcmSubnet(new NtripConfiguration()), Is.EqualTo("192.168.5"));

        var config = new NtripConfiguration
        {
            SubnetAddress = "192.168.5",
            SubnetProvider = () => throw new InvalidOperationException(),
        };
        Assert.That(NtripClientService.ResolveRtcmSubnet(config), Is.EqualTo("192.168.5"));
    }

    [Test]
    public void FollowsAChangeWithoutReconnecting()
    {
        string live = "192.168.5";
        var config = new NtripConfiguration { SubnetProvider = () => live };
        Assert.That(NtripClientService.ResolveRtcmSubnet(config), Is.EqualTo("192.168.5"));
        live = "192.168.1";
        Assert.That(NtripClientService.ResolveRtcmSubnet(config), Is.EqualTo("192.168.1"));
    }
}
