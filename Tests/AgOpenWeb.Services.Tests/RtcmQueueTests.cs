using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models.Timing;
using AgOpenWeb.Services;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// RTCM forwarding plan, Phase 2: RTCM goes to the module as whole messages in paced
/// datagrams (#169), and a backlog is thinned to what the receiver can still use: the newest
/// observation epoch and the newest station data. Driven by a TestClock.
/// </summary>
[TestFixture]
public class RtcmQueueTests
{
    private TestClock _clock = null!;
    private RtcmQueue _queue = null!;

    [SetUp]
    public void SetUp()
    {
        _clock = new TestClock();
        _queue = new RtcmQueue(_clock);
    }

    /// <summary>An observation message of the given type and epoch (the 30 bits, or 27 for
    /// GLONASS legacy, after the message number and station id).</summary>
    private static byte[] Obs(int type, uint epoch, int payload = 60)
    {
        byte[] m = RtcmFramerTests.Message(type, payload, (int)epoch);
        int bits = type is >= 1009 and <= 1012 ? 27 : 30;
        for (int i = 0; i < bits; i++)
        {
            int bit = 24 + i, at = 3 + (bit >> 3), mask = 0x80 >> (bit & 7);
            if (((epoch >> (bits - 1 - i)) & 1) != 0) m[at] |= (byte)mask; else m[at] &= (byte)~mask;
        }
        return Resealed(m);
    }

    /// <summary>An ephemeris message for one satellite (the 6 bits after the message number).</summary>
    private static byte[] Eph(int type, int satellite, int seed = 0)
    {
        byte[] m = RtcmFramerTests.Message(type, 61, seed);
        m[4] = (byte)((m[4] & 0xF0) | (satellite >> 2));
        m[5] = (byte)((m[5] & 0x3F) | ((satellite & 3) << 6));
        return Resealed(m);
    }

    private static byte[] Resealed(byte[] m)
    {
        uint crc = RtcmFramer.Crc24Q(m.AsSpan(0, m.Length - 3));
        m[^3] = (byte)(crc >> 16); m[^2] = (byte)(crc >> 8); m[^1] = (byte)crc;
        return m;
    }

    private static byte[] Station(int type, int seed = 0) => RtcmFramerTests.Message(type, 19, seed);

    private void Enqueue(params byte[][] messages)
    {
        var framer = new RtcmFramer();
        foreach (var m in messages) framer.Feed(m, _queue.Enqueue);
    }

    /// <summary>Drain the queue as the send loop would, stepping the clock by the wait.</summary>
    private List<(double AtMs, byte[] Chunk)> Drain()
    {
        var sent = new List<(double, byte[])>();
        double at = 0;
        while (_queue.Count > 0)
        {
            double wait = _queue.MsUntilDue();
            _clock.AdvanceMs(wait);
            at += wait;
            Assert.That(_queue.TryDequeue(out var chunk), Is.True);
            sent.Add((at, chunk));
        }
        return sent;
    }

    /// <summary>What reaches the receiver: the datagrams joined and framed again.</summary>
    private List<byte[]> DrainMessages()
    {
        byte[] stream = Drain().SelectMany(s => s.Chunk).ToArray();
        var framer = new RtcmFramer();
        var found = new List<byte[]>();
        framer.Feed(stream, (_, message) => found.Add(message.ToArray()));
        Assert.That(framer.BytesSkipped, Is.Zero, "only whole messages are ever sent");
        return found;
    }

    private static int TypeOf(byte[] m) => (m[3] << 4) | (m[4] >> 4);

    // ── Pacing (as RtcmPacer did) ────────────────────────────────────────────

    [Test]
    public void TheFirstDatagram_GoesAtOnce_AndTheRestAreSpaced()
    {
        Enqueue(Obs(1077, 1, 700));

        var sent = Drain();

        Assert.That(sent.Select(s => s.Chunk.Length), Is.EqualTo(new[] { 256, 256, 194 }));
        Assert.That(sent.Select(s => s.AtMs), Is.EqualTo(new[] { 0, RtcmQueue.IntervalMs, 2 * RtcmQueue.IntervalMs }));
    }

