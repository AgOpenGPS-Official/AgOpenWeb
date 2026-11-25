// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Diagnostics;
using System.Globalization;

namespace AgOpenWeb.Services.DataQuality;

/// <summary>
/// Whether the positions arrive in the order the receiver made them. Every position sentence
/// carries the receiver's own time; one that is not newer than the last of its kind is the
/// same position again or an older one, and is not fit to steer by: the tractor seems to stand
/// still or jump back, which reads as a reverse.
/// <para>
/// It happens when the same data reaches the app by two ways, one slower than the other (a
/// tablet on the modules' cable that has also joined their router's Wi-Fi: on a tester's
/// tractor the Wi-Fi copies came 0.3 s late and 41 of them read as a reverse, each taking
/// AutoSteer off, #900), or when something on the way repeats or reorders it. The sensor does
/// not look for the cause; the data says it.
/// </para>
/// <para>
/// A healthy link gives none, so the bound is a count, not a share: 5 in a minute is a fault,
/// which ends when a whole minute had none (<see cref="RecentEvents"/>). A single stray one
/// is counted and shown but nobody is told.
/// </para>
/// <see cref="Record"/> is called from the receive thread and allocates nothing;
/// <see cref="Read"/> about once a second from the host loop.
/// </summary>
public sealed class FixTimeSensor : IDataSensor
{
    /// <summary>Kinds of sentence that can be told apart (<see cref="Record"/>'s <c>kind</c>).</summary>
    public const int Kinds = 8;

    private const double SilentSeconds = 2.0;
    private const double SessionGapSeconds = 5.0;
    private const int Day = 24 * 3600 * 100;

    private readonly object _lock = new();
    private readonly int[] _lastTime = new int[Kinds];
    private readonly RecentEvents _bad = new();
    private long _last;
    private bool _any;
    private DataFinding _finding;

    public FixTimeSensor(string name)
    {
        Name = name;
        Array.Fill(_lastTime, -1);
    }

    public string Name { get; }

    public string Advice => "Use one link to the modules, a network cable: turn off any second one (Wi-Fi)";

    public string Recovered => "in order again";

    public int Faults { get { lock (_lock) return _bad.Faults; } }

    /// <summary>Positions that were not newer than the one before, since the app started.</summary>
    public long Total { get { lock (_lock) return _bad.Total; } }

    /// <summary>One position sentence with the receiver's time on it.</summary>
    /// <param name="kind">Which sentence (0 to <see cref="Kinds"/> - 1): each kind is compared with
    /// its own last one, as a receiver sends several with the same time (GGA and HPR).</param>
    /// <param name="centiseconds">The receiver's time of day in 1/100 s; under 0 = none, not looked at.</param>
    /// <param name="timestamp">When it arrived (<see cref="Stopwatch.GetTimestamp"/>).</param>
    public void Record(int kind, int centiseconds, long timestamp)
    {
        if (centiseconds < 0 || (uint)kind >= Kinds) return;
        lock (_lock)
        {
            // A long silence is a new session (the module was unplugged, the receiver restarted).
            if (_any && Seconds(timestamp - _last) >= SessionGapSeconds) Array.Fill(_lastTime, -1);
            _any = true;
            _last = timestamp;

            int before = _lastTime[kind];
            // Midnight: the time of day starts again, which is newer.
            bool newer = before < 0 || centiseconds > before || before - centiseconds > Day / 2;
            if (newer)
            {
                _lastTime[kind] = centiseconds;
                return;
            }
            // A receiver that does not know the time yet sends 000000.00 on every sentence.
            if (centiseconds == 0 && before == 0) return;
            _bad.Add(timestamp);
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            Array.Fill(_lastTime, -1);
            _bad.Clear();
            _any = false;
            _finding = DataFinding.NoData;
        }
    }

    public DataReading Read() => Read(Stopwatch.GetTimestamp());

    public DataReading Read(long now)
    {
        lock (_lock)
        {
            if (!_any || Seconds(now - _last) >= SilentSeconds)
            {
                _bad.Clear();
                _finding = DataFinding.NoData;
                return new DataReading(Name, _finding, false, 0, 0, 0);
            }

            int n = _bad.Judge(now);
            _finding = n > 0 ? DataFinding.OutOfOrder : DataFinding.InOrder;
            return new DataReading(Name, _finding, _bad.Faulty, 0, 0, n);
        }
    }

    public string Describe()
    {
        lock (_lock)
            return string.Create(CultureInfo.InvariantCulture,
                $"{_bad.InWindow} in the last minute were the same position again or an older one");
    }

    public string Figures()
    {
        lock (_lock)
        {
            var inv = CultureInfo.InvariantCulture;
            return _finding switch
            {
                DataFinding.NoData => string.Create(inv, $"no data; {_bad.Total} not newer than the one before since the start"),
                DataFinding.InOrder => string.Create(inv, $"in order; {_bad.Total} not newer than the one before since the start"),
                _ => string.Create(inv, $"{_bad.InWindow} in the last minute not newer than the one before; {_bad.Total} since the start"),
            };
        }
    }

    /// <summary>The receiver's time of day in 1/100 s from a sentence's first field
    /// (<c>hhmmss.ss</c>; KSXT's <c>yyyymmddhhmmss.ss</c>), or -1 when it has none.</summary>
    public static int TimeOfDay(ReadOnlySpan<byte> sentence)
    {
        int at = sentence.IndexOf((byte)',');
        if (at < 0) return -1;
        long whole = 0;
        int digits = 0, i = at + 1;
        for (; i < sentence.Length && sentence[i] >= '0' && sentence[i] <= '9'; i++, digits++)
            whole = whole % 100_000_000_000_000L * 10 + (sentence[i] - '0');
        if (digits < 6) return -1;
        int hundredths = 0;
        if (i < sentence.Length && sentence[i] == '.')
        {
            if (i + 1 < sentence.Length && sentence[i + 1] >= '0' && sentence[i + 1] <= '9') hundredths = (sentence[i + 1] - '0') * 10;
            if (i + 2 < sentence.Length && sentence[i + 2] >= '0' && sentence[i + 2] <= '9') hundredths += sentence[i + 2] - '0';
        }
        else if (i < sentence.Length && sentence[i] != ',') return -1;
        int hhmmss = (int)(whole % 1_000_000);
        int hh = hhmmss / 10000, mm = hhmmss / 100 % 100, ss = hhmmss % 100;
        if (hh > 23 || mm > 59 || ss > 60) return -1;
        return ((hh * 60 + mm) * 60 + ss) * 100 + hundredths;
    }

    private static double Seconds(long ticks) => ticks / (double)Stopwatch.Frequency;
}
