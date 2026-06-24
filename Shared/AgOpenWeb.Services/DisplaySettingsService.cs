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
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services;

/// <summary>
/// Service for managing display and navigation settings. Grid visibility is a
/// display PREFERENCE (ConfigStore.Display); the camera view, day/night value,
/// and 2D/north-up orientation are persistent STATE (PersistentAppState).
/// </summary>
public class DisplaySettingsService : IDisplaySettingsService
{
    private const double CameraPitchStep = 5.0;

    private readonly ConfigurationStore _configStore;

    public DisplaySettingsService(ConfigurationStore configStore)
    {
        _configStore = configStore;
    }

    // Grid visibility is config; the view/orientation values are persistent state.
    private DisplayConfig Display => _configStore.Display;
    private static PersistentAppState Persistent => PersistentAppState.Instance;

    // Grid display - delegates to DisplayConfig
    public bool IsGridOn
    {
        get => Display.GridVisible;
        set
        {
            if (Display.GridVisible != value)
            {
                Display.GridVisible = value;
                GridVisibilityChanged?.Invoke(this, value);
            }
        }
    }
    public event EventHandler<bool>? GridVisibilityChanged;

    // Day/Night mode - delegates to DisplayConfig
    public bool IsDayMode
    {
        get => Persistent.IsDayMode;
        set
        {
            if (Persistent.IsDayMode != value)
            {
                Persistent.IsDayMode = value;
                DayNightModeChanged?.Invoke(this, value);
            }
        }
    }
    public event EventHandler<bool>? DayNightModeChanged;

    // Camera settings - delegates to DisplayConfig
    public double CameraPitch
    {
        get => Persistent.CameraPitch;
        set
        {
            // Clamp pitch between -90 (overhead/0 deg) and -20 (max tilt/70 deg)
            var clampedValue = Math.Max(-90, Math.Min(-20, value));
            if (Math.Abs(Persistent.CameraPitch - clampedValue) > 0.01)
            {
                Persistent.CameraPitch = clampedValue;
                CameraPitchChanged?.Invoke(this, clampedValue);
            }
        }
    }
    public event EventHandler<double>? CameraPitchChanged;

    public bool Is2DMode
    {
        get => Persistent.Is2DMode;
        set
        {
            if (Persistent.Is2DMode != value)
            {
                Persistent.Is2DMode = value;
                // When switching to 2D, set pitch to -90 (straight down)
                // When switching to 3D, restore previous pitch or default
                if (value)
                {
                    CameraPitch = -90.0;
                }
                else
                {
                    CameraPitch = -60.0; // Default 3D pitch (30 deg)
                }
                ViewModeChanged?.Invoke(this, value);
            }
        }
    }

    public bool IsNorthUp
    {
        get => Persistent.IsNorthUp;
        set
        {
            if (Persistent.IsNorthUp != value)
            {
                Persistent.IsNorthUp = value;
                ViewModeChanged?.Invoke(this, value);
            }
        }
    }
    public event EventHandler<bool>? ViewModeChanged;

    public void IncreaseCameraPitch()
    {
        CameraPitch += CameraPitchStep;
    }

    public void DecreaseCameraPitch()
    {
        CameraPitch -= CameraPitchStep;
    }

    public void ToggleGrid()
    {
        IsGridOn = !IsGridOn;
    }

    public void ToggleDayNight()
    {
        IsDayMode = !IsDayMode;
    }

    public void Toggle2D3D()
    {
        Is2DMode = !Is2DMode;
    }

    public void ToggleNorthUp()
    {
        IsNorthUp = !IsNorthUp;
    }

    public void LoadSettings()
    {
        // Settings are now loaded via ConfigurationService.LoadAppSettings()
        // This method exists for interface compatibility but doesn't need to do anything
        // since DisplayConfig is populated when app settings are loaded.

        // Fire events to notify UI of current values
        GridVisibilityChanged?.Invoke(this, Display.GridVisible);
        DayNightModeChanged?.Invoke(this, Persistent.IsDayMode);
        CameraPitchChanged?.Invoke(this, Persistent.CameraPitch);
        ViewModeChanged?.Invoke(this, Persistent.Is2DMode);
    }

    public void SaveSettings()
    {
        // Settings are now saved via ConfigurationService.SaveAppSettings()
        // This method exists for interface compatibility
    }
}
