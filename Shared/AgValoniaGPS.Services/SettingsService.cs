// AgValoniaGPS
// Copyright (C) 2024-2025 AgValoniaGPS Contributors
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

using AgValoniaGPS.Models;
using AgValoniaGPS.Services.Interfaces;
using AgValoniaGPS.Services.Storage;
using System;
using System.IO;
using System.Text.Json;

namespace AgValoniaGPS.Services
{
    /// <summary>
    /// Service for managing application settings persistence using JSON
    /// </summary>
    public class SettingsService : ISettingsService
    {
        private const string SettingsFileName = "appsettings.json";
        private readonly string _settingsDirectory;
        private readonly string _settingsFilePath;

        public AppSettings Settings { get; private set; }

        public LoadOutcome LastLoadStatus { get; private set; } = LoadOutcome.Missing;

        public DateTime? RecoveredBackupTime { get; private set; }

        public event EventHandler<AppSettings>? SettingsLoaded;
        public event EventHandler<AppSettings>? SettingsSaved;

        public SettingsService() : this(ResolveDefaultDirectory()) { }

        /// <summary>
        /// Test-friendly constructor. Tests should pass an isolated temp
        /// directory so they don't clobber the user's real
        /// <c>~/Documents/AgValoniaGPS/appsettings.json</c>.
        /// </summary>
        public SettingsService(string settingsDirectory)
        {
            _settingsDirectory = settingsDirectory;
            _settingsFilePath = Path.Combine(_settingsDirectory, SettingsFileName);

            // Initialize with defaults
            Settings = new AppSettings();
        }

        private static string ResolveDefaultDirectory()
        {
            // Store settings in Documents/AgValoniaGPS (same as Fields)
            // This works consistently across Desktop, iOS, and Android
            var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            // Fallback to Personal if MyDocuments is empty (some platforms)
            if (string.IsNullOrEmpty(documentsPath))
            {
                documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
            }

            // Last resort fallback
            if (string.IsNullOrEmpty(documentsPath))
            {
                documentsPath = Environment.CurrentDirectory;
            }

            return Path.Combine(documentsPath, "AgValoniaGPS");
        }

        public bool Load()
        {
            // Use same options as Save to match camelCase property names and handle NaN/Infinity.
            // PreferredObjectCreationHandling = Populate ensures that properties missing from
            // the JSON file keep their C# default initializer values (e.g. FieldTextureVisible = true)
            // rather than being reset to CLR defaults (false for bool, 0 for int, etc.).
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
                PreferredObjectCreationHandling = System.Text.Json.Serialization.JsonObjectCreationHandling.Populate
            };

            // Crash-safe load: a truncated/zero-length primary (the classic
            // power-loss-during-shutdown outcome) transparently recovers from
            // the .bak last-known-good copy instead of silently resetting to
            // defaults. The outcome is recorded so the UI can prompt the user.
            var result = AtomicJsonFile.Read<AppSettings>(_settingsFilePath, options);
            LastLoadStatus = result.Outcome;
            RecoveredBackupTime = result.BackupTimestamp;

            if (result.Outcome == LoadOutcome.Missing)
            {
                // First run - use defaults and set up fields directory
                Settings = new AppSettings { IsFirstRun = true };
                InitializeFieldsDirectory();
                return false;
            }

            if (result.Loaded && result.Value != null)
            {
                Settings = result.Value;

                // Validate loaded settings and fix out-of-range values
                var fixes = Settings.ValidateAndFix();
                foreach (var fix in fixes)
                {
                    System.Diagnostics.Debug.WriteLine($"[Settings] Validation fix: {fix}");
                }

                Settings.IsFirstRun = false;
                Settings.LastRunDate = DateTime.Now;

                // Ensure fields directory is set and points to current app container
                // On iOS, the app container path can change on reinstall, so we need to
                // verify the saved path is within the current Documents directory
                var currentDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                var needsReinit = string.IsNullOrEmpty(Settings.FieldsDirectory) ||
                                  !Directory.Exists(Settings.FieldsDirectory);

                // Also reinit if the saved path is not under current Documents (iOS container changed)
                if (!needsReinit && !string.IsNullOrEmpty(currentDocuments) &&
                    !Settings.FieldsDirectory.StartsWith(currentDocuments, StringComparison.OrdinalIgnoreCase))
                {
                    needsReinit = true;
                }

                if (needsReinit)
                {
                    InitializeFieldsDirectory();
                }

                SettingsLoaded?.Invoke(this, Settings);
                return true;
            }

            // CorruptNoBackup: the file existed but neither it nor a backup was
            // usable. Fall back to defaults (not a fresh install) and let the UI
            // inform the user via LastLoadStatus.
            Settings = new AppSettings { IsFirstRun = false };
            InitializeFieldsDirectory();
            return false;
        }

        /// <summary>
        /// Initialize fields directory to default location
        /// </summary>
        private void InitializeFieldsDirectory()
        {
            // Default to Documents/AgValoniaGPS/Fields (cross-platform compatible)
            var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            Settings.FieldsDirectory = Path.Combine(documentsPath, "AgValoniaGPS", "Fields");

            // Create the directory if it doesn't exist
            if (!Directory.Exists(Settings.FieldsDirectory))
            {
                Directory.CreateDirectory(Settings.FieldsDirectory);
            }
        }

        public bool Save()
        {
            try
            {
                // Ensure directory exists
                if (!Directory.Exists(_settingsDirectory))
                {
                    Directory.CreateDirectory(_settingsDirectory);
                }

                // Update last run date
                Settings.LastRunDate = DateTime.Now;

                // Serialize with indentation for readability
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
                };

                // Atomic write: scratch file flushed to disk, then promoted over
                // the primary while rolling the prior good copy into .bak. A
                // crash mid-write can never leave a truncated primary.
                AtomicJsonFile.WriteJson(_settingsFilePath, Settings, options);

                SettingsSaved?.Invoke(this, Settings);
                return true;
            }
            catch (Exception ex)
            {
                // Write error to a debug file for diagnosis
                try
                {
                    var errorPath = Path.Combine(_settingsDirectory, "save_error.txt");
                    File.WriteAllText(errorPath, $"Path: {_settingsFilePath}\nError: {ex.Message}\n{ex.StackTrace}");
                }
                catch { }
                return false;
            }
        }

        public void ResetToDefaults()
        {
            Settings = new AppSettings
            {
                IsFirstRun = false,
                LastRunDate = DateTime.Now
            };
        }

        public string GetSettingsFilePath()
        {
            return _settingsFilePath;
        }
    }
}
