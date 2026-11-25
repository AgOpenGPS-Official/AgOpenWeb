// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Diagnostics;
using AgOpenWeb.Services.DataQuality;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests;

[TestFixture]
public class StreamQualityMonitorTests
{
    private static long At(double seconds) => (long)(seconds * Stopwatch.Frequency);

    /// <summary>Feeds arrivals <paramref name="gapsMs"/> apart from <paramref name="start"/>, evaluating
    /// once a second as the app does. Returns the last evaluation and the time it ended.</summary>
    private static (StreamQuality Last, double End, double? FaultyAt) Feed(StreamQualityMonitor m, double start, System.Collections.Generic.IEnumerable<double> gapsMs)
    {
        double t = start, nextEval = start + 1;
        double? faultyAt = null;
        StreamQuality last = default;
        m.Record(At(t));
        foreach (double gap in gapsMs)
        {
            t += gap / 1000.0;
            while (nextEval <= t)
            {
                last = m.Evaluate(At(nextEval));
                if (last.Faulty) faultyAt ??= nextEval - start;
                nextEval += 1;
            }
            m.Record(At(t));
        }
        last = m.Evaluate(At(t));
        if (last.Faulty) faultyAt ??= t - start;
        return (last, t, faultyAt);
    }

    private static System.Collections.Generic.IEnumerable<double> Repeat(double gapMs, double seconds)
    {
        for (double t = 0; t < seconds * 1000; t += gapMs) yield return gapMs;
    }

    [Test]
    public void An_even_10_Hz_stream_is_good()
    {
        var m = new StreamQualityMonitor("GPS positions");

        var (q, _, faultyAt) = Feed(m, 100, Repeat(100, 60));

        Assert.That(q.Verdict, Is.EqualTo(StreamVerdict.Even));
        Assert.That(q.Faulty, Is.False);
        Assert.That(faultyAt, Is.Null);
        Assert.That(q.RateHz, Is.EqualTo(10).Within(0.1));
        Assert.That(q.NominalHz, Is.EqualTo(10).Within(0.1));
        Assert.That(q.MissedPercent, Is.LessThan(1));
    }

    [Test]
    public void A_dropped_fix_now_and_then_is_not_a_fault()
    {
        // One fix in fifty lost: 2 %.
        var m = new StreamQualityMonitor("GPS positions");
        var gaps = new System.Collections.Generic.List<double>();
        for (int i = 0; i < 600; i++) gaps.Add(i % 50 == 49 ? 200 : 100);

        var (q, _, faultyAt) = Feed(m, 100, gaps);

        Assert.That(faultyAt, Is.Null);
        Assert.That(q.Verdict, Is.EqualTo(StreamVerdict.Even));
        Assert.That(q.MissedPercent, Is.EqualTo(2).Within(1));
    }

    [Test]
    public void A_jittery_100_Hz_stream_is_good()
    {
        // Arrivals read up to 4 ms early or late (the receive thread's scheduling), and once
        // a second the process pauses 50 ms and reads five at once.
        var m = new StreamQualityMonitor("Steer module data (PGN 253)");
        var rnd = new Random(7);
        var gaps = new System.Collections.Generic.List<double>();
        double due = 0, arrived = 0;
        for (int i = 1; i <= 6000; i++)
        {
            due = i * 10.0;
            double at = due + (rnd.NextDouble() * 8 - 4);
            if (i % 100 < 5) at = (i - i % 100 + 5) * 10.0;   // held, then read together
            if (at < arrived) at = arrived;
            gaps.Add(at - arrived);
            arrived = at;
        }

        var (q, _, faultyAt) = Feed(m, 100, gaps);

        Assert.That(faultyAt, Is.Null);
        Assert.That(q.Verdict, Is.EqualTo(StreamVerdict.Even));
        Assert.That(q.RateHz, Is.EqualTo(100).Within(1));
    }

