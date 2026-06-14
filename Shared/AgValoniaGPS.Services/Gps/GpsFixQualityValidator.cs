// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System.Globalization;
using AgValoniaGPS.Models.Configuration;

namespace AgValoniaGPS.Services.Gps;

/// <summary>
/// Fix-quality gate extracted from <c>NmeaParserService.ParsePANDA</c>. A fix
/// is acceptable when its quality, HDOP, and differential age are all within
/// the user-configured thresholds. Called by the cycle worker at the top of
/// each tick — rejected fixes abort the cycle without writing state.
/// </summary>
public static class GpsFixQualityValidator
{
    /// <summary>
    /// Evaluate a parsed fix against <see cref="ConnectionConfig"/> thresholds.
    /// </summary>
    /// <param name="fixQuality">GGA fix-quality value (0 = invalid … 4 = RTK fixed).</param>
    /// <param name="hdop">Horizontal dilution of precision.</param>
    /// <param name="differentialAge">Age of differential corrections, seconds.
    /// Zero means no differential, which passes the age check.</param>
    /// <param name="rejectionReason">Populated with a human-readable reason
    /// when the method returns false; null on success.</param>
    /// <param name="configStore">Configuration store providing the connection thresholds.</param>
    public static bool IsAcceptable(int fixQuality, double hdop, double differentialAge,
        out string? rejectionReason, ConfigurationStore configStore)
    {
        var config = configStore.Connections;

        if (fixQuality < config.MinFixQuality)
        {
            rejectionReason = $"Fix quality {fixQuality} below minimum {config.MinFixQuality}";
            return false;
        }

        if (hdop > config.MaxHdop)
        {
            rejectionReason = $"HDOP {hdop.ToString("F1", CultureInfo.InvariantCulture)} "
                + $"exceeds maximum {config.MaxHdop.ToString("F1", CultureInfo.InvariantCulture)}";
            return false;
        }

        // differentialAge == 0 means no differential fix at all — not an age problem.
        if (differentialAge > 0 && differentialAge > config.MaxDifferentialAge)
        {
            rejectionReason = $"Differential age {differentialAge.ToString("F1", CultureInfo.InvariantCulture)}s "
                + $"exceeds maximum {config.MaxDifferentialAge.ToString("F1", CultureInfo.InvariantCulture)}s";
            return false;
        }

        rejectionReason = null;
        return true;
    }
}