    [Test]
    public void NothingIsDue_UntilTheIntervalHasPassed()
    {
        Enqueue(Obs(1077, 1, 400));
        Assert.That(_queue.TryDequeue(out _), Is.True);

        Assert.That(_queue.TryDequeue(out _), Is.False);
        Assert.That(_queue.MsUntilDue(), Is.EqualTo(RtcmQueue.IntervalMs));
        _clock.AdvanceMs(RtcmQueue.IntervalMs);
        Assert.That(_queue.TryDequeue(out _), Is.True);
    }

    [Test]
    public void ADatagram_IsFilledAcrossMessages_AndTheBytesArriveInOrder()
    {
        byte[][] epoch = { Obs(1077, 5, 100), Obs(1087, 5, 80), Obs(1097, 5, 120), Station(1005) };
        Enqueue(epoch);

        var sent = Drain();

        Assert.That(sent[0].Chunk.Length, Is.EqualTo(256), "small messages share a datagram");
        Assert.That(sent.SelectMany(s => s.Chunk).ToArray(), Is.EqualTo(epoch.SelectMany(m => m).ToArray()));
    }

    [Test]
    public void ASteadyStream_IsSentWhole_WithNothingReplaced()
    {
        for (uint epoch = 1; epoch <= 5; epoch++)
        {
            byte[][] messages = { Obs(1077, epoch * 1000, 300), Obs(1087, epoch * 1000, 250), Obs(1127, epoch * 1000, 200) };
            Enqueue(messages);
            Assert.That(DrainMessages(), Is.EqualTo(messages), "each epoch is sent before the next arrives");
            _clock.AdvanceMs(1000);
        }
        Assert.That(_queue.SupersededObservations + _queue.SupersededOther + _queue.MemoryGuardDrops, Is.Zero);
    }

    // ── Backlog ──────────────────────────────────────────────────────────────

    [Test]
    public void ABacklogOfEpochs_SendsOnlyTheNewestOne()
    {
        for (uint epoch = 1; epoch <= 10; epoch++)
            Enqueue(Obs(1077, epoch * 1000, 300), Obs(1087, epoch * 1000, 250), Obs(1097, epoch * 1000, 280), Obs(1127, epoch * 1000, 200));

        var sent = DrainMessages();

        Assert.That(sent.Select(TypeOf), Is.EqualTo(new[] { 1077, 1087, 1097, 1127 }));
        Assert.That(sent[0], Is.EqualTo(Obs(1077, 10_000, 300)), "the newest epoch");
        Assert.That(_queue.SupersededObservations, Is.EqualTo(36));
    }

    [Test]
    public void StationData_SurvivesABacklog_AsItsNewestCopy()
    {
        for (uint epoch = 1; epoch <= 30; epoch++)
        {
            Enqueue(Obs(1077, epoch * 1000, 300), Obs(1087, epoch * 1000, 250));
            if (epoch % 10 == 0) Enqueue(Station(1005, (int)epoch), Station(1008, (int)epoch), Station(1033, (int)epoch));
            if (epoch == 30) Enqueue(Station(1006), Station(1230));
        }

        var sent = DrainMessages();

        Assert.That(sent.Select(TypeOf).OrderBy(t => t), Is.EqualTo(new[] { 1005, 1006, 1008, 1033, 1077, 1087, 1230 }));
        Assert.That(sent.Single(m => TypeOf(m) == 1005), Is.EqualTo(Station(1005, 30)), "the newest base position");
        Assert.That(_queue.SupersededOther, Is.EqualTo(6));
    }

