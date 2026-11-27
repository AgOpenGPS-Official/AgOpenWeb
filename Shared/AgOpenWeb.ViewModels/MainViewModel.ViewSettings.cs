// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services;

using CommunityToolkit.Mvvm.ComponentModel;

namespace AgOpenWeb.ViewModels;

/// <summary>
/// MainViewModel partial class containing View Settings and Panel Visibility.
/// Manages UI state for panels, display settings, and camera/display controls.
/// </summary>
public partial class MainViewModel
{
    #region Panel Visibility Fields

    private bool _isScreenAlertsPanelVisible;
    private bool _isFileMenuPanelVisible;
    private bool _isToolsPanelVisible;
    private bool _isFieldOperationsPanelVisible;
    private bool _isFieldToolsPanelVisible;
    private bool _isNetworkIoPanelVisible;
    private bool _isSimulatorPanelVisible;

    #endregion

    #region Panel Visibility Properties

    public bool IsScreenAlertsPanelVisible
    {
        get => _isScreenAlertsPanelVisible;
        set { SetProperty(ref _isScreenAlertsPanelVisible, value); OnPropertyChanged(nameof(IsAnyNavFlyoutOpen)); }
    }

    public bool IsFileMenuPanelVisible
    {
        get => _isFileMenuPanelVisible;
        set { SetProperty(ref _isFileMenuPanelVisible, value); OnPropertyChanged(nameof(IsAnyNavFlyoutOpen)); }
    }

    public bool IsToolsPanelVisible
    {
        get => _isToolsPanelVisible;
        set { SetProperty(ref _isToolsPanelVisible, value); OnPropertyChanged(nameof(IsAnyNavFlyoutOpen)); }
    }

    public bool IsFieldOperationsPanelVisible
    {
        get => _isFieldOperationsPanelVisible;
        set { SetProperty(ref _isFieldOperationsPanelVisible, value); OnPropertyChanged(nameof(IsAnyNavFlyoutOpen)); }
    }

    public bool IsFieldToolsPanelVisible
    {
        get => _isFieldToolsPanelVisible;
        set { SetProperty(ref _isFieldToolsPanelVisible, value); OnPropertyChanged(nameof(IsAnyNavFlyoutOpen)); }
    }

    public bool IsNetworkIoPanelVisible
    {
        get => _isNetworkIoPanelVisible;
        set { SetProperty(ref _isNetworkIoPanelVisible, value); OnPropertyChanged(nameof(IsAnyNavFlyoutOpen)); }
    }

    /// <summary>True while any left-nav fly-out is open. Drives the light-dismiss
    /// scrim that closes the open menu when the operator taps outside it.</summary>
    public bool IsAnyNavFlyoutOpen =>
        IsScreenAlertsPanelVisible || IsFileMenuPanelVisible || IsToolsPanelVisible
        || IsFieldOperationsPanelVisible || IsFieldToolsPanelVisible
        || IsNetworkIoPanelVisible;

    public bool IsSimulatorPanelVisible
    {
        get => _isSimulatorPanelVisible;
        set => SetProperty(ref _isSimulatorPanelVisible, value);
    }




    /// <summary>
    /// Close every left-nav fly-out menu. Used to enforce "one menu open at a
    /// time" (a toggle closes the others first) and to dismiss the open menu
    /// when one of its items launches a dialog.
    /// </summary>
    public void CloseAllNavFlyouts()
    {
        // Remember which fly-out we're closing so a chain dialog launched by the
        // same item-click can still capture it as its origin (see OpenChainDialog).
        var open = CurrentFlyout();
        if (open != NavFlyout.None)
            _lastClosedFlyout = open;

        IsScreenAlertsPanelVisible = false;
        IsFileMenuPanelVisible = false;
        IsToolsPanelVisible = false;
        IsFieldOperationsPanelVisible = false;
        IsFieldToolsPanelVisible = false;
        IsNetworkIoPanelVisible = false;
    }

    #endregion

    #region Clock

    private string _currentTime = "";
    public string CurrentTime
    {
        get => _currentTime;
        private set => SetProperty(ref _currentTime, value);
    }

    private void InitializeClock()
    {
        CurrentTime = DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        var clockTimer = _timerFactory.Create();
        clockTimer.Interval = TimeSpan.FromSeconds(1);
        clockTimer.Tick += (_, _) => CurrentTime = DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        clockTimer.Start();
    }

    #endregion

    #region Camera Mode

    private CameraMode _cameraMode = CameraMode.HeadingUp;
    private CameraMode _previousCameraMode = CameraMode.HeadingUp;
    public CameraMode CameraMode
    {
        get => _cameraMode;
        set
        {
            var old = _cameraMode;
            SetProperty(ref _cameraMode, value);
            if (old != value)
            {
                ApplyCameraMode();
            }
        }
    }


