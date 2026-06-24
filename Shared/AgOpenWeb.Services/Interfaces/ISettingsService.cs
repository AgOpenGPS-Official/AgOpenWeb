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

using AgOpenWeb.Models;
using AgOpenWeb.Services.Storage;
using System;

namespace AgOpenWeb.Services.Interfaces
{
    /// <summary>
    /// Service for managing application settings persistence
    /// </summary>
    public interface ISettingsService
    {
        /// <summary>
        /// Current application settings
        /// </summary>
        AppSettings Settings { get; }

        /// <summary>
        /// How the most recent <see cref="Load"/> resolved the settings file.
        /// <see cref="LoadOutcome.RecoveredFromBackup"/> or
        /// <see cref="LoadOutcome.CorruptNoBackup"/> means the primary file was
        /// damaged; the UI surfaces a recovery prompt on these.
        /// </summary>
        LoadOutcome LastLoadStatus { get; }

        /// <summary>
        /// Last-write time of the backup that <see cref="Load"/> recovered from,
        /// when <see cref="LastLoadStatus"/> is
        /// <see cref="LoadOutcome.RecoveredFromBackup"/>; otherwise <c>null</c>.
        /// </summary>
        DateTime? RecoveredBackupTime { get; }

        /// <summary>
        /// Raised when settings are loaded
        /// </summary>
        event EventHandler<AppSettings>? SettingsLoaded;

        /// <summary>
        /// Raised when settings are saved
        /// </summary>
        event EventHandler<AppSettings>? SettingsSaved;

        /// <summary>
        /// Load settings from disk
        /// </summary>
        /// <returns>True if settings were loaded successfully</returns>
        bool Load();

        /// <summary>
        /// Save settings to disk
        /// </summary>
        /// <returns>True if settings were saved successfully</returns>
        bool Save();

        /// <summary>
        /// Reset settings to defaults
        /// </summary>
        void ResetToDefaults();

        /// <summary>
        /// Get the path where settings are stored
        /// </summary>
        string GetSettingsFilePath();
    }
}
