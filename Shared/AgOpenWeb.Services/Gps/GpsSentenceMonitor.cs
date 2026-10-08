// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace AgOpenWeb.Services.Gps;

/// <summary>
/// Keeps what the GPS module last sent, for the System Data card: the latest text of each
/// position sentence, the last datagram that was not accepted, the arrival rate and how
/// many sentences were missed. Fed from the UDP receive thread at the GPS rate, read at
/// the status rate; nothing is allocated per sentence.
/// </summary>
public sealed class GpsSentenceMonitor
{
    public sealed record Sentence(string Type, string Text, double AgeSeconds);

    public sealed record Snapshot(double RateHz, long Missed, long Rejected, IReadOnlyList<Sentence> Sentences);

    /// <summary>The <see cref="Sentence.Type"/> of the last datagram the parser refused.</summary>
    public const string RejectedType = "REJECTED";

    private const int MaxLength = 200;
    private const int RateWindow = 16;
    // A gap this long is a new session (module unplugged, simulator in between), not
    // missed sentences.
    private const double SessionGapSeconds = 5.0;
    // No sentence for this long: the rate reads zero.
    private const double SilentSeconds = 2.0;

    private sealed class Slot
    {
        public readonly string Type;
        public readonly byte[] Bytes = new byte[MaxLength];
        public int Length;
        public long Stamp;
        public bool Seen;
        public Slot(string type) => Type = type;
    }

    private readonly object _lock = new();
    private readonly Slot _panda = new("PANDA");
    private readonly Slot _paogi = new("PAOGI");
    private readonly Slot _ksxt = new("KSXT");
    private readonly Slot _rejected = new(RejectedType);
    private readonly long[] _arrivals = new long[RateWindow];
    private int _arrivalCount;
    private int _arrivalNext;
    private long _lastArrival;
    private double _meanInterval; // seconds, of sentences that came on time
    private long _missed;
    private long _rejectedCount;

    /// <summary>Record one datagram from the GPS module and whether the parser took it.</summary>
    public void Record(ReadOnlySpan<byte> data, bool accepted) => Record(data, accepted, Stopwatch.GetTimestamp());

    /// <summary>As <see cref="Record(ReadOnlySpan{byte}, bool)"/>, with the arrival time given (tests).</summary>
    public void Record(ReadOnlySpan<byte> data, bool accepted, long timestamp)
    {
        lock (_lock)
        {
            Slot slot = !accepted ? _rejected
                : data.Length > 5 && data.Slice(1, 5).SequenceEqual("PAOGI"u8) ? _paogi
                : data.Length > 5 && data.Slice(1, 5).SequenceEqual("KSXT,"u8) ? _ksxt
                : _panda;
            int n = Math.Min(data.Length, MaxLength);
            data.Slice(0, n).CopyTo(slot.Bytes);
            slot.Length = n;
            slot.Stamp = timestamp;
            slot.Seen = true;

            if (!accepted)
            {
                _rejectedCount++;
                return;
            }

            if (_arrivalCount > 0)
            {
                double dt = Seconds(timestamp - _lastArrival);
                if (dt >= SessionGapSeconds)
                {
                    _arrivalCount = 0;
                    _arrivalNext = 0;
                    _meanInterval = 0;
                }
                else if (_meanInterval > 0 && dt > 1.5 * _meanInterval)
                {
                    _missed += (long)Math.Round(dt / _meanInterval) - 1;
                }
                else
                {
                    _meanInterval = _meanInterval > 0 ? 0.9 * _meanInterval + 0.1 * dt : dt;
                }
            }
            _lastArrival = timestamp;
            _arrivals[_arrivalNext] = timestamp;
            _arrivalNext = (_arrivalNext + 1) % RateWindow;
            if (_arrivalCount < RateWindow) _arrivalCount++;
        }
    }

    public Snapshot GetSnapshot() => GetSnapshot(Stopwatch.GetTimestamp());

    /// <summary>As <see cref="GetSnapshot()"/>, with the current time given (tests).</summary>
    public Snapshot GetSnapshot(long now)
    {
        lock (_lock)
        {
            double rate = 0;
            if (_arrivalCount >= 2 && Seconds(now - _lastArrival) < SilentSeconds)
            {
                long oldest = _arrivals[_arrivalCount < RateWindow ? 0 : _arrivalNext];
                double span = Seconds(_lastArrival - oldest);
                if (span > 0) rate = (_arrivalCount - 1) / span;
            }

            var sentences = new List<Sentence>(4);
            foreach (var slot in new[] { _panda, _paogi, _ksxt, _rejected })
                if (slot.Seen)
                    sentences.Add(new Sentence(slot.Type, Text(slot), Math.Max(0, Seconds(now - slot.Stamp))));
            return new Snapshot(rate, _missed, _rejectedCount, sentences);
        }
    }

    private static double Seconds(long ticks) => ticks / (double)Stopwatch.Frequency;

    // Printable ASCII only: a refused datagram can hold anything, and this text goes to a
    // browser. Line ends are dropped.
    private static string Text(Slot slot)
    {
        Span<char> chars = stackalloc char[slot.Length];
        int n = 0;
        for (int i = 0; i < slot.Length; i++)
        {
            byte b = slot.Bytes[i];
            if (b == '\r' || b == '\n') continue;
            chars[n++] = b is >= 0x20 and < 0x7F ? (char)b : '?';
        }
        return new string(chars.Slice(0, n));
    }
}
