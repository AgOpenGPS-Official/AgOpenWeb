using System.Net;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services.Tests;

[TestFixture]
public class UdpNetworkAddressTests
{
    [TestCase("192.168.5.42", 24, "192.168.5.255")]
    [TestCase("10.20.33.8", 16, "10.20.255.255")]
    [TestCase("172.16.4.9", 20, "172.16.15.255")]
    [TestCase("192.168.5.42", 32, "192.168.5.42")]
    [TestCase("192.168.5.42", 0, "255.255.255.255")]
    public void CalculateBroadcastAddress_UsesPrefixLength(
        string address,
        int prefixLength,
        string expected)
    {
        var localAddress = new LocalNetworkAddress(
            IPAddress.Parse(address),
            prefixLength);

        var actual = UdpCommunicationService.CalculateBroadcastAddress(localAddress);

        Assert.That(actual.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void CalculateBroadcastAddress_RejectsNonIPv4Address()
    {
        var localAddress = new LocalNetworkAddress(
            IPAddress.Parse("fe80::1"),
            64);

        Assert.That(
            () => UdpCommunicationService.CalculateBroadcastAddress(localAddress),
            Throws.ArgumentException);
    }

    [TestCase(-1)]
    [TestCase(33)]
    public void CalculateBroadcastAddress_RejectsOutOfRangePrefixLength(int prefixLength)
    {
        var localAddress = new LocalNetworkAddress(
            IPAddress.Parse("192.168.5.42"),
            prefixLength);

        Assert.That(
            () => UdpCommunicationService.CalculateBroadcastAddress(localAddress),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void GetLocalIpAddresses_UsesInjectedProviderAndRemovesDuplicates()
    {
        var provider = new FakeLocalNetworkInfoProvider(
            new LocalNetworkAddress(IPAddress.Parse("192.168.5.20"), 24, "wlan0"),
            new LocalNetworkAddress(IPAddress.Parse("192.168.5.20"), 24, "wlan0"),
            new LocalNetworkAddress(IPAddress.Parse("10.0.0.4"), 24, "eth0"));
        var service = new UdpCommunicationService(provider);

        Assert.That(
            service.GetLocalIpAddresses(),
            Is.EqualTo(new[] { "192.168.5.20", "10.0.0.4" }));
    }

    private sealed class FakeLocalNetworkInfoProvider : ILocalNetworkInfoProvider
    {
        private readonly IReadOnlyList<LocalNetworkAddress> _addresses;

        public FakeLocalNetworkInfoProvider(params LocalNetworkAddress[] addresses)
        {
            _addresses = addresses;
        }

        public IReadOnlyList<LocalNetworkAddress> GetIPv4Addresses() => _addresses;
    }
}
