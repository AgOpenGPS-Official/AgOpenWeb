// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System.Collections.Generic;
using AgOpenWeb.Models.Timing;

namespace AgOpenWeb.Services;

/// <summary>
/// Tracks when each inbound UDP source (GPS NMEA, steer PGN 253, …) was last heard and reports
/// each silence once: when it starts (<see cref="CheckSilences"/>) and when the source
/// resumes (<see cref="MarkSeen"/>, with the gap). Issue #169 diagnostics — the next log
/// shows whether the whole board went quiet or only its GPS. A source never heard is not
/// reported. Thread-safe; time comes from the <see cref="IClock"/>.
/// </summary>
internal sealed class SourceSilenceMonitor
{
    public const double SilenceMs = 2000.0;

    private sealed class Source
    {
        public long LastSeen;
        public bool Silent;
        public string? From;
    }

    private readonly IClock? _clock;
    private readonly object _lock = new();
    private readonly Dictionary<string, Source> _sources = new();

    /// <param name="clock">Time source; null follows <see cref="Clock.Current"/>.</param>
    public SourceSilenceMonitor(IClock? clock = null) => _clock = clock;

    private IClock Time => _clock ?? Clock.Current;

    /// <summary>Record a packet from <paramref name="name"/>. Returns the silence in
    /// milliseconds if this packet ends one that was reported, else null.</summary>
    public double? MarkSeen(string name, string? from)
    {
        long now = Time.GetTimestamp();
        lock (_lock)
        {
            if (!_sources.TryGetValue(name, out var src))
            {
                _sources[name] = new Source { LastSeen = now, From = from };
                return null;
            }
            double? gap = src.Silent ? Time.ElapsedMs(src.LastSeen, now) : null;
            src.LastSeen = now;
            src.Silent = false;
            src.From = from;
            return gap;
        }
    }

    /// <summary>Sources that have just passed <see cref="SilenceMs"/> without a packet
    /// (each reported once per silence), with the address last heard from.</summary>
    public List<(string Name, double SilentMs, string? From)> CheckSilences()
    {
        var result = new List<(string, double, string?)>();
        long now = Time.GetTimestamp();
        lock (_lock)
        {
            foreach (var (name, src) in _sources)
            {
                if (src.Silent) continue;
                double ms = Time.ElapsedMs(src.LastSeen, now);
                if (ms <= SilenceMs) continue;
                src.Silent = true;
                result.Add((name, ms, src.From));
            }
        }
        return result;
    }

    /// <summary>The address <paramref name="name"/> was last heard from and how long ago,
    /// or null if it has never been heard.</summary>
    public (string? From, double AgeMs)? LastSeen(string name)
    {
        lock (_lock)
            return _sources.TryGetValue(name, out var src)
                ? (src.From, Time.ElapsedMs(src.LastSeen, Time.GetTimestamp()))
                : null;
    }

    /// <summary>Forget all sources (UDP stopped).</summary>
    public void Reset()
    {
        lock (_lock) _sources.Clear();
    }
}
