// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services.Profile;

/// <summary>What an AgOpenGPS profile import converted, newest AgOpenGPS file first.</summary>
public sealed record AgOpenGpsImport(IReadOnlyList<string> Vehicles, IReadOnlyList<string> Tools, IReadOnlyList<string> Skipped)
{
    public static readonly AgOpenGpsImport None = new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
    public bool Any => Vehicles.Count + Tools.Count > 0;
}

/// <summary>
/// One-way import of AgOpenGPS 6.8.2+ profiles, the reverse of its <c>FormConvertProfiles</c>.
/// A farmer moving over copies AgOpenGPS's <c>VehicleProfiles</c>, <c>ToolProfiles</c> and
/// <c>Environment</c> folders into AgOpenWeb's data folder, beside <c>Fields</c>; each tractor
/// becomes a vehicle profile (with its AutoSteer settings) and each implement a tool profile, the
/// environment's U-turn, tram and GPS settings going to each (from <c>environment.xml</c>, or
/// <c>DefaultEnvironment.xml</c> on a fresh install, as AgOpenGPS reads them). A profile AgOpenWeb already has by
/// that name is left alone. As with an AgOpenGPS field, the copied files are deleted once
/// converted, so a profile the farmer deletes later does not come back; AgOpenGPS's own folder is
/// never touched.
/// </summary>
public static class AgOpenGpsProfileImport
{
    public const string VehicleProfilesFolder = "VehicleProfiles";
    public const string ToolProfilesFolder = "ToolProfiles";
    public const string EnvironmentFolder = "Environment";
    public const string EnvironmentFile = "environment.xml";
    /// <summary>The environment file of a fresh AgOpenGPS 6.8.2+ install: <c>environment.xml</c> is
    /// made only when a combined profile from before 6.8.2 is converted.</summary>
    public const string DefaultEnvironmentFile = "DefaultEnvironment.xml";

    /// <summary>The guidance gains, the steering dead zone and the nudge steps: AgOpenGPS 6.8.2+
    /// keeps them with the implement, AgOpenWeb with the vehicle. They are taken from the
    /// implement AgOpenGPS saved last, the one in use. The U-turn compensation too, where the
    /// implement has it (AgOpenGPS after 6.8.6; before, the environment's stands).</summary>
    private static readonly string[] GuidanceKeys =
    {
        "setVehicle_isStanleyUsed", "setVehicle_goalPointLookAheadHold", "setVehicle_goalPointLookAheadMult",
        "setVehicle_goalPointAcquireFactor", "stanleyDistanceErrorGain", "stanleyHeadingErrorGain",
        "stanleyIntegralGainAB", "purePursuitIntegralGainAB", "setAS_uTurnCompensation",
        "setAS_deadZoneHeading", "setAS_deadZoneDelay", "setAS_snapDistance", "setAS_snapDistanceRef",
    };

    private const string HitchKey = "setVehicle_hitchLength";

    /// <param name="dataRoot">AgOpenWeb's data folder (the one holding <c>Vehicles</c> and <c>Fields</c>).</param>
    /// <param name="vehiclesDirectory">Where vehicle profiles are written.</param>
    /// <param name="tools">The tool profile service the implements are saved with.</param>
    /// <param name="saveAutoSteer">Writes a vehicle's AutoSteer settings beside its profile.</param>
    public static AgOpenGpsImport Run(string dataRoot, string vehiclesDirectory, IToolProfileService tools, Action<string, AutoSteerConfig> saveAutoSteer)
    {
        var vehicleFiles = XmlFilesNewestFirst(Path.Combine(dataRoot, VehicleProfilesFolder));
        var toolFiles = XmlFilesNewestFirst(Path.Combine(dataRoot, ToolProfilesFolder));
        if (vehicleFiles.Count + toolFiles.Count == 0)
            return AgOpenGpsImport.None;

        var environment = TryRead(EnvironmentPath(dataRoot)) ?? new Dictionary<string, string>();
        var fromImplements = new Dictionary<string, string>();
        if (toolFiles.Count > 0 && TryRead(toolFiles[0]) is { } inUse)
            foreach (var key in GuidanceKeys)
                if (inUse.TryGetValue(key, out var value)) fromImplements[key] = value;
        // The tractor's hitch points: AgOpenGPS keeps them as its implements' hitch lengths,
        // AgOpenWeb with the tractor (#547). The drawbar pin from the trailed implement saved last;
        // the rear 3-point and the front hitch from the rear- and front-mounted implements saved
        // last, each fixed implement then its AgOpenGPS distance less that hitch (AgOpenGpsSettings).
        var hitches = new Dictionary<string, string>();
        foreach (var file in toolFiles)
        {
            if (TryRead(file) is not { } tool || !tool.TryGetValue(HitchKey, out var hitch)) continue;
            string key = IsTrue(tool, "setTool_isToolTrailing") || IsTrue(tool, "setTool_isToolTBT") ? HitchKey
                : IsTrue(tool, "setTool_isToolFront") ? AgOpenGpsSettings.FrontHitchKey
                : AgOpenGpsSettings.ThreePointHitchKey;
            hitches.TryAdd(key, hitch);
        }
        foreach (var (k, v) in hitches) fromImplements[k] = v;

        var vehicles = new List<string>();
        var toolNames = new List<string>();
        var skipped = new List<string>();

        foreach (var file in vehicleFiles)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (VehicleProfileJsonService.Exists(vehiclesDirectory, name) || TryRead(file) is not { } own)
            {
                skipped.Add(VehicleProfilesFolder + "/" + Path.GetFileName(file));
                continue;
            }
            try
            {
                var store = new ConfigurationStore();
                AgOpenGpsSettings.ApplyToStore(Merge(environment, fromImplements, own), name, file, store);
                VehicleProfileJsonService.Save(vehiclesDirectory, name, store);
                saveAutoSteer(name, store.AutoSteer);
            }
            catch (Exception)
            {
                skipped.Add(VehicleProfilesFolder + "/" + Path.GetFileName(file));
                continue;
            }
            vehicles.Add(name);
            DeleteWithSibling(file);
        }