    [Test]
    public void One_fix_in_five_lost_is_a_fault_after_the_hold_time()
    {
        var m = new StreamQualityMonitor("GPS positions");
        var gaps = new System.Collections.Generic.List<double>();
        for (int i = 0; i < 600; i++) gaps.Add(i % 4 == 3 ? 200 : 100);

        var (q, _, faultyAt) = Feed(m, 100, gaps);

        Assert.That(q.Faulty, Is.True);
        Assert.That(q.Verdict, Is.EqualTo(StreamVerdict.Gaps));
        Assert.That(q.MissedPercent, Is.EqualTo(20).Within(2));
        Assert.That(q.NominalHz, Is.EqualTo(10).Within(0.1));
        Assert.That(q.RateHz, Is.EqualTo(8).Within(0.2));
        Assert.That(q.Faults, Is.EqualTo(1));
        // Not before a window is full and has stayed bad for the hold time.
        Assert.That(faultyAt, Is.GreaterThanOrEqualTo(StreamQualityMonitor.HoldSeconds));
        Assert.That(faultyAt, Is.LessThan(30));
    }

    [Test]
    public void A_short_bad_stretch_is_not_a_fault()
    {
        // 3 s of losses in a good stream: the windows that hold it are bad, but not for the hold time.
        var m = new StreamQualityMonitor("GPS positions");
        var gaps = new System.Collections.Generic.List<double>();
        gaps.AddRange(Repeat(100, 30));
        for (int i = 0; i < 10; i++) gaps.Add(300);
        gaps.AddRange(Repeat(100, 40));

        var (q, _, faultyAt) = Feed(m, 100, gaps);

        Assert.That(faultyAt, Is.Null);
        Assert.That(q.Faulty, Is.False);
        Assert.That(q.Faults, Is.EqualTo(0));
        Assert.That(q.WorstMissedPercent, Is.GreaterThan(10), "The bad stretch is kept for the bug report");
    }

    [Test]
    public void Bursts_at_a_Wi_Fi_beacon_are_a_fault()
    {
        // Measured on #900: 8 datagrams every 205 ms.
        var m = new StreamQualityMonitor("Steer module data (PGN 253)");
        var gaps = new System.Collections.Generic.List<double>();
        for (int i = 0; i < 2400; i++) gaps.Add(i % 8 == 0 ? 204.3 : 0.1);

        var (q, _, faultyAt) = Feed(m, 100, gaps);

        Assert.That(q.Faulty, Is.True);
        Assert.That(q.Verdict, Is.EqualTo(StreamVerdict.Bursts));
        Assert.That(q.NominalHz, Is.EqualTo(0), "The sender's rate cannot be read from bursts");
        Assert.That(q.RateHz, Is.EqualTo(39).Within(1));
        Assert.That(faultyAt, Is.LessThan(30));
    }

    [Test]
    public void A_fault_ends_when_the_stream_has_been_good_for_the_hold_time()
    {
        var m = new StreamQualityMonitor("GPS positions");
        var bad = new System.Collections.Generic.List<double>();
        for (int i = 0; i < 300; i++) bad.Add(i % 4 == 3 ? 200 : 100);
        var (q, end, _) = Feed(m, 100, bad);
        Assert.That(q.Faulty, Is.True);

        // Good again: still faulty while the window holds the bad stretch and for the hold time after.
        (q, end, _) = Feed(m, end + 0.1, Repeat(100, 8));
        Assert.That(q.Faulty, Is.True);
        (q, _, _) = Feed(m, end + 0.1, Repeat(100, 30));

        Assert.That(q.Faulty, Is.False);
        Assert.That(q.Verdict, Is.EqualTo(StreamVerdict.Even));
        Assert.That(q.Faults, Is.EqualTo(1));
    }

    [Test]
    public void Silence_is_not_this_fault()
    {
        var m = new StreamQualityMonitor("GPS positions");
        var bad = new System.Collections.Generic.List<double>();
        for (int i = 0; i < 300; i++) bad.Add(i % 4 == 3 ? 200 : 100);
        var (q, end, _) = Feed(m, 100, bad);
        Assert.That(q.Faulty, Is.True);

        q = m.Evaluate(At(end + 3));

        Assert.That(q.Verdict, Is.EqualTo(StreamVerdict.NoData));
        Assert.That(q.Faulty, Is.False);
    }

