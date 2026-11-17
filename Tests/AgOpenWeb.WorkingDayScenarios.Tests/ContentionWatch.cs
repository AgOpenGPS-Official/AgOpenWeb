// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace AgOpenWeb.WorkingDayScenarios.Tests;

/// <summary>
/// Which lock a slow control tick waited for: the runtime's contention events (start: the lock
/// object and the thread holding it; stop: how long the wait took), each wait of a millisecond
/// or more kept with the lock's name when it is one of the backend's (<see cref="Register"/>:
/// the lock fields of the services the view model holds, and of the services they hold).
/// The events reach the listener a little after the wait, so the waits are joined with the
/// slow moments when the report is written (<see cref="During"/>), not as they happen.
/// </summary>
public sealed class ContentionWatch : EventListener
{
    public static readonly ContentionWatch Instance = new();

    private const long ContentionKeyword = 0x4000;
    private readonly ConcurrentDictionary<long, (DateTime Start, nint Obj, ulong Owner)> _open = new();
    private readonly List<(DateTime Start, double Ms, string Lock, ulong Owner, long Waiter)> _waits = new();
    private readonly object _gate = new();
    private volatile (string Name, object Obj)[] _locks = Array.Empty<(string, object)>();

    private ContentionWatch() { }

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(source, EventLevel.Informational, (EventKeywords)ContentionKeyword);
    }

    /// <summary>Name the lock fields of <paramref name="root"/>'s services, and of the services
    /// those hold (a new backend, the next morning, registers again).</summary>
    public void Register(object root)
    {
        var found = new List<(string, object)>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Walk(object o, int depth)
        {
            if (!seen.Add(o)) return;
            for (var t = o.GetType(); t != null && t != typeof(object); t = t.BaseType)
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    object? v;
                    try { v = f.GetValue(o); } catch { continue; }
                    if (v == null) continue;
                    if (f.FieldType == typeof(object) || v is System.Threading.Lock)
                        found.Add((o.GetType().Name + "." + f.Name, v));
                    else if (depth > 0 && !f.FieldType.IsValueType && v.GetType().Namespace?.StartsWith("AgOpenWeb.", StringComparison.Ordinal) == true)
                        Walk(v, depth - 1);
                }
        }
        Walk(root, 2);
        _locks = found.ToArray();
        LiveLog.Write(string.Create(CultureInfo.InvariantCulture, $"[wait] contention watch on: {_locks.Length} lock fields of the backend's services named, waits of 1 ms or more kept"));
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (e.EventName == null || e.Payload == null) return;
        if (e.EventName.StartsWith("ContentionStart", StringComparison.Ordinal))
        {
            nint obj = 0; ulong owner = 0;
            if (e.PayloadNames is { } names)
            {
                int i = names.IndexOf("AssociatedObjectID");
                if (i >= 0) obj = (nint)Number(e.Payload[i]);
                i = names.IndexOf("LockOwnerThreadID");
                if (i >= 0) owner = Number(e.Payload[i]);
            }
            _open[e.OSThreadId] = (e.TimeStamp, obj, owner);
        }
        else if (e.EventName.StartsWith("ContentionStop", StringComparison.Ordinal))
        {
            if (!_open.TryRemove(e.OSThreadId, out var s)) return;
            double ms = (e.TimeStamp - s.Start).TotalMilliseconds;
            if (e.PayloadNames is { } names && names.IndexOf("DurationNs") is var i and >= 0)
                ms = Number(e.Payload[i]) / 1e6;
            if (ms < 1) return;
            string name = Name(s.Obj);
            if (ms >= 5)
                LiveLog.Write(string.Create(CultureInfo.InvariantCulture, $"[wait] {ms:0.0} ms for {name} (OS thread {e.OSThreadId} waited; OS thread {s.Owner} held it)"));
            lock (_gate)
            {
                if (_waits.Count >= 20_000) _waits.RemoveRange(0, 10_000);
                _waits.Add((s.Start.ToUniversalTime(), ms, name, s.Owner, e.OSThreadId));
            }
        }
    }

    // A payload number: the object's address comes as a pointer (IntPtr), which Convert can't
    // read (it threw on every wait, and no wait was ever kept).
    private static ulong Number(object? v) => v switch
    {
        IntPtr p => (ulong)(long)p,
        UIntPtr u => (ulong)u,
        ulong l => l,
        long l => (ulong)l,
        uint n => n,
        int n => (ulong)n,
        _ => Convert.ToUInt64(v, CultureInfo.InvariantCulture),
    };

    // The lock's name by its address now: a lock object of a long-lived service sits in the
    // oldest generation and rarely moves, so the address the event gave still matches.
    private string Name(nint obj)
    {
        if (obj == 0) return "a lock (no object given)";
        foreach (var (name, o) in _locks)
        {
            object x = o;
            if (Unsafe.As<object, nint>(ref x) == obj) return name;
        }
        return "a lock not among the services' fields";
    }

    /// <summary>The waits that overlapped [<paramref name="startUtc"/>, + <paramref name="ms"/>],
    /// longest first, as text ("" when none).</summary>
    public string During(DateTime startUtc, double ms)
    {
        var end = startUtc.AddMilliseconds(ms);
        List<(DateTime Start, double Ms, string Lock, ulong Owner, long Waiter)> hit;
        lock (_gate)
            hit = _waits.Where(w => w.Start < end && w.Start.AddMilliseconds(w.Ms) > startUtc).OrderByDescending(w => w.Ms).Take(3).ToList();
        return string.Join("; ", hit.Select(w => string.Create(CultureInfo.InvariantCulture,
            $"a thread waited {w.Ms:0.0} ms for {w.Lock} (held by OS thread {w.Owner})")));
    }
}
