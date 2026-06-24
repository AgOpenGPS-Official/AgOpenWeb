// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using AgOpenWeb.Services.AutoSteer;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// Byte-exact parity with AgIO's module-network PGNs (FormUDP.cs /
/// UDP.designer.cs) so the existing AiO board install base responds correctly.
/// </summary>
[TestFixture]
public class ModuleNetworkPgnTests
{
    // ===== PGN 202 — scan request =====

    [Test]
    public void BuildScanRequest_MatchesAgIoBytesExactly()
    {
        // AgIO FormUDP.cs:137 — { 0x80, 0x81, 0x7F, 202, 3, 202, 202, 5, 0x47 }
        var expected = new byte[] { 0x80, 0x81, 0x7F, 202, 3, 202, 202, 5, 0x47 };
        Assert.That(PgnBuilder.BuildScanRequest(), Is.EqualTo(expected));
    }

    // ===== PGN 201 — set subnet =====

    [Test]
    public void BuildSubnetChange_MatchesAgIoLayout_WithGivenOctets()
    {
        // AgIO FormUDP.cs:19 — { 0x80,0x81,0x7F,201,5,201,201, o1,o2,o3, 0x47 }
        var pgn = PgnBuilder.BuildSubnetChange(192, 168, 5);
        var expected = new byte[] { 0x80, 0x81, 0x7F, 201, 5, 201, 201, 192, 168, 5, 0x47 };
        Assert.That(pgn, Is.EqualTo(expected));
    }

    [Test]
    public void BuildSubnetChange_OnlyOctets7To9Vary()
    {
        var a = PgnBuilder.BuildSubnetChange(10, 0, 0);
        var b = PgnBuilder.BuildSubnetChange(172, 16, 9);

        // Magic/header/crc bytes are identical; only [7..9] differ.
        Assert.That(a[7], Is.EqualTo(10));
        Assert.That(a[8], Is.EqualTo(0));
        Assert.That(a[9], Is.EqualTo(0));
        Assert.That(b[7], Is.EqualTo(172));
        Assert.That(b[8], Is.EqualTo(16));
        Assert.That(b[9], Is.EqualTo(9));
        for (int i = 0; i < a.Length; i++)
        {
            if (i is 7 or 8 or 9) continue;
            Assert.That(a[i], Is.EqualTo(b[i]), $"byte {i} should be constant");
        }
    }

    // ===== PGN 203 — scan reply parse =====

    [Test]
    public void TryParseScanReply_ExtractsModuleIpAndSubnet()
    {
        // module id 126 (steer), IP 192.168.5.126, subnet 192.168.5
        var reply = new byte[] { 0x80, 0x81, 126, 203, 7, 192, 168, 5, 126, 192, 168, 5, 0x17 };

        Assert.That(PgnBuilder.TryParseScanReply(reply, out var id, out var ip, out var subnet), Is.True);
        Assert.That(id, Is.EqualTo(126));
        Assert.That(ip, Is.EqualTo("192.168.5.126"));
        Assert.That(subnet, Is.EqualTo("192.168.5"));
    }

    [TestCase((byte)126, "192.168.5.126")]
    [TestCase((byte)123, "192.168.5.123")]
    [TestCase((byte)121, "192.168.5.121")]
    [TestCase((byte)120, "192.168.5.124")]
    public void TryParseScanReply_HandlesEachModuleId(byte moduleId, string lastOctetIp)
    {
        var parts = lastOctetIp.Split('.');
        var reply = new byte[]
        {
            0x80, 0x81, moduleId, 203, 7,
            byte.Parse(parts[0]), byte.Parse(parts[1]), byte.Parse(parts[2]), byte.Parse(parts[3]),
            byte.Parse(parts[0]), byte.Parse(parts[1]), byte.Parse(parts[2]), 0x00
        };

        Assert.That(PgnBuilder.TryParseScanReply(reply, out var id, out var ip, out _), Is.True);
        Assert.That(id, Is.EqualTo(moduleId));
        Assert.That(ip, Is.EqualTo(lastOctetIp));
    }

    [Test]
    public void TryParseScanReply_RejectsWrongPgnOrShortPacket()
    {
        // wrong PGN (200, not 203)
        var notReply = new byte[] { 0x80, 0x81, 126, 200, 3, 56, 0, 0, 0x47 };
        Assert.That(PgnBuilder.TryParseScanReply(notReply, out _, out _, out _), Is.False);

        // too short
        var tooShort = new byte[] { 0x80, 0x81, 126, 203, 7 };
        Assert.That(PgnBuilder.TryParseScanReply(tooShort, out _, out _, out _), Is.False);

        // bad header
        var badHeader = new byte[] { 0x00, 0x81, 126, 203, 7, 1, 2, 3, 4, 1, 2, 3, 0 };
        Assert.That(PgnBuilder.TryParseScanReply(badHeader, out _, out _, out _), Is.False);
    }
}
