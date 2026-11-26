// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Runtime;

namespace AgOpenWeb.WorkingDayScenarios.Tests;

/// <summary>
/// Which kinds of object are on the managed heap, and how many: the process writes a dump of
/// itself (the runtime's own createdump, through its diagnostics port) and ClrMD counts the
/// live objects by type. Two censuses, taken in the same state (no field open, the browsers
/// connected) hours apart, tell what piled up: the type, the count, the bytes, and the types
/// of the objects that hold them. Off with <c>AGOPENWEB_DAY_CENSUS=0</c>.
/// </summary>
public sealed class HeapCensus
{
    public sealed record Row(string Type, long Count, long Bytes);

    public string Label { get; }
    public Dictionary<string, Row> Types { get; } = new(StringComparer.Ordinal);
    public long Objects { get; private set; }
    public long Bytes { get; private set; }
    public double Seconds { get; private set; }
    public string? Error { get; private set; }
    /// <summary>For the types asked about: the path from a GC root to the biggest live object of
    /// the type (<see cref="PathText"/>).</summary>
    public Dictionary<string, string> Paths { get; } = new(StringComparer.Ordinal);
    /// <summary>Bytes <c>CoverageMapService</c> holds, by its field.</summary>
    public Dictionary<string, long> CoverageHeld { get; } = new(StringComparer.Ordinal);
    public const string CoverageService = "AgOpenWeb.Services.Coverage.CoverageMapService";

    /// <summary>Live timers by owner (<see cref="TimerOwner"/>).</summary>
    public Dictionary<string, int> Timers { get; } = new(StringComparer.Ordinal);

    private HeapCensus(string label) { Label = label; }

    public static bool Enabled => Environment.GetEnvironmentVariable("AGOPENWEB_DAY_CENSUS") != "0";

