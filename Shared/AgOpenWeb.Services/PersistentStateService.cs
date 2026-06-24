// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.IO;
using System.Text.Json;
using AgOpenWeb.Models;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Storage;

namespace AgOpenWeb.Services;

/// <summary>
/// Persists <see cref="PersistentAppState"/> to <c>appstate.json</c>, mirroring
/// how <c>SettingsService</c> persists <c>AppSettings</c>. The store object is
/// serialized directly (public properties); INotify backing fields are ignored.
/// </summary>
public class PersistentStateService : IPersistentStateService
{
    private const string StateFileName = "appstate.json";
    private const string LegacySettingsFileName = "appsettings.json";

    private readonly string _stateFilePath;
    private readonly string _settingsDirectory;

    public PersistentAppState State { get; }

    public LoadOutcome LastLoadStatus { get; private set; } = LoadOutcome.Missing;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        // Properties absent from the JSON keep their C# default initializers
        // (e.g. SimulatorPanelX = NaN) instead of resetting to CLR defaults.
        PreferredObjectCreationHandling = System.Text.Json.Serialization.JsonObjectCreationHandling.Populate,
    };

    /// <summary>
    /// Production constructor. The state file lives beside <c>appsettings.json</c>
    /// in the same Documents/AgOpenWeb directory.
    /// </summary>
    public PersistentStateService(PersistentAppState state, ISettingsService settingsService)
        : this(state, Path.GetDirectoryName(settingsService.GetSettingsFilePath()) ?? string.Empty)
    {
    }

    /// <summary>Test-friendly constructor: pass an isolated temp directory.</summary>
    public PersistentStateService(PersistentAppState state, string settingsDirectory)
    {
        State = state;
        _settingsDirectory = settingsDirectory;
        _stateFilePath = Path.Combine(_settingsDirectory, StateFileName);
    }

    public bool Load()
    {
        var result = AtomicJsonFile.Read<PersistentAppState>(_stateFilePath, JsonOptions);
        LastLoadStatus = result.Outcome;

        if (result.Outcome == LoadOutcome.Missing)
        {
            // No state file yet. Either a true first run, or an upgrade from a
            // build that kept these values in appsettings.json — migrate those
            // once so the user's window/last-field/sim state survives.
            bool migrated = MigrateFromLegacyAppSettings();
            State.IsFirstRun = !migrated;
            State.LastRunDate = DateTime.Now;
            return false;
        }

        if (result.Loaded && result.Value != null)
        {
            ApplySnapshot(result.Value);
            State.IsFirstRun = false;
            State.LastRunDate = DateTime.Now;
            return true;
        }

        // CorruptNoBackup: file existed but was unusable. Keep defaults.
        State.IsFirstRun = false;
        State.LastRunDate = DateTime.Now;
        return false;
    }

    public bool Save()
    {
        try
        {
            if (!Directory.Exists(_settingsDirectory))
                Directory.CreateDirectory(_settingsDirectory);

            State.LastRunDate = DateTime.Now;
            AtomicJsonFile.WriteJson(_stateFilePath, State, JsonOptions);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PersistentState] Save failed: {ex.Message}");
            return false;
        }
    }

    public string GetStateFilePath() => _stateFilePath;

    /// <summary>Copy a loaded snapshot into the live store (consumers hold this reference).</summary>
    private void ApplySnapshot(PersistentAppState s)
    {
        State.WindowWidth = s.WindowWidth;
        State.WindowHeight = s.WindowHeight;
        State.WindowX = s.WindowX;
        State.WindowY = s.WindowY;
        State.WindowMaximized = s.WindowMaximized;
        State.SimulatorPanelX = s.SimulatorPanelX;
        State.SimulatorPanelY = s.SimulatorPanelY;
        State.SimulatorPanelVisible = s.SimulatorPanelVisible;
        State.CameraZoom = s.CameraZoom;
        State.CameraPitch = s.CameraPitch;
        State.CameraMode = s.CameraMode;
        State.IsDayMode = s.IsDayMode;
        State.Is2DMode = s.Is2DMode;
        State.IsNorthUp = s.IsNorthUp;
        State.SimulatorLatitude = s.SimulatorLatitude;
        State.SimulatorLongitude = s.SimulatorLongitude;
        State.SimulatorSpeed = s.SimulatorSpeed;
        State.SimulatorSteerAngle = s.SimulatorSteerAngle;
        State.LastOpenedField = s.LastOpenedField;
        State.BoundaryDrawRightSide = s.BoundaryDrawRightSide;
        State.BoundaryDrawAtPivot = s.BoundaryDrawAtPivot;
        State.BoundaryOffset = s.BoundaryOffset;
        // IsFirstRun / LastRunDate are recomputed by Load(), not copied.
    }

    /// <summary>
    /// One-way migration: pull the state-typed values that older builds stored
    /// in <c>appsettings.json</c> into the new state store. Runs only when no
    /// <c>appstate.json</c> exists yet, so it fires at most once per install.
    /// Reads the legacy file raw (the typed AppSettings DTO no longer carries
    /// these fields) and tolerates any missing key.
    /// </summary>
    /// <returns>true if a legacy settings file was found (i.e. this is an upgrade, not a first run).</returns>
    private bool MigrateFromLegacyAppSettings()
    {
        var legacyPath = Path.Combine(_settingsDirectory, LegacySettingsFileName);
        if (!File.Exists(legacyPath))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(legacyPath));
            var root = doc.RootElement;

            if (TryDouble(root, "windowWidth", out var d)) State.WindowWidth = d;
            if (TryDouble(root, "windowHeight", out d)) State.WindowHeight = d;
            if (TryDouble(root, "windowX", out d)) State.WindowX = d;
            if (TryDouble(root, "windowY", out d)) State.WindowY = d;
            if (TryBool(root, "windowMaximized", out var b)) State.WindowMaximized = b;
            if (TryDouble(root, "simulatorPanelX", out d)) State.SimulatorPanelX = d;
            if (TryDouble(root, "simulatorPanelY", out d)) State.SimulatorPanelY = d;
            if (TryBool(root, "simulatorPanelVisible", out b)) State.SimulatorPanelVisible = b;
            // cameraZoom is intentionally NOT migrated: the legacy field was a
            // dead value never wired to the live map zoom, so importing it would
            // restore a meaningless zoom. New installs start at the 1.0 default.
            if (TryDouble(root, "cameraPitch", out d)) State.CameraPitch = d;
            if (TryInt(root, "cameraMode", out var i)) State.CameraMode = (CameraMode)i;
            if (TryBool(root, "isDayMode", out b)) State.IsDayMode = b;
            if (TryDouble(root, "simulatorLatitude", out d)) State.SimulatorLatitude = d;
            if (TryDouble(root, "simulatorLongitude", out d)) State.SimulatorLongitude = d;
            if (TryDouble(root, "simulatorSpeed", out d)) State.SimulatorSpeed = d;
            if (TryDouble(root, "simulatorSteerAngle", out d)) State.SimulatorSteerAngle = d;
            if (TryString(root, "lastOpenedField", out var str)) State.LastOpenedField = str;
            // Is2DMode / IsNorthUp / CurrentFieldName were never persisted in the
            // legacy file — nothing to migrate; they keep their defaults.
            // HasMigratedIsMetric stays in AppSettings (config-migration meta).
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PersistentState] Legacy migration failed: {ex.Message}");
        }

        return true;
    }

    private static bool TryDouble(JsonElement root, string name, out double value)
    {
        value = 0;
        if (root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number)
            return el.TryGetDouble(out value);
        return false;
    }

    private static bool TryInt(JsonElement root, string name, out int value)
    {
        value = 0;
        if (root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number)
            return el.TryGetInt32(out value);
        return false;
    }

    private static bool TryBool(JsonElement root, string name, out bool value)
    {
        value = false;
        if (root.TryGetProperty(name, out var el) &&
            (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False))
        {
            value = el.GetBoolean();
            return true;
        }
        return false;
    }

    private static bool TryString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
        {
            value = el.GetString() ?? string.Empty;
            return true;
        }
        return false;
    }
}
