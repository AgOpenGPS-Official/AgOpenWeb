// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using AgOpenWeb.Models.Timing;

namespace AgOpenWeb.Services;

/// <summary>
/// The RTCM waiting to go to the GPS module, held as whole messages and released in small,
/// spaced UDP datagrams (AgIO's metering: 256 bytes per tick; issue #169, an unpaced backlog
/// stalled the AiO board). Replaces the byte queue of <c>RtcmPacer</c>
/// (Plans/RTCM_FORWARDING_PLAN.md, Phase 2).
///
/// Nothing is ever dropped except as a whole message, and a message that has started going
/// out always finishes. When the caster's stream arrives faster than it can be sent (after an
/// internet pause TCP hands over everything at once), the backlog is thinned by what the
/// receiver can still use:
/// <list type="bullet">
/// <item>Observations (legacy and MSM): a message replaces the unsent ones of its own type
/// from an earlier epoch. A stale epoch sent ahead of a fresh one only adds delay.</item>
/// <item>Station data (base position, antenna, receiver, GLONASS biases): a message replaces
/// the unsent one of its type. It is rare and the receiver needs the newest.</item>
/// <item>Ephemerides: a message replaces an unsent byte-for-byte copy of itself. Casters
/// repeat them every few seconds; two different ones for a satellite are both kept.</item>
/// <item>Everything else is kept in order.</item>
/// </list>
/// A hard cap on the queue is a memory guard only; it drops the oldest unsent messages.
///
/// Datagram boundaries do not follow message boundaries: the module writes the datagrams to
/// the receiver's serial port one after another. Pure logic, thread-safe; time comes from the
/// <see cref="IClock"/>, so tests drive it without sleeping.
/// </summary>
internal sealed class RtcmQueue
{
    /// <summary>Largest datagram sent (AgIO's default packetSizeNTRIP). The AiO firmwares
    /// cannot take a 2233 datagram over 512 bytes.</summary>
    public const int ChunkSize = 256;

    /// <summary>Nominal gap between datagrams. 256 B / 25 ms ≈ 10 KB/s: several times a
    /// heavy multi-constellation stream, so normal corrections never back up, and the first
    /// datagram of an epoch goes out with no wait.</summary>
    public const double IntervalMs = 25.0;

    /// <summary>Shortest gap, used only to catch up after the sender was woken late (a timer
    /// that fires at 40–50 ms instead of 25 would otherwise halve the rate). The v26 AiO keeps
    /// only the newest datagram between two polls of its socket, so datagrams never go back
    /// to back. 256 B / 10 ms is 25 KB/s, under the receiver's 46 KB/s serial link.</summary>
    public const double MinGapMs = 10.0;

    /// <summary>Most lateness that is made up: ten datagrams' worth. Bounds the fast run
    /// after a long stall of the sender.</summary>
    public const double MaxCreditMs = 250.0;

    /// <summary>Memory guard: past this the oldest unsent messages are dropped.</summary>
    public const int MaxQueuedBytes = 64 * 1024;

    /// <summary>Message type for bytes that are not framed RTCM 3 (a stream in another
    /// format): queued and sent in order, never thinned.</summary>
    public const int Opaque = -2;

    private enum Kind { Keep, Observation, Latest, Repeat }

    private sealed class Item
    {
        public required byte[] Data;
        public required Kind Kind;
        public long Key;
        public int Offset;
    }

    private readonly IClock? _clock;
    private readonly object _lock = new();
    private readonly LinkedList<Item> _items = new();
    private int _count;
    private long _lastSendTimestamp;
    private bool _hasSent;
    // Byte budget: the rate is IntervalMs per datagram on average. Time the sender spent
    // late (data was waiting and due, and it had not come for it) is credit, spent by
    // shortening the following gaps down to MinGapMs.
    private double _creditMs;
    private long _nonEmptySince;
    private long _datagrams, _catchUpDatagrams;
    private double _maxLateMs;

    /// <param name="clock">Time source; null follows <see cref="Clock.Current"/>.</param>
    public RtcmQueue(IClock? clock = null) => _clock = clock;

    private IClock Time => _clock ?? Clock.Current;

    /// <summary>Bytes waiting to be sent.</summary>
    public int Count { get { lock (_lock) return _count; } }

    /// <summary>Observation messages replaced by a newer epoch before they were sent.</summary>
    public long SupersededObservations { get; private set; }
    /// <summary>Station messages replaced by a newer one, and repeated ephemerides, before they were sent.</summary>
    public long SupersededOther { get; private set; }
    /// <summary>Messages dropped by the <see cref="MaxQueuedBytes"/> guard.</summary>
    public long MemoryGuardDrops { get; private set; }

    /// <summary>Datagrams sent, how many of them went at a catch-up gap, and the longest the
    /// sender came late for one, since the previous call. For the NTRIP health line: it shows
    /// whether this device's timers are late and whether the lateness is being made up.</summary>
    public (long Datagrams, long CatchUp, double MaxLateMs) TakePacingStats()
    {
        lock (_lock)
        {
            var stats = (_datagrams, _catchUpDatagrams, _maxLateMs);
            _datagrams = _catchUpDatagrams = 0;
            _maxLateMs = 0;
            return stats;
        }
    }

