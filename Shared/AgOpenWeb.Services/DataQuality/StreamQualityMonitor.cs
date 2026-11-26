// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Diagnostics;
using System.Globalization;

namespace AgOpenWeb.Services.DataQuality;

/// <summary>What a stream's arrivals looked like over the last window. The first four of
/// <see cref="DataFinding"/>, with the same numbers.</summary>
public enum StreamVerdict : byte
{
    /// <summary>Silent, or too few arrivals to judge.</summary>
    NoData = 0,
    /// <summary>Arriving at its own rate.</summary>
    Even = 1,
    /// <summary>Some never arrived: the gaps are whole intervals long.</summary>
    Gaps = 2,
    /// <summary>Held back and delivered several at a time (a Wi-Fi link's beacons).</summary>
    Bursts = 3,
}

/// <param name="Name">The stream, as the operator is told of it.</param>
/// <param name="Faulty">Out of bounds for <see cref="StreamQualityMonitor.HoldSeconds"/>; stays until it has been good that long.</param>
/// <param name="RateHz">Arrivals a second over the window.</param>
/// <param name="NominalHz">The rate the sender sends at (from the usual interval); 0 when not known (bursts, no data).</param>
/// <param name="MissedPercent">The share that did not arrive when it should have, over the window.</param>
/// <param name="LongestGapMs">The longest wait between two arrivals in the window.</param>
/// <param name="WorstMissedPercent">The highest <paramref name="MissedPercent"/> of any judged window since the stream started.</param>
/// <param name="Faults">How often the stream went faulty since the app started.</param>
public readonly record struct StreamQuality(
    string Name, StreamVerdict Verdict, bool Faulty, double RateHz, double NominalHz, double MissedPercent,
    double LongestGapMs, double WorstMissedPercent, int Faults);

/// <summary>
/// Judges whether a periodic stream from a module (position fixes, PGN 253, ...) arrives as it
/// was sent. A sender on a timer gives arrivals one interval apart; a link that loses packets
/// gives gaps of two or three intervals, and a link that holds them back (Wi-Fi delivers
/// broadcasts at its beacons) gives several at once and then nothing.
/// <para>
/// Over the last <see cref="WindowSeconds"/> the usual interval is the median wait between
/// arrivals and the mean wait is the window divided by what came. Where nothing is lost they
/// are the same. With losses the median stays at the sender's interval while the mean grows,
/// so <c>1 - median / mean</c> is the share that is missing; with bursts the median falls to
/// almost nothing. Both are ratios, so the sender's rate need not be known, and a pause of
/// this process (packets read late, together) moves neither.
/// </para>
/// <para>
/// The app only reports this (the operator is told, the log and the bug report say so). It
/// does not repair the stream: a stream that arrives like this is fixed at the link.
/// </para>
/// <see cref="Record()"/> is called from the receive thread and allocates nothing;
/// <see cref="Evaluate()"/> about once a second from the host loop.
/// </summary>
public sealed class StreamQualityMonitor : IDataSensor
{
    /// <summary>The stretch that is judged.</summary>
    public const double WindowSeconds = 10.0;
    /// <summary>Fewer arrivals than this in the window are not judged.</summary>
    public const int MinArrivals = 20;
    /// <summary>A window with this share missing (or more) is bad.</summary>
    public const double FaultShare = 0.10;
    /// <summary>A window with less than this share missing is good; in between, the state holds.</summary>
    public const double ClearShare = 0.05;
    /// <summary>Bad for this long is a fault; good for this long ends it. Longer than the window, so
    /// one event (a dropped packet, a second without the link) is never a fault while it passes through it.</summary>
    public const double HoldSeconds = 15.0;
    /// <summary>The usual interval under this part of the mean one: the stream comes in bursts.</summary>
    public const double BurstRatio = 0.25;

    // No arrival for this long: nothing to judge (GPS lost and the module dots report silence).
    private const double SilentSeconds = 2.0;
    // A wait this long starts the stream anew (module unplugged, simulator in between).
    private const double SessionGapSeconds = 5.0;
    // 10 s at 200 a second; a faster stream is judged over its last 2048 arrivals.
    private const int Capacity = 2048;

    private readonly object _lock = new();
    private readonly long[] _stamps = new long[Capacity];
    private readonly double[] _gaps = new double[Capacity];
    private int _count;
    private int _next;
    private long _last;
    private long? _badSince;
    private long? _goodSince;
    private bool _faulty;
    private int _faults;
    private double _worst;

    /// <param name="name">The stream, as the operator is told of it ("GPS positions").</param>
    /// <param name="warns">False for a stream that is measured and reported but never called
    /// faulty: one whose sender is not known to send on a timer.</param>
    public StreamQualityMonitor(string name, bool warns = true)
    {
        Name = name;
        Warns = warns;
    }

    public string Name { get; }
    public bool Warns { get; }

    public string Advice => "Check the network cable to the modules (Wi-Fi is not a link for module data)";

    public string Recovered => "arriving evenly again";

    /// <inheritdoc />
    public IDataSensor? ExplainedBy { get; set; }

    public int Faults { get { lock (_lock) return _faults; } }

    private StreamQuality _read;

    /// <summary>The last <see cref="Read"/>, for a sensor that builds on this one.</summary>
    public StreamQuality Last { get { lock (_lock) return _read; } }

