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

using System;

using Microsoft.Extensions.Logging;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Services.Interfaces;

using CommunityToolkit.Mvvm.ComponentModel;

namespace AgValoniaGPS.ViewModels;

/// <summary>
/// MainViewModel partial class containing Simulator state, properties, and event handlers.
/// Handles GPS simulation for testing guidance without real GPS hardware.
/// </summary>
public partial class MainViewModel
{
    #region Simulator Fields

    // LocalPlane for coordinate conversion (created on first GPS data update)
    private AgValoniaGPS.Models.LocalPlane? _simulatorLocalPlane;

    // Backing fields for properties
    private bool _isSimulatorEnabled;
    private double _simulatorSteerAngle;
    private double _simulatorSpeedKph;
    private bool _isSimulatorSpeed10x;

    #endregion

    #region Simulator Event Handlers

    private void OnSimulatorTick(object? sender, EventArgs e)
    {
        // Call simulator Tick with current steer angle
        _simulatorService.Tick(SimulatorSteerAngle);
    }

    private void OnSimulatorGpsDataUpdated(object? sender, GpsSimulationEventArgs e)
    {
        // Ignore GPS data when simulator is disabled
        if (!_isSimulatorEnabled) return;

        // The simulator builds GpsData and feeds it to the GpsService.
        // The GpsPipelineService (subscribed to GpsService.GpsDataUpdated)
        // handles all heavy processing: tool position, guidance, section control,
        // coverage painting, and boundary checks on a background thread.

        var simulatedData = e.Data;

        // Create LocalPlane if not yet created
        // Use FIELD origin if a field is loaded, otherwise use simulator position
        if (_simulatorLocalPlane == null)
        {
            var sharedProps = new AgValoniaGPS.Models.SharedFieldProperties();
            AgValoniaGPS.Models.Wgs84 origin;

            if (State.Field.OriginLatitude != 0 && State.Field.OriginLongitude != 0)
            {
                // Use field origin so coordinates match the field's boundary/track data
                origin = new AgValoniaGPS.Models.Wgs84(State.Field.OriginLatitude, State.Field.OriginLongitude);
                _logger.LogDebug("[Simulator] Using field origin: {FieldOriginLatitude}, {FieldOriginLongitude}", State.Field.OriginLatitude, State.Field.OriginLongitude);
            }
            else
            {
                // No field loaded, use simulator position as origin
                origin = simulatedData.Position;
                _logger.LogDebug("[Simulator] Using simulator position as origin: {Latitude}, {Longitude}", origin.Latitude, origin.Longitude);
            }

            _simulatorLocalPlane = new AgValoniaGPS.Models.LocalPlane(origin, sharedProps);
        }

        // Convert WGS84 to local coordinates (Northing/Easting)
        var localCoord = _simulatorLocalPlane.ConvertWgs84ToGeoCoord(simulatedData.Position);

        // Build Position object with both WGS84 and UTM coordinates
        var position = new AgValoniaGPS.Models.Position
        {
            Latitude = simulatedData.Position.Latitude,
            Longitude = simulatedData.Position.Longitude,
            Altitude = simulatedData.Altitude,
            Easting = localCoord.Easting,
            Northing = localCoord.Northing,
            Heading = simulatedData.HeadingDegrees,
            Speed = simulatedData.SpeedKmh / 3.6  // Convert km/h to m/s
        };

        // Build GpsData object
        var gpsData = new AgValoniaGPS.Models.GpsData
        {
            CurrentPosition = position,
            FixQuality = 4,  // RTK Fixed
            SatellitesInUse = simulatedData.SatellitesTracked,
            Hdop = simulatedData.Hdop,
            DifferentialAge = 0.0,
            Timestamp = Models.Timing.Clock.Current.Now
        };

        // Feed into GpsService — this fires GpsDataUpdated which the pipeline picks up
        _gpsService.UpdateGpsData(gpsData);
    }

    #endregion

    #region Simulator Properties

    public bool IsSimulatorEnabled
    {
        get => _isSimulatorEnabled;
        set
        {
            // Hardware parity stop: when DISABLING, emit one final stationary
            // frame BEFORE flipping the flag. OnSimulatorGpsDataUpdated guards
            // on !_isSimulatorEnabled and drops events once the flag flips,
            // so a Tick after SetProperty would never reach the GPS pipeline.
            // Without this, the position estimator's last snapshot retains
            // non-zero speed, the 30 Hz vehicle render-pull tick dead-reckons
            // the tractor forward up to MaxStaleSeconds (1 s), and the
            // implement (which only updates on cycle results) sits frozen.
            if (!value && _isSimulatorEnabled)
            {
                _simulatorService.StepDistance = 0;
                _simulatorService.IsAcceleratingForward = false;
                _simulatorService.IsAcceleratingBackward = false;
                _simulatorService.Tick(SimulatorSteerAngle);
            }

            if (SetProperty(ref _isSimulatorEnabled, value))
            {
                State.Simulator.IsEnabled = value; // mirror for the web-UI projector
                // Persist the "simulator is the GPS source" preference through
                // the store (config), not by writing the DTO directly.
                ConfigStore.Simulator.Enabled = value;
                _configurationService.SaveAppSettings();

                // Start or stop simulator timer based on enabled state
                if (value)
                {
                    // Initialize simulator with the last saved position (state).
                    _simulatorService.Initialize(new AgValoniaGPS.Models.Wgs84(
                        PersistentState.SimulatorLatitude,
                        PersistentState.SimulatorLongitude));

                    _simulatorTimer.Start();
                    StatusMessage = $"Simulator ON at {PersistentState.SimulatorLatitude:F8}, {PersistentState.SimulatorLongitude:F8}";
                }
                else
                {
                    _simulatorTimer.Stop();
                    StatusMessage = "Simulator OFF";
                }
            }
        }
    }

