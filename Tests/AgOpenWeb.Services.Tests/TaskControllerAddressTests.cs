// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Net;
using AgOpenWeb.Models;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// The ISOBUS task controller (AOG-TaskController) binds 8888 at its own LAN address and reads
/// the fix quality only from PGN 214. AgOpenWeb sent that to AgIO's loopback port 15555 only,
/// so the task controller told every implement plain GPS (ISOBUS nightly, 20 Nov 2026,
/// ISOBUS-NO-GNSS-QUALITY). Its heartbeat (PGN 240, source 0x80) gives its address.
/// </summary>
[TestFixture]
public class TaskControllerAddressTests
{
    private static readonly IPEndPoint TaskController = new(IPAddress.Parse("192.168.5.80"), 8888);

    private sealed class NoNetwork : ILocalNetworkInfoProvider
    {
        public IReadOnlyList<LocalNetworkAddress> GetIPv4Addresses() => Array.Empty<LocalNetworkAddress>();
    }

    /// <summary>PGN 240 as the task controller sends it: alive, no implement.</summary>
    private static byte[] Heartbeat(byte source = 0x80)
    {
        var msg = new byte[] { 0x80, 0x81, source, PgnNumbers.ISOBUS_HEARTBEAT, 2, 0x00, 0x00, 0 };
        int crc = 0;
        for (int i = 2; i < msg.Length - 1; i++) crc += msg[i];
        msg[^1] = (byte)crc;
        return msg;
    }

    [Test]
    public void NoHeartbeat_NoTaskController()
    {
        using var udp = new UdpCommunicationService(new NoNetwork());

        Assert.That(udp.IsTaskControllerHeard, Is.False);
    }

    [Test]
    public void ItsHeartbeat_MakesItHeard()
    {
        using var udp = new UdpCommunicationService(new NoNetwork());

        udp.UpdateModuleConnection(Heartbeat(), TaskController);

        Assert.That(udp.IsTaskControllerHeard, Is.True);
    }

    [Test]
    public void PGN240FromAnotherSource_IsNotTheTaskController()
    {
        using var udp = new UdpCommunicationService(new NoNetwork());

        udp.UpdateModuleConnection(Heartbeat(source: 0x7F), TaskController);

        Assert.That(udp.IsTaskControllerHeard, Is.False);
    }
}
