// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Headland;
using AgOpenWeb.Models.Job;
using AgOpenWeb.Models.Guidance;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Models.Timing;
using AgOpenWeb.Models.YouTurn;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Fields;
using AgOpenWeb.Services.YouTurn;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Models.GPS;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Track;
using AgOpenWeb.Models.State;
using AgOpenWeb.Models.Communication;
using AgOpenWeb.Models.Ntrip;
using AgOpenWeb.Models.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AgOpenWeb.ViewModels;

/// <summary>
/// The steps of the field session (MainViewModel.FieldSession.cs), each domain's open beside its
/// close, so a missing close is seen next to its open. Their order is declared in the session.
/// The bodies are what OpenFieldCoreAsync and ClearFieldState ran, moved as they were.
/// </summary>
public partial class MainViewModel
{
    // ── The busy overlay and the open flag ────────────────────────────────────────────────────────

    private async Task OpenShowBusy(FieldOpening o)
    {
        // Force UI to render busy overlay
        await _dispatcher.InvokeAsync(() => { }, UiDispatcherPriority.Render);
        await Task.Delay(50);
    }

    private Task OpenMarkFieldOpen(FieldOpening o)
    {
        var fieldPath = o.FieldPath;
        // Update field state. CurrentFieldName is a pass-through over
        // State.Field.ActiveField.Name, set via SetActiveField below.
        IsFieldOpen = true;
        FieldsRootDirectory = Path.GetDirectoryName(fieldPath) ?? string.Empty;
        _gpsPipelineService.SetHasActiveField(true);
        return Task.CompletedTask;
    }

    private void CloseMarkFieldClosed()
    {
        IsFieldOpen = false;
        _gpsPipelineService.SetHasActiveField(false);
    }

    private async Task OpenSettle(FieldOpening o)
    {
        // Let GPS events propagate through the UI
        await _dispatcher.InvokeAsync(() => { }, UiDispatcherPriority.Render);
        await Task.Delay(100);
    }

    private Task OpenAnnounce(FieldOpening o)
    {
        var field = o.Field;
        var fieldName = o.FieldName;
        var fieldPath = o.FieldPath;
        // Notify subscribers that the field is fully loaded
        FieldFullyLoaded?.Invoke(field);

        StatusMessage = $"Opened field: {fieldName}";

        // A newer AgOpenWeb's field: this build reads it but writes nothing to it, so it
        // can't overwrite what it doesn't understand (GeoJsonFieldService.CurrentFormatVersion).
        if (Services.GeoJson.GeoJsonFieldService.IsNewerFormat(fieldPath))
            ReportFailure("This field was saved by a newer AgOpenWeb: changes to its boundaries, tracks, flags and headland are not saved. Update AgOpenWeb to edit it.");
        return Task.CompletedTask;
    }

    // ── The field file and its origin ─────────────────────────────────────────────────────────────

    private Task OpenLoadField(FieldOpening o)
    {
        var fieldPath = o.FieldPath;
        var fieldName = o.FieldName;
        // Load the field from field.geojson, importing (and deleting) an AgOpenGPS
        // field's files first.
        Field? loadedField = null;
        try
        {
            var fieldInfo = loadedField = _fieldService.LoadField(fieldPath);

            // Recovery: if the field has no origin or a zero origin, fall
            // back to field.origin — a separate file written at field-
            // create time and never touched by close-save. Fields
            // corrupted by the pre-#270 save-with-zero bug can be healed
            // this way; next close writes the real origin back to
            // field.geojson.
            if (fieldInfo.Origin == null
                || (fieldInfo.Origin.Latitude == 0 && fieldInfo.Origin.Longitude == 0))
            {
                var originBackupPath = Path.Combine(fieldPath, "field.origin");
                if (File.Exists(originBackupPath))
                {
                    var originLine = File.ReadAllText(originBackupPath).Trim();
                    var parts = originLine.Split(',');
                    if (parts.Length == 2
                        && double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var backupLat)
                        && double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var backupLon)
                        && (backupLat != 0 || backupLon != 0)
                        && backupLat >= -90 && backupLat <= 90
                        && backupLon >= -180 && backupLon <= 180)
                    {
                        fieldInfo.Origin = new Position
                        {
                            Latitude = backupLat,
                            Longitude = backupLon,
                        };
                        _logger.LogDebug($"[Field] Recovered origin from field.origin backup: {backupLat}, {backupLon}");
                    }
                }
            }

