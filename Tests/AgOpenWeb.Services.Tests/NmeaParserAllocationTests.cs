// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Text;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// The GPS receive path parses a datagram straight into <see cref="VehicleState"/> with
/// no heap allocation — what <c>[AutoSteerRx-PERF]</c> measures at runtime, asserted here
/// so a decoder that starts allocating fails in CI (receiver plan, non-regression 2).
/// </summary>
[TestFixture]
[NonParallelizable]
public class NmeaParserAllocationTests
{
    private static byte[] Sentence(string body)
    {
        byte checksum = 0;
        foreach (char c in body) checksum ^= (byte)c;
        return Encoding.ASCII.GetBytes($"${body}*{checksum:X2}");
    }

    [TestCase("PANDA,123456.00,4807.038,N,01131.000,E,4,12,0.9,100.0,0.0,5.5,900,12,0,0.00")]
    [TestCase("PAOGI,123456.00,4807.038,N,01131.000,E,4,12,0.9,100.0,0.0,5.5,90.0,1.25,0,0.00")]
    [TestCase("KSXT,20261009001935.90,-87.18038977,32.59045030,57.6625,20.54,-1.00,140.70,0.006,0.00,3,3,27,31,-13.911,-11.418,-3.127,0.004,-0.005,0.005,,")]
    public void ParseIntoState_AllocatesNothing(string body)
    {
        var data = Sentence(body);
        var config = new ConfigurationStore();
        var state = new VehicleState();

        // Warm up: JIT, first-call statics.
        for (int i = 0; i < 100; i++)
            Assert.That(NmeaParserServiceFast.ParseIntoState(data, ref state, config), Is.True);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
            NmeaParserServiceFast.ParseIntoState(data, ref state, config);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.That(allocated, Is.EqualTo(0), $"{body[..5]}: {allocated} bytes allocated over 1000 parses");
    }
}
