// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using AgOpenWeb.Models.GPS;
using AgOpenWeb.Services.DataQuality;
using AgOpenWeb.Services.Gps;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// Positions that are not newer than the one before (<see cref="FixTimeSensor"/>). From #900: a
/// tablet cabled to the modules' router that had also joined its Wi-Fi got every broadcast
/// twice, the Wi-Fi copy late; a late fix read as a reverse and AutoSteer went off.
/// </summary>
[TestFixture]
public class FixTimeSensorTests
{
    private const int Panda = 0, Gga = 3, Hpr = 5;

    private static long At(double seconds) => (long)(seconds * Stopwatch.Frequency);

    /// <summary>The receiver's time of day for a fix made <paramref name="seconds"/> after 08:00:00.</summary>
    private static int Time(double seconds) => 8 * 360000 + (int)Math.Round(seconds * 100);

    /// <summary>A 10 Hz receiver on a cable for <paramref name="seconds"/>, and whatever copies
    /// <paramref name="copies"/> adds: (the fix's own time, when the copy arrives).</summary>
    private static (FixTimeSensor sensor, StreamQualityMonitor arrivals, List<DataReading> read) Run(
        double seconds, Func<double, IEnumerable<(double made, double arrives)>>? copies = null)
    {
        var events = new List<(double arrives, double made)>();
        for (int i = 0; i < seconds * 10; i++)
        {
            double made = 100 + i * 0.1;
            events.Add((made + 0.02, made));
            if (copies != null) events.AddRange(copies(made).Select(c => (c.arrives, c.made)));
        }
        events.Sort();

        var sensor = new FixTimeSensor("GPS position times");
        var arrivals = new StreamQualityMonitor("GPS positions");
        var read = new List<DataReading>();
        double next = 101;
        foreach (var (arrives, made) in events)
        {
            for (; next <= arrives; next += 1) { read.Add(sensor.Read(At(next))); arrivals.Evaluate(At(next)); }
            sensor.Record(Panda, Time(made), At(arrives));
            arrivals.Record(At(arrives));
        }
        return (sensor, arrivals, read);
    }

    [Test]
    public void EveryPositionNewer_InOrder()
    {
        var (sensor, _, read) = Run(120);

        Assert.That(read.Skip(1).Select(r => r.Finding), Is.All.EqualTo(DataFinding.InOrder));
        Assert.That(read.Any(r => r.Faulty), Is.False);
        Assert.That(sensor.Total, Is.Zero);
    }

    [Test]
    public void TwoLinks_AsMeasuredOnTheBench_IsAFaultWithinSeconds_ThoughTheFixesArriveEvenly()
    {
        // #900, 22 Nov: the Wi-Fi delivered half of what the cable did, 1.5 s late, at every
        // second beacon (205 ms).
        var rnd = new Random(1);
        var (sensor, arrivals, read) = Run(60, made => rnd.Next(2) == 0
            ? new[] { (made, Math.Ceiling((made + 1.5) / 0.205) * 0.205) }
            : Array.Empty<(double, double)>());

        Assert.That(read.FindIndex(r => r.Faulty), Is.InRange(1, 4), "seconds until the fault");
        Assert.That(read[^1].Finding, Is.EqualTo(DataFinding.OutOfOrder));
        Assert.That(read[^1].Amount, Is.GreaterThan(200));
        Assert.That(sensor.Faults, Is.EqualTo(1));
        // Why this sensor is needed: how the fixes arrive shows nothing.
        var q = arrivals.Evaluate(At(160));
        Assert.That(q.Faulty, Is.False);
        Assert.That(q.Verdict, Is.EqualTo(StreamVerdict.Even));
    }

    [Test]
    public void TwoLinks_AsOnTheTestersTractor_AFewLateFixesAMinute_IsAFault()
    {
        // #900, 20 Nov: 98 late fixes in ten minutes, 0.3 s late. One every 6 s.
        var (_, _, read) = Run(120, made => Math.Abs(made % 6) < 0.05
            ? new[] { (made, made + 0.3) }
            : Array.Empty<(double, double)>());

        Assert.That(read.FindIndex(r => r.Faulty), Is.InRange(20, 40), "seconds until the fifth one");
        Assert.That(read[^1].Faulty, Is.True);
    }

    [Test]
    public void OneStrayRepeat_IsCountedButNoFault()
    {
        var (sensor, _, read) = Run(120, made => Math.Abs(made - 130) < 0.01
            ? new[] { (made, made + 0.3) }
            : Array.Empty<(double, double)>());

        Assert.That(read.Any(r => r.Faulty), Is.False);
        Assert.That(read.Count(r => r.Finding == DataFinding.OutOfOrder), Is.InRange(59, 61), "shown for the minute it is in the window");
        Assert.That(sensor.Total, Is.EqualTo(1));
    }

