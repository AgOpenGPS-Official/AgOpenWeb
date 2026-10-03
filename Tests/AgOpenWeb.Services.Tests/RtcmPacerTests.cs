using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models.Timing;
using AgOpenWeb.Services;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// Issue #169: RTCM to the AiO is metered AgIO-style (≤256-byte datagrams, spaced), and a
/// backlog that would only deliver stale corrections is dropped (#334). Driven by a TestClock.
/// </summary>
[TestFixture]
public class RtcmPacerTests
{
    private TestClock _clock = null!;
    private RtcmPacer _pacer = null!;

    [SetUp]
    public void SetUp()
    {
        _clock = new TestClock();
        _pacer = new RtcmPacer(_clock);
    }

    private static byte[] Sequence(int length, int start = 0) =>
        Enumerable.Range(start, length).Select(i => (byte)i).ToArray();

    /// <summary>Drain the queue as the send loop would, stepping the clock by the wait.</summary>
    private List<(double AtMs, byte[] Chunk)> Drain()
    {
        var sent = new List<(double, byte[])>();
        double t = 0;
        for (int guard = 0; _pacer.Count > 0 && guard < 10_000; guard++)
        {
            if (_pacer.TryDequeue(out var chunk, out _)) sent.Add((t, chunk));
            double wait = _pacer.MsUntilDue();
            if (_pacer.Count > 0 && wait > 0) { _clock.AdvanceMs(wait); t += wait; }
        }
        return sent;
    }

    [Test]
    public void Chunks_AreAtMost256Bytes()
    {
        _pacer.Enqueue(Sequence(4096));
        _pacer.Enqueue(Sequence(3000));

        var sent = Drain();

        Assert.That(sent.All(s => s.Chunk.Length <= RtcmPacer.ChunkSize), Is.True);
        Assert.That(RtcmPacer.ChunkSize, Is.EqualTo(256));
        Assert.That(sent.Sum(s => s.Chunk.Length), Is.EqualTo(7096));
    }

    [Test]
    public void Order_IsPreserved_AcrossReadsAndChunks()
    {
        byte[] a = Sequence(300, 0), b = Sequence(100, 44), c = Sequence(700, 7);
        _pacer.Enqueue(a);
        _pacer.Enqueue(b);
        _pacer.Enqueue(c);

        var all = Drain().SelectMany(s => s.Chunk).ToArray();

        Assert.That(all, Is.EqualTo(a.Concat(b).Concat(c).ToArray()));
    }

    [Test]
    public void FirstDatagram_GoesImmediately_ThenSpacedByInterval()
    {
        _pacer.Enqueue(Sequence(1000));

        Assert.That(_pacer.TryDequeue(out var first, out _), Is.True, "no wait for the first datagram");
        Assert.That(first.Length, Is.EqualTo(256));

        Assert.That(_pacer.TryDequeue(out _, out _), Is.False, "second datagram must wait");
        Assert.That(_pacer.MsUntilDue(), Is.EqualTo(RtcmPacer.IntervalMs).Within(0.01));

        _clock.AdvanceMs(RtcmPacer.IntervalMs - 1);
        Assert.That(_pacer.TryDequeue(out _, out _), Is.False);
        _clock.AdvanceMs(1);
        Assert.That(_pacer.TryDequeue(out var second, out _), Is.True);
        Assert.That(second.Length, Is.EqualTo(256));
    }

    [Test]
    public void Burst_IsSpreadOverTime_NotSentBackToBack()
    {
        _pacer.Enqueue(Sequence(4096));
        _pacer.Enqueue(Sequence(3000));

        var sent = Drain();

        Assert.That(sent, Has.Count.EqualTo(28)); // ceil(7096 / 256)
        for (int i = 1; i < sent.Count; i++)
            Assert.That(sent[i].AtMs - sent[i - 1].AtMs, Is.GreaterThanOrEqualTo(RtcmPacer.IntervalMs - 1e-6));
        // 27 gaps at 25 ms: drained within a second, so nothing reaches MaxAgeMs.
        Assert.That(sent[^1].AtMs, Is.LessThan(RtcmPacer.MaxAgeMs));
    }

    [Test]
    public void NormalEpoch_AfterIdle_IsNotDelayed()
    {
        // A basic 1 Hz stream (~355 B/epoch) never backs up: each epoch's first datagram
        // goes straight out and the rest follows one interval later.
        for (int epoch = 0; epoch < 5; epoch++)
        {
            _pacer.Enqueue(Sequence(355));
            Assert.That(_pacer.TryDequeue(out var c1, out _), Is.True);
            Assert.That(c1.Length, Is.EqualTo(256));
            _clock.AdvanceMs(RtcmPacer.IntervalMs);
            Assert.That(_pacer.TryDequeue(out var c2, out _), Is.True);
            Assert.That(c2.Length, Is.EqualTo(99));
            Assert.That(_pacer.Count, Is.Zero);
            _clock.AdvanceMs(1000 - RtcmPacer.IntervalMs);
        }
    }

