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

using AgOpenWeb.Models.Configuration;
using Avalonia;
using Avalonia.Styling;
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
            // Re-evaluate whether a confirmation can offer a Back-to-fly-out button
            // (it depends on which fly-out, if any, was just closed).
            if (e.Current == Models.State.DialogType.Confirmation)
                OnPropertyChanged(nameof(IsConfirmationBackVisible));
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

        // Panel toggle commands. Only one left-nav fly-out is open at a time:
        // each toggle closes the others first, then opens (or closes) its own.
        ToggleScreenAlertsPanelCommand = new RelayCommand(() =>
        {
            bool open = !IsScreenAlertsPanelVisible;
            CloseAllNavFlyouts();
            // The panel's display/sounds/buttons sections bind to
            // ConfigurationViewModel, which is created lazily; mirror the
            // config-backed dialogs so the bindings resolve on first open.
            if (open && ConfigurationViewModel == null)
            {
                ConfigurationViewModel = new ConfigurationViewModel(_configurationService);
            }
            IsScreenAlertsPanelVisible = open;
        });

        ToggleFileMenuPanelCommand = new RelayCommand(() =>
        {
            bool open = !IsFileMenuPanelVisible;
            CloseAllNavFlyouts();
            IsFileMenuPanelVisible = open;
        });

        ToggleToolsPanelCommand = new RelayCommand(() =>
        {
            bool open = !IsToolsPanelVisible;
            CloseAllNavFlyouts();
            IsToolsPanelVisible = open;
        });

        ToggleFieldOperationsPanelCommand = new RelayCommand(() =>
        {
            bool open = !IsFieldOperationsPanelVisible;
            CloseAllNavFlyouts();
            IsFieldOperationsPanelVisible = open;
        });

        ToggleFieldToolsPanelCommand = new RelayCommand(() =>
        {
            bool open = !IsFieldToolsPanelVisible;
            CloseAllNavFlyouts();
            IsFieldToolsPanelVisible = open;
        });

        ToggleNetworkIoPanelCommand = new RelayCommand(() =>
        {
            bool open = !IsNetworkIoPanelVisible;
            CloseAllNavFlyouts();
            IsNetworkIoPanelVisible = open;
        });

        // The fly-out close (X) is a pure close, not a toggle: a toggle would
        // re-open the panel because the bubbling item-close already shut it.
        CloseAllNavFlyoutsCommand = new RelayCommand(CloseAllNavFlyouts);

        // Back from a Field Tools tool overlay (boundary recording, recorded path,
        // offset-fix pad): close the overlays and reopen the Field Tools fly-out.
        // These are bool-visibility panels, not DialogType dialogs, so they use
        // this dedicated command rather than the dialog back-stack.
        BackToFieldToolsCommand = new RelayCommand(() =>
        {
            IsBoundaryPanelVisible = false;
            IsRecordedPathPanelVisible = false;
            State.UI.IsOffsetFixPanelVisible = false;
            // Route through the chain Back path so Field Tools reopens where the
            // chain currently is (flagged reopen) rather than snapping home.
            ReopenFlyout(NavFlyout.FieldTools);
        });

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
            ApplyThemeVariant(IsDayMode);
            // Disable auto day/night when user manually toggles theme
            _configStore.Display.AutoDayNight = false;
        });

        Toggle2D3DCommand = new RelayCommand(() =>
        {
            if (Is2DMode)
            {
                // Switching to 3D: restore last 3D pitch
                Is2DMode = false;
                CameraPitch = _last3DPitch;
                _mapService.Set3DMode(true);
            }
            else
            {
                // Switching to 2D: overhead view
                Is2DMode = true;
                CameraPitch = -90.0;
                _mapService.Set3DMode(false);
            }
        });

        ToggleNorthUpCommand = new RelayCommand(() =>
        {
            IsNorthUp = !IsNorthUp;
            _mapService.SetNorthUp(IsNorthUp);
        });

        ToggleCameraModeCommand = new RelayCommand(() =>
        {
            var oldMode = CameraMode;
            // Explicit 4-state cycle: H -> N -> M -> C -> H
            CameraMode = CameraMode switch
            {
                Models.CameraMode.HeadingUp => Models.CameraMode.NorthUp,
                Models.CameraMode.NorthUp => Models.CameraMode.Map,
                Models.CameraMode.Map => Models.CameraMode.Free,
                Models.CameraMode.Free => Models.CameraMode.HeadingUp,
                _ => Models.CameraMode.Map
            };
            Console.WriteLine($"[Compass] {oldMode} -> {CameraMode}");
        });

        // Camera controls - tilt transitions between 2D and 3D automatically
        // "Tilt Down" = look more toward horizon = more 3D (pitch increases toward -10)
        // "Tilt Up" = look more overhead = more 2D (pitch decreases toward -90)
        IncreaseCameraPitchCommand = new RelayCommand(() =>
        {
            double newPitch = CameraPitch - 5.0;
            if (newPitch <= -90.0)
            {
                Is2DMode = true;
                CameraPitch = -90.0;
                _mapService.Set3DMode(false);
            }
            else
            {
                CameraPitch = newPitch;
            }
        });

        DecreaseCameraPitchCommand = new RelayCommand(() =>
        {
            if (Is2DMode)
            {
                Is2DMode = false;
                CameraPitch = -85.0;
                _mapService.Set3DMode(true);
            }
            else
            {
                CameraPitch += 5.0;
            }
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

        // iOS Sheet toggle commands
        ToggleFileMenuCommand = new RelayCommand(() =>
        {
            IsFileMenuVisible = !IsFileMenuVisible;
        });

        ToggleFieldToolsCommand = new RelayCommand(() =>
        {
            IsFieldToolsVisible = !IsFieldToolsVisible;
        });

        ToggleSettingsCommand = new RelayCommand(() =>
        {
            IsSettingsVisible = !IsSettingsVisible;
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

    /// <summary>
    /// Applies the Avalonia theme variant based on day/night mode.
    /// Day mode = Light theme, Night mode = Dark theme.
    /// </summary>
    internal static void ApplyThemeVariant(bool isDayMode)
    {
        var app = Application.Current;
        if (app == null) return; // headless daemon — no Avalonia Application to theme

        var variant = isDayMode ? ThemeVariant.Light : ThemeVariant.Dark;
        // RequestedThemeVariant is UI-thread-affine. The windowed App calls this on the UI
        // thread (direct set); the in-process launcher constructs the VM on a worker thread
        // while an Avalonia Application IS live, so marshal to the UI thread there instead of
        // throwing "calling thread cannot access this object".
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            app.RequestedThemeVariant = variant;
        else
            Avalonia.Threading.Dispatcher.UIThread.Post(() => app.RequestedThemeVariant = variant);
    }
}
