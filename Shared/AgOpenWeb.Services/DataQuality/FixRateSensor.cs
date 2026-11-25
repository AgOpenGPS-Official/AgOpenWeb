// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Diagnostics;
using System.Globalization;

namespace AgOpenWeb.Services.DataQuality;

/// <summary>
/// Whether the receiver sends positions as often as the app is set up for. A stream that
/// arrives evenly at half the rate is "even" to <see cref="StreamQualityMonitor"/>, which
/// knows no rate to expect; here the sender's own rate (its usual interval, so not what a
/// faulty link loses: that is the arrival sensor's to say) is held against the rate that is
/// expected. Guidance and the steering are tuned for that rate.
/// <para>
/// Under <see cref="FaultShare"/> of it for <see cref="HoldSeconds"/> is a fault; at
/// <see cref="ClearShare"/> or more for as long ends it. Not judged while the arrivals come
/// in bursts or are too few (the sender's rate is then not known).
/// </para>
/// Read from the host loop, after the arrival sensor it takes its figures from.
/// </summary>
public sealed class FixRateSensor : IDataSensor
{
    /// <summary>The sender's rate under this share of the expected one is bad.</summary>
    public const double FaultShare = 0.8;
    /// <summary>At this share or more it is good; in between, the state holds.</summary>
    public const double ClearShare = 0.9;
    /// <summary>Bad for this long is a fault; good for this long ends it.</summary>
    public const double HoldSeconds = 15.0;

    private readonly StreamQualityMonitor _arrivals;
    private readonly Func<double> _expectedHz;
    private long? _badSince;
    private long? _goodSince;
    private bool _faulty;
    private int _faults;
    private double _rate, _expected;
    private DataFinding _finding;

    /// <param name="arrivals">The stream's arrival sensor, read before this one.</param>
    /// <param name="expectedHz">The rate the app is set up for.</param>
    public FixRateSensor(string name, StreamQualityMonitor arrivals, Func<double> expectedHz)
    {
        Name = name;
        _arrivals = arrivals;
        _expectedHz = expectedHz;
    }

    public string Name { get; }

    public string Advice => "Set the GPS receiver to send its positions at that rate";

    public string Recovered => "at the expected rate again";

    public int Faults => _faults;

    public void Reset()
    {
        _badSince = _goodSince = null;
        _faulty = false;
        _finding = DataFinding.NoData;
    }

    public DataReading Read(long now)
    {
        var q = _arrivals.Last;
        _expected = _expectedHz();
        _rate = q.NominalHz;
        if (q.Verdict is StreamVerdict.NoData or StreamVerdict.Bursts || _rate <= 0 || _expected <= 0)
        {
            // Silent ends a fault (GPS lost says that); bursts or too few only leave it unjudged.
            if (q.Verdict == StreamVerdict.NoData && q.RateHz <= 0) Reset();
            _badSince = _goodSince = null;
            _finding = DataFinding.NoData;
            return new DataReading(Name, _finding, _faulty, 0, _expected, 0);
        }

        double share = _rate / _expected;
        if (!_faulty)
        {
            if (share < FaultShare)
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
        else if (share >= ClearShare)
        {
            _goodSince ??= now;
            if (Seconds(now - _goodSince.Value) >= HoldSeconds)
            {
                _faulty = false;
                _badSince = null;
            }
        }
        else _goodSince = null;

        _finding = share < FaultShare ? DataFinding.Slow : DataFinding.AtRate;
        return new DataReading(Name, _finding, _faulty, _rate, _expected, 0);
    }

    public string Describe() =>
        string.Create(CultureInfo.InvariantCulture, $"only {_rate:0.#} a second, {_expected:0.#} expected");

    public string Figures()
    {
        var inv = CultureInfo.InvariantCulture;
        return _finding switch
        {
            DataFinding.NoData => string.Create(inv, $"not judged; {_expected:0.#} a second expected"),
            DataFinding.Slow => string.Create(inv, $"slow, sent at {_rate:0.#} a second, {_expected:0.#} expected"),
            _ => string.Create(inv, $"sent at {_rate:0.#} a second, {_expected:0.#} expected"),
        };
    }

    private static double Seconds(long ticks) => ticks / (double)Stopwatch.Frequency;
}
