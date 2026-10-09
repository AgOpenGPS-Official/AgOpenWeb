// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.GPS;
using AgOpenWeb.Services.AutoSteer;
using AgOpenWeb.Services.Interfaces;
using NSubstitute;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// The app listens on Ace's GPS1 (2211) and GPS2 (2222) ports as well as the module port,
/// and a datagram on one of them reaches the parser tagged with its source.
/// </summary>
[TestFixture]
[NonParallelizable] // real sockets on fixed ports
public class UdpGpsPortsTests
{
    private static string Panda()
    {
        const string body = "PANDA,162255.50,3924.90,N,00731.80,W,4,12,0.7,341.9,1.2,4.8,2217,31,-12,0.5";
        byte checksum = 0;
        foreach (char c in body) checksum ^= (byte)c;
        return $"${body}*{checksum:X2}\r\n";
    }

    private sealed class Loopback : ILocalNetworkInfoProvider
    {
        public IReadOnlyList<LocalNetworkAddress> GetIPv4Addresses() =>
            new[] { new LocalNetworkAddress(IPAddress.Loopback, 8, "lo0") };
    }

    [TestCase(UdpCommunicationService.Gps1Port, GpsSource.Gps1)]
    [TestCase(UdpCommunicationService.Gps2Port, GpsSource.Gps2)]
    [TestCase(UdpCommunicationService.ModulePort, GpsSource.ModulePort)]
    public async Task A_sentence_on_a_GPS_port_reaches_the_parser_with_its_source(int port, GpsSource expected)
    {
        var udp = new UdpCommunicationService(new Loopback());
        var gps = Substitute.For<IGpsService>();
        var steer = new AutoSteerService(Substitute.For<ITrackGuidanceService>(), udp, gps, new ConfigurationStore());
        udp.SetAutoSteerService(steer);
        steer.Start();
        await udp.StartAsync();
        try
        {
            using var sender = new UdpClient();
            var bytes = Encoding.ASCII.GetBytes(Panda());
            bool seen = false;
            for (int attempt = 0; attempt < 20 && !seen; attempt++)
            {
                sender.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, port));
                for (int wait = 0; wait < 20 && !seen; wait++)
                {
                    await Task.Delay(25);
                    seen = gps.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IGpsService.MarkRealGpsParsed));
                }
            }
            Assert.That(seen, Is.True, $"no fix parsed from port {port}");
            Assert.That(steer.LastGpsSource, Is.EqualTo(expected));
        }
        finally
        {
            steer.Stop();
            await udp.StopAsync();
            udp.Dispose();
        }
    }
}
