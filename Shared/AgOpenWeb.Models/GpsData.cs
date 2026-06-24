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

using System;

namespace AgOpenWeb.Models;

/// <summary>
/// Represents GPS data received from receiver
/// </summary>
public class GpsData
{
    public Position CurrentPosition { get; set; } = new();

    /// <summary>
    /// GPS fix quality (0=invalid, 1=GPS fix, 2=DGPS fix, 4=RTK fixed, 5=RTK float)
    /// </summary>
    public int FixQuality { get; set; }

    /// <summary>
    /// Number of satellites in use
    /// </summary>
    public int SatellitesInUse { get; set; }

    /// <summary>
    /// Horizontal dilution of precision
    /// </summary>
    public double Hdop { get; set; }

    /// <summary>
    /// Age of differential corrections in seconds
    /// </summary>
    public double DifferentialAge { get; set; }

    /// <summary>IMU roll angle in degrees (from $PANDA field 13)</summary>
    public double ImuRoll { get; set; }

    /// <summary>IMU pitch angle in degrees (from $PANDA field 14)</summary>
    public double ImuPitch { get; set; }

    /// <summary>IMU yaw rate in degrees/second (from $PANDA field 15)</summary>
    public double ImuYawRate { get; set; }

    /// <summary>
    /// IMU heading in degrees (0-360), separate from <see cref="Position.Heading"/>.
    /// Only populated for PANDA sentences with a valid IMU. PAOGI sets this to 0
    /// and <see cref="ImuValid"/> to false because dual-antenna heading is ground
    /// truth and doesn't need fusion. Consumed by <c>GpsHeadingFusionService</c>
    /// to blend with fix-to-fix using <c>HeadingFusionWeight</c>.
    /// </summary>
    public double ImuHeading { get; set; }

    /// <summary>
    /// True when the IMU block in the most recent NMEA sentence is valid
    /// (PANDA field 12 != 65535 sentinel). Gates use of <see cref="ImuHeading"/>
    /// and <see cref="ImuRoll"/>.
    /// </summary>
    public bool ImuValid { get; set; }

    /// <summary>
    /// Timestamp when data was received
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>
    /// Whether GPS data is currently valid (can be overridden by parser for quality filtering)
    /// </summary>
    private bool? _isValidOverride;
    public bool IsValid
    {
        get => _isValidOverride ?? (FixQuality > 0 && SatellitesInUse >= 4);
        set => _isValidOverride = value;
    }
}