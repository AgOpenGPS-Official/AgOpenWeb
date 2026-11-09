// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.VehicleToolScenarios.Tests;

/// <summary>
/// What a piece of work cost the thread that ran it: the <see cref="WorkCounters"/> (polygon
/// edges, coverage lookups, offset points), the bytes it allocated, and the files it read
/// (read calls, from Linux's per-thread I/O accounting in <c>/proc/thread-self/io</c>; -1
/// where the system has none). Counts, not time: the same on every machine.
/// </summary>
public readonly record struct Cost(WorkCounters.Snapshot Work, long ReadCalls)
{
    public long Edges => Work.PolygonEdges;
    public long CoverageLookups => Work.CoverageLookups;
    public long OffsetPoints => Work.OffsetCurvePoints + Work.PolygonOffsetPoints;
    public long Bytes => Work.AllocatedBytes;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{Edges:N0} edges, {CoverageLookups:N0} coverage lookups, {OffsetPoints:N0} offset points, {Bytes / 1024.0:N1} KB, {(ReadCalls < 0 ? "?" : ReadCalls.ToString(CultureInfo.InvariantCulture))} reads");
}

public static class CostMeter
{
    private const string IoFile = "/proc/thread-self/io";
    private static readonly bool HasIo = File.Exists(IoFile);

    [ThreadStatic] private static byte[]? _buf;

    /// <summary>Starts a measurement on this thread; <see cref="Stop"/> on the same thread ends it.</summary>
    public static (WorkCounters.Snapshot Work, long Reads) Start()
    {
        long reads = ReadCalls();          // first, so its own allocations are not counted
        return (WorkCounters.Take(), reads);
    }

    public static Cost Stop((WorkCounters.Snapshot Work, long Reads) start)
    {
        var work = WorkCounters.Take() - start.Work;
        long reads = ReadCalls();
        // The read that took the starting count is counted once it returns: take it off.
        return new Cost(work, start.Reads < 0 || reads < 0 ? -1 : reads - start.Reads - 1);
    }

    /// <summary>Read calls this thread has made so far (<c>syscr</c>), or -1.</summary>
    public static long ReadCalls()
    {
        if (!HasIo) return -1;
        try
        {
            // Opened each time: /proc/thread-self is the thread that opens it. One read call.
            using var h = File.OpenHandle(IoFile);
            var buf = _buf ??= new byte[512];
            int n = RandomAccess.Read(h, buf, 0);
            var text = System.Text.Encoding.ASCII.GetString(buf, 0, n);
            foreach (var line in text.Split('\n'))
                if (line.StartsWith("syscr:", StringComparison.Ordinal))
                    return long.Parse(line.AsSpan(6).Trim(), CultureInfo.InvariantCulture);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return -1;
    }
}
