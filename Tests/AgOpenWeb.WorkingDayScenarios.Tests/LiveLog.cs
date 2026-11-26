// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace AgOpenWeb.WorkingDayScenarios.Tests;

/// <summary>
/// The day as it happens, a line at a time and flushed at once: the day's events, every slow GPS
/// cycle and control tick, every lock wait of 5 ms or more. Beside the report
/// (working-day-live.log, AGOPENWEB_DAY_LIVE to put it elsewhere), and on the test's progress
/// output: what a run is looking for can be read while it runs (on the harness host,
/// <c>docker exec &lt;container&gt; tail -f /out/working-day-live.log</c>), not only at its end.
/// </summary>
public static class LiveLog
{
    private static readonly object Gate = new();
    private static StreamWriter? _w;
    private static readonly System.Diagnostics.Stopwatch Wall = System.Diagnostics.Stopwatch.StartNew();

    public static void Open(string path)
    {
        lock (Gate)
        {
            _w?.Dispose();
            _w = new StreamWriter(path, append: false) { AutoFlush = true };
        }
    }

    public static void Close()
    {
        lock (Gate) { _w?.Dispose(); _w = null; }
    }

    public static void Write(string line)
    {
        string text = string.Create(CultureInfo.InvariantCulture, $"[wall {Wall.Elapsed.TotalSeconds,7:0.0} s] {line}");
        lock (Gate) _w?.WriteLine(text);
        TestContext.Progress.WriteLine(text);
    }
}
