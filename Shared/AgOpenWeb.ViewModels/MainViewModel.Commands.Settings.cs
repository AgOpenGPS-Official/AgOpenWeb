// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Input;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Logging;
using Microsoft.Extensions.Logging;
using CommunityToolkit.Mvvm.Input;

using CommunityToolkit.Mvvm.ComponentModel;

using AgOpenWeb.Services;

namespace AgOpenWeb.ViewModels;

public partial class MainViewModel
{
    private void InitializeSettingsCommands()
    {
        SetLanguageCommand = new RelayCommand<string>(code =>
        {
            if (string.IsNullOrEmpty(code)) return;
            // Language lives only in AppSettings (no store mirror); set it on the
            // DTO, then persist via SaveAppSettings so the rest of the file stays
            // in sync with the store (avoids writing a stale DTO).
            _settingsService.Settings.Language = code;
            _configurationService.SaveAppSettings();

            // Notify that language changed - Views layer applies via LanguageChanged event
            LanguageChanged?.Invoke(code);

            try
            {
                var culture = new System.Globalization.CultureInfo(code);
                StatusMessage = $"Language: {culture.NativeName}";
            }
            catch
            {
                StatusMessage = $"Language: {code}";
            }
            State.UI.CloseDialog();
        });

        // Offset Fix (#36) - GPS drift compensation
        const double OFFSET_STEP = 0.01; // 1cm per click

        OffsetFixNorthCommand = new RelayCommand(() => ApplyDrift(0, OFFSET_STEP));
        OffsetFixSouthCommand = new RelayCommand(() => ApplyDrift(0, -OFFSET_STEP));
        OffsetFixEastCommand = new RelayCommand(() => ApplyDrift(OFFSET_STEP, 0));
        OffsetFixWestCommand = new RelayCommand(() => ApplyDrift(-OFFSET_STEP, 0));
        OffsetFixZeroCommand = new RelayCommand(() =>
        {
            State.Field.DriftEasting = 0;
            State.Field.DriftNorthing = 0;
            _autoSteerService.SetDriftCompensation(0, 0);
            double headingRad = Heading * Math.PI / 180.0;
            _toolPositionService.ResetTrailingState(
                new Models.Base.Vec3(Easting, Northing, headingRad), headingRad);
            SyncGuidanceStateToPipeline();
        });
    }

    private string _bugReportTitle = string.Empty;
    public string BugReportTitle
    {
        get => _bugReportTitle;
        set { _bugReportTitle = value; OnPropertyChanged(); }
    }

    private string _bugReportDescription = string.Empty;
    public string BugReportDescription
    {
        get => _bugReportDescription;
        set { _bugReportDescription = value; OnPropertyChanged(); }
    }

    public ObservableCollection<BugReportAttachment> BugReportAttachments { get; } = new();

    private void ApplyDrift(double deltaEasting, double deltaNorthing)
    {
        State.Field.DriftEasting += deltaEasting;
        State.Field.DriftNorthing += deltaNorthing;
        _autoSteerService.SetDriftCompensation(State.Field.DriftEasting, State.Field.DriftNorthing);

        // Reset trailing tool at the NEW drifted position.
        // Easting/Northing still reflect the OLD drift (pre-delta), so add the delta.
        double headingRad = Heading * Math.PI / 180.0;
        var driftedPos = new Models.Base.Vec3(
            Easting + deltaEasting,
            Northing + deltaNorthing,
            headingRad);
        _toolPositionService.ResetTrailingState(driftedPos, headingRad);
        SyncGuidanceStateToPipeline();
    }

    /// <summary>
    /// Set the GPS drift offset to absolute values (meters). Used by the remote web
    /// Offset Fix panel's manual Easting/Northing inputs; applies the same trailing-tool
    /// reset + pipeline sync as the directional nudges by routing through the delta path.
    /// </summary>
    public void OffsetFixSet(double easting, double northing)
    {
        // An offset that is no number would put every position the GPS gives at NaN.
        if (!double.IsFinite(easting) || !double.IsFinite(northing)) return;
        ApplyDrift(easting - State.Field.DriftEasting, northing - State.Field.DriftNorthing);
    }
}

// --- Log Viewer (#22) ---
public partial class MainViewModel
{
    public ObservableCollection<LogEntry> FilteredLogEntries { get; } = new();

    public ICommand? OffsetFixNorthCommand { get; private set; }
    public ICommand? OffsetFixSouthCommand { get; private set; }
    public ICommand? OffsetFixEastCommand { get; private set; }
    public ICommand? OffsetFixWestCommand { get; private set; }
    public ICommand? OffsetFixZeroCommand { get; private set; }
}

// --- Flag By Lat/Lon (#23) ---
public partial class MainViewModel
{
    private string _flagLatitudeInput = "";
    public string FlagLatitudeInput
    {
        get => _flagLatitudeInput;
        set => SetProperty(ref _flagLatitudeInput, value);
    }

    private string _flagLongitudeInput = "";
    public string FlagLongitudeInput
    {
        get => _flagLongitudeInput;
        set => SetProperty(ref _flagLongitudeInput, value);
    }

    private string _flagByLatLonError = "";
    public string FlagByLatLonError
    {
        get => _flagByLatLonError;
        set => SetProperty(ref _flagByLatLonError, value);
    }
}

// --- View All Settings (#29) ---
public partial class MainViewModel
{
    public ObservableCollection<SettingsGroupItem> SettingsTree { get; } = new();

    public ICommand? SetLanguageCommand { get; private set; }
}

public class SettingsGroupItem
{
    public string Name { get; }
    public ObservableCollection<SettingsValueItem> Items { get; } = new();

    public SettingsGroupItem(string name) => Name = name;
}

public class SettingsValueItem
{
    public string Name { get; }
    public string Value { get; }

    public SettingsValueItem(string name, string value)
    {
        Name = name;
        Value = value;
    }
}

public class AppDirectoryInfo
{
    public string Name { get; }
    public string Path { get; }
    public bool Exists { get; }

    public AppDirectoryInfo(string name, string path)
    {
        Name = name;
        Path = path;
        Exists = Directory.Exists(path);
    }
}

public record BugReportAttachment(string FileName, string FilePath, string FileSize);