    [Test]
    public void A_stream_that_has_just_started_is_not_judged()
    {
        var m = new StreamQualityMonitor("GPS positions");
        var gaps = new System.Collections.Generic.List<double>();
        for (int i = 0; i < 40; i++) gaps.Add(i % 2 == 1 ? 300 : 100);

        var (q, _, _) = Feed(m, 100, gaps);   // 8 s

        Assert.That(q.Verdict, Is.EqualTo(StreamVerdict.NoData));
        Assert.That(q.Faulty, Is.False);
    }

    [Test]
    public void A_few_arrivals_are_not_judged()
    {
        // One a second: 10 in the window.
        var m = new StreamQualityMonitor("Hello");

        var (q, _, _) = Feed(m, 100, Repeat(1000, 60));

        Assert.That(q.Verdict, Is.EqualTo(StreamVerdict.NoData));
    }

    [Test]
    public void A_stream_that_does_not_warn_is_measured_and_never_faulty()
    {
        var m = new StreamQualityMonitor("Machine module data (PGN 237)", warns: false);
        var gaps = new System.Collections.Generic.List<double>();
        for (int i = 0; i < 600; i++) gaps.Add(i % 4 == 3 ? 200 : 100);

        var (q, _, faultyAt) = Feed(m, 100, gaps);

        Assert.That(faultyAt, Is.Null);
        Assert.That(q.Verdict, Is.EqualTo(StreamVerdict.Gaps));
        Assert.That(q.MissedPercent, Is.EqualTo(20).Within(2));
    }

    [Test]
    public void A_long_wait_starts_the_stream_anew()
    {
        var m = new StreamQualityMonitor("GPS positions");
        var (_, end, _) = Feed(m, 100, Repeat(100, 30));

        // Unplugged for a minute, then good again: the minute is not a gap of 600 fixes.
        var (q, _, faultyAt) = Feed(m, end + 60, Repeat(100, 30));

        Assert.That(faultyAt, Is.Null);
        Assert.That(q.Verdict, Is.EqualTo(StreamVerdict.Even));
        Assert.That(q.LongestGapMs, Is.LessThan(150));
    }

    [Test]
    public void Reset_forgets_the_stream()
    {
        var m = new StreamQualityMonitor("GPS positions");
        var (_, end, _) = Feed(m, 100, Repeat(100, 30));

        m.Reset();

        Assert.That(m.Evaluate(At(end)).Verdict, Is.EqualTo(StreamVerdict.NoData));
    }

