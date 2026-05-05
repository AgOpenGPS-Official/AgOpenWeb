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
using AgValoniaGPS.Models;
using AgValoniaGPS.Models.Configuration;
using AgValoniaGPS.Models.Timing;
using AgValoniaGPS.Services.Interfaces;

namespace AgValoniaGPS.Services;

/// <summary>
/// Implementation of GPS service for data processing
/// </summary>
public class GpsService : IGpsService
{
    public event EventHandler<GpsData>? GpsDataUpdated;

    public GpsData CurrentData { get; private set; } = new();

    public bool IsConnected { get; private set; }

    private DateTime _lastGpsDataReceived = DateTime.MinValue;
    private DateTime _lastImuDataReceived = DateTime.MinValue;
    private const int GPS_TIMEOUT_MS = 300; // 10Hz data = 100ms cycle, allow 300ms
    private const int IMU_TIMEOUT_MS = 300; // 10Hz data = 100ms cycle, allow 300ms

    public void Start()
    {
        IsConnected = true;
    }

    public void Stop()
    {
        IsConnected = false;
    }

    public void ProcessNmeaSentence(string sentence)
    {
        if (string.IsNullOrWhiteSpace(sentence))
            return;

        // Called by the parser after a sentence is parsed.
        // Just trigger event notification
        GpsDataUpdated?.Invoke(this, CurrentData);
    }

    /// <summary>
    /// Update GPS data directly (called by NMEA parser).
    /// Stores the raw antenna position. Antenna-to-pivot transform is
    /// applied by GpsPipelineService.ProcessCycle (step 1b) so it lives
    /// in one place and runs consistently — the previous transform here
    /// was skipped on (0,0) ticks (sim sitting at field origin) but ran
    /// once N became non-zero, producing a discontinuous antenna offset
    /// that the heading fusion read as a 90° east jump on the very first
    /// non-zero-speed tick. (See diag in /tmp/flip.log.)
    /// </summary>
    public void UpdateGpsData(GpsData newData)
    {
        CurrentData = newData;
        _lastGpsDataReceived = Clock.Current.Now;
        IsConnected = newData.IsValid;
        GpsDataUpdated?.Invoke(this, CurrentData);
    }

    /// <summary>
    /// Update IMU data timestamp (called when IMU data received)
    /// </summary>
    public void UpdateImuData()
    {
        _lastImuDataReceived = Clock.Current.Now;
    }

    /// <summary>
    /// Check if GPS data is flowing (10Hz expected)
    /// </summary>
    public void MarkGpsReceived()
    {
        _lastGpsDataReceived = Clock.Current.Now;
        IsConnected = true;
    }

    public bool IsGpsDataOk()
    {
        bool ok = (Clock.Current.Now - _lastGpsDataReceived).TotalMilliseconds < GPS_TIMEOUT_MS;

        if (!ok && IsConnected)
        {
            IsConnected = false;
        }

        return ok;
    }

    /// <summary>
    /// Check if IMU data is flowing (10Hz expected)
    /// </summary>
    public bool IsImuDataOk()
    {
        return (Clock.Current.Now - _lastImuDataReceived).TotalMilliseconds < IMU_TIMEOUT_MS;
    }
}