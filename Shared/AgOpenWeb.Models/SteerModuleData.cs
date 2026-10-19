// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
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

namespace AgOpenWeb.Models;

/// <summary>
/// Parsed data from PGN 253 (Steer Data FROM Module).
/// Immutable record containing actual steering sensor readings.
/// </summary>
/// <param name="ActualSteerAngle">Actual wheel angle in degrees (from WAS sensor)</param>
/// <param name="ImuHeading">Heading from the module's IMU in degrees (0-360)</param>
/// <param name="ImuRoll">Roll from the module's IMU in degrees, uncalibrated</param>
/// <param name="WorkSwitchActive">Work switch is engaged (ON)</param>
/// <param name="SteerSwitchActive">Raw PGN 253 byte 11 bit 1, AgOpenGPS <c>steerSwitchHigh</c>:
/// set when the module is NOT steering (switch off, button, sensor kickout, not armed).
/// Despite the name it does not mean "steering enabled" (#126).</param>
/// <param name="RemoteButtonPressed">Remote steer button is pressed</param>
/// <param name="VwasFusionActive">Virtual WAS fusion is active</param>
/// <param name="PwmDisplay">Current PWM output magnitude (0-255)</param>
/// <param name="HasImuRoll">The module has an IMU: roll is not AgOpenGPS's 8888 "none"</param>
/// <param name="HasImuHeading">The module has an IMU: heading is not AgOpenGPS's 9999 "none"</param>
public readonly record struct SteerModuleData(
    double ActualSteerAngle,
    double ImuHeading,
    double ImuRoll,
    bool WorkSwitchActive,
    bool SteerSwitchActive,
    bool RemoteButtonPressed,
    bool VwasFusionActive,
    byte PwmDisplay,
    bool HasImuRoll = false,
    bool HasImuHeading = false)
{
    /// <summary>
    /// Indicates valid data was received (used to check parse success).
    /// </summary>
    public bool IsValid => true;

    /// <summary>
    /// Empty/invalid data instance.
    /// </summary>
    public static SteerModuleData Empty => default;
}

/// <summary>
/// Parsed data from PGN 250 (Sensor Data FROM Module).
/// Contains pressure/current sensor readings if hardware supports it.
/// </summary>
/// <param name="SensorValue">Raw sensor value (0-255), interpretation depends on hardware config</param>
public readonly record struct SensorModuleData(byte SensorValue)
{
    /// <summary>
    /// Empty/invalid data instance.
    /// </summary>
    public static SensorModuleData Empty => default;
}
