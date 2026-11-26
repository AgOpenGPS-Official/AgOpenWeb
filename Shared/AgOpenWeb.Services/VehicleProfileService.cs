// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Profile;

namespace AgOpenWeb.Services;

/// <summary>
/// Service for managing vehicle profiles.
/// Loads/saves directly to/from ConfigurationStore using JSON format.
/// Supports legacy AgOpenGPS XML import.
/// </summary>
public class VehicleProfileService : IVehicleProfileService
{
    private readonly ILogger<VehicleProfileService> _logger;

    public string VehiclesDirectory { get; }

    public VehicleProfileService(ILogger<VehicleProfileService> logger)
        : this(logger, DefaultVehiclesDirectory())
    {
    }

    /// <summary>
    /// Test seam: lets a test subclass redirect VehiclesDirectory away
    /// from MyDocuments without touching env vars or filesystem mocks.
    /// </summary>
    protected VehicleProfileService(ILogger<VehicleProfileService> logger, string vehiclesDirectory)
    {
        _logger = logger;
        VehiclesDirectory = vehiclesDirectory;

        if (!Directory.Exists(VehiclesDirectory))
        {
            Directory.CreateDirectory(VehiclesDirectory);
        }
    }

    private static string DefaultVehiclesDirectory()
        => Path.Combine(AppDataRoot.Documents, "Vehicles");

    public List<string> GetAvailableProfiles()
    {
        if (!Directory.Exists(VehiclesDirectory))
            return new List<string>();

        var xmlProfiles = Directory.GetFiles(VehiclesDirectory, "*.XML")
            .Select(f => Path.GetFileNameWithoutExtension(f));
        var jsonProfiles = Directory.GetFiles(VehiclesDirectory, "*.json")
            .Where(f => !f.EndsWith(".AutoSteer.json", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetFileNameWithoutExtension(f));

        return xmlProfiles.Concat(jsonProfiles)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n)
            .ToList();
    }

    public bool Load(string profileName, ConfigurationStore store)
    {
        try
        {
            // Prefer v2 vehicle-only format (#346) — JSON with FormatVersion >= 2.
            if (VehicleProfileJsonService.Load(VehiclesDirectory, profileName, store))
                return true;

            // Fall back to v1 combined JSON. V1 hydrates the entire store
            // (Vehicle + Guidance + YouTurn + General + Tool + Sections); the
            // Tool side will be re-written to its own file on next save, and
            // the v1 → v2 migration in ConfigurationService handles the
            // proactive split for users who don't save first.
            if (ProfileJsonServiceV1.Load(VehiclesDirectory, profileName, store))
                return true;

            // Last-resort fall back to legacy AOG XML.
            var filePath = ResolveExistingFile(profileName, ".xml");
            if (filePath == null)
                return false;

            // Build a single dictionary by merging the primary file (legacy combined
            // or 6.8.2 VehicleSettings) with any sibling 6.8.2 split files. The new
            // format keeps the same <setting name="..."> keys as the old combined
            // file — they're just spread across multiple files — so a merged
            // dictionary feeds ApplyXmlSettingsToStore unchanged.
            var settings = ParseSettings(XDocument.Load(filePath));
            MergeSiblingIfPresent(profileName, ".tool.xml", settings);
            MergeSiblingIfPresent(profileName, ".env.xml", settings);

            ApplyXmlSettingsToStore(settings, profileName, filePath, store);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading vehicle profile '{ProfileName}'", profileName);
            return false;
        }
    }

    /// <summary>
    /// Case-insensitively resolve a {profileName}{suffix} file in the Vehicles
    /// directory. Returns the actual on-disk path or null if not found.
    /// Needed because the existing convention is .XML (uppercase) for the
    /// primary file but the 6.8.2 sibling files use .xml (lowercase).
    /// </summary>
    private string? ResolveExistingFile(string profileName, string suffix)
    {
        // Check exact match first (cheapest case on case-sensitive filesystems)
        var primary = Path.Combine(VehiclesDirectory, profileName + suffix);
        if (File.Exists(primary)) return primary;

        var upper = Path.Combine(VehiclesDirectory, profileName + suffix.ToUpperInvariant());
        if (File.Exists(upper)) return upper;

        // Fallback: enumerate the directory once and match case-insensitively
        if (!Directory.Exists(VehiclesDirectory)) return null;
        var target = profileName + suffix;
        foreach (var path in Directory.EnumerateFiles(VehiclesDirectory))
        {
            if (string.Equals(Path.GetFileName(path), target, StringComparison.OrdinalIgnoreCase))
                return path;
        }
        return null;
    }

    private void MergeSiblingIfPresent(string profileName, string suffix, Dictionary<string, string> dict)
    {
        var path = ResolveExistingFile(profileName, suffix);
        if (path == null) return;

        try
        {
            var siblingDoc = XDocument.Load(path);
            foreach (var kv in ParseSettings(siblingDoc))
            {
                // Sibling values take precedence on key collision; with the 6.8.2
                // split there shouldn't be any, but if there is the more-specific
                // file (tool/env) wins.
                dict[kv.Key] = kv.Value;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not parse sibling settings file '{Path}'", path);
        }
    }

    public void Save(string profileName, ConfigurationStore store)
    {
        // v2 vehicle-only format (#346). Tool/Sections are persisted via
        // IToolProfileService — ConfigurationService coordinates both sides.
        VehicleProfileJsonService.Save(VehiclesDirectory, profileName, store);
    }

    public bool Rename(string oldName, string newName)
    {
        if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName))
            return false;

