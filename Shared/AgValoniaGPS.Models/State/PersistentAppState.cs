// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
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
using AgValoniaGPS.Models; // CameraMode
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgValoniaGPS.Models.State;

/// <summary>
/// Persistent application state — the second of the two application-state
/// stores. Holds runtime state that MUST survive app close/reopen ("where the
/// app was when it closed"): window geometry, last camera view, panel layout,
/// last simulator position, the open-field pointer, app-lifecycle flags, and
/// last-used boundary-recording setup.
///
/// This is distinct from <see cref="ApplicationState"/> (the ephemeral,
/// in-memory-only store) and from
/// <see cref="Configuration.ConfigurationStore"/> (configuration — "how the app
/// should behave"). Persistence is handled by the persistent-state service,
/// which serializes this object to <c>appstate.json</c> and hydrates it on
/// startup, exactly as <c>ConfigurationService</c> bridges
/// <c>ConfigurationStore</c> ↔ <c>appsettings.json</c>.
///
/// Property names mirror their former homes (mostly <c>ConfigStore.Display</c>)
/// so the on-disk JSON and call sites read naturally. The object is serialized
/// directly (public properties); the private INotify backing fields are ignored
/// by the serializer.
/// </summary>
public class PersistentAppState : ObservableObject
{
    private static PersistentAppState? _instance;

    /// <summary>Singleton instance. Prefer DI; this mirrors ConfigurationStore for static access.</summary>
    public static PersistentAppState Instance => _instance ??= new PersistentAppState();

    /// <summary>For testing — replace the singleton instance.</summary>
    public static void SetInstance(PersistentAppState state) => _instance = state;

    // ---- Window geometry (Desktop; ignored on mobile) ----
    private double _windowWidth = 1200;
    public double WindowWidth { get => _windowWidth; set => SetProperty(ref _windowWidth, value); }

    private double _windowHeight = 800;
    public double WindowHeight { get => _windowHeight; set => SetProperty(ref _windowHeight, value); }

    private double _windowX = 100;
    public double WindowX { get => _windowX; set => SetProperty(ref _windowX, value); }

    private double _windowY = 100;
    public double WindowY { get => _windowY; set => SetProperty(ref _windowY, value); }

    private bool _windowMaximized;
    public bool WindowMaximized { get => _windowMaximized; set => SetProperty(ref _windowMaximized, value); }

    // ---- Panel layout ----
    private double _simulatorPanelX = double.NaN; // NaN = not yet positioned
    public double SimulatorPanelX { get => _simulatorPanelX; set => SetProperty(ref _simulatorPanelX, value); }

    private double _simulatorPanelY = double.NaN;
    public double SimulatorPanelY { get => _simulatorPanelY; set => SetProperty(ref _simulatorPanelY, value); }

    private bool _simulatorPanelVisible;
    public bool SimulatorPanelVisible { get => _simulatorPanelVisible; set => SetProperty(ref _simulatorPanelVisible, value); }

    // ---- Last camera view ----
    // Map zoom scalar (SkiaMapControl._zoom space, ~0.02–100, default 1.0).
    private double _cameraZoom = 1.0;
    public double CameraZoom { get => _cameraZoom; set => SetProperty(ref _cameraZoom, value); }

    private double _cameraPitch = -60.0;
    public double CameraPitch { get => _cameraPitch; set => SetProperty(ref _cameraPitch, Math.Clamp(value, -90, -20)); }

    private CameraMode _cameraMode = CameraMode.Map;
    public CameraMode CameraMode { get => _cameraMode; set => SetProperty(ref _cameraMode, value); }

    private bool _isDayMode = true;
    public bool IsDayMode { get => _isDayMode; set => SetProperty(ref _isDayMode, value); }

    private bool _is2DMode;
    public bool Is2DMode { get => _is2DMode; set => SetProperty(ref _is2DMode, value); }

    private bool _isNorthUp;
    public bool IsNorthUp { get => _isNorthUp; set => SetProperty(ref _isNorthUp, value); }

    // ---- Last simulator position ----
    private double _simulatorLatitude = 40.7128;
    public double SimulatorLatitude { get => _simulatorLatitude; set => SetProperty(ref _simulatorLatitude, Math.Clamp(value, -90, 90)); }

    private double _simulatorLongitude = -74.0060;
    public double SimulatorLongitude { get => _simulatorLongitude; set => SetProperty(ref _simulatorLongitude, Math.Clamp(value, -180, 180)); }

    private double _simulatorSpeed;
    public double SimulatorSpeed { get => _simulatorSpeed; set => SetProperty(ref _simulatorSpeed, value); }

    private double _simulatorSteerAngle;
    public double SimulatorSteerAngle { get => _simulatorSteerAngle; set => SetProperty(ref _simulatorSteerAngle, value); }

    // ---- Open-field resume pointer (field DATA lives in field files; this is
    // just the name to reopen on next launch). The currently-open field name is
    // live runtime state on the view-model, not persisted here. ----
    private string _lastOpenedField = string.Empty;
    public string LastOpenedField { get => _lastOpenedField; set => SetProperty(ref _lastOpenedField, value); }

    // ---- App lifecycle / migration ----
    private bool _isFirstRun = true;
    public bool IsFirstRun { get => _isFirstRun; set => SetProperty(ref _isFirstRun, value); }

    private DateTime _lastRunDate = DateTime.MinValue;
    public DateTime LastRunDate { get => _lastRunDate; set => SetProperty(ref _lastRunDate, value); }

    // Note: the units-migration latch (HasMigratedIsMetric) is config-migration
    // metadata and stays in AppSettings, not here.

    // ---- Last-used boundary-recording setup (defaults mirror BoundaryRecState) ----
    private bool _boundaryDrawRightSide = true;
    public bool BoundaryDrawRightSide { get => _boundaryDrawRightSide; set => SetProperty(ref _boundaryDrawRightSide, value); }

    private bool _boundaryDrawAtPivot;
    public bool BoundaryDrawAtPivot { get => _boundaryDrawAtPivot; set => SetProperty(ref _boundaryDrawAtPivot, value); }

    private double _boundaryOffset = 100.0; // cm
    public double BoundaryOffset { get => _boundaryOffset; set => SetProperty(ref _boundaryOffset, value); }
}
