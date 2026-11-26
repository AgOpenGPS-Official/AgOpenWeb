// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;

namespace AgOpenWeb.Services.Interfaces
{
    /// <summary>
    /// Service for managing display and navigation settings
    /// </summary>
    public interface IDisplaySettingsService
    {
        // Grid display
        bool IsGridOn { get; set; }
        event EventHandler<bool>? GridVisibilityChanged;

        // Day/Night mode
        bool IsDayMode { get; set; }
        event EventHandler<bool>? DayNightModeChanged;

        // Camera settings
        double CameraPitch { get; set; }
        bool Is2DMode { get; set; }
        bool IsNorthUp { get; set; }
        event EventHandler<double>? CameraPitchChanged;
        event EventHandler<bool>? ViewModeChanged;

        // Methods
        void IncreaseCameraPitch();
        void DecreaseCameraPitch();
        void ToggleGrid();
        void ToggleDayNight();
        void Toggle2D3D();
        void ToggleNorthUp();

        // Settings persistence
        void LoadSettings();
        void SaveSettings();
    }
}
