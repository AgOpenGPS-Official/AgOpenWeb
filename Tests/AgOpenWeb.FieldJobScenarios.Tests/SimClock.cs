// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using AgOpenWeb.Models.Timing;

namespace AgOpenWeb.FieldJobScenarios.Tests;

/// <summary>
/// The job's own time as AgOpenWeb's clock (<see cref="Clock.Set"/>). It follows the wall
/// clock while the operator sets AgOpenWeb up; once the tractor moves (<see cref="Step"/>) it
/// advances only with the job: 10 ms per tick of the 100 Hz control loop, 100 ms per fix,
/// whatever the harness's pace. Contour guidance rebuilds its line every 2 s of this clock and
/// the pass pick waits on it too; on the wall clock at 12x real time those would run 12 times
/// too seldom on the simulated ground.
/// </summary>
public sealed class SimClock : IClock
{
    private readonly DateTime _start = new(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc);
    private readonly long _base = Stopwatch.GetTimestamp();
    private long _ticks;          // 100 ns, once stepping
    private volatile bool _stepping;

    private long Ticks => _stepping
        ? Interlocked.Read(ref _ticks)
        : (long)((Stopwatch.GetTimestamp() - _base) * (1e7 / Stopwatch.Frequency));

    /// <summary>From now on the clock moves only by <see cref="AdvanceMs"/>.</summary>
    public void Step()
    {
        if (_stepping) return;
        Interlocked.Exchange(ref _ticks, Ticks);
        _stepping = true;
    }

    public void AdvanceMs(double ms)
    {
        if (_stepping) Interlocked.Add(ref _ticks, (long)(ms * 10_000));
    }

    public DateTime UtcNow => _start.AddTicks(Ticks);
    public DateTime Now => UtcNow.ToLocalTime();
    public long GetTimestamp() => Ticks;
    public long Frequency => 10_000_000;
    public double ElapsedSeconds(long start, long end) => (end - start) / 1e7;
    public double ElapsedMs(long start, long end) => (end - start) / 1e4;
    public double TimeScale { get; set; } = 1.0;
}
