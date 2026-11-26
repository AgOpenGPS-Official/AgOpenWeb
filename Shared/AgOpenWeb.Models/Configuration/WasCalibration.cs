// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;

namespace AgOpenWeb.Models.Configuration;

/// <summary>
/// Zeroing the wheel angle sensor (#103). Every AgOpenGPS steer firmware (AgOpenGPS-Official/Boards:
/// AiO v2.5 / v4 Teensy, Arduino UDP / USB v5) turns raw counts into an angle as
/// <code>
///   normal:   angle = (raw - 6805 + wasOffset) /  countsPerDegree
///   inverted: angle = (raw - 6805 - wasOffset) / -countsPerDegree
/// </code>
/// so the offset that makes the current reading 0 is, in BOTH cases,
/// <c>wasOffset' = wasOffset - angle * countsPerDegree</c> — exactly AgOpenGPS
/// FormSteer.btnZeroWAS_Click. Invert WAS doesn't change the formula; the firmware already
/// applies it to the reported angle.
///
/// Invert WAS does change what the offset means: the firmware adds it for a normal sensor and
/// subtracts it for an inverted one, so a centre zeroed as <c>raw - 6805 = -wasOffset</c> reads
/// <c>2 × wasOffset / countsPerDegree</c> off after the toggle. <see cref="OffsetForInvert"/>
/// is the offset that keeps that centre.
/// </summary>
public static class WasCalibration
{
    /// <summary>AgOpenGPS refuses a zero that would push the offset past this
    /// ("Excessive Steer Angle - Cannot Zero").</summary>
    public const int MaxOffset = 3900;

    /// <summary>The offset that makes <paramref name="reportedAngleDeg"/> (the module's live
    /// PGN 253 angle) read zero.</summary>
    public static int ZeroedOffset(int currentOffset, double reportedAngleDeg, double countsPerDegree)
        => currentOffset - (int)Math.Round(reportedAngleDeg * countsPerDegree);

    /// <summary>The offset that keeps straight ahead at 0° after Invert WAS is toggled (either
    /// way): the firmware flips the sign it applies to the offset, so the offset flips too.</summary>
    public static int OffsetForInvert(int currentOffset) => -currentOffset;

    /// <summary><see cref="ZeroedOffset"/>, refused (false) when the result is out of range.</summary>
    public static bool TryZero(int currentOffset, double reportedAngleDeg, double countsPerDegree, out int newOffset)
    {
        newOffset = ZeroedOffset(currentOffset, reportedAngleDeg, countsPerDegree);
        return Math.Abs(newOffset) <= MaxOffset;
    }
}
