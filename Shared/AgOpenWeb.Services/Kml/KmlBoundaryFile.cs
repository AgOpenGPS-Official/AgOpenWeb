// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AgOpenWeb.Services.Kml;

/// <summary>The polygons of a KML file in WGS84 (the first the outer boundary, the rest inner
/// ones), the average of all their points, and how many points were read.</summary>
public sealed record KmlBoundaryRead(
    List<List<(double Latitude, double Longitude)>> Polygons,
    double CenterLatitude,
    double CenterLongitude,
    int PointCount);

/// <summary>
/// Reading a KML or KMZ file's boundary polygons (Plans/MAINVIEWMODEL_REFACTOR_PLAN.md item 4:
/// file parsing out of the view model). Moved from MainViewModel.ParseKmlFile and OpenKmlText;
/// the view model keeps what it does with the result. No state, so any thread.
/// </summary>
public static class KmlBoundaryFile
{
    /// <summary>Text of a KML file, or of the KML inside a KMZ (a zip — usually doc.kml).
    /// KMZ files were listed but read as raw zip bytes, so nothing parsed (#111).</summary>
    public static TextReader OpenKmlText(string filePath)
    {
        if (!filePath.EndsWith(".kmz", StringComparison.OrdinalIgnoreCase))
            return new StreamReader(filePath);

        var zip = System.IO.Compression.ZipFile.OpenRead(filePath);
        var entry = zip.Entries.FirstOrDefault(e => string.Equals(e.Name, "doc.kml", StringComparison.OrdinalIgnoreCase))
                    ?? zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".kml", StringComparison.OrdinalIgnoreCase));
        if (entry == null) { zip.Dispose(); return new StringReader(string.Empty); }
        // Read it all so the zip can close now.
        using (zip)
        using (var r = new StreamReader(entry.Open()))
            return new StringReader(r.ReadToEnd());
    }

    /// <summary>Every polygon of the file; throws when the file cannot be read.</summary>
    public static KmlBoundaryRead Read(string filePath)
    {
        var polygons = new List<List<(double Latitude, double Longitude)>>();
        using var reader = OpenKmlText(filePath);
        double sumLat = 0, sumLon = 0;
        int totalValidPoints = 0;

        // Every <coordinates> block, wherever the lines break. GDAL's KML driver (so a
        // QGIS export) writes a whole polygon on one line, outer ring and holes together,
        // and reading only the first block of each line kept the outer ring and lost
        // every hole.
        const string Open = "<coordinates>", Close = "</coordinates>";
        string text = reader.ReadToEnd();
        int at = 0;
        while ((at = text.IndexOf(Open, at, StringComparison.Ordinal)) != -1)
        {
            int start = at + Open.Length;
            int end = text.IndexOf(Close, start, StringComparison.Ordinal);
            if (end == -1) break;
            at = end + Close.Length;
            string coordinates = text.Substring(start, end - start);

            // Parse coordinate pairs: format is "lon,lat,alt lon,lat,alt ..."
            char[] delimiterChars = { ' ', '\t', '\r', '\n' };
            string[] numberSets = coordinates.Split(delimiterChars, StringSplitOptions.RemoveEmptyEntries);

            if (numberSets.Length >= 3)
            {
                var polygonPoints = new List<(double Latitude, double Longitude)>();

                foreach (string item in numberSets)
                {
                    if (item.Length < 3) continue;

                    string[] parts = item.Split(',');
                    if (parts.Length >= 2 &&
                        double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double lon) &&
                        double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double lat))
                    {
                        polygonPoints.Add((lat, lon));
                        sumLat += lat;
                        sumLon += lon;
                        totalValidPoints++;
                    }
                }

                // A KML ring repeats its first point at the end; a boundary doesn't (AgOpenGPS
                // FixFenceLine drops points that close together), so drop it here.
                if (polygonPoints.Count >= 4 && polygonPoints[0] == polygonPoints[^1])
                {
                    sumLat -= polygonPoints[^1].Latitude;
                    sumLon -= polygonPoints[^1].Longitude;
                    totalValidPoints--;
                    polygonPoints.RemoveAt(polygonPoints.Count - 1);
                }

                if (polygonPoints.Count >= 3)
                    polygons.Add(polygonPoints);
            }
            // Continue to parse additional coordinate blocks (inner boundaries)
        }

        return totalValidPoints > 0
            ? new KmlBoundaryRead(polygons, sumLat / totalValidPoints, sumLon / totalValidPoints, totalValidPoints)
            : new KmlBoundaryRead(polygons, 0, 0, 0);
    }
}
