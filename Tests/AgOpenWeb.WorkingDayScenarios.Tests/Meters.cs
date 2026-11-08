// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;

namespace AgOpenWeb.WorkingDayScenarios.Tests;

/// <summary>
/// Durations in a fixed log-scale histogram (1 µs to 10 s, 40 buckets per decade), so a whole
/// day of GPS cycles or control ticks costs a few kilobytes and still gives percentiles.
/// </summary>
public sealed class Timings
{
    private const int PerDecade = 40, Decades = 7;           // 1 µs .. 10 s
    private readonly long[] _b = new long[PerDecade * Decades + 2];
    public long Count { get; private set; }
    public double MaxMs { get; private set; }
    public double SumMs { get; private set; }
    /// <summary>How many took longer than the GPS fix period (100 ms).</summary>
    public long Over100Ms { get; private set; }

    public void Add(double ms)
    {
        Count++;
        SumMs += ms;
        if (ms > MaxMs) MaxMs = ms;
        if (ms > 100) Over100Ms++;
        double us = ms * 1000;
        int i = us <= 1 ? 0 : (int)Math.Min(_b.Length - 1, 1 + Math.Floor(Math.Log10(us) * PerDecade));
        _b[i]++;
    }

    public void Add(Timings t)
    {
        for (int i = 0; i < _b.Length; i++) _b[i] += t._b[i];
        Count += t.Count; SumMs += t.SumMs; MaxMs = Math.Max(MaxMs, t.MaxMs); Over100Ms += t.Over100Ms;
    }

    /// <summary>The p-th percentile, ms (the upper edge of its bucket).</summary>
    public double P(double p)
    {
        if (Count == 0) return double.NaN;
        long want = (long)Math.Ceiling(Count * p / 100.0), acc = 0;
        for (int i = 0; i < _b.Length; i++)
        {
            acc += _b[i];
            if (acc >= want) return i == 0 ? 0.001 : Math.Min(MaxMs, Math.Pow(10, i / (double)PerDecade) / 1000.0);
        }
        return MaxMs;
    }

    public double MeanMs => Count == 0 ? double.NaN : SumMs / Count;

    /// <summary>How many took longer than <paramref name="ms"/> (to the resolution of the buckets).</summary>
    public long CountOver(double ms)
    {
        int first = (int)Math.Ceiling(1 + Math.Log10(ms * 1000) * PerDecade);
        long n = 0;
        for (int i = Math.Max(0, first); i < _b.Length; i++) n += _b[i];
        return n;
    }

    public Timings Clone()
    {
        var t = new Timings();
        t.Add(this);
        return t;
    }
}

/// <summary>The garbage collector's counts and the lock contentions at one moment: did a
/// collection run since, how long did it pause the process, did some thread wait for a lock
/// another held (a slow tick that these explain is not the tick's own work).</summary>
public readonly record struct GcMark(int Gen0, int Gen1, int Gen2, TimeSpan Paused, long Contentions)
{
    public static GcMark Now() => new(GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalPauseDuration(), Monitor.LockContentionCount);

    /// <summary>"" when nothing of the kind happened since this mark, else what did.</summary>
    public string Since()
    {
        var n = Now();
        var parts = new List<string>(2);
        if (n.Gen0 != Gen0)
        {
            int gen = n.Gen2 != Gen2 ? 2 : n.Gen1 != Gen1 ? 1 : 0;
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"a gen{gen} collection paused the process {(n.Paused - Paused).TotalMilliseconds:0} ms"));
        }
        if (n.Contentions != Contentions)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{n.Contentions - Contentions} lock contention(s) in the process (a process-wide count: which lock is not known)"));
        return string.Join("; ", parts);
    }

    public bool Collected => GC.CollectionCount(0) != Gen0;
}

