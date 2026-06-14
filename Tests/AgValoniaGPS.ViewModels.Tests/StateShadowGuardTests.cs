using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using AgValoniaGPS.Models.Configuration;
using AgValoniaGPS.Models.State;
using AgValoniaGPS.ViewModels;
using NUnit.Framework;

namespace AgValoniaGPS.ViewModels.Tests;

/// <summary>
/// SoT guard (CONFIG_STATE_AUDIT §12.6). A MainViewModel private field whose type
/// is a domain type ALSO held by a central store (ApplicationState sub-states or
/// ConfigurationStore sub-configs) is a potential single-source-of-truth shadow —
/// the class of cruft that caused the field-geometry duplication. Every such field
/// must be classified below as either genuine VM-local state or a known shadow
/// tracked for cleanup. A NEW unclassified one fails this test, so shadows can't
/// silently regrow. (Name-divergent shadows on primitives, e.g. _activeSections vs
/// SectionState.ActiveSectionCount, can't be caught mechanically — those stay in
/// the §12 catalog. This guard covers the high-value DOMAIN-typed shadows.)
/// </summary>
[TestFixture]
public class StateShadowGuardTests
{
    // fieldName -> classification. "VM-LOCAL: why" = legitimate; "SHADOW §12.x: ..." = known cruft.
    private static readonly Dictionary<string, string> Classified = new()
    {
        // --- Genuine VM-local: no central-store home (editor / preview / in-flight / cache) ---
        ["_cachedClipPath"] = "VM-LOCAL: boundary-clip render cache",
        ["_drawnCurvePoints"] = "VM-LOCAL: in-progress AB-curve drawing buffer",
        ["_recordedCurvePoints"] = "VM-LOCAL: in-progress curve recording buffer",
        ["_contourRecordingPoints"] = "VM-LOCAL: in-progress contour recording buffer",
        ["_recPathRecordingPoints"] = "VM-LOCAL: in-progress recorded-path buffer (committed to RecordedPathState on save)",
        ["_lastContourPoint"] = "VM-LOCAL: contour recording cursor",
        ["_lastCurvePoint"] = "VM-LOCAL: curve recording cursor",
        ["_lastRecPathPoint"] = "VM-LOCAL: recorded-path recording cursor",
        ["_headlandPoint1Position"] = "VM-LOCAL: headland editor handle",
        ["_headlandPoint2Position"] = "VM-LOCAL: headland editor handle",
        ["_headlandSelectedMarkers"] = "VM-LOCAL: headland editor selection markers",
        ["_headlandPreviewLine"] = "VM-LOCAL: uncommitted headland preview (not the active headland)",
        ["_previousHeadlandLine"] = "VM-LOCAL: headland undo snapshot",
        ["_lastMirroredDisplayTrack"] = "VM-LOCAL: dedup cache for last track pushed to the map",
        ["_lastMirroredBaseTrack"] = "VM-LOCAL: dedup cache for last track pushed to the map",
        ["_lastMirroredNextTrack"] = "VM-LOCAL: dedup cache for last track pushed to the map",
        ["_selectedTrack"] = "VM-LOCAL: sole home for track selection (FieldState.SelectedTrack deleted §12.2)",
        ["_simulatorLocalPlane"] = "VM-LOCAL: simulator's own plane; may overlap State.Field.LocalPlane when a field is open — review under §12.4",

        // No domain shadows remain — the field-geometry cluster (incl. _activeField)
        // was collapsed to State.Field in §12.1. New entries here should be VM-LOCAL.
    };

    [Test]
    public void MainViewModel_DomainTypedFields_AreClassified()
    {
        var central = CollectCentralStoreDomainTypeNames();

        var candidates = typeof(MainViewModel)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(f => !f.IsDefined(typeof(CompilerGeneratedAttribute), false))
            .Where(f => DomainTypeNames(f.FieldType).Any(central.Contains))
            .Select(f => $"{f.Name}  ({Pretty(f.FieldType)})")
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var candidateNames = candidates.Select(c => c.Split("  ")[0]).ToList();
        var unclassified = candidates.Where(c => !Classified.ContainsKey(c.Split("  ")[0])).ToList();
        var stale = Classified.Keys.Where(k => !candidateNames.Contains(k)).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(unclassified, Is.Empty,
                $"\n{unclassified.Count} VM field(s) hold a central-store DOMAIN type — classify each in " +
                "StateShadowGuardTests.Classified (\"VM-LOCAL: why\" or \"SHADOW §12.x: ...\"):\n  " +
                string.Join("\n  ", unclassified) + "\n");
            Assert.That(stale, Is.Empty,
                "Classified field(s) no longer exist — remove from the allowlist:\n  " + string.Join("\n  ", stale));
        });
    }

    private static HashSet<string> CollectCentralStoreDomainTypeNames()
    {
        var names = new HashSet<string>();
        foreach (var store in new object[] { new ApplicationState(), ConfigurationStore.Instance })
        {
            foreach (var subProp in store.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                object? sub;
                try { sub = subProp.GetValue(store); } catch { continue; }
                var subType = sub?.GetType();
                if (subType?.Namespace?.StartsWith("AgValoniaGPS.Models", StringComparison.Ordinal) != true) continue;
                foreach (var p in subType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    foreach (var n in DomainTypeNames(p.PropertyType))
                        names.Add(n);
            }
        }
        return names;
    }

    // Domain type FullName(s) for a member type: the type itself if it's one of our
    // model types, plus collection element types (so List<Vec3> contributes Vec3).
    private static IEnumerable<string> DomainTypeNames(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (IsDomain(t)) yield return t.FullName!;
        if (t.IsGenericType)
            foreach (var arg in t.GetGenericArguments())
            {
                var a = Nullable.GetUnderlyingType(arg) ?? arg;
                if (IsDomain(a)) yield return a.FullName!;
            }
    }

    private static bool IsDomain(Type t) =>
        !t.IsPrimitive && !t.IsEnum && t != typeof(string) && t != typeof(decimal) && t != typeof(DateTime)
        && t.Namespace?.StartsWith("AgValoniaGPS.Models", StringComparison.Ordinal) == true;

    private static string Pretty(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (!t.IsGenericType) return t.Name;
        return $"{t.Name.Split('`')[0]}<{string.Join(",", t.GetGenericArguments().Select(a => a.Name))}>";
    }
}
