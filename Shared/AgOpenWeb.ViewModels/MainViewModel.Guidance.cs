// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Track;
using AgOpenWeb.Services.AutoSteer;
using AgOpenWeb.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace AgOpenWeb.ViewModels;

/// <summary>
/// MainViewModel partial class containing guidance-related state and event handlers.
/// Guidance computation is done by GpsPipelineService on a background thread.
/// Results are applied via ApplyGpsCycleResult.
/// </summary>
public partial class MainViewModel
{
    #region Guidance State

    // Track guidance state (carried between iterations for track snapping/preview)
    private TrackGuidanceState? _trackGuidanceState;

    #endregion

    #region AutoSteer Event Handlers

    /// <summary>
    /// The service stopped steering because guidance went stale (#169). Steering is already
    /// off at the module; take AutoSteer off in the app too so the operator re-engages
    /// deliberately instead of the tractor lunging at a line that moved during the stall.
    /// </summary>
    private void OnGuidanceLost(object? sender, EventArgs e) => _dispatcher.Post(() =>
    {
        _logger.LogWarning("[AutoSteer] No guidance update for {Limit:F1} s while engaged — AutoSteer turned off (#169)",
            AutoSteerService.GuidanceStaleLimit.TotalSeconds);
        if (IsAutoSteerEngaged) ToggleAutoSteerCommand?.Execute("no GPS/guidance update for over 1 s");
        ReportFailure("AutoSteer off: no GPS/guidance update for over 1 s");
    });

    /// <summary>
    /// The service stopped steering on a pressure or current sensor kickout (PGN 250). Take
    /// AutoSteer off in the app too, as when guidance goes stale; otherwise the button stays
    /// on while nothing steers, and nothing says why.
    /// </summary>
    private void OnSensorKickout(object? sender, string reason) => _dispatcher.Post(() =>
    {
        if (IsAutoSteerEngaged) ToggleAutoSteerCommand?.Execute(reason);
        ReportFailure($"AutoSteer off: {reason}");
    });

    /// <summary>
    /// Every way AutoSteer goes off logs why here, so logs.txt in a bug report has the
    /// reason: the status line only shows the latest message, and the steer module sees
    /// only that PGN 254 stopped asking it to steer.
    /// </summary>
    private void LogAutoSteerOff(string reason) =>
        _logger.LogInformation("[AutoSteer] Off: {Reason}", reason);

    /// <summary>
    /// Why the steer module stopped steering, as far as the app can tell. The module's own
    /// pressure or current kickout clears the same PGN 253 bit as its switch and button, so
    /// the latest sensor reading (PGN 250) goes with it when a sensor is in use.
    /// </summary>
    private string ModuleStoppedSteeringReason()
    {
        const string reason = "steer module stopped steering (switch, button or kickout)";
        var a = ConfigStore.AutoSteer;
        double pct = _autoSteerService.SensorPercent;
        if (a.PressureSensorEnabled)
            return FormattableString.Invariant($"{reason}, pressure sensor {pct:F0}% (trip point {a.PressureTripPoint}%)");
        if (a.CurrentSensorEnabled)
            return FormattableString.Invariant($"{reason}, current sensor {pct:F0}% (trip point {a.CurrentTripPoint}%)");
        return reason;
    }

    private void OnAutoSteerStateUpdated(object? sender, VehicleStateSnapshot state)
    {
        // AutoSteer fires StateUpdated at the unified control-loop rate
        // (100 Hz, decoupled from GPS — see Plans/Completed/
        // UNIFIED_CONTROL_LOOP_PLAN.md). Latency display can't sit on this
        // path — at 100 Hz the PropertyChanged → TextLayout cascade for
        // the status-bar "Lat: 0.00ms" readout was 5.9% main-thread CPU
        // (Phase 2b trace). We cache the latest value here and let
        // OnStatusTick publish it at 5 Hz.
        //
        // TramControlByte → map service is structural (not display) and
        // stays on the source rate. Single-double / single-byte writes are
        // atomic on x86/ARM — no lock needed.
        _latestGpsToPgnLatencyMs = state.TotalLatencyMs;
        if (_dispatcher.CheckAccess())
        {
            TramControlByte = state.TramState;
            _mapService.SetTramControlByte(state.TramState);
        }
        else
        {
            _dispatcher.Post(() =>
            {
                TramControlByte = state.TramState;
                _mapService.SetTramControlByte(state.TramState);
            });
        }
    }

    #endregion
}