        var oldPath = Path.Combine(VehiclesDirectory, $"{oldName}.json");
        var newPath = Path.Combine(VehiclesDirectory, $"{newName}.json");
        if (!File.Exists(oldPath))
            return false;

        // Allow case-only rename on case-insensitive filesystems.
        bool caseOnly = string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase)
                      && !string.Equals(oldName, newName, StringComparison.Ordinal);
        if (!caseOnly && File.Exists(newPath) &&
            !string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
            return false;

        File.Move(oldPath, newPath, overwrite: caseOnly);
        return true;
    }

    public bool Delete(string profileName)
    {
        if (string.IsNullOrEmpty(profileName))
            return false;
        var path = Path.Combine(VehiclesDirectory, $"{profileName}.json");
        if (!File.Exists(path))
            return false;
        File.Delete(path);
        return true;
    }

    public void CreateDefaultProfile(string profileName, ConfigurationStore store)
    {
        // Reset store to defaults for vehicle-profile-related fields
        store.Vehicle.Name = profileName;
        store.Vehicle.Type = VehicleType.Tractor;
        store.Vehicle.AntennaHeight = 3.0;
        store.Vehicle.AntennaPivot = 0.0;
        store.Vehicle.AntennaOffset = 0.0;
        store.Vehicle.ImuPivot = 0.0;
        store.Vehicle.ImuHeight = 0.0;
        store.Vehicle.ImuOffset = 0.0;
        store.Vehicle.Wheelbase = 2.5;
        store.Vehicle.TrackWidth = 1.8;
        store.Vehicle.HitchLength = 1.8;
        store.Vehicle.ThreePointHitchLength = 1.8;
        store.Vehicle.FrontHitchLength = 0.0;
        store.Vehicle.MaxSteerAngle = 35.0;
        store.Vehicle.MaxAngularVelocity = 35.0;

        store.Guidance.IsPurePursuit = true;
        store.Guidance.GoalPointLookAheadHold = 4.0;
        store.Guidance.GoalPointLookAheadMult = 1.4;
        store.Guidance.GoalPointAcquireFactor = 0.9;
        store.Guidance.MinLookAheadDistance = 2.0;
        store.Guidance.StanleyDistanceErrorGain = 0.8;
        store.Guidance.StanleyHeadingErrorGain = 1.0;
        store.Guidance.StanleyIntegralGainAB = 0.0;
        store.Guidance.PurePursuitIntegralGain = 0.0;
        store.Guidance.UTurnCompensation = 1.0;

        store.Tool.Width = 6.0;
        store.Tool.Overlap = 0.0;
        store.Tool.Offset = 0.0;
        store.Tool.WorkingPointFromHitch = 0.0;
        store.Tool.TrailingHitchLength = 2.5;
        store.Tool.TankTrailingHitchLength = 3.0;
        store.Tool.TrailingToolToPivotLength = 0.0;
        store.Tool.IsToolTrailing = false;
        store.Tool.IsToolTBT = false;
        store.Tool.IsToolRearFixed = true;
        store.Tool.IsToolFrontFixed = false;
        store.Tool.LookAheadOnSetting = Models.Configuration.ToolConfig.DefaultLookAheadOn;
        store.Tool.LookAheadOffSetting = Models.Configuration.ToolConfig.DefaultLookAheadOff;
        store.Tool.TurnOffDelay = 0.0;
        store.Tool.MinCoverage = 100;
        store.Tool.IsMultiColoredSections = false;
        store.Tool.IsSectionOffWhenOut = true;
        store.Tool.IsHeadlandSectionControl = true;

        store.NumSections = 1;
        var sectionPositions = new double[17];
        sectionPositions[0] = -3.0;  // Left edge
        sectionPositions[1] = 3.0;   // Right edge
        store.SectionPositions = sectionPositions;

        store.Ahrs.RollZero = 0.0;
        store.Ahrs.RollFilter = 0.0;
        store.Ahrs.IsRollInvert = false;

        // IsMetric used to be reset here; it now lives in AppSettings and
        // is unaffected by creating a new default vehicle profile.

        store.ActiveVehicleProfileName = profileName;
        store.ActiveVehicleProfilePath = Path.Combine(VehiclesDirectory, $"{profileName}.json");

        // Save the new default profile
        Save(profileName, store);
    }

    #region Legacy XML Parsing

    /// <summary>
    /// Applies parsed XML settings directly to ConfigurationStore.
    /// Used for one-way import from AgOpenGPS XML format.
    /// </summary>
    private static void ApplyXmlSettingsToStore(Dictionary<string, string> settings, string profileName, string filePath, ConfigurationStore store)
        => AgOpenGpsSettings.ApplyToStore(settings, profileName, filePath, store);

    private static Dictionary<string, string> ParseSettings(XDocument doc) => AgOpenGpsSettings.Parse(doc);

    #endregion
}
