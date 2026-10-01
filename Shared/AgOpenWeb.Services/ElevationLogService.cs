// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services;

/// <summary>
/// Logs GPS elevation data to elevation.csv in the field directory: one header row, then a row
/// per sample once the vehicle has moved more than 2.9 m (AgOpenGPS's sampling).
/// </summary>
public class ElevationLogService : IElevationLogService
{
    public const string FileName = "elevation.csv";
    private const string Header = "latitude,longitude,elevation,fixQuality,easting,northing,heading,roll";

    private const double MinDistanceMeters = 2.9;
    private const double MinDistanceSq = MinDistanceMeters * MinDistanceMeters;

    private readonly StringBuilder _buffer = new();
    private double _lastEasting = double.NaN;
    private double _lastNorthing = double.NaN;

    public bool IsEnabled { get; set; }

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

        double elevation = altitude - antennaHeight;

        _buffer.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "{0:F7},{1:F7},{2:F3},{3},{4:F3},{5:F3},{6:F3},{7:F3}",
            latitude, longitude, elevation, fixQuality,
            easting, northing, heading, roll));
    }

    public void Flush(string fieldDirectory)
    {
        if (_buffer.Length == 0) return;
        AppendRows(fieldDirectory, _buffer.ToString());
        _buffer.Clear();
    }

    public void Clear()
    {
        _buffer.Clear();
        _lastEasting = double.NaN;
        _lastNorthing = double.NaN;
    }

    // Plain UTF-8: a byte-order mark shows up as junk before "latitude" in some CSV tools.
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static void AppendRows(string fieldDirectory, string rows)
    {
        var path = Path.Combine(fieldDirectory, FileName);
        if (!File.Exists(path))
            File.WriteAllText(path, Header + Environment.NewLine, Utf8);
        File.AppendAllText(path, rows, Utf8);
    }

    /// <summary>
    /// Import AgOpenGPS's Elevation.txt (field metadata lines, then a
    /// "Latitude,Longitude,Elevation,Quality,Easting,Northing,Heading,Roll" header and rows in
    /// that order) into elevation.csv, then delete it.
    /// </summary>
    public static void ImportAgOpenGpsFile(string fieldDirectory)
    {
        var path = Path.Combine(fieldDirectory, "Elevation.txt");
        if (!File.Exists(path))
            return;
        var lines = File.ReadAllLines(path);
        int header = Array.FindIndex(lines, l => l.TrimStart().StartsWith("Latitude,", StringComparison.OrdinalIgnoreCase));
        var rows = header < 0 ? Enumerable.Empty<string>() : lines.Skip(header + 1)
            .Select(l => l.Trim())
            .Where(l => l.Split(',') is { Length: 8 } f &&
                        f.All(x => double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out _)));
        var text = string.Concat(rows.Select(r => r + Environment.NewLine));
        if (text.Length > 0)
            AppendRows(fieldDirectory, text);
        File.Delete(path);
    }
}
