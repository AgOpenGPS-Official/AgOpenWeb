// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Runtime.InteropServices;

namespace AgOpenWeb.WorkingDayScenarios.Tests;

/// <summary>
/// Where a slow control tick's thread waits (Linux): a watchdog thread looks every 2 ms, once a
/// tick has run 15 ms, at the tick thread's system call (/proc/self/task/&lt;tid&gt;/syscall:
/// its number, or "running"), the kernel function it sleeps in (wchan) and, when readable, its
/// kernel stack; the samples are counted and handed back when the tick ends
/// (<see cref="End"/>). A thread that gave up the CPU many times on little CPU time is shown
/// waiting in what it waited in, a lock or not.
/// </summary>
public static class TickWatch
{
    [DllImport("libc")] private static extern int gettid();

    private static readonly bool Supported = OperatingSystem.IsLinux();
    private static volatile int _tid;
    private static long _startTs;
    private static readonly Dictionary<string, int> Samples = new();
    private static readonly object Gate = new();
    private static Thread? _watchdog;

    /// <summary>A tick starts on this thread.</summary>
    public static void Begin()
    {
        if (!Supported) return;
        if (_watchdog == null)
        {
            _watchdog = new Thread(Watch) { IsBackground = true, Name = "tick-watch", Priority = ThreadPriority.AboveNormal };
            _watchdog.Start();
            LiveLog.Write("[watch] tick watch on: " + RuntimeInformation.ProcessArchitecture + ", sampling the tick thread's system call and wait every 2 ms after 15 ms");
        }
        lock (Gate) Samples.Clear();
        Volatile.Write(ref _startTs, System.Diagnostics.Stopwatch.GetTimestamp());
        _tid = gettid();
    }

    /// <summary>The tick ended: what the watchdog saw, most frequent first ("" when nothing).</summary>
    public static string End()
    {
        if (!Supported) return "";
        _tid = 0;
        lock (Gate)
        {
            if (Samples.Count == 0) return "";
            return "seen waiting in: " + string.Join("; ", Samples.OrderByDescending(x => x.Value).Take(4).Select(x => x.Value + "× " + x.Key));
        }
    }

    private static void Watch()
    {
        while (true)
        {
            Thread.Sleep(2);
            int tid = _tid;
            if (tid == 0) continue;
            if (System.Diagnostics.Stopwatch.GetElapsedTime(Volatile.Read(ref _startTs)).TotalMilliseconds < 15) continue;
            string where = Read(tid);
            if (_tid != tid) continue;
            lock (Gate) Samples[where] = Samples.GetValueOrDefault(where) + 1;
        }
    }

    private static string Read(int tid)
    {
        string dir = "/proc/self/task/" + tid.ToString(CultureInfo.InvariantCulture) + "/";
        string sys = Try(dir + "syscall");
        string call = sys.Length == 0 ? "?" : sys.StartsWith("running", StringComparison.Ordinal) ? "running" : "syscall " + sys.Split(' ')[0];
        string wchan = Try(dir + "wchan");
        string stack = Try(dir + "stack");
        string top = stack.Length == 0 ? "" : " [" + string.Join(" < ", stack.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(5)
            .Select(l => l.Contains(' ') ? l[(l.IndexOf(' ') + 1)..] : l).Select(l => l.Split('+')[0])) + "]";
        return call + (wchan.Length > 0 && wchan != "0" ? " in " + wchan : "") + top;
    }

    private static string Try(string path)
    {
        try { return File.ReadAllText(path).Trim(); } catch { return ""; }
    }
}
