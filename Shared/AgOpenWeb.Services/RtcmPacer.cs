// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using AgOpenWeb.Models.Timing;

namespace AgOpenWeb.Services;

/// <summary>
/// Queue that meters NTRIP RTCM out to the GPS module in small, spaced UDP datagrams
/// (AgIO's ntripMeterTimer: at most packetSizeNTRIP = 256 bytes per 50 ms tick, queue cleared
/// past 10,000 bytes). Issue #169: when the caster resumes after an internet pause, TCP hands
/// over the whole backlog in one read; firing that at subnet.255:2233 back to back stalled the
/// AiO board and its NMEA output for ~14 s.
///
/// Pure logic: the caller enqueues each TCP read and polls <see cref="TryDequeue"/>; time comes
/// from the <see cref="IClock"/>, so tests drive it without sleeping. Thread-safe.
///
/// RTCM frame boundaries are not kept (AgIO doesn't either): the receiver reassembles the
/// byte stream on the 0xD3 preamble + CRC, so a frame split across datagrams is fine.
/// </summary>
internal sealed class RtcmPacer
{
    /// <summary>Largest datagram sent (AgIO's default packetSizeNTRIP). The AiO firmware
    /// drops any 2233 datagram over its 512-byte receive buffer outright.</summary>
    public const int ChunkSize = 256;

    /// <summary>Gap between datagrams. 256 B / 25 ms ≈ 10 KB/s: twice AgIO's 5 KB/s, about
    /// 3× a heavy MSM7 multi-constellation stream (2–4 KB per 1 Hz epoch) and ~30× a basic
    /// one (~355 B/epoch), so normal corrections never back up — a typical epoch goes out as
    /// one or two datagrams, the first with no wait. It also stays under a 115200-baud GPS
    /// serial link (~11.5 KB/s), so the module never has to block on its serial writes.</summary>
    public const double IntervalMs = 25.0;

    /// <summary>Backlog past which the whole queue is dropped (AgIO parity). At the paced
    /// rate it would take ~1 s to drain: corrections that old only delay the fresh ones that
    /// re-establish RTK fix (#334).</summary>
    public const int MaxBacklogBytes = 10_000;

    /// <summary>Bytes that have waited longer than this are dropped instead of sent (#334:
    /// never deliver stale corrections).</summary>
    public const double MaxAgeMs = 1000.0;

    private readonly IClock? _clock;
    private readonly object _lock = new();
    private readonly Queue<Segment> _queue = new();
    private int _count;
    private long _lastSendTimestamp;
    private bool _hasSent;

    private sealed class Segment
    {
        public required byte[] Data;
        public int Offset;
        public long Timestamp;
    }

    /// <param name="clock">Time source; null follows <see cref="Clock.Current"/>.</param>
    public RtcmPacer(IClock? clock = null) => _clock = clock;

    private IClock Time => _clock ?? Clock.Current;

    /// <summary>Bytes waiting to be sent.</summary>
    public int Count { get { lock (_lock) return _count; } }

    /// <summary>Queue one read. Returns the number of bytes dropped because the backlog
    /// passed <see cref="MaxBacklogBytes"/> (the queue plus this read), else 0.</summary>
    public int Enqueue(byte[] data)
    {
        if (data.Length == 0) return 0;
        lock (_lock)
        {
            _queue.Enqueue(new Segment { Data = data, Timestamp = Time.GetTimestamp() });
            _count += data.Length;
            if (_count <= MaxBacklogBytes) return 0;

            // Can't keep up — the internet dumped a backlog. Clear it, as AgIO does.
            int dropped = _count;
            _queue.Clear();
            _count = 0;
            return dropped;
        }
    }

    /// <summary>Take the next datagram if one is due: the queue holds data and
    /// <see cref="IntervalMs"/> has passed since the last one. Bytes older than
    /// <see cref="MaxAgeMs"/> are discarded first and counted in <paramref name="staleDropped"/>.</summary>
    public bool TryDequeue(out byte[] chunk, out int staleDropped)
    {
        chunk = Array.Empty<byte>();
        staleDropped = 0;
        lock (_lock)
        {
            long now = Time.GetTimestamp();
            while (_queue.Count > 0 && Time.ElapsedMs(_queue.Peek().Timestamp, now) > MaxAgeMs)
            {
                var old = _queue.Dequeue();
                int left = old.Data.Length - old.Offset;
                staleDropped += left;
                _count -= left;
            }

            if (_count == 0 || !IsDue(now)) return false;

            int size = Math.Min(_count, ChunkSize);
            chunk = new byte[size];
            int filled = 0;
            while (filled < size)
            {
                var seg = _queue.Peek();
                int take = Math.Min(size - filled, seg.Data.Length - seg.Offset);
                Buffer.BlockCopy(seg.Data, seg.Offset, chunk, filled, take);
                seg.Offset += take;
                filled += take;
                if (seg.Offset == seg.Data.Length) _queue.Dequeue();
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
            double wait = IntervalMs - Time.ElapsedMs(_lastSendTimestamp, Time.GetTimestamp());
            return wait > 0 ? wait : 0;
        }
    }

    /// <summary>Drop everything queued (session teardown).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _queue.Clear();
            _count = 0;
        }
    }

    private bool IsDue(long now) =>
        !_hasSent || Time.ElapsedMs(_lastSendTimestamp, now) >= IntervalMs;
}