    // The first minute of the GPS log in the bug report dump of #922 (22 Nov 2026): a 10 Hz dual
    // receiver's fixes as they reached an Android tablet, in ms between one and the next. The
    // dump has 3,000 fixes in 380.7 s where 10 Hz gives about 3,800; the report said "it works
    // maybe at 1 Hz", and the app said nothing.
    private static readonly double[] Issue922GapsMs =
    {
        105, 407, 104, 103, 101, 103, 104, 102, 118, 85, 102, 103, 307, 104, 102, 116, 88, 103, 101, 105, 100, 103, 102, 103, 103,
        111, 194, 105, 104, 101, 103, 100, 103, 103, 116, 87, 413, 101, 205, 102, 102, 104, 101, 106, 202, 422, 91, 101, 107, 98,
        204, 105, 98, 104, 114, 89, 104, 306, 105, 102, 109, 94, 206, 102, 615, 102, 107, 96, 209, 99, 104, 1, 99, 104, 328,
        290, 99, 102, 307, 116, 197, 97, 102, 103, 207, 102, 219, 89, 100, 102, 207, 408, 107, 97, 102, 105, 204, 102, 100, 110,
        110, 300, 95, 104, 210, 96, 103, 115, 192, 103, 103, 103, 101, 102, 103, 224, 82, 208, 99, 102, 105, 100, 413, 100, 103,
        103, 103, 100, 108, 98, 102, 106, 12, 88, 410, 1, 102, 101, 104, 115, 91, 101, 304, 105, 101, 102, 104, 113, 91, 103,
        99, 109, 101, 99, 102, 105, 510, 104, 101, 206, 101, 102, 102, 104, 103, 104, 510, 113, 98, 96, 101, 103, 105, 103, 99,
        107, 100, 106, 96, 103, 106, 100, 310, 101, 103, 112, 91, 410, 101, 205, 101, 107, 101, 204, 104, 99, 423, 96, 98, 99,
        103, 106, 101, 102, 104, 102, 113, 92, 102, 102, 102, 104, 102, 100, 105, 101, 117, 89, 99, 113, 403, 100, 102, 111, 98,
        100, 106, 101, 99, 107, 428, 81, 203, 103, 101, 103, 103, 103, 114, 89, 101, 107, 98, 311, 102, 103, 113, 88, 208, 103,
        100, 103, 100, 207, 119, 187, 106, 101, 101, 210, 97, 110, 403, 206, 100, 102, 104, 117, 93, 97, 204, 103, 102, 104, 100,
        103, 205, 310, 101, 204, 106, 100, 206, 102, 103, 100, 104, 102, 100, 104, 101, 106, 99, 411, 2, 100, 103, 100, 117, 92,
        101, 203, 104, 102, 102, 100, 106, 114, 90, 102, 103, 104, 103, 203, 100, 102, 205, 310, 101, 104, 101, 102, 104, 109, 94,
        205, 411, 108, 202, 97, 102, 205, 103, 205, 103, 107, 115, 300, 93, 103, 101, 104, 99, 116, 193, 103, 104, 101, 104, 202,
        104, 110, 93, 127, 76, 207, 411, 13, 100, 91, 103, 103, 104, 100, 103, 102, 104, 202, 109, 96, 103, 102, 104, 101, 103,
        102, 117, 90, 99, 103, 107, 97, 101, 106, 102, 104, 112, 89, 103, 112, 93, 102, 103, 104, 409, 105, 99, 207, 100, 104,
        102, 115, 93, 100, 101, 204, 214, 93, 221, 86, 103, 102, 106, 99, 104, 100, 105, 100, 121, 91, 97, 101, 103, 101, 103,
        103, 101, 104, 410, 100, 104, 1, 99, 105, 105, 113, 96, 95, 102, 103, 205, 102, 105, 100, 117, 87, 104, 102, 105, 100,
        103, 102, 104, 101, 205, 102, 103, 101, 105, 98, 106, 103, 102, 112, 90, 413, 99, 107, 101, 115, 96, 95, 204, 104, 102,
        204, 103, 118, 88, 102, 103, 102, 101, 104, 101, 104, 103, 203, 102, 102, 103, 101, 106, 101, 203, 116, 89, 104, 100, 409,
        105, 99, 20, 95, 194, 102, 103, 102, 103, 102, 208, 119, 185, 105, 98, 104, 103, 104, 103, 112, 90, 103, 101, 102, 106,
        101, 100, 104, 107, 112, 89, 101, 103, 103, 104, 407, 107, 97, 104, 1, 308, 100, 103, 311, 99, 104, 101, 101, 103, 102,
        103, 101, 115, 93, 204, 103, 100, 103, 105, 101, 216, 89, 307, 104, 104, 103, 409, 105, 99, 102, 103, 3, 99, 102, 102,
        115, 97, 95, 104, 202, 102, 106, 101, 104, 113, 92, 101, 103, 102, 103, 103, 102, 103, 104, 115, 87, 103, 102, 205, 101,
    };

    [Test]
    public void The_fixes_of_issue_922_are_a_fault_within_a_minute()
    {
        var m = new StreamQualityMonitor("GPS positions");

        var (q, _, faultyAt) = Feed(m, 100, Issue922GapsMs);

        Assert.That(faultyAt, Is.Not.Null, "The app said nothing of a stream with one fix in five missing");
        Assert.That(faultyAt, Is.LessThan(60));
        Assert.That(q.Faulty, Is.True);
        Assert.That(q.NominalHz, Is.EqualTo(10).Within(0.5), "The receiver's own rate, read from the fixes that came on time");
        Assert.That(q.RateHz, Is.LessThan(8.5));
        Assert.That(q.WorstMissedPercent, Is.GreaterThan(15));
    }
}
