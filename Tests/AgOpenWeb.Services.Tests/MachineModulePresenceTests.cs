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
/// The machine module counts as present when it answers the hello (PGN 123) or sends its own
/// PGN 237 (five times a second on the AiO v26 and Sprayer_ESP32). Over Wi-Fi the hello and its
/// reply are broadcasts nothing resends, so on the hello alone the Modules dot went amber for
/// seconds with the module working.
/// </summary>
[TestFixture]
public class MachineModulePresenceTests
{
    private static readonly IPEndPoint Module = new(IPAddress.Parse("192.168.5.123"), 9999);

    private sealed class NoNetwork : ILocalNetworkInfoProvider
    {
        public IReadOnlyList<LocalNetworkAddress> GetIPv4Addresses() => Array.Empty<LocalNetworkAddress>();
    }

    /// <summary>A PGN from the machine module: 80 81 7B pgn len data… crc.</summary>
    private static byte[] FromModule(byte pgn, int length)
    {
        var msg = new byte[6 + length];
        msg[0] = 0x80; msg[1] = 0x81; msg[2] = 0x7B; msg[3] = pgn; msg[4] = (byte)length;
        int crc = 0;
        for (int i = 2; i < msg.Length - 1; i++) crc += msg[i];
        msg[^1] = (byte)crc;
        return msg;
    }

    [Test]
    public void Nothing_heard_is_not_present()
    {
        using var udp = new UdpCommunicationService(new NoNetwork());

        Assert.That(udp.IsModuleHelloOk(ModuleType.Machine), Is.False);
        Assert.That(udp.IsModuleDataOk(ModuleType.Machine), Is.False);
    }

    [Test]
    public void Its_own_PGN_237_makes_it_present_without_a_hello()
    {
        using var udp = new UdpCommunicationService(new NoNetwork());

        udp.UpdateModuleConnection(FromModule(PgnNumbers.FROM_MACHINE, 8), Module);

        Assert.That(udp.IsModuleDataOk(ModuleType.Machine), Is.True);
        Assert.That(udp.IsModuleHelloOk(ModuleType.Machine), Is.False, "no hello reply was heard");
        Assert.That(udp.GetModuleIpAddress(ModuleType.Machine), Is.EqualTo("192.168.5.123"));
    }

    [Test]
    public void The_hello_reply_still_makes_it_present()
    {
        using var udp = new UdpCommunicationService(new NoNetwork());

        udp.UpdateModuleConnection(FromModule(PgnNumbers.HELLO_FROM_MACHINE, 5), Module);

        Assert.That(udp.IsModuleHelloOk(ModuleType.Machine), Is.True);
        Assert.That(udp.GetModuleIpAddress(ModuleType.Machine), Is.EqualTo("192.168.5.123"));
    }

    [Test]
    public void A_steer_module_message_does_not_make_the_machine_present()
    {
        using var udp = new UdpCommunicationService(new NoNetwork());

        udp.UpdateModuleConnection(FromModule(PgnNumbers.AUTOSTEER_DATA, 8), new IPEndPoint(IPAddress.Parse("192.168.5.126"), 9999));

        Assert.That(udp.IsModuleDataOk(ModuleType.Machine), Is.False);
        Assert.That(udp.IsModuleHelloOk(ModuleType.Machine), Is.False);
    }
}
