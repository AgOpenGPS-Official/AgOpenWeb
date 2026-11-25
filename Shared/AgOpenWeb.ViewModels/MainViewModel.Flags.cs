// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using AgOpenWeb.Models;
using AgOpenWeb.Services;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AgOpenWeb.ViewModels;

// Marker flags: placing, editing, showing and saving. The list itself is FieldFlags.
public partial class MainViewModel
{
    private readonly FieldFlags _flags = new();
    public ObservableCollection<Flag> Flags => _flags.Items;

    public ICommand? PlaceRedFlagCommand { get; private set; }
    public ICommand? PlaceFlagHereCommand { get; private set; }

    private void InitializeFlagCommands()
    {
        PlaceRedFlagCommand = new RelayCommand(() => PlaceFlag(FlagColor.Red));
        PlaceFlagHereCommand = new RelayCommand(() => PlaceFlag(null));
    }

    /// <summary>
    /// Place a flag at the given world coordinates (from map click).
    /// </summary>
    public void PlaceFlagAtWorldPosition(double easting, double northing, FlagColor? color = null)
    {
        if (!FieldIsOpen()) return;
        var flag = _flags.Add(easting, northing, color);
        UpdateFlagsOnMap();
        StatusMessage = $"{flag.FlagColor} flag '{flag.Name}' at E:{easting:F1} N:{northing:F1}";
    }

    private void PlaceFlag(FlagColor? color)
    {
        if (!FieldIsOpen()) return;
        if (!HasGpsPosition(Easting, Northing))
        {
            ReportFailure("No GPS position - cannot place flag");
            return;
        }
        PlaceFlagAtWorldPosition(Easting, Northing, color);
    }

    public void UpdateFlagsOnMap()
    {
        var flags = Flags.Select(f =>
            (f.Easting, f.Northing, f.FlagColor.ToString(), f.Name)).ToList();
        _mapService.SetFlags(flags);
        // Mirror a render snapshot into FieldState (the SoT the web-UI projector reads).
        State.Field.Flags = Flags
            .Select(f => new Models.State.FlagMarker(f.Easting, f.Northing, Flag.ColorToHex(f.FlagColor), f.Name))
            .ToList();
        SaveFlagsToField();
    }

    // Set while flags are being (re)loaded or cleared with the field, so UpdateFlagsOnMap
    // doesn't write a half-loaded list — or an empty one over the file on close.
    private bool _suppressFlagSave;

    /// <summary>
    /// Persist the open field's flags (#107: flags were never saved, so they vanished on
    /// restart). Every flag change goes through UpdateFlagsOnMap, which calls this.
    /// </summary>
    private void SaveFlagsToField()
    {
        if (_suppressFlagSave || !IsFieldOpen) return;
        var dir = _fieldService.ActiveField?.DirectoryPath;
        if (string.IsNullOrEmpty(dir)) return;
        try
        {
            Services.GeoJson.GeoJsonFieldService.SaveFlags(dir, Flags.ToList());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Flags] Failed to save flags");
        }
    }

    /// <summary>Replace the flags with the field's (empty when <paramref name="fieldPath"/> is null).</summary>
    private void LoadFlagsFromField(string? fieldPath)
    {
        _suppressFlagSave = true;
        try
        {
            var loaded = new List<Flag>();
            if (!string.IsNullOrEmpty(fieldPath))
            {
                try
                {
                    loaded = Services.GeoJson.GeoJsonFieldService.LoadFlags(fieldPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Flags] Failed to load flags");
                }
            }
            _flags.Replace(loaded);
            UpdateFlagsOnMap();
        }
        finally { _suppressFlagSave = false; }
    }

    // ---- Remote/web flag-list operations (index into Flags, matching the projected list) ----
    public void RenameFlagAt(int index, string name)
    {
        if (_flags.Rename(index, name)) UpdateFlagsOnMap();
    }

    public void SetFlagColorAt(int index, FlagColor color)
    {
        if (_flags.SetColor(index, color)) UpdateFlagsOnMap();
    }

    public void DeleteFlagAt(int index)
    {
        if (_flags.RemoveAt(index) is not { } flag) return;
        UpdateFlagsOnMap();
        StatusMessage = $"Deleted flag '{flag.Name}'";
    }

    public void DeleteAllFlagsRemote()
    {
        int count = Flags.Count;
        _flags.Replace([]);
        UpdateFlagsOnMap();
        StatusMessage = $"Deleted {count} flags";
    }
}
