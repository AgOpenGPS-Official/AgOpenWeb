// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Globalization;
using System.IO;
using System.Text;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services;

/// <summary>
/// Logs GPS elevation data to Elevation.txt in the field directory.
/// Matches legacy AgOpenGPS format and behavior.
/// </summary>
public class ElevationLogService : IElevationLogService
{
    private const double MinDistanceMeters = 2.9;
    private const double MinDistanceSq = MinDistanceMeters * MinDistanceMeters;

    private readonly StringBuilder _buffer = new();
    private double _lastEasting = double.NaN;
    private double _lastNorthing = double.NaN;
    // First position logged since the last flush — the header's StartLat/StartLon when
    // the file doesn't exist yet (logging turned on for an existing field, #112).
    private double _firstLat = double.NaN, _firstLon = double.NaN;

    public bool IsEnabled { get; set; }

    public void CreateHeader(string fieldDirectory, double startLat, double startLon)
    {
        var path = Path.Combine(fieldDirectory, "Elevation.txt");
        using var writer = new StreamWriter(path, false, Encoding.UTF8);
        writer.WriteLine($"$Elevation");
        writer.WriteLine($"Timestamp,{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        writer.WriteLine($"StartLat,{startLat.ToString("F7", CultureInfo.InvariantCulture)}");
        writer.WriteLine($"StartLon,{startLon.ToString("F7", CultureInfo.InvariantCulture)}");
        writer.WriteLine("Latitude,Longitude,Elevation,FixQuality,Easting,Northing,Heading,Roll");
    }

    public void LogPoint(double latitude, double longitude, double altitude,
        double antennaHeight, int fixQuality,
        double easting, double northing, double heading, double roll)
    {
        if (!IsEnabled) return;

        // Distance gate: only log when moved >2.9m
        if (!double.IsNaN(_lastEasting))
        {
            double dx = easting - _lastEasting;
            double dy = northing - _lastNorthing;
            if (dx * dx + dy * dy < MinDistanceSq)
                return;
        }

        _lastEasting = easting;
        _lastNorthing = northing;
        if (double.IsNaN(_firstLat)) { _firstLat = latitude; _firstLon = longitude; }

        double elevation = altitude - antennaHeight;

        _buffer.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "{0:F7},{1:F7},{2:F3},{3},{4:F3},{5:F3},{6:F3},{7:F3}",
            latitude, longitude, elevation, fixQuality,
            easting, northing, heading, roll));
    }

    public void Flush(string fieldDirectory)
    {
        if (_buffer.Length == 0) return;

        var path = Path.Combine(fieldDirectory, "Elevation.txt");

        // The header is only written when a field is created with the log on; if the log
        // was switched on later there's no file yet, so start it with a header (#112).
        if (!File.Exists(path))
            CreateHeader(fieldDirectory, _firstLat, _firstLon);

        File.AppendAllText(path, _buffer.ToString(), Encoding.UTF8);
        _buffer.Clear();
        _firstLat = _firstLon = double.NaN;
    }

    public void Clear()
    {
        _buffer.Clear();
        _firstLat = _firstLon = double.NaN;
        _lastEasting = double.NaN;
        _lastNorthing = double.NaN;
    }
}