        var existingTools = tools.GetAvailableProfiles();
        foreach (var file in toolFiles)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (existingTools.Contains(name, StringComparer.OrdinalIgnoreCase) || TryRead(file) is not { } own)
            {
                skipped.Add(ToolProfilesFolder + "/" + Path.GetFileName(file));
                continue;
            }
            try
            {
                var store = new ConfigurationStore();
                // The tractor's 3-point and front hitch go with each implement, so a fixed one's
                // distance is from them (the drawbar is the implement's own hitch length here).
                var tractorHitches = new Dictionary<string, string>();
                foreach (var key in new[] { AgOpenGpsSettings.ThreePointHitchKey, AgOpenGpsSettings.FrontHitchKey })
                    if (hitches.TryGetValue(key, out var h)) tractorHitches[key] = h;
                AgOpenGpsSettings.ApplyToStore(Merge(environment, tractorHitches, own), name, file, store);
                tools.Save(name, store);
            }
            catch (Exception)
            {
                skipped.Add(ToolProfilesFolder + "/" + Path.GetFileName(file));
                continue;
            }
            toolNames.Add(name);
            DeleteWithSibling(file);
        }

        if (vehicles.Count + toolNames.Count > 0)
        {
            // Both copies: the one not read is AgOpenGPS's own leftover, of no use here.
            TryDelete(Path.Combine(dataRoot, EnvironmentFolder, EnvironmentFile));
            TryDelete(Path.Combine(dataRoot, EnvironmentFolder, DefaultEnvironmentFile));
        }
        return new AgOpenGpsImport(vehicles, toolNames, skipped);
    }

    /// <summary>The environment file AgOpenGPS itself loads (its <c>Settings.Load</c>):
    /// <c>environment.xml</c> when there is one, else <c>DefaultEnvironment.xml</c>.</summary>
    private static string EnvironmentPath(string dataRoot)
    {
        string env = Path.Combine(dataRoot, EnvironmentFolder, EnvironmentFile);
        return File.Exists(env) ? env : Path.Combine(dataRoot, EnvironmentFolder, DefaultEnvironmentFile);
    }

    /// <summary>A profile's <c>.xml</c> files, the one AgOpenGPS saved last first (ties by name).</summary>
    private static List<string> XmlFilesNewestFirst(string dir)
    {
        if (!Directory.Exists(dir)) return new List<string>();
        return Directory.EnumerateFiles(dir)
            .Where(f => string.Equals(Path.GetExtension(f), ".xml", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenBy(f => f, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsTrue(Dictionary<string, string> settings, string key)
        => settings.TryGetValue(key, out var text) && bool.TryParse(text, out var v) && v;

    private static Dictionary<string, string>? TryRead(string path)
    {
        try { return File.Exists(path) ? AgOpenGpsSettings.Read(path) : null; }
        catch (Exception) { return null; }
    }

    /// <summary>The settings of several files, a later file winning on the same name.</summary>
    private static Dictionary<string, string> Merge(params Dictionary<string, string>[] files)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in files)
            foreach (var kv in f)
                merged[kv.Key] = kv.Value;
        return merged;
    }

    /// <summary>The converted file and AgOpenGPS's <c>.last</c> copy of it.</summary>
    private static void DeleteWithSibling(string file)
    {
        TryDelete(file);
        TryDelete(Path.ChangeExtension(file, ".last"));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception) { }
    }
}