    [Test]
    public void TheFaultEnds_AfterAWholeMinuteInOrder()
    {
        var (sensor, _, read) = Run(200, made => made < 130
            ? new[] { (made, made + 0.3) }
            : Array.Empty<(double, double)>());

        int began = read.FindIndex(r => r.Faulty), ended = read.FindIndex(began, r => !r.Faulty);
        Assert.That(began, Is.LessThan(3));
        Assert.That(ended, Is.InRange(88, 92), "30 s of copies, then 60 s without");
        Assert.That(read[^1].Finding, Is.EqualTo(DataFinding.InOrder));
        Assert.That(sensor.Faults, Is.EqualTo(1));
    }

    [Test]
    public void SentencesOfOneEpoch_ShareATime_AndAreNotRepeats()
    {
        var sensor = new FixTimeSensor("GPS position times");
        for (int i = 0; i < 600; i++)
        {
            sensor.Record(Gga, Time(i * 0.1), At(100 + i * 0.1));
            sensor.Record(Hpr, Time(i * 0.1), At(100 + i * 0.1 + 0.005));
        }
        Assert.That(sensor.Total, Is.Zero);
        Assert.That(sensor.Read(At(160)).Finding, Is.EqualTo(DataFinding.InOrder));
    }

    [Test]
    public void Midnight_IsNewer()
    {
        var sensor = new FixTimeSensor("GPS position times");
        const int day = 24 * 360000;
        for (int i = -50; i < 50; i++)
            sensor.Record(Panda, ((i * 10) % day + day) % day, At(100 + i * 0.1));
        Assert.That(sensor.Total, Is.Zero);
    }

    [Test]
    public void AReceiverWithoutTheTime_IsNotJudged()
    {
        var sensor = new FixTimeSensor("GPS position times");
        for (int i = 0; i < 100; i++)
        {
            sensor.Record(Panda, -1, At(100 + i * 0.1));   // an empty time field
            sensor.Record(Gga, 0, At(100 + i * 0.1));      // 000000.00 on every sentence
        }
        Assert.That(sensor.Total, Is.Zero);
        Assert.That(sensor.Read(At(110)).Faulty, Is.False);
    }

    [Test]
    public void Silence_IsNoData_AndANewSessionStartsClean()
    {
        var sensor = new FixTimeSensor("GPS position times");
        for (int i = 0; i < 50; i++) sensor.Record(Panda, Time(50 + i * 0.1), At(100 + i * 0.1));
        Assert.That(sensor.Read(At(108)).Finding, Is.EqualTo(DataFinding.NoData));

        // The receiver restarted with an earlier time (its clock not yet set): not a repeat.
        for (int i = 0; i < 50; i++) sensor.Record(Panda, Time(i * 0.1), At(120 + i * 0.1));
        Assert.That(sensor.Total, Is.Zero);
        Assert.That(sensor.Read(At(125)).Finding, Is.EqualTo(DataFinding.InOrder));
    }

    [TestCase("$PANDA,162255.50,3924.90,N,00731.80,W,4,12,0.7,341.9,1.2,4.8,2217,31,-12,0.5*5A", 16 * 360000 + 22 * 6000 + 5550)]
    [TestCase("$GNGGA,000001.00,3924.90,N,00731.80,W,4,12,0.7,341.9,M,1.2,M,4.8,2217*5A", 100)]
    [TestCase("$GNGGA,235959.9,3924.90,N", 24 * 360000 - 10)]
    [TestCase("$KSXT,20261124084745.10,7.5,39.4,341.9", 8 * 360000 + 47 * 6000 + 4510)]
    [TestCase("$GNGGA,,,,,,0,00,99.9,,,,,,*56", -1)]
    [TestCase("$GPVTG,054.7,T,034.4,M,005.5,N,010.2,K*48", -1)]
    [TestCase("$GNGGA,996199.00,3924.90,N", -1)]
    [TestCase("no comma", -1)]
    public void TheTimeIsReadFromTheFirstField(string sentence, int centiseconds)
    {
        Assert.That(FixTimeSensor.TimeOfDay(Encoding.ASCII.GetBytes(sentence)), Is.EqualTo(centiseconds));
    }

    [Test]
    public void TheSentenceMonitor_FeedsTheTimeOfEverySentenceThatPassed()
    {
        var gps = new GpsSentenceMonitor();
        byte[] Line(string time) => Encoding.ASCII.GetBytes("$PANDA," + time + ",3924.90,N,00731.80,W,4,12,0.7,341.9,1.2,4.8,2217,31,-12,0.5*5A\r\n");

        gps.Record(Line("080000.00"), NmeaParseResult.Accepted, At(100.0));
        gps.Record(Line("080000.10"), NmeaParseResult.Accepted, At(100.1));
        gps.Record(Line("080000.00"), NmeaParseResult.Accepted, At(100.15));   // the first one again
        gps.Record(Line("075959.00"), NmeaParseResult.BadChecksum, At(100.16)); // refused: not looked at
        gps.Record(Line("080000.20"), NmeaParseResult.Accepted, At(100.2));

        Assert.That(gps.Times.Total, Is.EqualTo(1));
        Assert.That(gps.DataSensors.Take(2), Is.EqualTo(new IDataSensor[] { gps.Fixes, gps.Times }));
    }
}
