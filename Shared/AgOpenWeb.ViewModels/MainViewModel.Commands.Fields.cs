// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml;

using Microsoft.Extensions.Logging;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.IsoXml;
using AgOpenWeb.Models.State;
using AgOpenWeb.Models.Track;
using AgOpenWeb.Services;
using AgOpenWeb.Services.IsoXml;
using CommunityToolkit.Mvvm.Input;

namespace AgOpenWeb.ViewModels;

/// <summary>
/// Field management commands - field selection, creation, import.
/// </summary>
public partial class MainViewModel
{
    private void InitializeFieldCommands()
    {
        // Start Work Session Dialog (#349 M3) — replaces the FieldSelection
        // dialog conceptually, but the legacy dialog is left wired to its
        // own button until M5 cleanup so this can revert without breaking
        // the menu.
        ShowStartWorkSessionDialogCommand = new RelayCommand(() =>
        {
            StartWorkSessionDialogVm = new StartWorkSessionDialogViewModel(
                _fieldService,
                _jobService,
                _settingsService,
                _appState,
                close: () => State.UI.CloseDialog(),
                openField: (path, name) => _ = OpenFieldOnlyAsync(path, name),
                openFieldStartingNewJob: (path, name, workType, notes, taskName) =>
                    _ = OpenFieldStartingNewJobAsync(path, name, workType, notes, taskName),
                openFieldResumingJob: (path, name, taskName) =>
                    _ = OpenFieldResumingJobAsync(path, name, taskName),
                confirm: (msg, action) => ShowConfirmationDialog("Delete Job", msg, action),
                confirmWithOption: (title, msg, checkboxLabel, defaultChecked, action) =>
                    ShowConfirmationDialog(title, msg, checkboxLabel, defaultChecked, action));
            StartWorkSessionDialogVm.Refresh();
            OpenChainDialog(DialogType.StartWorkSession);
        });

        CancelStartWorkSessionDialogCommand = new RelayCommand(() =>
        {
            State.UI.CloseDialog();
        });

        // Resume Job cross-field history dialog (#349 M4).
        ShowResumeJobDialogCommand = new RelayCommand(() =>
        {
            ResumeJobDialogVm = new ResumeJobDialogViewModel(
                _jobService,
                _settingsService,
                close: () => State.UI.CloseDialog(),
                openFieldResumingJob: (path, name, taskName) =>
                    _ = OpenFieldResumingJobAsync(path, name, taskName));
            ResumeJobDialogVm.Refresh();
            OpenChainDialog(DialogType.ResumeJob);
        });

        CancelResumeJobDialogCommand = new RelayCommand(() =>
        {
            State.UI.CloseDialog();
        });

        // Resume Last Job: one-tap reopen of the most recent job across
        // all fields. Short-circuits the picker when the operator just
        // wants to pick up where they left off.
        ResumeLastJobCommand = new RelayCommand(() =>
        {
            var mostRecent = _jobService.ListAllJobs().FirstOrDefault();
            if (mostRecent == null) return;
            var fieldsRoot = _settingsService.Settings.FieldsDirectory;
            var fieldPath = Path.Combine(fieldsRoot, mostRecent.FieldName);
            _ = OpenFieldResumingJobAsync(fieldPath, mostRecent.FieldName, mostRecent.TaskName);
        });

        // Field Selection Dialog
        ShowFieldSelectionDialogCommand = new RelayCommand(() =>
        {
            var fieldsDir = _settingsService.Settings.FieldsDirectory;
            if (string.IsNullOrWhiteSpace(fieldsDir))
            {
                fieldsDir = Path.Combine(
                    AppDataRoot.Documents, "Fields");
            }
            _fieldSelectionDirectory = fieldsDir;
            PopulateAvailableFields(fieldsDir);
            State.UI.ShowDialog(DialogType.FieldSelection);
        });

        CancelFieldSelectionDialogCommand = new RelayCommand(() =>
        {
            State.UI.CloseDialog();
            SelectedFieldInfo = null;
        });

        ConfirmFieldSelectionDialogCommand = new AsyncRelayCommand(async () =>
        {
            if (SelectedFieldInfo == null) return;

            var fieldPath = Path.Combine(_fieldSelectionDirectory, SelectedFieldInfo.Name);
            var fieldName = SelectedFieldInfo.Name;

            // Check if this is a legacy field that will be auto-converted
            bool isLegacy = !File.Exists(Path.Combine(fieldPath, "field.geojson")) &&
                            File.Exists(Path.Combine(fieldPath, "Field.txt"));

            if (isLegacy)
            {
                State.UI.CloseDialog();
                ShowConfirmationDialog(
                    "Import Legacy Field",
                    $"'{fieldName}' uses the legacy AgOpenGPS format. " +
                    "It will be imported and converted to the new format. " +
                    "The original files will be kept. Continue?",
                    () =>
                    {
                        SelectedFieldInfo = null;
                        _ = OpenFieldAsync(fieldPath, fieldName).ContinueWith(_ =>
                            _dispatcher.Post(() =>
                                IsFieldOperationsPanelVisible = false));
                    });
                return;
            }

            State.UI.CloseDialog();
            SelectedFieldInfo = null;

            await OpenFieldAsync(fieldPath, fieldName);
            IsFieldOperationsPanelVisible = false;
        });

        DeleteSelectedFieldCommand = new RelayCommand(() =>
        {
            if (SelectedFieldInfo == null) return;

            var fieldPath = Path.Combine(_fieldSelectionDirectory, SelectedFieldInfo.Name);
            try
            {
                if (Directory.Exists(fieldPath))
                {
                    Directory.Delete(fieldPath, true);
                    StatusMessage = $"Deleted field: {SelectedFieldInfo.Name}";
                    PopulateAvailableFields(_fieldSelectionDirectory);
                    SelectedFieldInfo = null;
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error deleting field: {ex.Message}";
            }
        });

        SortFieldsCommand = new RelayCommand(() =>
        {
            _fieldsSortedAZ = !_fieldsSortedAZ;
            var sorted = _fieldsSortedAZ
                ? AvailableFields.OrderBy(f => f.Name).ToList()
                : AvailableFields.OrderByDescending(f => f.Name).ToList();
            AvailableFields.Clear();
            foreach (var field in sorted)
            {
                AvailableFields.Add(field);
            }
        });

        // New Field Dialog
        ShowNewFieldDialogCommand = new RelayCommand(() =>
        {
            NewFieldLatitude = Latitude != 0 ? Latitude : 40.7128;
            NewFieldLongitude = Longitude != 0 ? Longitude : -74.0060;
            NewFieldName = string.Empty;
            OpenChainDialog(DialogType.NewField);
        });

        CancelNewFieldDialogCommand = new RelayCommand(() =>
        {
            State.UI.CloseDialog();
            NewFieldName = string.Empty;
        });

        ConfirmNewFieldDialogCommand = new AsyncRelayCommand(async () =>
        {
            if (string.IsNullOrWhiteSpace(NewFieldName))
            {
                StatusMessage = "Please enter a field name";
                return;
            }

            var fieldsDir = _settingsService.Settings.FieldsDirectory;
            if (string.IsNullOrWhiteSpace(fieldsDir))
            {
                fieldsDir = Path.Combine(
                    AppDataRoot.Documents, "Fields");
            }

            var fieldPath = Path.Combine(fieldsDir, NewFieldName);
            if (Directory.Exists(fieldPath))
            {
                StatusMessage = $"Field '{NewFieldName}' already exists";
                return;
            }

            try
            {
                Directory.CreateDirectory(fieldPath);
                WriteNewFieldSkeleton(fieldPath, NewFieldName, NewFieldLatitude, NewFieldLongitude);

                // Create elevation log header if enabled (#120)
                if (_configStore.Display.ElevationLogEnabled)
                    _elevationLogService.CreateHeader(fieldPath, NewFieldLatitude, NewFieldLongitude);

                var name = NewFieldName;
                await OpenCreatedFieldAsync(fieldPath, name);
                StatusMessage = $"Created field: {name}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error creating field: {ex.Message}";
            }
        });

        // From Existing Field Dialog
        ShowFromExistingFieldDialogCommand = new RelayCommand(() =>
        {
            var fieldsDir = _settingsService.Settings.FieldsDirectory;
            if (string.IsNullOrWhiteSpace(fieldsDir))
            {
                fieldsDir = Path.Combine(
                    AppDataRoot.Documents, "Fields");
            }
            _fieldSelectionDirectory = fieldsDir;
            PopulateAvailableFields(fieldsDir);

            CopyFlags = true;
            CopyMapping = true;
            CopyHeadland = true;
            CopyLines = true;
            FromExistingFieldName = string.Empty;
            FromExistingSelectedField = null;

            if (AvailableFields.Count > 0)
            {
                FromExistingSelectedField = AvailableFields[0];
            }

            OpenChainDialog(DialogType.FromExistingField);
        });

        CancelFromExistingFieldDialogCommand = new RelayCommand(() =>
        {
            State.UI.CloseDialog();
            FromExistingSelectedField = null;
            FromExistingFieldName = string.Empty;
        });

        ConfirmFromExistingFieldDialogCommand = new AsyncRelayCommand(async () =>
        {
            if (FromExistingSelectedField == null)
            {
                StatusMessage = "Please select a field to copy from";
                return;
            }

            var newFieldName = FromExistingFieldName.Trim();
            if (string.IsNullOrWhiteSpace(newFieldName))
            {
                StatusMessage = "Please enter a field name";
                return;
            }

            var fieldsDir = _settingsService.Settings.FieldsDirectory;
            if (string.IsNullOrWhiteSpace(fieldsDir))
            {
                fieldsDir = Path.Combine(
                    AppDataRoot.Documents, "Fields");
            }

            var sourcePath = Path.Combine(fieldsDir, FromExistingSelectedField.Name);
            var newFieldPath = Path.Combine(fieldsDir, newFieldName);

            if (Directory.Exists(newFieldPath))
            {
                StatusMessage = $"Field '{newFieldName}' already exists";
                return;
            }

            try
            {
                // Build the new field from the source's real files (#107: this used to copy
                // *.json names nothing writes, giving an essentially empty field), then open it
                // through the normal path — which closes (and saves) the current field first.
                FieldCopyService.CreateFromExisting(_fieldService, sourcePath, newFieldPath, newFieldName,
                    CopyFlags, CopyMapping, CopyHeadland, CopyLines);
                await OpenCreatedFieldAsync(newFieldPath, newFieldName);
                StatusMessage = $"Created field from existing: {newFieldName}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error creating field: {ex.Message}";
            }
        });

        // Field name helper commands
        AppendVehicleNameCommand = new RelayCommand(() =>
        {
            var vehicleName = Vehicle.VehicleTypeDisplayName;
            if (!string.IsNullOrWhiteSpace(vehicleName))
            {
                FromExistingFieldName = (FromExistingFieldName + " " + vehicleName).Trim();
            }
        });

        AppendDateCommand = new RelayCommand(() =>
        {
            var dateStr = DateTime.Now.ToString("yyyy-MMM-dd");
            FromExistingFieldName = (FromExistingFieldName + " " + dateStr).Trim();
        });

        AppendTimeCommand = new RelayCommand(() =>
        {
            var timeStr = DateTime.Now.ToString("HH-mm");
            FromExistingFieldName = (FromExistingFieldName + " " + timeStr).Trim();
        });

        BackspaceFieldNameCommand = new RelayCommand(() =>
        {
            if (FromExistingFieldName.Length > 0)
            {
                FromExistingFieldName = FromExistingFieldName.Substring(0, FromExistingFieldName.Length - 1);
            }
        });

        ToggleCopyFlagsCommand = new RelayCommand(() => CopyFlags = !CopyFlags);
        ToggleCopyMappingCommand = new RelayCommand(() => CopyMapping = !CopyMapping);
        ToggleCopyHeadlandCommand = new RelayCommand(() => CopyHeadland = !CopyHeadland);
        ToggleCopyLinesCommand = new RelayCommand(() => CopyLines = !CopyLines);

        // KML Import Dialog
        ShowKmlImportDialogCommand = new RelayCommand(() =>
        {
            _kmlImportToExistingField = false;
            PopulateAvailableKmlFiles();
            KmlImportFieldName = string.Empty;
            KmlBoundaryPointCount = 0;
            KmlCenterLatitude = 0;
            KmlCenterLongitude = 0;
            _kmlBoundaryPoints.Clear();
            _kmlParsedPolygons.Clear();
            SelectedKmlFile = null;

            if (AvailableKmlFiles.Count > 0)
            {
                SelectedKmlFile = AvailableKmlFiles[0];
            }

            OpenChainDialog(DialogType.KmlImport);
        });

        CancelKmlImportDialogCommand = new RelayCommand(() =>
        {
            State.UI.CloseDialog();
            SelectedKmlFile = null;
            KmlImportFieldName = string.Empty;
        });

        ConfirmKmlImportDialogCommand = new AsyncRelayCommand(async () =>
        {
            if (SelectedKmlFile == null)
            {
                StatusMessage = "Please select a KML file";
                return;
            }

            if (_kmlParsedPolygons.Count == 0 || _kmlBoundaryPoints.Count < 3)
            {
                StatusMessage = "KML file must contain at least 3 boundary points";
                return;
            }

            // Import to existing field mode (opened from boundary panel)
            if (_kmlImportToExistingField)
            {
                ImportKmlToExistingField();
                return;
            }

            // Create new field mode (opened from field creation)
            var newFieldName = KmlImportFieldName.Trim();
            if (string.IsNullOrWhiteSpace(newFieldName))
            {
                StatusMessage = "Please enter a field name";
                return;
            }

            var fieldsDir = _settingsService.Settings.FieldsDirectory;
            if (string.IsNullOrWhiteSpace(fieldsDir))
            {
                fieldsDir = Path.Combine(
                    AppDataRoot.Documents, "Fields");
            }

            var newFieldPath = Path.Combine(fieldsDir, newFieldName);
            if (Directory.Exists(newFieldPath))
            {
                StatusMessage = $"Field '{newFieldName}' already exists";
                return;
            }

            try
            {
                Directory.CreateDirectory(newFieldPath);
                // Invariant culture (the old {x:F8} wrote "42,03" in comma-decimal locales, #112)
                // and a Field.txt, which the open path needs.
                WriteNewFieldSkeleton(newFieldPath, newFieldName, KmlCenterLatitude, KmlCenterLongitude);

                var origin = new Wgs84(KmlCenterLatitude, KmlCenterLongitude);
                var sharedProps = new SharedFieldProperties();
                var localPlane = new LocalPlane(origin, sharedProps);

                var boundary = new Boundary();

                // First polygon = outer boundary
                for (int polyIdx = 0; polyIdx < _kmlParsedPolygons.Count; polyIdx++)
                {
                    var polygon = new BoundaryPolygon();
                    foreach (var (lat, lon) in _kmlParsedPolygons[polyIdx])
                    {
                        var wgs84 = new Wgs84(lat, lon);
                        var geoCoord = localPlane.ConvertWgs84ToGeoCoord(wgs84);
                        polygon.Points.Add(new BoundaryPoint(geoCoord.Easting, geoCoord.Northing, 0));
                    }

                    if (polyIdx == 0)
                        boundary.OuterBoundary = polygon;
                    else
                        boundary.InnerBoundaries.Add(polygon);
                }

                _boundaryFileService.SaveBoundary(boundary, newFieldPath);

                // Open through the normal path: closes (and saves) the current field first
                // and loads the new one, boundary included (#107).
                await OpenCreatedFieldAsync(newFieldPath, newFieldName);
                var innerCount = _kmlParsedPolygons.Count - 1;
                var innerMsg = innerCount > 0 ? $" ({innerCount} inner boundaries)" : "";
                StatusMessage = $"Imported KML: {newFieldName}{innerMsg}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error importing KML: {ex.Message}";
            }
        });

        KmlAppendDateCommand = new RelayCommand(() =>
        {
            var dateStr = DateTime.Now.ToString("yyyy-MMM-dd");
            KmlImportFieldName = (KmlImportFieldName + " " + dateStr).Trim();
        });

        KmlAppendTimeCommand = new RelayCommand(() =>
        {
            var timeStr = DateTime.Now.ToString("HH-mm");
            KmlImportFieldName = (KmlImportFieldName + " " + timeStr).Trim();
        });

        KmlBackspaceFieldNameCommand = new RelayCommand(() =>
        {
            if (KmlImportFieldName.Length > 0)
            {
                KmlImportFieldName = KmlImportFieldName.Substring(0, KmlImportFieldName.Length - 1);
            }
        });

        // ISO-XML Import Dialog
        ShowIsoXmlImportDialogCommand = new RelayCommand(() =>
        {
            PopulateAvailableIsoXmlFiles();
            IsoXmlImportFieldName = string.Empty;
            SelectedIsoXmlFile = null;

            if (AvailableIsoXmlFiles.Count > 0)
            {
                SelectedIsoXmlFile = AvailableIsoXmlFiles[0];
            }

            OpenChainDialog(DialogType.IsoXmlImport);
        });

        CancelIsoXmlImportDialogCommand = new RelayCommand(() =>
        {
            State.UI.CloseDialog();
            SelectedIsoXmlFile = null;
            IsoXmlImportFieldName = string.Empty;
        });

        ConfirmIsoXmlImportDialogCommand = new AsyncRelayCommand(async () =>
        {
            if (SelectedIsoXmlFile == null)
            {
                StatusMessage = "Please select an ISO-XML folder";
                return;
            }

            var newFieldName = IsoXmlImportFieldName.Trim();
            if (string.IsNullOrWhiteSpace(newFieldName))
            {
                StatusMessage = "Please enter a field name";
                return;
            }

            var fieldsDir = _settingsService.Settings.FieldsDirectory;
            if (string.IsNullOrWhiteSpace(fieldsDir))
            {
                fieldsDir = Path.Combine(
                    AppDataRoot.Documents, "Fields");
            }

            var newFieldPath = Path.Combine(fieldsDir, newFieldName);
            if (Directory.Exists(newFieldPath))
            {
                StatusMessage = $"Field '{newFieldName}' already exists";
                return;
            }

            try
            {
                // Locate TASKDATA.XML inside the selected dataset folder (case-insensitive
                // so real-world uppercase datasets work on Linux too).
                var taskDataPath = Directory.Exists(SelectedIsoXmlFile.FullPath)
                    ? Directory.EnumerateFiles(SelectedIsoXmlFile.FullPath)
                        .FirstOrDefault(f => string.Equals(Path.GetFileName(f), "TASKDATA.XML", StringComparison.OrdinalIgnoreCase))
                    : null;
                if (taskDataPath == null)
                {
                    StatusMessage = "TASKDATA.XML not found in the selected folder";
                    return;
                }

                var doc = new XmlDocument();
                doc.Load(taskDataPath);
                var pfd = doc.SelectSingleNode("//PFD");
                if (pfd == null)
                {
                    StatusMessage = "ISO-XML file contains no field (PFD)";
                    return;
                }
                var fieldParts = pfd.ChildNodes;

                // The local plane needs an origin. ISO-XML stores absolute WGS84, so use
                // the centroid of the outer boundary (falling back to all points).
                if (!TryComputeIsoXmlOrigin(pfd, out double originLat, out double originLon))
                {
                    StatusMessage = "ISO-XML file has no coordinates to import";
                    return;
                }
                var localPlane = new LocalPlane(new Wgs84(originLat, originLon), new SharedFieldProperties());

                var parsedBoundaries = IsoXmlParserHelpers.ParseBoundaries(fieldParts, localPlane);
                if (parsedBoundaries.Count == 0 || parsedBoundaries[0].FenceLine.Count < 3)
                {
                    StatusMessage = "ISO-XML file has no usable boundary";
                    return;
                }
                var parsedHeadland = IsoXmlParserHelpers.ParseHeadland(fieldParts, localPlane);
                var parsedTracks = IsoXmlParserHelpers.ParseAllGuidanceLines(fieldParts, localPlane);

                Directory.CreateDirectory(newFieldPath);
                WriteNewFieldSkeleton(newFieldPath, newFieldName, originLat, originLon);

                // Build the boundary (first = outer, rest = inner holes).
                var boundary = new Boundary();
                for (int i = 0; i < parsedBoundaries.Count; i++)
                {
                    var poly = new BoundaryPolygon { IsDriveThrough = parsedBoundaries[i].IsDriveThru };
                    foreach (var v in parsedBoundaries[i].FenceLine)
                        poly.Points.Add(new BoundaryPoint(v.Easting, v.Northing, 0));
                    poly.UpdateBounds();
                    if (i == 0)
                        boundary.OuterBoundary = poly;
                    else
                        boundary.InnerBoundaries.Add(poly);
                }
                if (parsedHeadland.Count >= 3)
                {
                    var hp = new BoundaryPolygon();
                    foreach (var v in parsedHeadland)
                        hp.Points.Add(new BoundaryPoint(v.Easting, v.Northing, 0));
                    hp.UpdateBounds();
                    boundary.HeadlandPolygon = hp;
                }

                // Convert guidance lines into Tracks.
                var tracks = new List<Track>();
                foreach (var t in parsedTracks)
                {
                    var track = new Track { Name = t.Name, NudgeDistance = t.NudgeDistance, IsVisible = t.IsVisible };
                    if (t.Mode == IsoXmlTrackMode.Curve && t.CurvePoints.Count >= 2)
                    {
                        track.Type = TrackType.Curve;
                        track.Points = new List<Vec3>(t.CurvePoints);
                    }
                    else
                    {
                        track.Type = TrackType.ABLine;
                        track.Points = new List<Vec3>
                        {
                            new Vec3(t.PtA.Easting, t.PtA.Northing, 0),
                            new Vec3(t.PtB.Easting, t.PtB.Northing, 0),
                        };
                    }
                    if (track.Points.Count >= 2) tracks.Add(track);
                }

                // Persist the boundary + tracks, then open through the normal path, which closes
                // (and saves) the current field first and loads the new one (#107).
                _boundaryFileService.SaveBoundary(boundary, newFieldPath);
                if (tracks.Count > 0)
                    TrackFilesService.Save(newFieldPath, tracks);
                await OpenCreatedFieldAsync(newFieldPath, newFieldName);

                // The headland save writes to the ACTIVE field, so it runs once the new field is
                // open; then reload it into the live state.
                if (boundary.HeadlandPolygon != null)
                {
                    SaveHeadlandToFile(boundary.HeadlandPolygon.Points
                        .Select(p => new Vec3(p.Easting, p.Northing, 0)).ToList());
                    LoadHeadlandFromField(_fieldService.ActiveField);
                }

                var innerCount = parsedBoundaries.Count - 1;
                var extras = new List<string>();
                if (innerCount > 0) extras.Add($"{innerCount} inner");
                if (tracks.Count > 0) extras.Add($"{tracks.Count} track{(tracks.Count == 1 ? "" : "s")}");
                var extraMsg = extras.Count > 0 ? $" ({string.Join(", ", extras)})" : "";
                StatusMessage = $"Imported ISO-XML: {newFieldName}{extraMsg}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error importing ISO-XML: {ex.Message}";
            }
        });

        IsoXmlAppendDateCommand = new RelayCommand(() =>
        {
            var dateStr = DateTime.Now.ToString("yyyy-MMM-dd");
            IsoXmlImportFieldName = (IsoXmlImportFieldName + " " + dateStr).Trim();
        });

        IsoXmlAppendTimeCommand = new RelayCommand(() =>
        {
            var timeStr = DateTime.Now.ToString("HH-mm");
            IsoXmlImportFieldName = (IsoXmlImportFieldName + " " + timeStr).Trim();
        });

        IsoXmlBackspaceFieldNameCommand = new RelayCommand(() =>
        {
            if (IsoXmlImportFieldName.Length > 0)
            {
                IsoXmlImportFieldName = IsoXmlImportFieldName.Substring(0, IsoXmlImportFieldName.Length - 1);
            }
        });

        // Field close and resume commands
        CloseFieldCommand = new AsyncRelayCommand(async () =>
        {
            async Task FinishCloseAsync()
            {
                await CloseFieldAsync();

                // Disconnect NTRIP if connected
                if (_ntripService.IsConnected)
                {
                    await _ntripService.DisconnectAsync();
                }

                StatusMessage = "Field closed";
            }

            // Warn before dropping coverage painted with no active job. If the
            // guard shows its prompt, the close runs from one of its buttons.
            if (!TryShowUnsavedCoverageGuard(() => _ = FinishCloseAsync()))
            {
                await FinishCloseAsync();
            }
        });

        // "Drive In" — AgOpen-style nearby-field shortcut. Looks for fields
        // whose origin is within 0.5 km of the operator's current GPS fix
        // (matches AgOpenGPS FormJob.btnInField_Click). One match opens
        // directly; multiple matches go through StartWorkSessionDialog
        // pre-filtered to nearby. Zero matches surface a status message.
        DriveInCommand = new RelayCommand(() =>
        {
            if (Latitude == 0 && Longitude == 0)
            {
                StatusMessage = "No GPS fix — Drive In needs current position";
                return;
            }

            var fieldsRoot = _settingsService.Settings.FieldsDirectory;
            var nearby = _fieldService.FindFieldsNear(fieldsRoot, Latitude, Longitude, maxKm: 0.5);

            if (nearby.Count == 0)
            {
                StatusMessage = "No fields within 0.5 km";
                return;
            }

            if (nearby.Count == 1)
            {
                var only = nearby[0];
                _ = OpenFieldAsync(only.DirectoryPath, only.Name);
                IsFieldOperationsPanelVisible = false;
                return;
            }

            // 2+ — open the picker with the list pre-filtered to nearby.
            StartWorkSessionDialogVm = new StartWorkSessionDialogViewModel(
                _fieldService,
                _jobService,
                _settingsService,
                _appState,
                close: () => State.UI.CloseDialog(),
                openField: (path, name) => _ = OpenFieldOnlyAsync(path, name),
                openFieldStartingNewJob: (path, name, workType, notes, taskName) =>
                    _ = OpenFieldStartingNewJobAsync(path, name, workType, notes, taskName),
                openFieldResumingJob: (path, name, taskName) =>
                    _ = OpenFieldResumingJobAsync(path, name, taskName),
                confirm: (msg, action) => ShowConfirmationDialog("Delete Job", msg, action),
                confirmWithOption: (title, msg, checkboxLabel, defaultChecked, action) =>
                    ShowConfirmationDialog(title, msg, checkboxLabel, defaultChecked, action),
                nearbyMaxKm: 0.5);
            StartWorkSessionDialogVm.Refresh();
            OpenChainDialog(DialogType.StartWorkSession);
        });

        ResumeFieldCommand = new AsyncRelayCommand(async () =>
        {
            var lastField = PersistentState.LastOpenedField;
            if (string.IsNullOrEmpty(lastField))
            {
                StatusMessage = "No previous field to resume";
                return;
            }

            // Get fields directory from settings
            var fieldsDir = _settingsService.Settings.FieldsDirectory;
            if (string.IsNullOrEmpty(fieldsDir))
            {
                fieldsDir = Path.Combine(
                    AppDataRoot.Documents, "Fields");
            }

            var fieldPath = Path.Combine(fieldsDir, lastField);

            if (!Directory.Exists(fieldPath))
            {
                StatusMessage = $"Field not found: {lastField}";
                return;
            }

            // Check if this is a legacy field that will be auto-converted
            bool isLegacy = !File.Exists(Path.Combine(fieldPath, "field.geojson")) &&
                            File.Exists(Path.Combine(fieldPath, "Field.txt"));

            if (isLegacy)
            {
                ShowConfirmationDialog(
                    "Import Legacy Field",
                    $"'{lastField}' uses the legacy AgOpenGPS format. " +
                    "It will be imported and converted to the new format. " +
                    "The original files will be kept. Continue?",
                    () =>
                    {
                        _ = OpenFieldAsync(fieldPath, lastField).ContinueWith(_ =>
                            _dispatcher.Post(() =>
                                IsFieldOperationsPanelVisible = false));
                    });
                return;
            }

            await OpenFieldAsync(fieldPath, lastField);
            IsFieldOperationsPanelVisible = false;
        });
    }

    /// <summary>
    /// Host-driven Fields-and-Jobs VM for the remote (web) client. Reuses the real
    /// <see cref="StartWorkSessionDialogViewModel"/> — same field/job orchestration as
    /// the native dialog — but with web-appropriate callbacks: no native dialog to
    /// close, and the confirm callbacks proceed immediately (the browser does its own
    /// confirm before sending a delete). The web sets SelectedField / the new-job form
    /// then executes the VM's commands; we Refresh() so the lists are current.
    /// </summary>
    public StartWorkSessionDialogViewModel EnsureRemoteStartWorkSession()
    {
        StartWorkSessionDialogVm = new StartWorkSessionDialogViewModel(
            _fieldService,
            _jobService,
            _settingsService,
            _appState,
            close: () => { },
            openField: (path, name) => _ = OpenFieldOnlyAsync(path, name),
            openFieldStartingNewJob: (path, name, workType, notes, taskName) =>
                _ = OpenFieldStartingNewJobAsync(path, name, workType, notes, taskName),
            openFieldResumingJob: (path, name, taskName) =>
                _ = OpenFieldResumingJobAsync(path, name, taskName),
            confirm: (_, action) => action(),
            confirmWithOption: (_, _, _, _, action) => action(true));
        StartWorkSessionDialogVm.Refresh();
        return StartWorkSessionDialogVm;
    }

    // Remote (web) field-creation entry points. Each populates the relevant picker
    // collection (no native dialog), sets the same input properties the native dialog
    // binds, then runs the real Confirm command — so the create logic isn't duplicated.
    private string RemoteFieldsDir()
    {
        var dir = _settingsService.Settings.FieldsDirectory;
        return string.IsNullOrWhiteSpace(dir)
            ? Path.Combine(AppDataRoot.Documents, "Fields")
            : dir;
    }

    /// <summary>
    /// Write the files a brand-new field needs before it can be opened: field.origin and
    /// Field.txt, both InvariantCulture (FieldPlaneFileService parses with it; a comma-decimal
    /// culture wrote "42,03", the origin fell back to 0,0 and "near me" dropped the field).
    /// </summary>
    private static void WriteNewFieldSkeleton(string fieldPath, string name, double lat, double lon)
    {
        var inv = CultureInfo.InvariantCulture;
        var latStr = lat.ToString("F8", inv);
        var lonStr = lon.ToString("F8", inv);
        File.WriteAllText(Path.Combine(fieldPath, "field.origin"), $"{latStr},{lonStr}");
        File.WriteAllText(Path.Combine(fieldPath, "Field.txt"),
            $"{DateTime.Now.ToString("yyyy-MMM-dd hh:mm:ss tt", inv)}\n" +
            "$FieldDir\n" +
            $"{name}\n" +
            "$Offsets\n" +
            "0,0\n" +
            "Convergence\n" +
            "0\n" +
            "StartFix\n" +
            $"{latStr},{lonStr}\n");
    }

    /// <summary>
    /// Open a field that was just created on disk (New / From Existing / KML / ISO-XML)
    /// through the normal open path, which closes — and saves — the current field first and
    /// loads everything for the new one. Previously each flow set the active field by hand,
    /// so the previous field's tracks, flags, coverage and job carried over unsaved (#107).
    /// </summary>
    private async Task OpenCreatedFieldAsync(string fieldPath, string name)
    {
        State.UI.CloseDialog();
        IsFieldOperationsPanelVisible = false;
        await OpenFieldOnlyAsync(fieldPath, name);
    }

    public void RemoteCreateFromExisting(string sourceName, string newName,
        bool copyFlags, bool copyMapping, bool copyHeadland, bool copyLines)
    {
        PopulateAvailableFields(RemoteFieldsDir());
        FromExistingSelectedField = AvailableFields.FirstOrDefault(f =>
            string.Equals(f.Name, sourceName, StringComparison.OrdinalIgnoreCase));
        FromExistingFieldName = newName;
        CopyFlags = copyFlags; CopyMapping = copyMapping; CopyHeadland = copyHeadland; CopyLines = copyLines;
        ConfirmFromExistingFieldDialogCommand.Execute(null);
    }

    public void RemoteCreateFromKml(string fileName, string newName)
    {
        PopulateAvailableKmlFiles();
        SelectedKmlFile = AvailableKmlFiles.FirstOrDefault(f =>
            string.Equals(f.Name, fileName, StringComparison.OrdinalIgnoreCase));
        KmlImportFieldName = newName;
        ConfirmKmlImportDialogCommand.Execute(null);
    }

    /// <summary>
    /// Computes a field origin (WGS84) for an ISO-XML import as the centroid of the outer
    /// boundary ring (PLN type 1/9, LSG type 1). Falls back to the centroid of every point
    /// in the partfield. Returns false if no coordinates are present.
    /// </summary>
    private static bool TryComputeIsoXmlOrigin(XmlNode pfd, out double lat, out double lon)
    {
        lat = 0;
        lon = 0;

        XmlNodeList points = null;
        var plns = pfd.SelectNodes("PLN");
        if (plns != null)
        {
            foreach (XmlNode pln in plns)
            {
                var type = pln.Attributes?["A"]?.Value;
                if (type != "1" && type != "9") continue;
                points = pln.SelectSingleNode("LSG[@A='1']")?.SelectNodes("PNT");
                if (points != null && points.Count > 0) break;
            }
        }
        if (points == null || points.Count == 0)
            points = pfd.SelectNodes(".//PNT");
        if (points == null || points.Count == 0)
            return false;

        double latSum = 0, lonSum = 0;
        int n = 0;
        foreach (XmlNode pnt in points)
        {
            if (double.TryParse(pnt.Attributes?["C"]?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double la) &&
                double.TryParse(pnt.Attributes?["D"]?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double lo))
            {
                latSum += la;
                lonSum += lo;
                n++;
            }
        }
        if (n == 0) return false;

        lat = latSum / n;
        lon = lonSum / n;
        return true;
    }

    public void RemoteCreateFromIsoXml(string fileName, string newName)
    {
        PopulateAvailableIsoXmlFiles();
        SelectedIsoXmlFile = AvailableIsoXmlFiles.FirstOrDefault(f =>
            string.Equals(f.Name, fileName, StringComparison.OrdinalIgnoreCase));
        IsoXmlImportFieldName = newName;
        ConfirmIsoXmlImportDialogCommand.Execute(null);
    }
}
