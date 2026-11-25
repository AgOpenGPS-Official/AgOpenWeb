// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AgOpenWeb.Services.DataQuality;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// <see cref="FixRateSensor"/>: the receiver sends less often than the app is set up for.
/// <see cref="RefusedSentenceSensor"/>: sentences that arrive damaged.
/// </summary>
[TestFixture]
public class FixRateAndWholeSensorTests
{
    private static long At(double seconds) => (long)(seconds * Stopwatch.Frequency);

    /// <summary>A stream at <paramref name="hz"/> for <paramref name="seconds"/>, one in
    /// <paramref name="loseEvery"/> lost, both sensors read once a second.</summary>
    private static List<DataReading> Rate(double hz, double seconds, double expected = 10, int loseEvery = 0,
        FixRateSensor? sensor = null, StreamQualityMonitor? arrivals = null, double from = 100)
    {
        arrivals ??= new StreamQualityMonitor("GPS positions");
        sensor ??= new FixRateSensor("GPS position rate", arrivals, () => expected);
        var read = new List<DataReading>();
        double next = from + 1;
        for (int i = 0; i < seconds * hz; i++)
        {
            double t = from + i / hz;
            for (; next <= t; next += 1) { arrivals.Read(At(next)); read.Add(sensor.Read(At(next))); }
            if (loseEvery == 0 || i % loseEvery != 0) arrivals.Record(At(t));
        }
        return read;
    }

    [Test]
    public void AtTheExpectedRate_Good()
    {
        var read = Rate(10, 60);
        Assert.That(read.Any(r => r.Faulty), Is.False);
        Assert.That(read[^1].Finding, Is.EqualTo(DataFinding.AtRate));
        Assert.That(read[^1].RateHz, Is.EqualTo(10).Within(0.1));
    }

    [Test]
    public void HalfTheExpectedRate_IsAFaultAfterTheHold()
    {
        var read = Rate(5, 60);
        Assert.That(read.FindIndex(r => r.Faulty), Is.InRange(18, 26), "seconds: enough arrivals to judge, then the hold");
        Assert.That(read[^1].Finding, Is.EqualTo(DataFinding.Slow));
        Assert.That(read[^1].RateHz, Is.EqualTo(5).Within(0.1));
        Assert.That(read[^1].NominalHz, Is.EqualTo(10));
    }

    [Test]
    public void AFasterReceiver_Good()
    {
        Assert.That(Rate(20, 60).Any(r => r.Faulty || r.Finding == DataFinding.Slow), Is.False);
    }

    [Test]
    public void ALinkThatLosesPackets_IsNotASlowReceiver()
    {
        // One in five lost (#922): 8 arrive a second, but the receiver still sends at 10. The
        // arrival sensor reports that; this one has nothing to add. (With a third or more
        // lost the usual wait itself doubles, and the data is slow by any measure.)
        var read = Rate(10, 60, loseEvery: 5);
        Assert.That(read.Any(r => r.Faulty || r.Finding == DataFinding.Slow), Is.False);
    }

    [Test]
    public void TheFaultEnds_WhenTheRateIsRight_AndWhenTheStreamStops()
    {
        var arrivals = new StreamQualityMonitor("GPS positions");
        var sensor = new FixRateSensor("GPS position rate", arrivals, () => 10);
        Assert.That(Rate(5, 60, sensor: sensor, arrivals: arrivals)[^1].Faulty, Is.True);

        var after = Rate(10, 60, sensor: sensor, arrivals: arrivals, from: 160);
        Assert.That(after[^1].Faulty, Is.False);
        Assert.That(sensor.Faults, Is.EqualTo(1));

        Assert.That(Rate(5, 60, sensor: sensor, arrivals: arrivals, from: 220)[^1].Faulty, Is.True);
        arrivals.Read(At(290));
        var silent = sensor.Read(At(290));
        Assert.That(silent.Faulty, Is.False);
        Assert.That(silent.Finding, Is.EqualTo(DataFinding.NoData));
    }

    [Test]
    public void DamagedSentences_FiveInAMinuteIsAFault_AndACleanMinuteEndsIt()
    {
        var sensor = new RefusedSentenceSensor("GPS sentences");
        var read = new List<DataReading>();
        for (int i = 0; i < 2000; i++)
        {
            double t = 100 + i * 0.1;
            bool damaged = i < 600 && i % 100 == 50;   // one every 10 s for the first minute
            sensor.Record(damaged, badChecksum: damaged, At(t));
            if (i % 10 == 9) read.Add(sensor.Read(At(t)));
        }

        int began = read.FindIndex(r => r.Faulty), ended = read.FindIndex(began, r => !r.Faulty);
        Assert.That(began, Is.InRange(44, 47), "the fifth one");
        Assert.That(ended, Is.InRange(114, 117), "a minute after the last");
        Assert.That(read[^1].Finding, Is.EqualTo(DataFinding.Whole));
        Assert.That(sensor.Total, Is.EqualTo(6));
        Assert.That(sensor.Figures(), Is.EqualTo("whole; 6 damaged since the start (6 failed the checksum)"));
    }

    [Test]
    public void OneDamagedSentence_IsCountedButNoFault()
    {
        var sensor = new RefusedSentenceSensor("GPS sentences");
        for (int i = 0; i < 300; i++) sensor.Record(damaged: i == 100, badChecksum: false, At(100 + i * 0.1));
        var r = sensor.Read(At(130));
        Assert.That(r.Faulty, Is.False);
        Assert.That(r.Finding, Is.EqualTo(DataFinding.Damaged));
        Assert.That(r.Amount, Is.EqualTo(1));
    }

    [Test]
    public void ASentenceMonitor_CountsOnlySentencesItDecodes()
    {
        var gps = new AgOpenWeb.Services.Gps.GpsSentenceMonitor();
        byte[] line = System.Text.Encoding.ASCII.GetBytes("$PANDA,080000.00,3924.90,N*00\r\n");
        gps.Record(line, AgOpenWeb.Models.GPS.NmeaParseResult.BadChecksum, At(100));
        gps.Record(line, AgOpenWeb.Models.GPS.NmeaParseResult.BadFields, At(100.1));
        gps.Record(line, AgOpenWeb.Models.GPS.NmeaParseResult.UnknownSentence, At(100.2));   // a kind not decoded
        gps.Record(line, AgOpenWeb.Models.GPS.NmeaParseResult.BadFrame, At(100.3));          // no sentence at all
        gps.Record(line, AgOpenWeb.Models.GPS.NmeaParseResult.Accepted, At(100.4));

        Assert.That(gps.Whole.Total, Is.EqualTo(2));
        Assert.That(gps.DataSensors, Is.EqualTo(new IDataSensor[] { gps.Fixes, gps.Times, gps.Rate, gps.Whole }));
    }
}
