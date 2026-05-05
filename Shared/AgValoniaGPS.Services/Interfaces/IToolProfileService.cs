// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using AgValoniaGPS.Models.Configuration;

namespace AgValoniaGPS.Services.Interfaces;

/// <summary>
/// Service for managing tool profiles (implement geometry + sections).
/// Mirrors AgOpenGPS 6.8.2's split: a tool profile lives independently
/// from the vehicle profile so one implement can be reused across multiple
/// tractors. See <see cref="IVehicleProfileService"/> for the parallel
/// vehicle-side service.
/// </summary>
public interface IToolProfileService
{
    /// <summary>
    /// Directory where tool profiles are stored
    /// (~/Documents/AgValoniaGPS/Tools by default).
    /// </summary>
    string ToolsDirectory { get; }

    /// <summary>
    /// List of available tool profile names (filenames without extension),
    /// case-insensitively de-duplicated and alphabetically sorted.
    /// </summary>
    List<string> GetAvailableProfiles();

    /// <summary>
    /// Loads a tool profile by name into the ConfigurationStore.
    /// Returns true if the file exists and was deserialized successfully.
    /// </summary>
    bool Load(string profileName, ConfigurationStore store);

    /// <summary>
    /// Saves the current ConfigurationStore tool/section state as a JSON
    /// profile under <paramref name="profileName"/>.
    /// </summary>
    void Save(string profileName, ConfigurationStore store);

    /// <summary>
    /// Resets the tool/section sub-stores in <paramref name="store"/> to
    /// built-in defaults and saves the result under
    /// <paramref name="profileName"/>.
    /// </summary>
    void CreateDefaultProfile(string profileName, ConfigurationStore store);

    /// <summary>Rename an existing tool profile file (see vehicle counterpart).</summary>
    bool Rename(string oldName, string newName);

    /// <summary>Delete a tool profile file (see vehicle counterpart).</summary>
    bool Delete(string profileName);
}
