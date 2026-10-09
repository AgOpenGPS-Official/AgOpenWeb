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

    /// <summary>A receiver that prints only Unicore '#' logs (a UM981) is a GPS source too.</summary>
    [Test]
    public async Task A_hash_log_marks_the_GPS_source_address()
    {
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Bind(new IPEndPoint(IPAddress.Any, UdpCommunicationService.ModulePort));
        }
        catch (SocketException)
        {
            Assert.Ignore("port 9999 is held by another process on this machine");
        }

        var udp = new UdpCommunicationService(new Loopback());
        await udp.StartAsync();
        try
        {
            const string body = "#INSPVAXA,COM3,0,80.0,FINE,2439,503918.400,0,424572,0;INS_ALIGNING,NONE,0.0,0.0,-17.0,17.0,0.0,0.0,0.0,0.0,0.0,0.0,0,0,0,0,0,0,0,0,0,0,0";
            var line = Encoding.ASCII.GetBytes(body + "*" + AgOpenWeb.Services.Gps.UnicoreCrc32.Compute(Encoding.ASCII.GetBytes(body[1..])).ToString("x8") + "\r\n");
            using var sender = new UdpClient();
            IPAddress? source = null;
            for (int attempt = 0; attempt < 20 && source == null; attempt++)
            {
                sender.Send(line, line.Length, new IPEndPoint(IPAddress.Loopback, UdpCommunicationService.ModulePort));
                for (int wait = 0; wait < 20 && source == null; wait++) { await Task.Delay(25); source = udp.GetGpsSourceAddress(); }
            }
            Assert.That(source, Is.EqualTo(IPAddress.Loopback));
        }
        finally
        {
            await udp.StopAsync();
            udp.Dispose();
        }
    }

    [TestCase(UdpCommunicationService.Gps1Port, GpsSource.Gps1)]
    [TestCase(UdpCommunicationService.Gps2Port, GpsSource.Gps2)]
    [TestCase(UdpCommunicationService.ModulePort, GpsSource.ModulePort)]
    public async Task A_sentence_on_a_GPS_port_reaches_the_parser_with_its_source(int port, GpsSource expected)
    {
        // A running AgOpenWeb on this machine holds the port too (ReuseAddress lets both
        // bind, but the loopback datagram lands in one of them): skip rather than guess.
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Bind(new IPEndPoint(IPAddress.Any, port));
        }
        catch (SocketException)
        {
            Assert.Ignore($"port {port} is held by another process on this machine");
        }

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
            Assert.That(udp.GetGpsSourceAddress(), Is.EqualTo(IPAddress.Loopback), "where the corrections go back to");
        }
        finally
        {
            steer.Stop();
            await udp.StopAsync();
            udp.Dispose();
        }
    }
}