    /// <summary><see cref="Evaluate(long)"/> as the data quality monitor reads it.</summary>
    public DataReading Read(long now)
    {
        var q = Evaluate(now);
        lock (_lock) _read = q;
        return new DataReading(Name, (DataFinding)(byte)q.Verdict, q.Faulty, q.RateHz, q.NominalHz, q.MissedPercent);
    }

    public string Describe()
    {
        StreamQuality q;
        lock (_lock) q = _read;
        var inv = CultureInfo.InvariantCulture;
        return q.Verdict == StreamVerdict.Bursts
            ? string.Create(inv, $"arriving in bursts, not evenly ({q.RateHz:0.#} a second)")
            : string.Create(inv, $"arriving with gaps, about {q.MissedPercent:0}% missing ({q.RateHz:0.#} of {q.NominalHz:0.#} a second)");
    }

    public string Figures()
    {
        StreamQuality q;
        lock (_lock) q = _read;
        var inv = CultureInfo.InvariantCulture;
        string now = q.Verdict switch
        {
            StreamVerdict.NoData => "no data",
            StreamVerdict.Bursts => string.Create(inv, $"in bursts, {q.RateHz:0.#} a second, longest wait {q.LongestGapMs:0} ms"),
            _ => string.Create(inv, $"{(q.Verdict == StreamVerdict.Gaps ? "gaps" : "even")}, {q.RateHz:0.#} of {q.NominalHz:0.#} a second, {q.MissedPercent:0.#}% missing, longest wait {q.LongestGapMs:0} ms"),
        };
        return string.Create(inv, $"{now}; worst {q.WorstMissedPercent:0.#}% missing");
    }

    /// <summary>One arrival, now.</summary>
    public void Record() => Record(Stopwatch.GetTimestamp());

    /// <summary>As <see cref="Record()"/>, with the arrival time given (tests).</summary>
    public void Record(long timestamp)
    {
        lock (_lock)
        {
            if (_count > 0 && Seconds(timestamp - _last) >= SessionGapSeconds) ClearArrivals();
            _last = timestamp;
            _stamps[_next] = timestamp;
            _next = (_next + 1) % Capacity;
            if (_count < Capacity) _count++;
        }
    }

    /// <summary>Forget the stream (the socket closed).</summary>
    public void Reset()
    {
        lock (_lock)
        {
            ClearArrivals();
            _faulty = false;
        }
    }

    public StreamQuality Evaluate() => Evaluate(Stopwatch.GetTimestamp());

    /// <summary>As <see cref="Evaluate()"/>, with the current time given (tests).</summary>
    public StreamQuality Evaluate(long now)
    {
        lock (_lock)
        {
            if (_count == 0 || Seconds(now - _last) >= SilentSeconds)
            {
                // Silence is another fault with its own message; this one ends without a word.
                _badSince = _goodSince = null;
                _faulty = false;
                return new StreamQuality(Name, StreamVerdict.NoData, false, 0, 0, 0, 0, _worst * 100, _faults);
            }

            // The waits between the arrivals of the window, newest first.
            long from = now - (long)(WindowSeconds * Stopwatch.Frequency);
            int n = 0;
            long newer = 0, oldest = _last;
            double longest = 0;
            for (int i = 0; i < _count; i++)
            {
                long stamp = _stamps[((_next - 1 - i) % Capacity + Capacity) % Capacity];
                if (stamp < from) break;
                if (i > 0)
                {
                    double gap = Seconds(newer - stamp);
                    _gaps[n++] = gap;
                    if (gap > longest) longest = gap;
                }
                newer = stamp;
                oldest = stamp;
            }

            int arrivals = n + 1;
            double span = Seconds(_last - oldest);
            // A stream that has only just started is judged when it has run most of a window.
            bool whole = arrivals == _count ? arrivals == Capacity || span >= 0.8 * WindowSeconds : true;
            if (arrivals < MinArrivals || span <= 0 || !whole)
                return new StreamQuality(Name, StreamVerdict.NoData, _faulty, span > 0 ? n / span : 0, 0, 0, longest * 1000, _worst * 100, _faults);

            Array.Sort(_gaps, 0, n);
            double usual = _gaps[n / 2];
            double mean = span / n;
            double missed = Math.Clamp(1 - usual / mean, 0, 1);
            bool bursts = usual < BurstRatio * mean;
            if (missed > _worst) _worst = missed;

            if (Warns)
            {
                if (!_faulty)
                {
                    if (missed >= FaultShare)
                    {
                        _badSince ??= now;
                        if (Seconds(now - _badSince.Value) >= HoldSeconds)
                        {
                            _faulty = true;
                            _faults++;
                            _goodSince = null;
                        }
                    }
                    else _badSince = null;
                }
                else if (missed < ClearShare)
                {
                    _goodSince ??= now;
                    if (Seconds(now - _goodSince.Value) >= HoldSeconds)
                    {
                        _faulty = false;
                        _badSince = null;
                    }
                }
                else _goodSince = null;
            }

            var verdict = bursts ? StreamVerdict.Bursts : missed >= ClearShare ? StreamVerdict.Gaps : StreamVerdict.Even;
            return new StreamQuality(Name, verdict, _faulty, 1 / mean, bursts ? 0 : 1 / usual, missed * 100,
                longest * 1000, _worst * 100, _faults);
        }
    }

    private void ClearArrivals()
    {
        _count = 0;
        _next = 0;
        _badSince = _goodSince = null;
    }

    private static double Seconds(long ticks) => ticks / (double)Stopwatch.Frequency;
}