    /// <summary>Queue one whole message (or, with <see cref="Opaque"/>, one read of an
    /// unframed stream). The bytes are copied.</summary>
    public void Enqueue(int messageType, ReadOnlySpan<byte> message)
    {
        if (message.Length == 0) return;
        var (kind, key) = Classify(messageType, message);
        lock (_lock)
        {
            if (kind != Kind.Keep)
            {
                for (var node = _items.First; node != null;)
                {
                    var next = node.Next;
                    var it = node.Value;
                    bool superseded = it.Offset == 0 && it.Kind == kind && kind switch
                    {
                        Kind.Observation => (it.Key >> 32) == (key >> 32) && it.Key != key, // same type, another epoch
                        Kind.Latest => it.Key == key,                                      // same type
                        _ => it.Key == key && message.SequenceEqual(it.Data),              // the same bytes
                    };
                    if (superseded)
                    {
                        _items.Remove(node);
                        _count -= it.Data.Length;
                        if (kind == Kind.Observation) SupersededObservations++; else SupersededOther++;
                    }
                    node = next;
                }
            }

            if (_count == 0) _nonEmptySince = Time.GetTimestamp();
            _items.AddLast(new Item { Data = message.ToArray(), Kind = kind, Key = key });
            _count += message.Length;

            // Memory guard. The message in flight (Offset > 0) is never dropped.
            for (var node = _items.First; _count > MaxQueuedBytes && node != null && node != _items.Last;)
            {
                var next = node.Next;
                if (node.Value.Offset == 0)
                {
                    _count -= node.Value.Data.Length;
                    _items.Remove(node);
                    MemoryGuardDrops++;
                }
                node = next;
            }
        }
    }

    /// <summary>Take the next datagram if one is due: the queue holds data and the gap since
    /// the last one has passed (<see cref="IntervalMs"/>, or less while catching up).</summary>
    public bool TryDequeue(out byte[] chunk)
    {
        chunk = Array.Empty<byte>();
        lock (_lock)
        {
            long now = Time.GetTimestamp();
            if (_count == 0 || !IsDue(now)) return false;

            if (_hasSent)
            {
                // How long this datagram sat ready: from the end of its gap, or from when
                // it arrived if the queue was empty then. An idle queue earns no credit.
                double gap = Gap;
                double sinceLast = Time.ElapsedMs(_lastSendTimestamp, now);
                double late = Math.Max(0, Math.Min(sinceLast - gap, Time.ElapsedMs(_nonEmptySince, now)));
                _creditMs = Math.Clamp(_creditMs - (IntervalMs - gap) + late, 0, MaxCreditMs);
                if (gap < IntervalMs - 0.5) _catchUpDatagrams++;
                if (late > _maxLateMs) _maxLateMs = late;
            }
            _datagrams++;

            int size = Math.Min(_count, ChunkSize);
            chunk = new byte[size];
            int filled = 0;
            while (filled < size)
            {
                var it = _items.First!.Value;
                int take = Math.Min(size - filled, it.Data.Length - it.Offset);
                Buffer.BlockCopy(it.Data, it.Offset, chunk, filled, take);
                it.Offset += take;
                filled += take;
                if (it.Offset == it.Data.Length) _items.RemoveFirst();
            }
            _count -= size;
            _lastSendTimestamp = now;
            _hasSent = true;
            return true;
        }
    }

    /// <summary>Milliseconds until the next datagram may go (0 when one is due now).</summary>
    public double MsUntilDue()
    {
        lock (_lock)
        {
            if (!_hasSent) return 0;
            double wait = Gap - Time.ElapsedMs(_lastSendTimestamp, Time.GetTimestamp());
            return wait > 0 ? wait : 0;
        }
    }

    /// <summary>Drop everything queued (session teardown). The counters are kept.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _items.Clear();
            _count = 0;
            _creditMs = 0;
        }
    }

    // The gap the next datagram must keep: shorter while there is lateness to make up.
    private double Gap => Math.Max(MinGapMs, IntervalMs - _creditMs);

    private bool IsDue(long now) =>
        !_hasSent || Time.ElapsedMs(_lastSendTimestamp, now) >= Gap;

    // What a message may replace. Observations: (type, epoch time). Station data: its type.
    // Ephemerides: an identical message (the key only narrows the comparison).
    private static (Kind, long) Classify(int type, ReadOnlySpan<byte> message)
    {
        if (type < 0 || message.Length < RtcmFramer.Overhead) return (Kind.Keep, 0);
        var payload = message[3..^3];

        if (RtcmMessages.IsObservation(type))
        {
            // After the 12-bit message number and 12-bit station id comes the epoch time:
            // 27 bits in GLONASS legacy messages, 30 bits in all the others.
            int bits = type is >= 1009 and <= 1012 ? 27 : 30;
            if (payload.Length * 8 < 24 + bits) return (Kind.Keep, 0);
            return (Kind.Observation, ((long)type << 32) | Bits(payload, 24, bits));
        }

        switch (type)
        {
            case 1005: case 1006: case 1007: case 1008: case 1013: case 1033: case 1230:
                return (Kind.Latest, (long)type << 32);
            case 1019: case 1020: case 1041: case 1042: case 1044: case 1045: case 1046:
                return (Kind.Repeat, ((long)type << 32) | (uint)message.Length);
            default:
                return (Kind.Keep, 0);
        }
    }

    private static uint Bits(ReadOnlySpan<byte> data, int start, int count)
    {
        uint value = 0;
        for (int i = start; i < start + count; i++)
            value = (value << 1) | (uint)((data[i >> 3] >> (7 - (i & 7))) & 1);
        return value;
    }
}
