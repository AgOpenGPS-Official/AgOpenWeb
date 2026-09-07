// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.Collections.Generic;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.AutoSteer;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// Every outbound PGN carries a correct trailing checksum, for every builder in
/// <see cref="PgnBuilder"/> — not just the module-network trio covered by
/// <c>ModuleNetworkPgnTests</c>.
/// </summary>
/// <remarks>
/// All builders stamp their CRC through the single <c>WithCrc</c> helper, which
/// derives the checksum span from the buffer length. These tests are the fence
/// around that: if a packet grows a data byte, or a builder goes back to
/// hand-writing its own offset and gets it wrong, the round-trip here fails.
/// The expected sizes are asserted alongside so a silent layout change is
/// caught rather than being absorbed into a still-valid checksum.
/// </remarks>
[TestFixture]
public class PgnChecksumTests
{
    /// <summary>
    /// Reference CRC, computed independently of the production helper: sum of
    /// bytes [2 .. len-2], mod 256. Matches PgnMessage.CalculateCRC.
    /// </summary>
    private static byte ReferenceCrc(byte[] packet)
    {
        int sum = 0;
        for (int i = 2; i < packet.Length - 1; i++) sum += packet[i];
        return (byte)(sum & 0xFF);
    }

    private static VehicleState SampleState() => new()
    {
        IsAutoSteerEngaged = true,
        GpsValid = true,
        SteerSwitchActive = true,
        SteerAngle = 7.5,
        Speed = 9.25,
        SectionStates = 0xA5C3
    };

    /// <summary>
    /// One case per builder. Non-default field values throughout so a checksum
    /// that ignores part of the payload cannot pass by summing zeros.
    /// </summary>
    private static IEnumerable<TestCaseData> AllBuilders()
    {
        yield return Case("BuildAutoSteerPgn", 14, () => { var s = SampleState(); return PgnBuilder.BuildAutoSteerPgn(ref s); });
        yield return Case("BuildMachinePgn", 14, () => { var s = SampleState(); return PgnBuilder.BuildMachinePgn(ref s, uturn: 1, hydLift: 2, tram: 3, geoStop: 1); });
        yield return Case("BuildSection64Pgn", 16, () => { var s = SampleState(); return PgnBuilder.BuildSection64Pgn(ref s); });

        yield return Case("BuildMachineConfigPgn", 14, () => PgnBuilder.BuildMachineConfigPgn(new MachineConfig
        {
            RaiseTime = 3,
            LowerTime = 5,
            HydraulicLiftEnabled = true,
            User1Value = 11,
            User2Value = 22,
            User3Value = 33,
            User4Value = 44
        }));

        yield return Case("BuildMachinePinsPgn", 30, () => PgnBuilder.BuildMachinePinsPgn(new MachineConfig()));

        yield return Case("BuildSteerSettingsPgn", 14, () => PgnBuilder.BuildSteerSettingsPgn(new AutoSteerConfig
        {
            ProportionalGain = 90,
            MaxPwm = 200,
            MinPwm = 30,
            CountsPerDegree = 120,
            WasOffset = -1234,
            Ackermann = 150
        }));

        yield return Case("BuildSteerConfigPgn", 11, () => PgnBuilder.BuildSteerConfigPgn(new AutoSteerConfig
        {
            InvertWas = true,
            DanfossEnabled = true,
            PressureSensorEnabled = true,
            MinSteerSpeed = 1.5
        }));

        yield return Case("BuildScanRequest", 9, PgnBuilder.BuildScanRequest);
        yield return Case("BuildHelloPacket", 9, PgnBuilder.BuildHelloPacket);
        yield return Case("BuildSubnetChange", 11, () => PgnBuilder.BuildSubnetChange(172, 16, 9));

        static TestCaseData Case(string name, int expectedLength, Func<byte[]> build) =>
            new TestCaseData(build, expectedLength).SetName($"{name}_HasValidChecksum");
    }

    [TestCaseSource(nameof(AllBuilders))]
    public void EveryBuilder_StampsAChecksumThatValidates(Func<byte[]> build, int expectedLength)
    {
        var packet = build();

        Assert.Multiple(() =>
        {
            Assert.That(packet, Has.Length.EqualTo(expectedLength), "packet length");
            Assert.That(packet[0], Is.EqualTo(0x80), "header1");
            Assert.That(packet[1], Is.EqualTo(0x81), "header2");
            Assert.That(packet[^1], Is.EqualTo(ReferenceCrc(packet)), "trailing CRC");
            Assert.That(PgnBuilder.ValidateChecksum(packet), Is.True, "round-trips through ValidateChecksum");
        });
    }

    /// <summary>
    /// Corrupting any single payload byte must break validation. This is what
    /// makes the checksum worth computing at all — a CRC that passes regardless
    /// of the payload (the old hardcoded 0x47) would survive every other
    /// assertion in this fixture.
    /// </summary>
    [TestCaseSource(nameof(AllBuilders))]
    public void EveryBuilder_ChecksumTracksThePayload(Func<byte[]> build, int expectedLength)
    {
        _ = expectedLength;

        // Snapshot once and mutate copies. The hot-path builders hand back a
        // reused thread-local buffer, so calling build() again inside the loop
        // would corrupt the buffer the previous iteration just poked.
        var pristine = build();
        var original = (byte[])pristine.Clone();

        // Bytes [2 .. len-2] are covered by the checksum; [0..1] are the header.
        for (int i = 2; i < original.Length - 1; i++)
        {
            var packet = (byte[])original.Clone();
            packet[i]++;
            Assert.That(PgnBuilder.ValidateChecksum(packet), Is.False,
                $"corrupting byte {i} should invalidate the checksum");
        }
    }
}
