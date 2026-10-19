// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using AgOpenWeb.Models;

namespace AgOpenWeb.Services.Interfaces;

/// <summary>
/// Service for GPS data processing and management
/// </summary>
public interface IGpsService
{
    /// <summary>
    /// Event fired when new GPS data is received
    /// </summary>
    event EventHandler<GpsData>? GpsDataUpdated;

    /// <summary>
    /// Current GPS data
    /// </summary>
    GpsData CurrentData { get; }

    /// <summary>
    /// Whether GPS is currently connected and receiving data
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Start GPS service
    /// </summary>
    void Start();

    /// <summary>
    /// Stop GPS service
    /// </summary>
    void Stop();

    /// <summary>
    /// Process NMEA sentence
    /// </summary>
    void ProcessNmeaSentence(string sentence);

    /// <summary>
    /// Update GPS data from parsed NMEA sentence
    /// </summary>
    void UpdateGpsData(GpsData newData);

    /// <summary>
    /// Mark that GPS data was received (updates timeout tracking).
    /// Called by the zero-copy pipeline path which bypasses UpdateGpsData.
    /// </summary>
    void MarkGpsReceived();

    /// <summary>
    /// Mark that a real GPS fix was parsed from inbound NMEA. Stamped ONLY on the
    /// real receive/parse path — the internal simulator feeds via UpdateGpsData
    /// and never parses, so this is a real-source-only signal. Drives <see cref="IsGpsLive"/>.
    /// </summary>
    void MarkRealGpsParsed();

    /// <summary>
    /// True when real (parsed-from-NMEA) GPS data has arrived recently. Used to
    /// keep the internal simulator and a live GPS source mutually exclusive.
    /// </summary>
    bool IsGpsLive { get; }

    /// <summary>
    /// Update IMU data timestamp (called when IMU data received)
    /// </summary>
    void UpdateImuData();

    /// <summary>
    /// Check if GPS data is flowing (10Hz expected)
    /// </summary>
    bool IsGpsDataOk();

    /// <summary>
    /// Check if IMU data is flowing (10Hz expected)
    /// </summary>
    bool IsImuDataOk();
}