            if (fieldInfo.Origin != null)
            {
                SetFieldOrigin(fieldInfo.Origin.Latitude, fieldInfo.Origin.Longitude);
                _logger.LogDebug($"[Field] Set origin: {State.Field.OriginLatitude}, {State.Field.OriginLongitude}");
                // Only reposition the simulator if the field has a real (non-zero)
                // georeference. Fields that were never georeferenced persist an
                // origin of (0, 0), which otherwise clobbers the user's
                // simulator coords (saved to appsettings on window close).
                if (State.Field.OriginLatitude != 0 || State.Field.OriginLongitude != 0)
                    SetSimulatorCoordinates(State.Field.OriginLatitude, State.Field.OriginLongitude);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Field] Could not load field {FieldName}", fieldName);
        }
        o.LoadedField = loadedField;
        return Task.CompletedTask;
    }

    private Task OpenActiveField(FieldOpening o)
    {
        var loadedField = o.LoadedField;
        var fieldName = o.FieldName;
        var fieldPath = o.FieldPath;
        var boundary = o.Boundary;
        // The loaded field becomes the active one, so the close-save writes back what was
        // read (convergence, dates). Origin must be the one in effect (possibly recovered
        // from field.origin): a (0, 0) default here would be written back on close and
        // corrupt the field for every future session.
        var field = loadedField ?? new Field();
        field.Name = fieldName;
        field.DirectoryPath = fieldPath;
        field.Boundary = boundary;
        field.Origin = new Position
        {
            Latitude = State.Field.OriginLatitude,
            Longitude = State.Field.OriginLongitude,
        };

        // Update field service (triggers OnActiveFieldChanged for state sync only)
        _fieldService.SetActiveField(field);
        o.Field = field;
        return Task.CompletedTask;
    }

    private void CloseActiveField()
    {
        // CurrentFieldName clears via the pass-through when SetActiveField(null)
        // runs below (State.Field.ActiveField → null).
        // Update field service
        _fieldService.SetActiveField(null);
    }

    private Task OpenRememberField(FieldOpening o)
    {
        var fieldName = o.FieldName;
        // Save as last opened field (persistent state → appstate.json)
        PersistentState.LastOpenedField = fieldName;
        _persistentStateService.Save();
        return Task.CompletedTask;
    }

    private Task OpenSimulator(FieldOpening o)
    {
        // Force simulator ticks so vehicle position updates to field origin
        if (IsSimulatorEnabled)
        {
            _simulatorService.Tick(0);
            _simulatorService.Tick(0);
        }
        return Task.CompletedTask;
    }

    // ── Boundary ──────────────────────────────────────────────────────────────────────────────────

    private Task OpenBoundary(FieldOpening o)
    {
        var loadedField = o.LoadedField;
        var boundary = loadedField?.Boundary;
        if (boundary != null)
        {
            // Migrate legacy/imported dense boundaries to normalized resolution
            // in-memory so derived geometry (tram/headland), inside-tests, and
            // rendering operate on a sane point count. Persisted on the next
            // explicit boundary save. See Plans/Completed/BOUNDARY_RESOLUTION_NORMALIZATION.md.
            NormalizeBoundaryInPlace(boundary);
            SetCurrentBoundary(boundary);
            CenterMapOnBoundary(boundary);

            var boundaryAreas = new List<double> { boundary.AreaHectares * 10000 };
            _fieldStatistics.UpdateBoundaryAreas(boundaryAreas);
            OnPropertyChanged(nameof(BoundaryAreaDisplay));
        }
        o.Boundary = boundary;
        return Task.CompletedTask;
    }

    private void CloseBoundary()
    {
        // Clear boundary
        SetCurrentBoundary(null);
    }

    // ── Imagery ───────────────────────────────────────────────────────────────────────────────────

    private Task OpenImagery(FieldOpening o)
    {
        var fieldPath = o.FieldPath;
        var boundary = o.Boundary;
        // Load background image
        LoadBackgroundImage(fieldPath, boundary);
        return Task.CompletedTask;
    }

    private void CloseImagery()
    {
        // Clear background. The web client draws whatever State.Field.Imagery says, so
        // null it here or the closed field's picture stays on screen until the next open.
        _mapService.ClearBackground();
        State.Field.Imagery = null;
    }

    // ── Headland ──────────────────────────────────────────────────────────────────────────────────

    private Task OpenHeadland(FieldOpening o)
    {
        var field = o.Field;
        // Load headland
        LoadHeadlandFromField(field);
        return Task.CompletedTask;
    }

    private void CloseHeadland()
    {
        // Clear headland
        LoadHeadlandFromField(null);
    }

    // ── Tracks and U-turn ─────────────────────────────────────────────────────────────────────────

    private Task OpenTracks(FieldOpening o)
    {
        var field = o.Field;
        // Load tracks
        LoadTracksFromField(field);
        return Task.CompletedTask;
    }

    private void CloseTracks()
    {
        // Clear tracks (State.Field.Tracks mirrors SavedTracks via the ctor subscription)
        SavedTracks.Clear();
        SelectedTrack = null;
        IsAutoTrackEnabled = false; // AgOpenGPS turns Auto Track off with the job
    }

    private void CloseYouTurn()
    {
        // Clear U-turn state
        ClearYouTurnState();
    }

    // ── Recorded path ─────────────────────────────────────────────────────────────────────────────

    private Task OpenRecordedPath(FieldOpening o)
    {
        var fieldPath = o.FieldPath;
        // Load the recorded path in use
        LoadRecPathFromField(fieldPath);
        return Task.CompletedTask;
    }

    private void CloseRecordedPath()
    {
        // The recorded path ends with the field: a replay left driving would put the Auto
        // master back on at the next fix, right after the sections go off below.
        EndRecordedPathWithField();
    }

    // ── Flags ─────────────────────────────────────────────────────────────────────────────────────

    private Task OpenFlags(FieldOpening o)
    {
        var fieldPath = o.FieldPath;
        // Load flags from Flags.txt (#107 — they used to carry over from the previous field)
        LoadFlagsFromField(fieldPath);
        return Task.CompletedTask;
    }

    private void CloseFlags()
    {
        // Clear flags (without saving — IsFieldOpen is already false) so they don't carry
        // over into the next field (#107)
        LoadFlagsFromField(null);
    }

    // ── Contours ──────────────────────────────────────────────────────────────────────────────────

    private Task OpenContours(FieldOpening o)
    {
        var fieldPath = o.FieldPath;
        // Contour strips (#110)
        LoadContoursFromField(fieldPath);
        return Task.CompletedTask;
    }

    private void CloseContours()
    {
        // Contour (#110): save finished strips, then forget them and turn contour off
        // (AgOpenGPS FileSaveContour, ResetContour, isContourBtnOn = false).
        SaveContoursToField();
        ExportFieldIsoXml(); // AgOpenGPS ExportFieldAs_ISOXMLv4 on close (#110)
        LoadContoursFromField(null);
        IsContourModeOn = false;
    }

    // ── Job and coverage ──────────────────────────────────────────────────────────────────────────

    private Task OpenJob(FieldOpening o)
    {
        var fieldName = o.FieldName;
        // Establish (or resume) the active job before any coverage paint
        // is allowed. Coverage now lives under <field>/jobs/<task>/.
        //
        // The pending-intent fields below are set by the OpenField*Async
        // overloads so the dialog can express "open field A and start
        // job J2" without mutating JobService.ActiveJob before
        // CloseFieldAsync runs — doing so used to misroute the previous
        // job's in-memory coverage into the new job's folder.
        Job? activeJob = null;
        var newIntent = _pendingNewJob;
        var resumeIntent = _pendingResumeJob;
        var fieldOnly = _pendingFieldOnly;
        _pendingNewJob = null;
        _pendingResumeJob = null;
        _pendingFieldOnly = false;

        if (fieldOnly)
        {
            _logger.LogDebug("[Job] Field-only open; no job will be activated");
        }
        else if (newIntent != null)
        {
            activeJob = _jobService.CreateJob(
                newIntent.Value.FieldName,
                newIntent.Value.WorkType,
                newIntent.Value.Notes,
                newIntent.Value.TaskName);
        }
        else if (resumeIntent != null)
        {
            _jobService.ResumeJob(resumeIntent.Value.FieldName, resumeIntent.Value.TaskName);
            activeJob = _jobService.ActiveJob!;
        }
        else
        {
            activeJob = _jobService.GetOrCreateDefaultJob(fieldName);
        }
        if (activeJob != null)
        {
            _logger.LogDebug("[Job] Active job: {TaskName} (status={Status})",
                activeJob.TaskName, activeJob.Status);
        }
        o.ActiveJob = activeJob;
        return Task.CompletedTask;
    }

    private async Task OpenCoverage(FieldOpening o)
    {
        var fieldPath = o.FieldPath;
        var activeJob = o.ActiveJob;
        // Load coverage (shows busy overlay — pixel buffer callback needs UI thread for bitmap access)
        State.UI.BusyMessage = "Loading coverage...";
        await _dispatcher.InvokeAsync(() => { }, UiDispatcherPriority.Render);

        if (activeJob != null)
        {
            _coverageMapService.LoadFromFile(fieldPath, activeJob.TaskName);
            _logger.LogDebug($"[Coverage] Loaded coverage from {fieldPath} job={activeJob.TaskName}");
            // A newer AgOpenWeb's job: its coverage isn't read, and nothing is written to it
            // (JobJsonService.IsNewerFormat), so the work done now is not kept.
            if (Services.Fields.JobJsonService.IsNewerFormat(fieldPath, activeJob.TaskName))
                ReportFailure("This job was saved by a newer AgOpenWeb: its coverage is not shown and work done in it is not saved. Start a new job, or update AgOpenWeb.");
        }
        else
        {
            // Field-only opens skip coverage load — but they still need to
            // fire CoverageUpdated(IsFullReload=true) so the map control's
            // handler runs ClearCoveragePixels + MarkCoverageFullRebuildNeeded
            // during the busy overlay. Resume Job gets this for free via
            // LoadFromFile; without parity here, the coverage-bitmap
            // rebuild gets deferred until the first non-stationary GPS
            // cycle and lands on the UI thread as a 2-3 s freeze.
            _coverageMapService.ClearAll();
        }
        RefreshCoverageStatistics();
    }

    private Task OpenCoverageAutosave(FieldOpening o)
    {
        // Start periodic coverage autosave. The timer no-ops on each
        // tick when ActiveJob is null (field-only open), so it's safe
        // to start unconditionally here.
        StartCoverageAutosave();
        return Task.CompletedTask;
    }

    private void CloseCoverageAutosave()
    {
        // Stop autosave (idempotent if already stopped). Belt-and-braces
        // for callers that bypass CloseFieldAsync.
        StopCoverageAutosave();
    }

    private void CloseCoverage()
    {
        // Clear coverage
        _coverageMapService.ClearAll();
    }

    // ── Tram lines ────────────────────────────────────────────────────────────────────────────────

    private Task OpenTram(FieldOpening o)
    {
        var fieldPath = o.FieldPath;
        // Tram lines aren't saved (parsing a large saved file was costing seconds on the
        // open critical path, see Plans/Completed/BOUNDARY_RESOLUTION_NORMALIZATION.md):
        // they are regenerated from the tram systems and the track, below, once those are
        // loaded. Start clean so a prior field's lines don't linger (the track restore above
        // built lines with the previous field's systems).
        _tramLineService.Clear();
        _mapService.SetTramLines(
            _tramLineService.OuterBoundaryTrack,
            _tramLineService.InnerBoundaryTrack,
            _tramLineService.ParallelTramLines);

        // Load field-scoped tram scalar settings (resets to defaults if absent).
        Services.Tram.TramConfigFileService.Load(fieldPath, ConfigStore.Tram);

        // Load tram systems
        try
        {
            var systems = Services.Tram.TramSystemFileService.Load(fieldPath);
            ConfigStore.Tram.Systems.Clear();
            foreach (var sys in systems)
                ConfigStore.Tram.Systems.Add(sys);
            _hasTramSystemsEverUsed = systems.Count > 0;
            if (systems.Count > 0)
                _logger.LogDebug($"[Tram] Loaded {systems.Count} tram systems");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load tram systems");
        }

        // The trams come back with the field, as AgOpenGPS reads its Tram.txt back on open
        // (checklist 6.10): the same lines, regenerated from the systems and the restored
        // track. Nothing is built for a field that had neither.
        if (ConfigStore.Tram.Systems.Count > 0 || SelectedTrack != null)
            UpdateTramLines(SelectedTrack);
        return Task.CompletedTask;
    }

    private void CloseTram()
    {
        // Clear tram lines and the field's tram systems (saved above). Field open clears them
        // too, but between close and the next open the map was still drawing the old lines.
        _tramLineService.Clear();
        _tramSystemLineRanges.Clear();
        ConfigStore.Tram.Systems.Clear();
    }

    // ── NTRIP ─────────────────────────────────────────────────────────────────────────────────────

    private Task OpenNtrip(FieldOpening o)
    {
        var fieldName = o.FieldName;
        // Handle NTRIP profile
        _ = HandleNtripProfileForFieldAsync(fieldName);
        return Task.CompletedTask;
    }

    // ── Elevation log ─────────────────────────────────────────────────────────────────────────────

    private Task OpenElevationLog(FieldOpening o)
    {
        // Sync elevation log enabled state from config
        _elevationLogService.IsEnabled = _configStore.Display.ElevationLogEnabled;
        return Task.CompletedTask;
    }

    // ── AutoSteer, sections and recordings (close only) ───────────────────────────────────────────

    private void CloseAutoSteer()
    {
        // Disengage autosteer first so the cycle stops emitting steer commands
        // before the track / boundary / U-turn state vanishes out from under it.
        // Without this the tractor keeps executing the last-sent steer angle
        // (visible as "keeps circling" when closing mid-U-turn).
        if (IsAutoSteerEngaged)
        {
            IsAutoSteerEngaged = false;
            _autoSteerService.Disengage();
            SyncGuidanceStateToPipeline();
            LogAutoSteerOff("field closed");
        }
    }

    private void CloseSections()
    {
        // The sections too, switched on by hand included: with no field the section bar is
        // gone, so the operator could not switch them off, and the module went on getting
        // the on bits every tick (PGN 239, 229). Opening another field closes this one here
        // first, so that is covered too (web client flows F6, F7).
        SwitchAllSectionsOff();
    }

    private void CloseRecordings()
    {
        // A boundary being recorded and a line being drawn, driven or recorded belong to this
        // field: end them unsaved. Left running, their points went on into the next field and
        // were saved there as its boundary or line, in the wrong field's coordinates.
        if (_boundaryRecordingService.State != BoundaryRecordingState.Idle)
            _boundaryRecordingService.CancelRecording();
        IsBoundaryRecording = false;
        IsBoundaryPlayerPanelVisible = false;
        if (CurrentABCreationMode != ABCreationMode.None)
            CancelABCreationCommand?.Execute(null);
    }
}
