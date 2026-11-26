// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;

namespace AgOpenWeb.Services.DataQuality;

/// <summary>
/// Whether the receiver's sentences arrive whole. A sentence the app decodes that fails its
/// checksum, or whose fields do not read, was damaged on the way (a loose serial cable or a
/// wrong baud rate between receiver and module): its position is lost. Sentences of a kind
/// the app does not decode, and bytes that are no sentence at all, are not counted: they are
/// not damaged data, only unused.
/// <para>
/// A good link gives none: 5 in a minute is a fault, which ends when a whole minute had none
/// (<see cref="RecentEvents"/>).
/// </para>
/// Fed from the receive thread, nothing allocated; read about once a second from the host loop.
/// </summary>
public sealed class RefusedSentenceSensor : IDataSensor
{
    private const double SilentSeconds = 2.0;

    private readonly object _lock = new();
    private readonly RecentEvents _refused = new();
    private long _last;
    private bool _any;
    private long _badChecksum;
    private DataFinding _finding;

    public RefusedSentenceSensor(string name) => Name = name;

    public string Name { get; }

    public string Advice => "Check the cable and the baud rate between the GPS receiver and its module";

    public string Recovered => "arriving whole again";

    public int Faults { get { lock (_lock) return _refused.Faults; } }

    /// <summary>Damaged sentences since the app started.</summary>
    public long Total { get { lock (_lock) return _refused.Total; } }

    /// <summary>One line from the receiver.</summary>
    /// <param name="damaged">It is a sentence the app decodes, and it did not pass.</param>
    /// <param name="badChecksum">Of those, it failed the checksum.</param>
    public void Record(bool damaged, bool badChecksum, long timestamp)
    {
        lock (_lock)
        {
            _any = true;
            _last = timestamp;
            if (!damaged) return;
            if (badChecksum) _badChecksum++;
            _refused.Add(timestamp);
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _refused.Clear();
            _any = false;
            _finding = DataFinding.NoData;
        }
    }

    public DataReading Read(long now)
    {
        lock (_lock)
        {
            if (!_any || (now - _last) / (double)Stopwatch.Frequency >= SilentSeconds)
            {
                _refused.Clear();
                _finding = DataFinding.NoData;
                return new DataReading(Name, _finding, false, 0, 0, 0);
            }
            int n = _refused.Judge(now);
            _finding = n > 0 ? DataFinding.Damaged : DataFinding.Whole;
            return new DataReading(Name, _finding, _refused.Faulty, 0, 0, n);
        }
    }

    public string Describe()
    {
        lock (_lock)
            return string.Create(CultureInfo.InvariantCulture, $"{_refused.InWindow} in the last minute arrived damaged and were not used");
    }

    public string Figures()
    {
        lock (_lock)
        {
            var inv = CultureInfo.InvariantCulture;
            string since = string.Create(inv, $"{_refused.Total} damaged since the start ({_badChecksum} failed the checksum)");
            return _finding switch
            {
                DataFinding.NoData => "no data; " + since,
                DataFinding.Whole => "whole; " + since,
                _ => string.Create(inv, $"{_refused.InWindow} damaged in the last minute; ") + since,
            };
        }
    }
}