    /// <summary>A dump of the process now (the runtime's createdump, a few seconds): kept on disk
    /// and counted after the day (<see cref="Analyze"/>), so the counting's own memory never
    /// shows in the day's measures.</summary>
    public static (string? Path, string? Error, double Seconds) Dump(string label)
    {
        var sw = Stopwatch.StartNew();
        string path = Path.Combine(Path.GetTempPath(), $"aow-day-{Environment.ProcessId}-{label}.dmp");
        try
        {
            new DiagnosticsClient(Environment.ProcessId).WriteDump(DumpType.WithHeap, path);
            return (path, null, sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            try { File.Delete(path); } catch { }
            return (null, ex.GetType().Name + ": " + ex.Message, sw.Elapsed.TotalSeconds);
        }
    }

    /// <summary>Count the live objects of a dump by type (those reachable from the GC's roots,
    /// as the collector itself would find them), and delete the dump. With
    /// <paramref name="before"/>, also say where the types that grew most since that census are
    /// held: the path from a root to the biggest object of each type. <paramref name="pathsFor"/>
    /// asks for the path to a type whatever it did.</summary>
    public static HeapCensus Analyze(string label, string path, HeapCensus? before = null, IEnumerable<string>? pathsFor = null)
    {
        var c = new HeapCensus(label);
        var sw = Stopwatch.StartNew();
        try
        {
            using var dt = DataTarget.LoadDump(path);
            var info = dt.ClrVersions[0];
            ClrRuntime rt;
            try { rt = info.CreateRuntime(); }
            catch
            {
                // A runtime from a Linux distribution's package: its DAC is beside it but not signed
                // the way ClrMD checks by default.
                string dac = Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!,
                    OperatingSystem.IsWindows() ? "mscordaccore.dll" : OperatingSystem.IsMacOS() ? "libmscordaccore.dylib" : "libmscordaccore.so");
                rt = info.CreateRuntime(dac, ignoreMismatch: true);
            }
            using (rt)
            {
                var heap = rt.Heap;
                // Breadth first from the roots: every live object once, with the object and field
                // that first reached it, so a path from a root is the shortest one.
                var from = new Dictionary<ulong, (ulong Parent, string Via)>();
                var biggest = new Dictionary<string, (ulong Address, long Size)>(StringComparer.Ordinal);
                var queue = new Queue<ulong>();
                foreach (var root in heap.EnumerateRoots())
                {
                    ulong a = root.Object.Address;
                    if (a == 0 || from.ContainsKey(a)) continue;
                    from[a] = (0, root.RootKind.ToString());
                    queue.Enqueue(a);
                }
                while (queue.Count > 0)
                {
                    ulong a = queue.Dequeue();
                    var o = heap.GetObject(a);
                    if (!o.IsValid || o.IsFree || o.Type?.Name is not { } name) continue;
                    long size = (long)o.Size;
                    c.Objects++; c.Bytes += size;
                    c.Types.TryGetValue(name, out var row);
                    c.Types[name] = new Row(name, (row?.Count ?? 0) + 1, (row?.Bytes ?? 0) + size);
                    if (!biggest.TryGetValue(name, out var big) || size > big.Size) biggest[name] = (a, size);
                    if (name == "System.Threading.TimerQueueTimer")
                    {
                        string owner = TimerOwner(o);
                        c.Timers[owner] = c.Timers.GetValueOrDefault(owner) + 1;
                    }
                    foreach (var r in o.EnumerateReferencesWithFields(carefully: false, considerDependantHandles: true))
                    {
                        ulong t = r.Object.Address;
                        if (t == 0 || from.ContainsKey(t)) continue;
                        string via = r.IsDependentHandle ? "(dependent handle)"
                            : r.IsArrayElement ? "[]"
                            : (r.Field?.Name ?? "?") + (r.InnerField is { } inner ? "." + (inner.Field?.Name ?? "?") : "");
                        from[t] = (a, via);
                        queue.Enqueue(t);
                    }
                }

                var statics = StaticHolders(rt);
                if (before != null)
                    foreach (var g in Growth(before, c, 8))
                        if (biggest.TryGetValue(g.Type, out var b)) c.Paths[g.Type] = PathText(heap, from, b.Address, whole: false, statics);
                foreach (var type in pathsFor ?? Array.Empty<string>())
                    if (biggest.TryGetValue(type, out var b)) c.Paths[type] = PathText(heap, from, b.Address, whole: true, statics);

                // What the coverage service holds, by its field (the field's object and, for a
                // collection, the arrays inside it): with no field open it should hold little.
                foreach (var (addr, (parent, via)) in from)
                {
                    if (parent == 0) continue;
                    string? pt = heap.GetObjectType(parent)?.Name;
                    string? field = null;
                    if (pt == CoverageService) field = via;
                    else if (from.TryGetValue(parent, out var gp) && gp.Parent != 0 && heap.GetObjectType(gp.Parent)?.Name == CoverageService) field = gp.Via;
                    if (field == null) continue;
                    long size = (long)heap.GetObject(addr).Size;
                    c.CoverageHeld[field] = c.CoverageHeld.GetValueOrDefault(field) + size;
                }
            }
        }
        catch (Exception ex)
        {
            c.Error = ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            try { File.Delete(path); } catch { }
            c.Seconds = sw.Elapsed.TotalSeconds;
        }
        return c;
    }

    /// <summary>Whose timer this is: the type of its callback's target (the object whose method
    /// it calls, or the closure), else of its state.</summary>
    private static string TimerOwner(ClrObject timer)
    {
        try
        {
            var cb = timer.ReadObjectField("_timerCallback");
            string? target = cb.IsNull ? null : cb.ReadObjectField("_target").Type?.Name;
            string? state = timer.ReadObjectField("_state").Type?.Name;
            string who = target != null && !target.StartsWith("System.Threading.TimerCallback", StringComparison.Ordinal) ? target : state ?? "(no target)";
            // A Task.Delay is owned by whoever awaits it: its continuation is the awaiting
            // method's state machine (a box named after the method), or a task that waits on it.
            if (who.StartsWith("System.Threading.Tasks.Task+DelayPromise", StringComparison.Ordinal))
            {
                var promise = timer.ReadObjectField("_state");
                var cont = promise.IsNull ? default : promise.ReadObjectField("m_continuationObject");
                string? awaiter = cont.IsNull ? null : cont.Type?.Name;
                if (awaiter != null && cont.Type!.GetFieldByName("StateMachine") is { } sm && sm.Type?.Name is { } smName)
                    awaiter = smName;
                return "Task.Delay awaited by " + (awaiter == null ? "nothing (left running)" : Short(awaiter));
            }
            // A closure or a state machine tells its owner by its enclosing type.
            return Short(who);
        }
        catch { return "?"; }
    }