    private void ApplyCameraMode()
    {
        // Set camera follow mode directly on map control: 0=NorthUp, 1=HeadingUp, 2=Free, 3=Map
        int mapMode = _cameraMode switch
        {
            CameraMode.NorthUp => 0,
            CameraMode.HeadingUp => 1,
            CameraMode.Free => 2,
            CameraMode.Map => 3,
            _ => 3
        };
        var camPos = _mapService.GetCameraCenter();
        Console.WriteLine($"[Camera] ApplyCameraMode: {_cameraMode} (mapMode={mapMode}) cam=({camPos.X:F1},{camPos.Y:F1}) vehicle=({Easting:F1},{Northing:F1})");
        _mapService.SetCameraFollowMode(mapMode);

        // When switching FROM Free to a follow mode, immediately center on vehicle
        if (_cameraMode != CameraMode.Free)
        {
            _mapService.PanTo(Easting, Northing);
            Console.WriteLine($"[Camera] Recentered to ({Easting:F1},{Northing:F1})");
        }

        IsNorthUp = _cameraMode == CameraMode.NorthUp;

        // Persist last actively-chosen follow mode (Free is transient -- entered by
        // pan, not a mode the user explicitly picks to keep). State, not config.
        if (_cameraMode != CameraMode.Free)
            PersistentState.CameraMode = _cameraMode;
    }

    #endregion

    #region Display Settings Properties

    // Navigation settings properties (forwarded from service)
    public bool IsGridOn
    {
        get => _displaySettings.IsGridOn;
        set
        {
            _displaySettings.IsGridOn = value;
            OnPropertyChanged();
        }
    }

    public bool IsDayMode
    {
        get => _displaySettings.IsDayMode;
        set
        {
            _displaySettings.IsDayMode = value;
            OnPropertyChanged();
            _mapService.SetDayMode(value);
        }
    }

