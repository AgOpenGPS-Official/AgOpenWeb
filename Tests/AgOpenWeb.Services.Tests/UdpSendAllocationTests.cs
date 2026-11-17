// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using AgOpenWeb.Services;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// The control tick sends three PGNs a tick, each to the module and to localhost. Sent with
/// <c>SendTo(byte[], EndPoint)</c> each send allocated 64 bytes (the endpoint serialised every
/// call), and an allocating control tick waited up to 100 ms for allocation space while a
/// collection ran (Working Day scenarios, CONTROL-TICK-OVER-10-MS). Each endpoint's address is
/// now serialised once (<see cref="UdpCommunicationService.Address"/>) and sent with the
/// <see cref="SocketAddress"/> overload. Loopback only, on a port nothing uses.
/// </summary>
[TestFixture]
public class UdpSendAllocationTests
{
    [Test]
    public void AnEndpointsAddress_IsMadeOnce()
    {
        var ep = new IPEndPoint(IPAddress.Loopback, 47123);
        var first = UdpCommunicationService.Address(ep);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var again = UdpCommunicationService.Address(ep);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.That(again, Is.SameAs(first));
        Assert.That(allocated, Is.EqualTo(0));
        Assert.That(UdpCommunicationService.Address(new IPEndPoint(IPAddress.Loopback, 47124)), Is.Not.SameAs(first));
    }

    [Test]
    public void Sending_ToTwoEndpointsInTurn_AllocatesNothing()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        var a = new IPEndPoint(IPAddress.Loopback, 47123);
        var b = new IPEndPoint(IPAddress.Loopback, 47124);
        var data = new byte[14];
        for (int i = 0; i < 100; i++)
        {
            socket.SendTo(data, SocketFlags.None, UdpCommunicationService.Address(a));
            socket.SendTo(data, SocketFlags.None, UdpCommunicationService.Address(b));
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            socket.SendTo(data, SocketFlags.None, UdpCommunicationService.Address(a));
            socket.SendTo(data, SocketFlags.None, UdpCommunicationService.Address(b));
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.That(allocated, Is.EqualTo(0), "2,000 sends allocated " + allocated + " bytes");
    }
}
