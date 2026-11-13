// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Tool;

namespace AgOpenWeb.Services.Profile;

/// <summary>
/// Profile format 3 (#547, NonTracked/HITCH_MODEL_PLAN.md): the tractor's hitch points are the
/// vehicle's (drawbar, rear 3-point, front 3-point) and a fixed tool keeps only its distance from
/// its hitch, as ISO 11783-10 chains a device's connector and elements. Format 2 kept one number
/// per tool, the rear axle → working point. Reading a format-2 tool converts it against the vehicle
/// loaded with it, so the tool stays exactly where it was.
/// </summary>
public static class HitchMigration
{
    /// <summary>The profile format this build writes.</summary>
    public const int CurrentFormatVersion = 3;

    /// <summary>
    /// A format-2 tool's <c>HitchLength</c> (rear axle → working point, positive, ahead for a front
    /// tool) as format 3: the tool's distance from the vehicle's hitch, worked out so the working
    /// point stays where it was. The tool's type flags are already set. The vehicle's hitch is
    /// filled in when it has none: a vehicle file from before the drawbar moved to the vehicle
    /// (no hitch at all) takes the tool's value for its drawbar and 3-point, as that split did; a
    /// front tool on a vehicle with no front hitch gives it the tool's value. Returns what was
    /// done, for the log and the operator.
    /// </summary>
    public static string FromAxleToWorkingPoint(ConfigurationStore store, double axleToWorkingPoint)
    {
        var v = store.Vehicle;
        var t = store.Tool;
        double old = Math.Abs(axleToWorkingPoint);
        string? note = null;
        if (double.IsNaN(v.HitchLength)) v.HitchLength = old;
        if (double.IsNaN(v.ThreePointHitchLength)) v.ThreePointHitchLength = v.HitchLength;

        if (RigGeometry.Mount(t) == ToolMount.FrontFixed)
        {
            if (v.FrontHitchLength <= 0)
            {
                v.FrontHitchLength = old;
                note = string.Create(CultureInfo.InvariantCulture,
                    $"the front hitch is set to {old:0.00} m ahead of the rear axle, the tool's working point at it");
            }
            t.WorkingPointFromHitch = old - v.FrontHitchLength;
        }
        else
        {
            t.WorkingPointFromHitch = old - v.ThreePointHitchLength;
        }
        return note ?? string.Create(CultureInfo.InvariantCulture,
            $"the tool's working point is now measured from the tractor's hitch: {t.WorkingPointFromHitch:0.00} m");
    }

    /// <summary>
    /// A profile file's <c>formatVersion</c>; 0 when it has none or cannot be read (no file, damaged).
    /// </summary>
    public static int FormatVersionOf(string path)
    {
        if (!File.Exists(path)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return 0;
            foreach (var p in doc.RootElement.EnumerateObject())
                if (string.Equals(p.Name, "formatVersion", StringComparison.OrdinalIgnoreCase)
                    && p.Value.TryGetInt32(out int v))
                    return v;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        return 0;
    }

    /// <summary>A profile written by a newer build: this build leaves it as it is (as #825's fields and jobs).</summary>
    public static bool IsNewerFormat(string path) => FormatVersionOf(path) > CurrentFormatVersion;
}
