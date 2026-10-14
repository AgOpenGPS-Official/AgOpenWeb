// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.IO;
using System.Text.Json;
using AgOpenWeb.Models.Configuration;

namespace AgOpenWeb.Services.Tram;

/// <summary>
/// Per-field persistence of the tram state that belongs to a field
/// (TramConfig.json in the field directory): these load when a field opens and
/// save when it closes, alongside the tram <c>Systems</c> (TramSystems.json).
/// What the Tool panel's Tram tab edits is NOT written here: tram width, pass
/// count, display and first pass follow the implement and are saved with the
/// tool profile (#330). <c>Passes</c>, <c>DisplayMode</c> and <c>StartPass</c>
/// mirror <c>ConfigStore.Guidance</c> via the Guidance↔Tram sync, and
/// double-sourcing them would fight that.
/// </summary>
public static class TramConfigFileService
{
    private const string FileName = "TramConfig.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private sealed class TramConfigDto
    {
        public bool IsOuterInverted { get; set; }
        public double Alpha { get; set; } = 0.8;
        public bool IsDisplayTramControl { get; set; } = true;
        public bool IsEnabled { get; set; }
    }

    public static void Save(string fieldDirectory, TramConfig tram)
    {
        try
        {
            var dto = new TramConfigDto
            {
                IsOuterInverted = tram.IsOuterInverted,
                Alpha = tram.Alpha,
                IsDisplayTramControl = tram.IsDisplayTramControl,
                IsEnabled = tram.IsEnabled,
            };
            File.WriteAllText(Path.Combine(fieldDirectory, FileName),
                JsonSerializer.Serialize(dto, Options));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Tram] Save config failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Apply the field's saved tram scalars onto <paramref name="tram"/>. When
    /// the file is absent (older field, or never set), the scalars reset to
    /// defaults so a previous field's values don't bleed across.
    /// </summary>
    public static void Load(string fieldDirectory, TramConfig tram)
    {
        var path = Path.Combine(fieldDirectory, FileName);
        TramConfigDto dto;
        if (File.Exists(path))
        {
            try
            {
                dto = JsonSerializer.Deserialize<TramConfigDto>(File.ReadAllText(path), Options)
                      ?? new TramConfigDto();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Tram] Load config failed: {ex.Message}");
                dto = new TramConfigDto();
            }
        }
        else
        {
            dto = new TramConfigDto();
        }

        tram.IsOuterInverted = dto.IsOuterInverted;
        tram.Alpha = dto.Alpha;
        tram.IsDisplayTramControl = dto.IsDisplayTramControl;
        tram.IsEnabled = dto.IsEnabled;
    }
}
