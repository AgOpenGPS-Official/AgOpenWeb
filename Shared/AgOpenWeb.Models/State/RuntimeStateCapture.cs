// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgOpenWeb.Models.State;

/// <summary>
/// What a bug-report dump needs from the live <see cref="ApplicationState"/>, captured on
/// the UI thread as plain values: the runtime-state JSON and the open field's folder. The
/// dump service takes this instead of the state itself, so the UI-bound mirror is read only
/// where it is written (CONTRIBUTING, Threading Model).
/// </summary>
/// <param name="Json">The <c>runtime_state.json</c> entry: vehicle, field, guidance and UI summary.</param>
/// <param name="FieldDirectory">The open field's folder, or null when no field is open.</param>
public sealed record RuntimeStateCapture(string Json, string? FieldDirectory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    /// <summary>Capture the state as it is now. Call on the thread that owns it.</summary>
    public static RuntimeStateCapture From(ApplicationState state)
    {
        var snapshot = new
        {
            Vehicle = new
            {
                state.Vehicle.Latitude,
                state.Vehicle.Longitude,
                state.Vehicle.Easting,
                state.Vehicle.Northing,
                state.Vehicle.Heading,
                state.Vehicle.Speed,
                state.Vehicle.FixQuality,
                state.Vehicle.SatelliteCount,
                state.Vehicle.Hdop,
            },
            Field = new
            {
                ActiveField = state.Field.ActiveField?.Name ?? "None",
                HasBoundary = state.Field.CurrentBoundary?.IsValid ?? false,
                state.Field.HasHeadland,
                state.Field.HeadlandDistance,
                TrackCount = state.Field.Tracks.Count,
                ActiveTrack = state.Field.ActiveTrack?.Name,
                OriginLat = state.Field.OriginLatitude,
                OriginLon = state.Field.OriginLongitude,
                state.Field.DriftEasting,
                state.Field.DriftNorthing,
            },
            Guidance = new
            {
                state.Guidance.CrossTrackError,
                state.Guidance.SteerAngle,
                state.Guidance.HeadingError,
            },
            UI = new
            {
                ActiveDialog = state.UI.ActiveDialog.ToString(),
                state.UI.IsSimulatorPanelVisible,
                state.UI.IsBoundaryPanelVisible,
            },
        };
        return new RuntimeStateCapture(
            JsonSerializer.Serialize(snapshot, JsonOptions),
            state.Field.ActiveField?.DirectoryPath);
    }
}
