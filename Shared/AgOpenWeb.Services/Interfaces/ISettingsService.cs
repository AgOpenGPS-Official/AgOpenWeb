// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

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
