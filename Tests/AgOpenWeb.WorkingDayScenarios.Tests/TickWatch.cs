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
            bool first;
            lock (Gate) { first = Samples.Count == 0; Samples[where] = Samples.GetValueOrDefault(where) + 1; }
            if (first) LiveLog.Write("[watch] a tick past 15 ms, the process's threads: " + Threads(tid));
        }
    }

    private static string Read(int tid)
    {
        string dir = "/proc/self/task/" + tid.ToString(CultureInfo.InvariantCulture) + "/";
        string sys = Try(dir + "syscall");
        var parts = sys.Split(' ');
        string call = sys.Length == 0 ? "?" : sys.StartsWith("running", StringComparison.Ordinal) ? "running"
            : "syscall " + parts[0] + (parts[0] == "202" && parts.Length > 1 ? " on " + parts[1] : "");
        string wchan = Try(dir + "wchan");
        string stack = Try(dir + "stack");
        string top = stack.Length == 0 ? "" : " [" + string.Join(" < ", stack.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(5)
            .Select(l => l.Contains(' ') ? l[(l.IndexOf(' ') + 1)..] : l).Select(l => l.Split('+')[0])) + "]";
        return call + (wchan.Length > 0 && wchan != "0" ? " in " + wchan : "") + top;
    }

    /// <summary>Every thread of the process that is not asleep in the usual places, by name:
    /// its state (R running, S sleeping, D waiting on I/O), system call and wait.</summary>
    private static string Threads(int tickTid)
    {
        var list = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories("/proc/self/task"))
        {
            string tid = Path.GetFileName(dir);
            string comm = Try(dir + "/comm");
            string stat = Try(dir + "/stat");
            int close = stat.LastIndexOf(')');
            string state = close > 0 && close + 2 < stat.Length ? stat.Substring(close + 2, 1) : "?";
            string sys = Try(dir + "/syscall").Split(' ')[0];
            string wchan = Try(dir + "/wchan");
            bool tick = tid == tickTid.ToString(CultureInfo.InvariantCulture);
            // The idle: asleep in epoll, a timed wait or a futex with nothing to do. Keep the rest
            // and the tick's own thread.
            if (!tick && state == "S" && (wchan is "do_epoll_wait" or "hrtimer_nanosleep" or "do_sys_poll" or "ep_poll")) continue;
            list.Add((tick ? "TICK " : "") + comm + " " + state + " " + (sys == "running" ? "running" : "sc" + sys) + (wchan.Length > 0 && wchan != "0" ? "/" + wchan : ""));
        }
        return string.Join(", ", list);
    }

    private static string Try(string path)
    {
        try { return File.ReadAllText(path).Trim(); } catch { return ""; }
    }
}
