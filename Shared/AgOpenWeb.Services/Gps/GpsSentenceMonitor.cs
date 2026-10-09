// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors

using System;
using System.Collections.Generic;
using System.Diagnostics;
using AgOpenWeb.Models.GPS;

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

    /// <param name="Rejected">Lines the parser refused, of which <paramref name="BadChecksum"/> failed
    /// the checksum and <paramref name="UnknownSentence"/> were well-formed sentences this build does not decode.</param>
    public sealed record Snapshot(double RateHz, long Missed, long Rejected, long BadChecksum, long UnknownSentence,
        IReadOnlyList<Sentence> Sentences);

    /// <summary>The <see cref="Sentence.Type"/> of the last datagram the parser refused.</summary>
    public const string RejectedType = "REJECTED";

    private const int MaxLength = 200;
    private const int RateWindow = 16;
    // A gap this long is a new session (module unplugged, simulator in between), not
    // missed sentences.
    private const double SessionGapSeconds = 5.0;
    // No sentence for this long: the rate reads zero.
    private const double SilentSeconds = 2.0;
    // This many consecutive, alike, over-long gaps mean the rate changed: relearn it.
    private const int RateRelearnRun = 20;

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
    private readonly Slot _gga = new("GGA");
    private readonly Slot _gns = new("GNS");
    private readonly Slot _vtg = new("VTG");
    private readonly Slot _hpr = new("HPR");
    private readonly Slot _hdt = new("HDT");
    private readonly Slot _ths = new("THS");
    private readonly Slot _inspvax = new("INSPVAX");
    private readonly Slot _rejected = new(RejectedType);
    private readonly Slot[] _slots;
    private readonly long[] _arrivals = new long[RateWindow];
    private int _arrivalCount;
    private int _arrivalNext;
    private long _lastArrival;
    private double _meanInterval; // seconds, of sentences that came on time
    private int _slowInARow;      // consecutive gaps judged "missed" that all look alike
    private double _slowInterval; // the first of those gaps
    private long _missed;

    public GpsSentenceMonitor()
    {
        _slots = new[] { _panda, _paogi, _ksxt, _gga, _gns, _vtg, _hpr, _hdt, _ths, _inspvax, _rejected };
    }
    private long _rejectedCount;
    private long _badChecksum;
    private long _unknownSentence;

    /// <summary>Record one line from the GPS module and what the parser made of it.</summary>
    public void Record(ReadOnlySpan<byte> data, NmeaParseResult result) => Record(data, result, Stopwatch.GetTimestamp());

    /// <summary>As <see cref="Record(ReadOnlySpan{byte}, NmeaParseResult)"/>, with the arrival time given (tests).</summary>
    public void Record(ReadOnlySpan<byte> data, NmeaParseResult result, long timestamp)
    {
        bool accepted = result == NmeaParseResult.Accepted;
        bool member = result == NmeaParseResult.EpochMember;
        lock (_lock)
        {
            Slot slot = !accepted && !member ? _rejected : SlotFor(data);
            int n = Math.Min(data.Length, MaxLength);
            data.Slice(0, n).CopyTo(slot.Bytes);
            slot.Length = n;
            slot.Stamp = timestamp;
            slot.Seen = true;

            // A member of an open epoch is kept for the card but is not a fix: the rate and
            // the missed count follow emitted fixes.
            if (member) return;
            if (!accepted)
            {
                _rejectedCount++;
                if (result == NmeaParseResult.BadChecksum) _badChecksum++;
                else if (result == NmeaParseResult.UnknownSentence) _unknownSentence++;
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
                    _slowInARow = 0;
                }
                else if (_meanInterval > 0 && dt > 1.5 * _meanInterval)
                {
                    _missed += (long)Math.Round(dt / _meanInterval) - 1;

                    // A run of equal "long" gaps is a new rate, not missed sentences: the
                    // mean was learned from a burst (a module flushing a buffer) or the
                    // receiver's rate was lowered. Real losses come in uneven gaps.
                    if (_slowInARow > 0 && Math.Abs(dt - _slowInterval) <= 0.2 * _slowInterval)
                    {
                        if (++_slowInARow >= RateRelearnRun)
                        {
                            _meanInterval = dt;
                            _slowInARow = 0;
                        }
                    }
                    else
                    {
                        _slowInARow = 1;
                        _slowInterval = dt;
                    }
                }
                else
                {
                    _meanInterval = _meanInterval > 0 ? 0.9 * _meanInterval + 0.1 * dt : dt;
                    _slowInARow = 0;
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
            foreach (var slot in _slots)
                if (slot.Seen)
                    sentences.Add(new Sentence(slot.Type, Text(slot), Math.Max(0, Seconds(now - slot.Stamp))));
            return new Snapshot(rate, _missed, _rejectedCount, _badChecksum, _unknownSentence, sentences);
        }
    }

    private static double Seconds(long ticks) => ticks / (double)Stopwatch.Frequency;

    /// <summary>The slot for an accepted line by its sentence id.</summary>
    private Slot SlotFor(ReadOnlySpan<byte> data)
    {
        if (data.Length > 6 && (data[0] == '#' || data[0] == '%')) return _inspvax;
        if (data.Length > 6 && data[0] == '$')
        {
            var id5 = data.Slice(1, 5);
            if (id5.SequenceEqual("PAOGI"u8)) return _paogi;
            if (id5.SequenceEqual("PANDA"u8)) return _panda;
            if (id5.SequenceEqual("KSXT,"u8)) return _ksxt;
            var type = data.Slice(3, 3);
            if (type.SequenceEqual("GGA"u8)) return _gga;
            if (type.SequenceEqual("GNS"u8)) return _gns;
            if (type.SequenceEqual("VTG"u8)) return _vtg;
            if (type.SequenceEqual("HPR"u8)) return _hpr;
            if (type.SequenceEqual("HDT"u8)) return _hdt;
            if (type.SequenceEqual("THS"u8)) return _ths;
        }
        return _panda;
    }

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
