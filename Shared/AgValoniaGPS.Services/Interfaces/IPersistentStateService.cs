// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using AgValoniaGPS.Models.State;
using AgValoniaGPS.Services.Storage;

namespace AgValoniaGPS.Services.Interfaces;

/// <summary>
/// Persists the second application-state store (<see cref="PersistentAppState"/>)
/// to <c>appstate.json</c>. The counterpart of <c>ISettingsService</c>/
/// <c>IConfigurationService</c> for the state tier: load on startup, save on
/// close/background. Crash-safe via <see cref="AtomicJsonFile"/> with a
/// <c>.bak</c> last-known-good copy.
/// </summary>
public interface IPersistentStateService
{
    /// <summary>The live, in-memory persistent-state store (single source of truth).</summary>
    PersistentAppState State { get; }

    /// <summary>How the most recent <see cref="Load"/> resolved the state file.</summary>
    LoadOutcome LastLoadStatus { get; }

    /// <summary>
    /// Load <c>appstate.json</c> into <see cref="State"/>. On a missing state
    /// file, performs the one-way migration from the legacy
    /// <c>appsettings.json</c> (where these values used to live) so existing
    /// users don't lose their window/last-field/sim-position state on upgrade.
    /// </summary>
    bool Load();

    /// <summary>Save <see cref="State"/> to <c>appstate.json</c>.</summary>
    bool Save();

    /// <summary>Absolute path of the state file (for diagnostics).</summary>
    string GetStateFilePath();
}