    public double SimulatorSteerAngle
    {
        get => _simulatorSteerAngle;
        set
        {
            SetProperty(ref _simulatorSteerAngle, value);
            State.Simulator.SteerAngle = value; // mirror for the web-UI projector
            PersistentState.SimulatorSteerAngle = value; // persisted on close
            OnPropertyChanged(nameof(SimulatorSteerAngleDisplay)); // Notify display property
            if (_isSimulatorEnabled)
            {
                _simulatorService.SteerAngle = value;
            }
        }
    }

    public string SimulatorSteerAngleDisplay => $"{_simulatorSteerAngle:F1}°";

    /// <summary>
    /// Simulator speed in kph. Range: -10 to +25 kph (or -100 to +250 with 10x enabled).
    /// Converts to/from stepDistance using formula: speedKph = stepDistance * 40
    /// </summary>
    public double SimulatorSpeedKph
    {
        get => _simulatorSpeedKph;
        set
        {
            // Clamp to valid range
            value = Math.Max(-10, Math.Min(25, value));
            SetProperty(ref _simulatorSpeedKph, value);
            State.Simulator.SpeedKph = value; // mirror (RAW kph) for the web-UI projector
            UpdateSimulatorSpeed();
        }
    }

    /// <summary>
    /// When enabled, multiplies the speed slider value by 10 for testing large fields.
    /// </summary>
    public bool IsSimulatorSpeed10x
    {
        get => _isSimulatorSpeed10x;
        set
        {
            SetProperty(ref _isSimulatorSpeed10x, value);
            State.Simulator.Is10x = value; // mirror for the web-UI projector
            UpdateSimulatorSpeed();
            OnPropertyChanged(nameof(SimulatorSpeedDisplay));
        }
    }

    private void UpdateSimulatorSpeed()
    {
        double effectiveSpeed = _isSimulatorSpeed10x ? _simulatorSpeedKph * 10 : _simulatorSpeedKph;
        PersistentState.SimulatorSpeed = effectiveSpeed; // persisted on close
        OnPropertyChanged(nameof(SimulatorSpeedDisplay));
        if (_isSimulatorEnabled)
        {
            // Convert kph to stepDistance: stepDistance = speedKph / 40
            _simulatorService.StepDistance = effectiveSpeed / 40.0;
            // Disable acceleration when manually setting speed
            _simulatorService.IsAcceleratingForward = false;
            _simulatorService.IsAcceleratingBackward = false;
        }
    }

    public string SimulatorSpeedDisplay
    {
        get
        {
            // No "(10x)" suffix — the 10x toggle sits beside this readout and the
            // value already reflects the multiplier, so the readout stays compact
            // enough to hug the speed arrows while fitting a 3-digit speed.
            double speed = _isSimulatorSpeed10x ? _simulatorSpeedKph * 10 : _simulatorSpeedKph;
            if (ConfigStore.IsMetric)
                return $"{speed:F1} kph";
            else
                return $"{speed * 0.621371:F1} mph";
        }
    }

    #endregion

    #region Simulator Methods

    /// <summary>
    /// Set new starting coordinates for the simulator
    /// </summary>
    public void SetSimulatorCoordinates(double latitude, double longitude)
    {
        _logger.LogDebug("[SimCoords] Setting simulator to: {Latitude}, {Longitude}", latitude, longitude);

        // Reinitialize simulator with new coordinates
        _simulatorService.Initialize(new AgValoniaGPS.Models.Wgs84(latitude, longitude));
        _simulatorService.StepDistance = 0;

        // Clear LocalPlane so it will be recreated with new origin on next GPS data update
        _simulatorLocalPlane = null;

        // Reset steering
        SimulatorSteerAngle = 0;

        // Persist the last simulator position to the single source of truth —
        // the persistent-state store (appstate.json). No DTO/multi-home writes.
        PersistentState.SimulatorLatitude = latitude;
        PersistentState.SimulatorLongitude = longitude;

        var saved = _persistentStateService.Save();

        // Also update the Latitude/Longitude properties directly so that
        // the map boundary dialog uses the correct coordinates even if
        // the simulator timer hasn't ticked yet
        Latitude = latitude;
        Longitude = longitude;

        StatusMessage = saved
            ? $"Simulator reset to {latitude:F8}, {longitude:F8}"
            : $"Reset to {latitude:F8}, {longitude:F8} (save failed: {_settingsService.GetSettingsFilePath()})";
    }

    /// <summary>
    /// Get current simulator position
    /// </summary>
    public AgValoniaGPS.Models.Wgs84 GetSimulatorPosition()
    {
        return _simulatorService.CurrentPosition;
    }

    #endregion
}
