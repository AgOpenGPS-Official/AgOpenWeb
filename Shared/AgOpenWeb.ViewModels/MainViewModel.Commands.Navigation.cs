// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Configuration;
using CommunityToolkit.Mvvm.Input;

namespace AgOpenWeb.ViewModels;

/// <summary>
/// Navigation panel commands - view toggles, camera controls.
/// </summary>
public partial class MainViewModel
{
    private void InitializeNavigationCommands()
    {
        // When any dialog opens (e.g. a fly-out item launches one), dismiss the
        // open left-nav fly-out so menus don't linger behind the dialog.
        State.UI.DialogChanged += (_, e) =>
        {
            if (e.Current != Models.State.DialogType.None)
                CloseAllNavFlyouts();
            // The Vehicle/Tool config dialogs edit the live store and have no Apply
            // button; persist the active profiles to disk when navigating away from
            // them (Back to the picker or Close to the map) so edits aren't lost.
            if (e.Previous is Models.State.DialogType.VehicleConfig
                or Models.State.DialogType.ToolConfig)
            {
                _configurationService.SaveProfiles(
                    _configurationService.Store.ActiveVehicleProfileName,
                    _configurationService.Store.ActiveToolProfileName);
            }
        };

        ToggleAutoTrackCommand = new RelayCommand(() =>
        {
            IsAutoTrackEnabled = !IsAutoTrackEnabled;
            StatusMessage = IsAutoTrackEnabled ? "Auto track select ON" : "Auto track select OFF";
        });

        // View mode commands
        ToggleGridCommand = new RelayCommand(() =>
        {
            IsGridOn = !IsGridOn;
        });

        ToggleDayNightCommand = new RelayCommand(() =>
        {
            IsDayMode = !IsDayMode;
            _mapService.SetDayMode(IsDayMode);
            // Disable auto day/night when user manually toggles theme, and save it, or
            // auto comes back on after a restart (#111).
            if (_configStore.Display.AutoDayNight)
            {
                _configStore.Display.AutoDayNight = false;
                if (_configReady) _configurationService.SaveAppSettings();
            }
        });

        ToggleNorthUpCommand = new RelayCommand(() =>
        {
            IsNorthUp = !IsNorthUp;
            _mapService.SetNorthUp(IsNorthUp);
        });

        // Display resolution: cycle Ultra → High → Medium → Low → Min → Ultra
        CycleDisplayResolutionCommand = new RelayCommand(() =>
        {
            var display = ConfigStore.Display;
            display.DisplayResolutionMultiplier = display.DisplayResolutionMultiplier switch
            {
                < 1.25 => 1.5,  // Ultra → High
                < 2.0  => 2.5,  // High → Medium
                < 3.25 => 4.0,  // Medium → Low
                < 5.0  => 6.0,  // Low → Min
                _ => 1.0,       // Min → Ultra
            };
            OnPropertyChanged(nameof(DisplayResolutionLabel));

            // Apply the new resolution to the live coverage bitmap immediately
            // instead of only on the next field open: rebuild the display bitmap at
            // the new cell size and repaint from the resolution-independent detection
            // cells (camera preserved). Audit §13.1.
            if (IsFieldOpen)
            {
                _coverageMapService.RebuildDisplayForResolutionChange(); // shared layer → remote/web coverage feed + save
                _mapService.RebuildCoverageBitmapForResolutionChange();  // native map-control bitmap
            }
        });
    }

    /// <summary>
    /// Cap the display resolution quality (web auto-quality): ensure the multiplier is at
    /// least <paramref name="minMultiplier"/> — i.e. quality no FINER than that level — and
    /// rebuild the live coverage bitmap if it changed. Only ever COARSENS (idempotent; a
    /// no-op if already at/below this quality), so a mobile client capping itself at Medium
    /// on connect never raises a manually-chosen lower quality, and never fights a later
    /// manual change. Mirrors CycleDisplayResolutionCommand's apply path. Multiplier scale:
    /// 1.0 Ultra · 1.5 High · 2.5 Medium · 4.0 Low · 6.0 Min.
    /// </summary>
    public void CapDisplayResolution(double minMultiplier)
    {
        var display = ConfigStore.Display;
        if (display.DisplayResolutionMultiplier >= minMultiplier) return; // already this coarse or coarser
        display.DisplayResolutionMultiplier = minMultiplier;
        OnPropertyChanged(nameof(DisplayResolutionLabel));
        if (IsFieldOpen)
        {
            _coverageMapService.RebuildDisplayForResolutionChange();
            _mapService.RebuildCoverageBitmapForResolutionChange();
        }
    }
}
