// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections;
using System.Globalization;
using System.Reflection;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// Every public property of a view model, and of the models and state under it, as
/// <c>path = value</c>, for the characterization tests of the MainViewModel refactor
/// (<c>Plans/MAINVIEWMODEL_REFACTOR_PLAN.md</c>). Models and state are read through (lists item by
/// item, the first <see cref="ItemsRead"/>); services and other objects only by their type. A time
/// reads as "a time" and an id as "an id": they depend on the run, not on the code.
/// </summary>
internal static class VmStateReader
{
    public const int ItemsRead = 20;
    private const int MaxDepth = 6;

    // The clock on the status bar ("HH:mm:ss" from DateTime.Now): it changes with the wall clock,
    // whatever the code does.
    private static readonly HashSet<string> WallClock = new(StringComparer.Ordinal) { "vm.CurrentTime" };

    public static Dictionary<string, string> Read(MainViewModel vm)
    {
        var all = new Dictionary<string, string>(StringComparer.Ordinal);
        Walk(vm, "vm", 0, all, new Dictionary<object, string>(ReferenceEqualityComparer.Instance), throughServices: false);
        foreach (var k in WallClock) all.Remove(k);
        return all;
    }

    /// <summary>
    /// A service's public state as <c>path = value</c>, under <paramref name="path"/>: read as a
    /// view model is, and the service's own types (a section's state) read through too, not only
    /// the models. For the busy-field test, which reads what the services keep as well as the VM.
    /// </summary>
    public static void ReadService(object service, string path, Dictionary<string, string> into) =>
        Walk(service, path, 0, into, new Dictionary<object, string>(ReferenceEqualityComparer.Instance), throughServices: true);

    /// <summary>The paths whose value differs, or that are in one reading only.</summary>
    public static IEnumerable<string> Differences(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Keys.Union(b.Keys).Where(k => !a.TryGetValue(k, out var x) || !b.TryGetValue(k, out var y) || x != y);

    private static void Walk(object obj, string path, int depth, Dictionary<string, string> into, Dictionary<object, string> seen, bool throughServices)
    {
        // An object reached again is the same one as before: say where, so a field kept alive
        // under another name still shows.
        if (seen.TryGetValue(obj, out var first)) { into[path] = "same as " + first; return; }
        seen[obj] = path;
        foreach (var p in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0 || !p.CanRead) continue;
            var t = p.PropertyType;
            if (typeof(System.Windows.Input.ICommand).IsAssignableFrom(t) || typeof(Delegate).IsAssignableFrom(t)) continue;
            object? v;
            try { v = p.GetValue(obj); }
            catch { continue; }
            Value(v, path + "." + p.Name, depth, into, seen, throughServices);
        }
    }

    private static void Value(object? v, string at, int depth, Dictionary<string, string> into, Dictionary<object, string> seen, bool throughServices)
    {
        if (v == null) { into[at] = "null"; return; }
        if (IsValue(v.GetType())) { into[at] = Show(v); return; }
        if (v is IEnumerable e && v is not string)
        {
            var items = e.Cast<object?>().ToList();
            into[at] = $"count {items.Count} #{Hash(items.Select(i => i == null ? "null" : IsValue(i.GetType()) ? Show(i) : i.GetType().Name))}";
            if (depth < MaxDepth)
                for (int i = 0; i < Math.Min(items.Count, ItemsRead); i++)
                    if (items[i] is { } item && !IsValue(item.GetType()) && ReadThrough(item, throughServices))
                        Walk(item, $"{at}[{i}]", depth + 1, into, seen, throughServices);
            return;
        }
        if (depth < MaxDepth && ReadThrough(v, throughServices)) Walk(v, at, depth + 1, into, seen, throughServices);
        else into[at] = v.GetType().Name;
    }

    private static bool ReadThrough(object v, bool throughServices) =>
        IsModel(v) || (throughServices && (v.GetType().Namespace ?? "").StartsWith("AgOpenWeb.Services", StringComparison.Ordinal));

    private static bool IsModel(object v) => (v.GetType().Namespace ?? "").StartsWith("AgOpenWeb.Models", StringComparison.Ordinal);

    private static bool IsValue(Type t) =>
        t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime) ||
        t == typeof(TimeSpan) || t == typeof(Guid) || (Nullable.GetUnderlyingType(t) is { } u && IsValue(u)) ||
        (t.IsValueType && (t.Namespace ?? "").StartsWith("AgOpenWeb.Models", StringComparison.Ordinal));

    public static string Show(object v) => v switch
    {
        DateTime => "a time",
        Guid => "an id", // a field's id is new on every load
        double d => d.ToString("0.###", CultureInfo.InvariantCulture),
        float f => f.ToString("0.###", CultureInfo.InvariantCulture),
        IFormattable fm => fm.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    private static string Hash(IEnumerable<string> items)
    {
        unchecked
        {
            int h = 17;
            foreach (var s in items) foreach (char c in s) h = h * 31 + c;
            return h.ToString("x8", CultureInfo.InvariantCulture);
        }
    }
}