    [Test]
    public void RepeatedEphemerides_AreSentOnce_AndDifferentOnesAreAllKept()
    {
        // A backlog holds the same ephemeris several times over; two different ones for a
        // satellite (an update, or another data source) are both wanted.
        Enqueue(Eph(1019, 3), Eph(1019, 7), Eph(1020, 7), Eph(1019, 7, seed: 9),
                Eph(1019, 3), Eph(1019, 7), Eph(1020, 7));

        var sent = DrainMessages();

        Assert.That(sent, Is.EqualTo(new[] { Eph(1019, 7, seed: 9), Eph(1019, 3), Eph(1019, 7), Eph(1020, 7) }));
        Assert.That(_queue.SupersededOther, Is.EqualTo(3));
    }

    [Test]
    public void AMessageInFlight_IsFinished_BeforeTheNewerEpochGoes()
    {
        byte[] old = Obs(1077, 1000, 700);
        Enqueue(old, Obs(1087, 1000, 300));
        Assert.That(_queue.TryDequeue(out var first), Is.True);   // 256 bytes of the old 1077 are out

        Enqueue(Obs(1077, 2000, 700), Obs(1087, 2000, 300));
        byte[] rest = Drain().SelectMany(s => s.Chunk).ToArray();

        var framer = new RtcmFramer();
        var sent = new List<byte[]>();
        framer.Feed(first.Concat(rest).ToArray(), (_, m) => sent.Add(m.ToArray()));
        Assert.That(framer.BytesSkipped, Is.Zero, "the message that had started is completed, not cut");
        Assert.That(sent, Is.EqualTo(new[] { old, Obs(1077, 2000, 700), Obs(1087, 2000, 300) }));
    }

    [Test]
    public void AnMsmSplitInTwo_KeepsBothHalvesOfItsEpoch()
    {
        byte[] a = Obs(1077, 5000, 900), b = Obs(1077, 5000, 400);
        b[40] ^= 0x10; b = Resealed(b);   // same type and epoch, different content
        Enqueue(Obs(1077, 4000, 500), a, b);

        Assert.That(DrainMessages(), Is.EqualTo(new[] { a, b }));
    }

    [Test]
    public void GlonassLegacyMessages_AreMatchedOnTheir27BitEpoch()
    {
        Enqueue(Obs(1012, 100), Obs(1004, 100), Obs(1012, 200), Obs(1004, 200));

        Assert.That(DrainMessages(), Is.EqualTo(new[] { Obs(1012, 200), Obs(1004, 200) }));
    }

    [Test]
    public void UnknownMessages_AreKeptInOrder()
    {
        byte[][] messages = { RtcmFramerTests.Message(4072, 50, 1), RtcmFramerTests.Message(4072, 50, 2), RtcmFramerTests.Message(1029, 30, 3) };
        Enqueue(messages);

        Assert.That(DrainMessages(), Is.EqualTo(messages));
    }

    [Test]
    public void TheMemoryGuard_DropsTheOldestWholeMessages()
    {
        // Messages that nothing replaces, far past the cap.
        for (int i = 0; i < 200; i++)
            Enqueue(RtcmFramerTests.Message(4072, 1000, i));

        Assert.That(_queue.Count, Is.LessThanOrEqualTo(RtcmQueue.MaxQueuedBytes));
        Assert.That(_queue.MemoryGuardDrops, Is.GreaterThan(0));
        var sent = DrainMessages();
        Assert.That(sent[^1], Is.EqualTo(RtcmFramerTests.Message(4072, 1000, 199)), "the newest is kept");
    }

    [Test]
    public void AnUnframedStream_IsSentByteForByte()
    {
        byte[] a = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
        byte[] b = Enumerable.Range(0, 100).Select(i => (byte)(i * 3)).ToArray();
        _queue.Enqueue(RtcmQueue.Opaque, a);
        _queue.Enqueue(RtcmQueue.Opaque, b);

        Assert.That(Drain().SelectMany(s => s.Chunk).ToArray(), Is.EqualTo(a.Concat(b).ToArray()));
    }

    [Test]
    public void Clear_EmptiesTheQueue()
    {
        Enqueue(Obs(1077, 1, 500));
        _queue.Clear();
        Assert.That(_queue.Count, Is.Zero);
        Assert.That(_queue.TryDequeue(out _), Is.False);
    }
}
