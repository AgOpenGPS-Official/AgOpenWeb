// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Track;
using TrackModel = AgOpenWeb.Models.Track.Track;

namespace AgOpenWeb.Services;

/// <summary>
/// Reads AgOpenGPS's recorded paths (RecPath.txt, named *.rec), for the one-way import into
/// recorded-paths.geojson (FieldService.LoadField). The writers are internal: AgOpenWeb never
/// writes AgOpenGPS files, but tests build fixtures with them.
/// Format: header "$RecPath", point count, then CSV lines:
///   easting,northing,heading,speed,autoBtnState
/// </summary>
public static class RecPathFileService
{
    public const string FileName = "RecPath.txt";

    /// <summary>
    /// Load RecPath points with full data (speed, autoBtnState).
    /// </summary>
    public static List<RecPathPoint>? LoadRecPathPoints(string fieldDirectory, string fileName = "RecPath.txt")
    {
        var path = Path.Combine(fieldDirectory, fileName);
        return LoadRecPathPointsFromFile(path);
    }

    /// <summary>
    /// Load RecPath points from an arbitrary file path.
    /// </summary>
    public static List<RecPathPoint>? LoadRecPathPointsFromFile(string filePath)
    {
        if (!File.Exists(filePath)) return null;

        var points = new List<RecPathPoint>();

        using var reader = new StreamReader(filePath);

        // First line: "$RecPath" header or point count
        var line1 = reader.ReadLine()?.Trim();
        if (line1 == null) return null;

        string? countLine;
        if (line1.StartsWith("$"))
            countLine = reader.ReadLine()?.Trim();
        else
            countLine = line1;

        if (countLine == null || !int.TryParse(countLine, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int numPoints))
            return null;

        if (numPoints == 0) return null;

        for (int i = 0; i < numPoints && !reader.EndOfStream; i++)
        {
            var words = (reader.ReadLine() ?? string.Empty).Split(',');
            if (words.Length < 3) continue;

            if (double.TryParse(words[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double easting) &&
                double.TryParse(words[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double northing) &&
                double.TryParse(words[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double heading))
            {
                double speed = 0.0;
                bool autoBtnState = false;

                if (words.Length >= 4)
                    double.TryParse(words[3], NumberStyles.Float, CultureInfo.InvariantCulture, out speed);
                if (words.Length >= 5)
                    bool.TryParse(words[4], out autoBtnState);

                points.Add(new RecPathPoint(easting, northing, heading, speed, autoBtnState));
            }
        }

        return points.Count >= 2 ? points : null;
    }

    /// <summary>
    /// Save recorded path points to RecPath.txt in the field directory.
    /// </summary>
    internal static void SaveRecPath(string fieldDirectory, List<RecPathPoint> points)
    {
        SaveRecPathToFile(Path.Combine(fieldDirectory, "RecPath.txt"), points);
    }

    /// <summary>
    /// Save recorded path points to an arbitrary file path.
    /// </summary>
    internal static void SaveRecPathToFile(string filePath, List<RecPathPoint> points)
    {
        using var writer = new StreamWriter(filePath, false);
        writer.WriteLine("$RecPath");
        writer.WriteLine(points.Count.ToString(CultureInfo.InvariantCulture));

        foreach (var pt in points)
        {
            writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0:F3},{1:F3},{2:F3},{3:F1},{4}",
                pt.Easting, pt.Northing, pt.Heading, pt.Speed, pt.AutoBtnState));
        }
    }
}
