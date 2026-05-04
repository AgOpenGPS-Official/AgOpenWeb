// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using AgValoniaGPS.Models.Configuration;
using AgValoniaGPS.Services.Interfaces;
using AgValoniaGPS.Services.Profile;

namespace AgValoniaGPS.Services;

/// <summary>
/// Manages tool profiles (Tools/&lt;name&gt;.json). Counterpart to
/// <see cref="VehicleProfileService"/>; together they implement the
/// AgOpenGPS 6.8.2-style vehicle/tool split (#346).
/// </summary>
public class ToolProfileService : IToolProfileService
{
    private readonly ILogger<ToolProfileService> _logger;

    public string ToolsDirectory { get; }

    public ToolProfileService(ILogger<ToolProfileService> logger)
        : this(logger, DefaultToolsDirectory())
    {
    }

    /// <summary>Test seam: redirect ToolsDirectory without env vars.</summary>
    protected ToolProfileService(ILogger<ToolProfileService> logger, string toolsDirectory)
    {
        _logger = logger;
        ToolsDirectory = toolsDirectory;

        if (!Directory.Exists(ToolsDirectory))
            Directory.CreateDirectory(ToolsDirectory);
    }

    private static string DefaultToolsDirectory()
    {
        var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(documentsPath, "AgValoniaGPS", "Tools");
    }

    public List<string> GetAvailableProfiles()
    {
        if (!Directory.Exists(ToolsDirectory))
            return new List<string>();

        return Directory.GetFiles(ToolsDirectory, "*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n)
            .ToList();
    }

    public bool Load(string profileName, ConfigurationStore store)
    {
        try
        {
            return ToolProfileJsonService.Load(ToolsDirectory, profileName, store);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading tool profile '{ProfileName}'", profileName);
            return false;
        }
    }

    public void Save(string profileName, ConfigurationStore store)
    {
        ToolProfileJsonService.Save(ToolsDirectory, profileName, store);
    }

    public void CreateDefaultProfile(string profileName, ConfigurationStore store)
    {
        // Tool defaults (mirrors VehicleProfileService.CreateDefaultProfile but
        // only for tool/section state).
        store.Tool.Width = 6.0;
        store.Tool.Overlap = 0.0;
        store.Tool.Offset = 0.0;
        store.Tool.HitchLength = 1.8;
        store.Tool.TrailingHitchLength = 2.5;
        store.Tool.TankTrailingHitchLength = 3.0;
        store.Tool.TrailingToolToPivotLength = 0.0;
        store.Tool.IsToolTrailing = false;
        store.Tool.IsToolTBT = false;
        store.Tool.IsToolRearFixed = true;
        store.Tool.IsToolFrontFixed = false;
        store.Tool.LookAheadOnSetting = 0.0;
        store.Tool.LookAheadOffSetting = 0.0;
        store.Tool.TurnOffDelay = 0.0;
        store.Tool.MinCoverage = 100;
        store.Tool.IsMultiColoredSections = false;
        store.Tool.IsSectionOffWhenOut = true;
        store.Tool.IsHeadlandSectionControl = true;

        store.NumSections = 1;
        var sectionPositions = new double[17];
        sectionPositions[0] = -3.0;
        sectionPositions[1] = 3.0;
        store.SectionPositions = sectionPositions;

        store.ActiveToolProfileName = profileName;
        store.ActiveToolProfilePath = Path.Combine(ToolsDirectory, $"{profileName}.json");

        Save(profileName, store);
    }
}