    [Test]
    public void Backlog_OverLimit_IsDropped()
    {
        Assert.That(_pacer.Enqueue(Sequence(4096)), Is.Zero);
        Assert.That(_pacer.Enqueue(Sequence(4096)), Is.Zero);
        int dropped = _pacer.Enqueue(Sequence(4096)); // 12,288 > 10,000

        Assert.That(dropped, Is.EqualTo(3 * 4096));
        Assert.That(_pacer.Count, Is.Zero);
        Assert.That(_pacer.TryDequeue(out _, out _), Is.False);

        // Fresh corrections after the drop flow normally.
        _pacer.Enqueue(Sequence(100));
        Assert.That(_pacer.TryDequeue(out var chunk, out _), Is.True);
        Assert.That(chunk, Is.EqualTo(Sequence(100)));
    }

    [Test]
    public void Backlog_AtLimit_IsKept()
    {
        Assert.That(_pacer.Enqueue(Sequence(RtcmPacer.MaxBacklogBytes)), Is.Zero);
        Assert.That(_pacer.Count, Is.EqualTo(RtcmPacer.MaxBacklogBytes));
    }

    [Test]
    public void UnstartedRead_QueuedLongerThanMaxAge_IsDroppedAsStale()
    {
        _pacer.Enqueue(Sequence(600)); // old unstarted read
        _clock.AdvanceMs(RtcmPacer.MaxAgeMs + 1);
        byte[] fresh = Sequence(50, 200);
        _pacer.Enqueue(fresh);

        Assert.That(_pacer.TryDequeue(out var chunk, out int stale), Is.True);
        Assert.That(stale, Is.EqualTo(600), "unstarted stale read discarded");
        Assert.That(chunk, Is.EqualTo(fresh));
    }

    [Test]
    public void InProgressSegment_IsNotChoppedMidStream_EvenIfMaxAgeExceeded()
    {
        _pacer.Enqueue(Sequence(600));
        Assert.That(_pacer.TryDequeue(out var first, out _), Is.True);
        Assert.That(first.Length, Is.EqualTo(256));

        // Age exceeds MaxAgeMs, but segment is already partially sent
        _clock.AdvanceMs(RtcmPacer.MaxAgeMs + 1);
        Assert.That(_pacer.TryDequeue(out var second, out int stale), Is.True);
        Assert.That(stale, Is.Zero, "in-progress segment must not be chopped mid-stream");
        Assert.That(second.Length, Is.EqualTo(256));

        _clock.AdvanceMs(RtcmPacer.IntervalMs);
        Assert.That(_pacer.TryDequeue(out var third, out stale), Is.True);
        Assert.That(stale, Is.Zero);
        Assert.That(third.Length, Is.EqualTo(88));
        Assert.That(first.Concat(second).Concat(third).ToArray(), Is.EqualTo(Sequence(600)));
    }

    [Test]
    public void Clear_EmptiesTheQueue()
    {
        _pacer.Enqueue(Sequence(900));
        _pacer.Clear();
        Assert.That(_pacer.Count, Is.Zero);
        Assert.That(_pacer.TryDequeue(out _, out _), Is.False);
    }
}

/// <summary>Issue #169 diagnostics: each source silence is reported once, and its end once.</summary>
[TestFixture]
public class SourceSilenceMonitorTests
{
    [Test]
    public void Silence_IsReportedOnce_AndResumeCarriesTheGap()
    {
        var clock = new TestClock();
        var mon = new SourceSilenceMonitor(clock);

        Assert.That(mon.MarkSeen("GPS NMEA", "192.168.5.126"), Is.Null);
        clock.AdvanceMs(1500);
        Assert.That(mon.CheckSilences(), Is.Empty);

        clock.AdvanceMs(600); // 2.1 s
        var silent = mon.CheckSilences();
        Assert.That(silent, Has.Count.EqualTo(1));
        Assert.That(silent[0].Name, Is.EqualTo("GPS NMEA"));
        Assert.That(silent[0].From, Is.EqualTo("192.168.5.126"));

        clock.AdvanceMs(5000);
        Assert.That(mon.CheckSilences(), Is.Empty, "not repeated while still silent");

        clock.AdvanceMs(7300); // 14.4 s total
        Assert.That(mon.MarkSeen("GPS NMEA", "192.168.5.126"), Is.EqualTo(14400).Within(0.5));
        Assert.That(mon.MarkSeen("GPS NMEA", "192.168.5.126"), Is.Null, "resume reported once");
    }

    [Test]
    public void Sources_AreTrackedIndependently_AndUnheardSourcesAreNotReported()
    {
        var clock = new TestClock();
        var mon = new SourceSilenceMonitor(clock);
        mon.MarkSeen("GPS NMEA", "a");
        mon.MarkSeen("steer PGN 253", "b");

        for (int i = 0; i < 30; i++) // steer keeps talking at 10 Hz for 3 s
        {
            clock.AdvanceMs(100);
            mon.MarkSeen("steer PGN 253", "b");
        }

        var silent = mon.CheckSilences();
        Assert.That(silent.Select(s => s.Name), Is.EqualTo(new[] { "GPS NMEA" }));
    }

    [Test]
    public void ShortGaps_AreNotReported()
    {
        var clock = new TestClock();
        var mon = new SourceSilenceMonitor(clock);
        mon.MarkSeen("GPS NMEA", "a");
        clock.AdvanceMs(1900);
        Assert.That(mon.MarkSeen("GPS NMEA", "a"), Is.Null);
        Assert.That(mon.CheckSilences(), Is.Empty);
    }
}