    public double CameraPitch
    {
        get => _displaySettings.CameraPitch;
        set
        {
            _displaySettings.CameraPitch = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Is2DMode));
        }
    }


    public bool Is2DMode
    {
        get => _displaySettings.Is2DMode;
        set
        {
            _displaySettings.Is2DMode = value;
            OnPropertyChanged();
        }
    }

    public bool IsNorthUp
    {
        get => _displaySettings.IsNorthUp;
        set
        {
            _displaySettings.IsNorthUp = value;
            OnPropertyChanged();
        }
    }

    public string DisplayResolutionLabel =>
        AgOpenWeb.Models.Configuration.DisplayConfig.QualityLabel(ConfigStore.Display.DisplayResolutionMultiplier);

    #endregion

    #region Auto Day/Night

    private AgOpenWeb.Services.Interfaces.IUiTimer? _autoDayNightTimer;

    private void InitializeAutoDayNight()
    {
        CheckAutoDayNight();
        _autoDayNightTimer = _timerFactory.Create();
        _autoDayNightTimer.Interval = TimeSpan.FromSeconds(60);
        _autoDayNightTimer.Tick += (_, _) => CheckAutoDayNight();
        _autoDayNightTimer.Start();
    }

    /// <summary>
    /// Switch day/night mode automatically based on solar position.
    /// Uses GPS-based sunrise/sunset when available, falls back to configured hours.
    /// GPS-based only when AutoDayNight is enabled.
    /// </summary>
    private void CheckAutoDayNight()
    {
        var display = _configStore.Display;
        if (!display.AutoDayNight) return;
        
        bool shouldBeDay = false;
        // Try GPS-based solar calculation when we have a valid position
        if (_gpsService.IsGpsDataOk() && display.AutoDayNight)
        {
            shouldBeDay = SolarCalculator.IsDay(Latitude, Longitude, DateTime.UtcNow);
        }
        else
        {
            // Fallback to configurable hours
            int hour = DateTime.Now.Hour;
            int dayStart = display.DayStartHour;
            int nightStart = display.NightStartHour;

            if (dayStart < nightStart)
                shouldBeDay = hour >= dayStart && hour < nightStart;
            else
                // Handles wrap-around (e.g. day=22, night=6 for night-shift work)
                shouldBeDay = hour >= dayStart || hour < nightStart;
        }
        if (IsDayMode != shouldBeDay)
        {
            IsDayMode = shouldBeDay;
            _mapService.SetDayMode(shouldBeDay);
        }
    }

    #endregion

    #region ConfigurationStore Display Forwarding

    /// <summary>
    /// Right-nav auto-U-turn controls (arm toggle + direction toggle) are available
    /// when autosteer is available AND the active track isn't a closed loop (no
    /// U-turns on polygon tracks, #421). Deliberately NOT gated by the
    /// <see cref="DisplayConfig.UTurnButtonVisible"/> display-visibility preference:
    /// that flag governs only the on-map U-turn overlay (<see cref="IsUTurnOverlayVisible"/>),
    /// so hiding the overlay never makes arming/disarming auto-U-turn unreachable
    /// (config/state audit §13.2).
    /// </summary>
    public bool IsUTurnButtonVisible =>
        IsAutoSteerAvailable && !IsActiveTrackClosed;

    /// <summary>
    /// Manual U-turn left/right buttons: visible only while steering AND not on a
    /// closed (polygon) track (#421).
    /// </summary>
    public bool IsManualUTurnVisible => IsAutoSteerEngaged && !IsActiveTrackClosed;

    /// <summary>
    /// On-map U-Turn overlay (the two yellow manual-turn arrows). Same conditions
    /// as the manual U-turn buttons, additionally gated by the Screen &amp; Alerts
    /// "U-Turn" on-screen-button toggle (<see cref="DisplayConfig.UTurnButtonVisible"/>).
    /// </summary>
    public bool IsUTurnOverlayVisible =>
        _configStore.Display.UTurnButtonVisible && IsManualUTurnVisible;

    /// <summary>
    /// On-map Lateral overlay (the two cyan shift arrows). Shown only while
    /// autosteer is engaged (same gate as the U-turn overlay), additionally
    /// gated by the Screen &amp; Alerts "Lateral" on-screen-button toggle
    /// (<see cref="DisplayConfig.LateralButtonVisible"/>) — previously orphaned.
    /// </summary>
    public bool IsLateralOverlayVisible =>
        _configStore.Display.LateralButtonVisible && IsManualUTurnVisible;

    /// <summary>
    /// Notify IsUTurnButtonVisible and the on-map overlay visibilities when their
    /// inputs change. Called from MainViewModel.Guidance.cs when track state
    /// changes and from the ConfigStore.Display subscription when the on-screen-
    /// button toggles flip.
    /// </summary>
    private void RaiseUTurnButtonVisibleChanged()
    {
        OnPropertyChanged(nameof(IsUTurnButtonVisible));
        OnPropertyChanged(nameof(IsUTurnOverlayVisible));
        OnPropertyChanged(nameof(IsLateralOverlayVisible));
    }

    /// <summary>
    /// Aggregate of the four module-status flags shown in the top status strip,
    /// replacing the per-letter G/I/A/M cluster. Configured set comes from
    /// <see cref="ConnectionConfig.IsGpsConfigured"/> etc.; presence comes from
    /// <see cref="ConnectionState.IsGpsDataOk"/> etc.
    /// </summary>
    public ModuleStatusKind ModuleStatusKind
    {
        get
        {
            var cfg = _configStore.Connections;
            var st = State.Connections;
            int configured = 0;
            int present = 0;

            if (cfg.IsGpsConfigured)       { configured++; if (st.IsGpsDataOk)       present++; }
            if (cfg.IsImuConfigured)       { configured++; if (st.IsImuDataOk)       present++; }
            if (cfg.IsAutoSteerConfigured) { configured++; if (st.IsAutoSteerDataOk) present++; }
            if (cfg.IsMachineConfigured)   { configured++; if (st.IsMachineDataOk)   present++; }

            if (configured == 0 || present == 0) return ModuleStatusKind.NonePresent;
            if (present == configured) return ModuleStatusKind.AllPresent;
            return ModuleStatusKind.PartiallyPresent;
        }
    }

    private void RaiseModuleStatusKindChanged()
    {
        OnPropertyChanged(nameof(ModuleStatusKind));
    }

    private BatteryStatus _batteryStatus;

    /// <summary>
    /// Latest battery reading from the per-platform <see cref="IBatteryService"/>.
    /// The status-strip battery icon binds to the derived properties below.
    /// </summary>
    public BatteryStatus BatteryStatus => _batteryStatus;

    /// <summary>Battery level as a 0-1 fraction. Meaningless when <see cref="IsBatteryAvailable"/> is false.</summary>
    public double BatteryLevel => _batteryStatus.Level;

    /// <summary>True when the device is currently plugged in.</summary>
    public bool IsBatteryCharging => _batteryStatus.IsCharging;

    /// <summary>True when the platform exposed a real reading. The icon hides when this is false.</summary>
    public bool IsBatteryAvailable => _batteryStatus.IsAvailable;



    #endregion
}