    /// <summary>The path from a root to an object: "root kind: Type.field > Type.field > ... >
    /// Type", from the last AgOpenWeb object on the way when there is one (what holds it in the
    /// app), else the whole path.</summary>
    private static string PathText(ClrHeap heap, Dictionary<ulong, (ulong Parent, string Via)> from, ulong target, bool whole,
        Dictionary<ulong, string> statics)
    {
        var links = new List<(string Type, string Via)>();   // target first
        string rootKind = "";
        ulong cur = target;
        for (int guard = 0; guard < 200 && from.TryGetValue(cur, out var f); guard++)
        {
            string type = heap.GetObjectType(cur)?.Name ?? "?";
            // An object a static field holds: the field is the root that matters.
            if (statics.TryGetValue(cur, out var field)) { rootKind = "static " + field; links.Add((type, "")); break; }
            if (f.Parent == 0) { rootKind = f.Via; links.Add((type, "")); break; }
            links.Add((type, f.Via));
            cur = f.Parent;
        }
        links.Reverse();   // root first
        // links[i].Via is the field of links[i - 1] that refers to links[i]. Start from the last
        // AgOpenWeb object before the target (what holds it in the app), unless asked for all.
        int startAt = whole || links.Count < 2 ? 0
            : links.FindLastIndex(links.Count - 2, l => l.Type.StartsWith("AgOpenWeb.", StringComparison.Ordinal));
        var sb = new System.Text.StringBuilder();
        if (startAt <= 0) { startAt = 0; sb.Append('(').Append(rootKind).Append(") "); }
        else sb.Append("… ");
        for (int i = startAt; i < links.Count; i++)
        {
            sb.Append(Short(links[i].Type));
            if (i + 1 < links.Count) sb.Append('.').Append(links[i + 1].Via).Append(" > ");
        }
        return sb.ToString();
    }

    /// <summary>The objects AgOpenWeb's (and the harness's) static fields hold: address to
    /// "Type.field".</summary>
    private static Dictionary<ulong, string> StaticHolders(ClrRuntime rt)
    {
        var d = new Dictionary<ulong, string>();
        foreach (var domain in rt.AppDomains)
            foreach (var module in domain.Modules)
            {
                if (module.Name is not { } n || !Path.GetFileName(n).StartsWith("AgOpenWeb", StringComparison.Ordinal)) continue;
                foreach (var type in module.EnumerateTypesWithStaticFields())
                    foreach (var f in type.StaticFields)
                    {
                        if (!f.IsObjectReference) continue;
                        try
                        {
                            var o = f.ReadObject(domain);
                            if (!o.IsNull) d.TryAdd(o.Address, Short(type.Name ?? "?") + "." + f.Name);
                        }
                        catch { /* a static not initialised yet */ }
                    }
            }
        return d;
    }

    /// <summary>A type name without its namespaces (generic arguments too).</summary>
    public static string Short(string type) =>
        System.Text.RegularExpressions.Regex.Replace(type, @"(?<![\w])([A-Za-z_][\w]*\.)+(?=[A-Za-z_<])", "");

    public static HeapCensus Failed(string label, string error) => new(label) { Error = error };

    /// <summary>The types that grew from <paramref name="before"/> to <paramref name="after"/>,
    /// biggest growth in bytes first.</summary>
    public static List<(string Type, long DCount, long DBytes, long Count, long Bytes)> Growth(HeapCensus before, HeapCensus after, int top = 15)
    {
        var list = new List<(string, long, long, long, long)>();
        foreach (var (name, a) in after.Types)
        {
            before.Types.TryGetValue(name, out var b);
            long dc = a.Count - (b?.Count ?? 0), db = a.Bytes - (b?.Bytes ?? 0);
            if (db > 0 || dc > 0) list.Add((name, dc, db, a.Count, a.Bytes));
        }
        return list.OrderByDescending(x => x.Item3).Take(top).ToList();
    }
}
