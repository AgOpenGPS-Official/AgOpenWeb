// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgValoniaGPS.Models.Configuration;

namespace AgValoniaGPS.Services.Profile;

/// <summary>
/// v2 tool profile serializer. Persists the Tool config + Sections array.
/// All other config groups (Vehicle, Guidance, YouTurn, General) live in
/// the parallel vehicle profile (<see cref="VehicleProfileJsonService"/>);
/// see issue #346 / Plans/VEHICLE_TOOL_SPLIT_PLAN.md.
/// </summary>
public static class ToolProfileJsonService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static bool Exists(string toolsDirectory, string profileName)
        => File.Exists(GetJsonPath(toolsDirectory, profileName));

    public static void Save(string toolsDirectory, string profileName, ConfigurationStore store)
    {
        if (!Directory.Exists(toolsDirectory))
            Directory.CreateDirectory(toolsDirectory);

        var dto = ToDto(store);
        var json = JsonSerializer.Serialize(dto, Options);
        File.WriteAllText(GetJsonPath(toolsDirectory, profileName), json);
    }

    public static bool Load(string toolsDirectory, string profileName, ConfigurationStore store)
    {
        var path = GetJsonPath(toolsDirectory, profileName);
        if (!File.Exists(path))
            return false;

        var json = File.ReadAllText(path);
        var dto = JsonSerializer.Deserialize<ToolProfileDto>(json, Options);
        if (dto == null)
            return false;

        ApplyDtoToStore(dto, profileName, path, store);
        return true;
    }

    private static string GetJsonPath(string toolsDirectory, string profileName)
        => Path.Combine(toolsDirectory, $"{profileName}.json");

    // ── Store → DTO ────────────────────────────────────────────────────

    internal static ToolProfileDto ToDto(ConfigurationStore store)
    {
        // Section widths (cm) are the runtime source of truth; persist live
        // widths sized to the actual section count and derive Positions for
        // backward-compat readers (#section-width-persistence).
        int numSections = Math.Max(1, store.NumSections);
        var sectionWidths = new double[numSections];
        for (int i = 0; i < numSections; i++)
            sectionWidths[i] = store.Tool.GetSectionWidth(i);

        double totalMeters = 0;
        for (int i = 0; i < numSections; i++)
            totalMeters += sectionWidths[i] / 100.0;
        var sectionPositions = new double[numSections + 1];
        sectionPositions[0] = -totalMeters / 2.0 + store.Tool.Offset;
        for (int i = 0; i < numSections; i++)
            sectionPositions[i + 1] = sectionPositions[i] + sectionWidths[i] / 100.0;

        return new ToolProfileDto
        {
            FormatVersion = 2,
            Tool = new ToolDto
            {
                Width = store.Tool.Width,
                Overlap = store.Tool.Overlap,
                Offset = store.Tool.Offset,
                HitchLength = store.Tool.HitchLength,
                TrailingHitchLength = store.Tool.TrailingHitchLength,
                TankTrailingHitchLength = store.Tool.TankTrailingHitchLength,
                TrailingToolToPivotLength = store.Tool.TrailingToolToPivotLength,
                IsToolTrailing = store.Tool.IsToolTrailing,
                IsToolTBT = store.Tool.IsToolTBT,
                IsToolRearFixed = store.Tool.IsToolRearFixed,
                IsToolFrontFixed = store.Tool.IsToolFrontFixed,
                MinCoverage = store.Tool.MinCoverage,
                IsMultiColoredSections = store.Tool.IsMultiColoredSections,
                IsSectionsNotZones = store.Tool.IsSectionsNotZones,
                IsSectionOffWhenOut = store.Tool.IsSectionOffWhenOut,
                IsHeadlandSectionControl = store.Tool.IsHeadlandSectionControl,
                LookAheadOn = store.Tool.LookAheadOnSetting,
                LookAheadOff = store.Tool.LookAheadOffSetting,
                TurnOffDelay = store.Tool.TurnOffDelay,
                DefaultSectionWidth = store.Tool.DefaultSectionWidth,
                SlowSpeedCutoff = store.Tool.SlowSpeedCutoff,
                CoverageMargin = store.Tool.CoverageMargin,
                Zones = store.Tool.Zones,
                ZoneRanges = (int[])store.Tool.ZoneRanges.Clone(),
                IsWorkSwitchEnabled = store.Tool.IsWorkSwitchEnabled,
                IsWorkSwitchActiveLow = store.Tool.IsWorkSwitchActiveLow,
                IsWorkSwitchManualSections = store.Tool.IsWorkSwitchManualSections,
                IsSteerSwitchEnabled = store.Tool.IsSteerSwitchEnabled,
                IsSteerSwitchManualSections = store.Tool.IsSteerSwitchManualSections,
                SectionColors = (uint[])store.Tool.SectionColors.Clone(),
            },
            Sections = new SectionsDto
            {
                Count = numSections,
                Positions = sectionPositions,
                Widths = sectionWidths,
            },
        };
    }

    // ── DTO → Store ────────────────────────────────────────────────────

    internal static void ApplyDtoToStore(ToolProfileDto dto, string profileName, string filePath, ConfigurationStore store)
    {
        // Tool config — defaults match ToolConfig in-memory initializers so a
        // partial file (older v2 profile, missing field) yields the same
        // behavior as an unmodified store.
        store.Tool.Width = dto.Tool?.Width ?? 6.0;
        store.Tool.Overlap = dto.Tool?.Overlap ?? 0.0;
        store.Tool.Offset = dto.Tool?.Offset ?? 0.0;
        store.Tool.HitchLength = dto.Tool?.HitchLength ?? 1.8;
        // Legacy AOG sign convention used negative TrailingHitchLength; canonicalize.
        store.Tool.TrailingHitchLength = Math.Abs(dto.Tool?.TrailingHitchLength ?? 2.5);
        store.Tool.TankTrailingHitchLength = dto.Tool?.TankTrailingHitchLength ?? 3.0;
        store.Tool.TrailingToolToPivotLength = dto.Tool?.TrailingToolToPivotLength ?? 0.0;
        store.Tool.IsToolTrailing = dto.Tool?.IsToolTrailing ?? false;
        store.Tool.IsToolTBT = dto.Tool?.IsToolTBT ?? false;
        store.Tool.IsToolRearFixed = dto.Tool?.IsToolRearFixed ?? true;
        store.Tool.IsToolFrontFixed = dto.Tool?.IsToolFrontFixed ?? false;
        store.Tool.LookAheadOnSetting = dto.Tool?.LookAheadOn ?? 0.0;
        store.Tool.LookAheadOffSetting = dto.Tool?.LookAheadOff ?? 0.0;
        store.Tool.TurnOffDelay = dto.Tool?.TurnOffDelay ?? 0.0;
        store.Tool.MinCoverage = dto.Tool?.MinCoverage ?? 100;
        store.Tool.IsMultiColoredSections = dto.Tool?.IsMultiColoredSections ?? false;
        store.Tool.IsSectionsNotZones = dto.Tool?.IsSectionsNotZones ?? true;
        store.Tool.IsSectionOffWhenOut = dto.Tool?.IsSectionOffWhenOut ?? true;
        store.Tool.IsHeadlandSectionControl = dto.Tool?.IsHeadlandSectionControl ?? true;
        store.Tool.DefaultSectionWidth = dto.Tool?.DefaultSectionWidth ?? 100.0;
        store.Tool.SlowSpeedCutoff = dto.Tool?.SlowSpeedCutoff ?? 0.5;
        store.Tool.CoverageMargin = dto.Tool?.CoverageMargin ?? 5.0;
        store.Tool.Zones = dto.Tool?.Zones ?? 2;
        if (dto.Tool?.ZoneRanges != null && dto.Tool.ZoneRanges.Length == 9)
            store.Tool.ZoneRanges = (int[])dto.Tool.ZoneRanges.Clone();
        store.Tool.IsWorkSwitchEnabled = dto.Tool?.IsWorkSwitchEnabled ?? false;
        store.Tool.IsWorkSwitchActiveLow = dto.Tool?.IsWorkSwitchActiveLow ?? false;
        store.Tool.IsWorkSwitchManualSections = dto.Tool?.IsWorkSwitchManualSections ?? false;
        store.Tool.IsSteerSwitchEnabled = dto.Tool?.IsSteerSwitchEnabled ?? false;
        store.Tool.IsSteerSwitchManualSections = dto.Tool?.IsSteerSwitchManualSections ?? false;
        if (dto.Tool?.SectionColors != null && dto.Tool.SectionColors.Length == 16)
            store.Tool.SectionColors = (uint[])dto.Tool.SectionColors.Clone();

        // Section count first so width derivation knows how many to populate.
        store.NumSections = dto.Sections?.Count ?? 1;
        var sectionPositions = new double[17];
        if (dto.Sections?.Positions != null)
            Array.Copy(dto.Sections.Positions, sectionPositions, Math.Min(dto.Sections.Positions.Length, 17));
        store.SectionPositions = sectionPositions;

        int restoredNum = Math.Max(1, store.NumSections);
        if (dto.Sections?.Widths != null && dto.Sections.Widths.Length >= restoredNum)
        {
            for (int i = 0; i < restoredNum; i++)
                store.Tool.SetSectionWidth(i, dto.Sections.Widths[i]);
        }
        else if (dto.Sections?.Positions != null && dto.Sections.Positions.Length >= restoredNum + 1)
        {
            for (int i = 0; i < restoredNum; i++)
            {
                double widthM = dto.Sections.Positions[i + 1] - dto.Sections.Positions[i];
                if (widthM > 0)
                    store.Tool.SetSectionWidth(i, widthM * 100.0);
            }
        }

        // Profile metadata
        store.ActiveToolProfileName = profileName;
        store.ActiveToolProfilePath = filePath;
    }

    // ── DTOs ───────────────────────────────────────────────────────────

    internal class ToolProfileDto
    {
        public int FormatVersion { get; set; }
        public ToolDto? Tool { get; set; }
        public SectionsDto? Sections { get; set; }
    }

    internal class ToolDto
    {
        public double Width { get; set; }
        public double Overlap { get; set; }
        public double Offset { get; set; }
        public double HitchLength { get; set; }
        public double TrailingHitchLength { get; set; }
        public double TankTrailingHitchLength { get; set; }
        public double TrailingToolToPivotLength { get; set; }
        public bool IsToolTrailing { get; set; }
        public bool IsToolTBT { get; set; }
        public bool IsToolRearFixed { get; set; }
        public bool IsToolFrontFixed { get; set; }
        public int MinCoverage { get; set; }
        public bool IsMultiColoredSections { get; set; }
        public bool IsSectionsNotZones { get; set; }
        public bool IsSectionOffWhenOut { get; set; }
        public bool IsHeadlandSectionControl { get; set; }
        public double LookAheadOn { get; set; }
        public double LookAheadOff { get; set; }
        public double TurnOffDelay { get; set; }
        public double? DefaultSectionWidth { get; set; }
        public double? SlowSpeedCutoff { get; set; }
        public double? CoverageMargin { get; set; }
        public int? Zones { get; set; }
        public int[]? ZoneRanges { get; set; }
        public bool? IsWorkSwitchEnabled { get; set; }
        public bool? IsWorkSwitchActiveLow { get; set; }
        public bool? IsWorkSwitchManualSections { get; set; }
        public bool? IsSteerSwitchEnabled { get; set; }
        public bool? IsSteerSwitchManualSections { get; set; }
        public uint[]? SectionColors { get; set; }
    }

    internal class SectionsDto
    {
        public int Count { get; set; }
        public double[] Positions { get; set; } = Array.Empty<double>();
        public double[]? Widths { get; set; }
    }
}