/// <summary>What the process looks like right now: memory, threads, timers, open files.</summary>
public sealed record ProcessState(
    double HeapMB,          // managed heap after a full, blocking, compacting collection
    double RssMB,           // resident memory (VmRSS)
    double AnonMB,          // resident anonymous memory (RssAnon): heaps, native allocations
    int Threads,            // OS threads of the process
    int PoolThreads,        // .NET thread pool threads
    long Timers,            // System.Threading timers alive
    int Fds, int Sockets, int Files,
    int Gen0, int Gen1, int Gen2,
    double AllocatedMB,     // bytes allocated since the process started
    double CpuSeconds,
    long LockContentions,
    double CommittedMB)     // memory the GC holds from the OS (its heap plus what it keeps free)
{
    /// <summary>Anonymous resident memory that is not the GC's: native allocations (sockets,
    /// SkiaSharp, the runtime itself).</summary>
    public double NativeMB => AnonMB - CommittedMB;

    /// <summary>The harness's own pauses of the process so far (forced full collections, heap
    /// dumps), and when the last one ended (a <see cref="System.Diagnostics.Stopwatch"/>
    /// timestamp, 0 before the first). Each look makes two compacting collections that pause the
    /// whole process: a slow tick, a late host loop or a gap in the tablet's frames right after
    /// one is the harness's, not the app's.</summary>
    public static int ForcedCollections => Volatile.Read(ref _forced);
    private static int _forced;
    private static long _lastForcedEnd;

    /// <summary>Wall-clock ms since the last forced collection ended (infinity before the first).</summary>
    public static double SinceForcedCollectionMs
    {
        get
        {
            long t = Interlocked.Read(ref _lastForcedEnd);
            return t == 0 ? double.PositiveInfinity : System.Diagnostics.Stopwatch.GetElapsedTime(t).TotalMilliseconds;
        }
    }

    /// <summary>A pause of the harness's own (a forced collection, a heap dump) starts now.</summary>
    public static void PauseStarted() => Interlocked.Increment(ref _forced);

    /// <summary>The harness's pause ended now.</summary>
    public static void PauseEnded() => Interlocked.Exchange(ref _lastForcedEnd, System.Diagnostics.Stopwatch.GetTimestamp());

    public static ProcessState Take(bool collect = true)
    {
        if (collect)
        {
            PauseStarted();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            PauseEnded();
        }
        // The live bytes the last full blocking collection found (its own count, not disturbed by
        // what other threads allocated since), else the allocator's current total.
        var info = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        double heap = collect && info.Index > 0
            ? (info.HeapSizeBytes - info.FragmentedBytes) / 1048576.0
            : GC.GetTotalMemory(false) / 1048576.0;
        double rss = 0, anon = 0; int threads = 0;
        try
        {
            foreach (var line in File.ReadLines("/proc/self/status"))
            {
                if (line.StartsWith("VmRSS:", StringComparison.Ordinal)) rss = Kb(line) / 1024.0;
                else if (line.StartsWith("RssAnon:", StringComparison.Ordinal)) anon = Kb(line) / 1024.0;
                else if (line.StartsWith("Threads:", StringComparison.Ordinal)) threads = (int)Kb(line);
            }
        }
        catch
        {
            using var p = Process.GetCurrentProcess();
            rss = p.WorkingSet64 / 1048576.0; threads = p.Threads.Count;
        }
        int fds = 0, sockets = 0, files = 0;
        try
        {
            foreach (var fd in Directory.EnumerateFileSystemEntries("/proc/self/fd"))
            {
                fds++;
                string? target = null;
                try { target = new FileInfo(fd).LinkTarget; } catch { }
                if (target == null) continue;
                if (target.StartsWith("socket:", StringComparison.Ordinal)) sockets++;
                else if (IsDataFile(target)) files++;
            }
        }
        catch { /* not Linux */ }
        using var me = Process.GetCurrentProcess();
        return new ProcessState(heap, rss, anon, threads, ThreadPool.ThreadCount, Timer.ActiveCount, fds, sockets, files,
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
            GC.GetTotalAllocatedBytes() / 1048576.0, me.TotalProcessorTime.TotalSeconds, Monitor.LockContentionCount,
            GC.GetGCMemoryInfo().TotalCommittedBytes / 1048576.0);
    }

    private static double Kb(string line)
    {
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    /// <summary>A file the app opened (not a device, not the runtime's own assemblies and libraries).</summary>
    private static bool IsDataFile(string target) =>
        target.StartsWith('/') && !target.StartsWith("/dev/", StringComparison.Ordinal) && !target.StartsWith("/proc/", StringComparison.Ordinal)
        && !target.EndsWith(".dll", StringComparison.Ordinal) && !target.EndsWith(".so", StringComparison.Ordinal)
        && !target.Contains(".so.", StringComparison.Ordinal) && !target.EndsWith(".pdb", StringComparison.Ordinal)
        && !target.EndsWith(" (deleted)", StringComparison.Ordinal);

    /// <summary>The files open right now that are not the runtime's own (for "left open").</summary>
    public static List<string> OpenFiles()
    {
        var list = new List<string>();
        try
        {
            foreach (var fd in Directory.EnumerateFileSystemEntries("/proc/self/fd"))
            {
                string? target = null;
                try { target = new FileInfo(fd).LinkTarget; } catch { }
                if (target != null && IsDataFile(target)) list.Add(target);
            }
        }
        catch { }
        return list;
    }
}

/// <summary>The data folder by kind of file: what a day of work leaves on the disk.</summary>
public static class DiskUse
{
    public static Dictionary<string, (long Bytes, int Files)> Take(string root)
    {
        var d = new Dictionary<string, (long Bytes, int Files)>(StringComparer.Ordinal);
        if (!Directory.Exists(root)) return d;
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            long len;
            try { len = new FileInfo(f).Length; } catch { continue; }
            string k = Kind(Path.GetRelativePath(root, f));
            d.TryGetValue(k, out var v);
            d[k] = (v.Bytes + len, v.Files + 1);
        }
        return d;
    }

    /// <summary>Each file's size, by its path under the data folder.</summary>
    public static Dictionary<string, long> Files(string root)
    {
        var d = new Dictionary<string, long>(StringComparer.Ordinal);
        if (!Directory.Exists(root)) return d;
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            try { d[Path.GetRelativePath(root, f).Replace('\\', '/')] = new FileInfo(f).Length; } catch { }
        return d;
    }

    /// <summary>A file's kind: coverage tiles of a job, a field's sidecars, settings, profiles.</summary>
    public static string Kind(string rel)
    {
        rel = rel.Replace('\\', '/');
        var parts = rel.Split('/');
        string name = parts[^1];
        int fi = Array.IndexOf(parts, "Fields");
        if (fi >= 0 && parts.Length > fi + 2)
        {
            if (parts.Contains("coverage")) return "Fields/*/jobs/*/coverage/*";
            if (parts.Contains("jobs")) return "Fields/*/jobs/*/" + Generic(name);
            return "Fields/*/" + Generic(name);
        }
        if (name.EndsWith(".bak", StringComparison.Ordinal)) return "*.bak";
        if (parts.Length > 1 && parts[^2] is "Vehicles" or "Tools" or "VehicleProfiles" or "ToolProfiles") return parts[^2] + "/*";
        return parts.Length > 2 ? string.Join('/', parts.Skip(1).Take(parts.Length - 2)) + "/" + Generic(name) : Generic(name);
    }

    private static string Generic(string name)
    {
        // Numbered or dated files (tiles, logs) fold into one row.
        if (name.Any(char.IsDigit) && name.Contains('.', StringComparison.Ordinal))
            return "*" + Path.GetExtension(name);
        return name;
    }
}
