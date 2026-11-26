// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using AgOpenWeb.Models;

namespace AgOpenWeb.Services;

/// <summary>
/// Service for reading and writing Field.txt files
/// Ported from AgOpenGPS GPS/IO/FieldPlaneFiles.cs
/// </summary>
public class FieldPlaneFileService
{
    /// <summary>
    /// Load field metadata from Field.txt
    /// </summary>
    public Field LoadField(string fieldDirectory)
    {
        var field = new Field
        {
            Name = Path.GetFileName(fieldDirectory),
            DirectoryPath = fieldDirectory
        };

        var fieldFilePath = Path.Combine(fieldDirectory, "Field.txt");
        if (!File.Exists(fieldFilePath))
        {
            throw new FileNotFoundException("Field.txt not found", fieldFilePath);
        }

        using (var reader = new StreamReader(fieldFilePath))
        {
            // Line 1: Timestamp
            var timestampLine = reader.ReadLine();
            if (!string.IsNullOrWhiteSpace(timestampLine))
            {
                if (DateTime.TryParse(timestampLine, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime timestamp))
                {
                    field.CreatedDate = timestamp;
                }
            }

            // Line 2: $FieldDir header
            reader.ReadLine();

            // Line 3: Field name (usually "FieldNew")
            reader.ReadLine();

            // Line 4: $Offsets header
            reader.ReadLine();

            // Line 5: Offsets (X,Y)
            var offsetsLine = reader.ReadLine();
            if (!string.IsNullOrWhiteSpace(offsetsLine))
            {
                var parts = offsetsLine.Split(',');
                if (parts.Length >= 2)
                {
                    if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double offsetX))
                        field.OffsetX = offsetX;
                    if (double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double offsetY))
                        field.OffsetY = offsetY;
                }
            }

            // Line 6: Convergence header
            reader.ReadLine();

            // Line 7: Convergence value
            var convergenceLine = reader.ReadLine();
            if (!string.IsNullOrWhiteSpace(convergenceLine))
            {
                if (double.TryParse(convergenceLine, NumberStyles.Float, CultureInfo.InvariantCulture, out double convergence))
                {
                    field.Convergence = convergence;
                }
            }

            // Line 8: StartFix header
            var startFixHeader = reader.ReadLine();
            if (startFixHeader != null && startFixHeader.StartsWith("StartFix", StringComparison.OrdinalIgnoreCase))
            {
                // Line 9: Latitude,Longitude (always InvariantCulture with period decimal)
                var startFixLine = reader.ReadLine();
                if (!string.IsNullOrWhiteSpace(startFixLine))
                {
                    var parts = startFixLine.Split(',');
                    if (parts.Length >= 2 &&
                        double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat) &&
                        double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon) &&
                        lat >= -90 && lat <= 90 && lon >= -180 && lon <= 180)
                    {
                        field.Origin = new Position
                        {
                            Latitude = lat,
                            Longitude = lon,
                            Altitude = 0
                        };
                    }
                }
            }
        }

        return field;
    }

    /// <summary>
    /// Save field metadata to Field.txt
    /// AgOpenWeb never writes AgOpenGPS files (they are imported once and deleted, see
    /// FieldService.LoadField); internal so tests can build AgOpenGPS fixtures.
    /// </summary>
    internal void SaveField(Field field, string fieldDirectory)
    {
        if (string.IsNullOrWhiteSpace(fieldDirectory))
        {
            throw new ArgumentNullException(nameof(fieldDirectory));
        }

        if (!Directory.Exists(fieldDirectory))
        {
            Directory.CreateDirectory(fieldDirectory);
        }

        var fieldFilePath = Path.Combine(fieldDirectory, "Field.txt");

        using (var writer = new StreamWriter(fieldFilePath, false))
        {
            // Line 1: Timestamp
            writer.WriteLine(field.CreatedDate.ToString("yyyy-MMMM-dd hh:mm:ss tt", CultureInfo.InvariantCulture));

            // Lines 2-3: Headers
            writer.WriteLine("$FieldDir");
            writer.WriteLine("FieldNew");

            // Lines 4-5: Offsets
            writer.WriteLine("$Offsets");
            writer.WriteLine($"{field.OffsetX.ToString(CultureInfo.InvariantCulture)},{field.OffsetY.ToString(CultureInfo.InvariantCulture)}");

            // Lines 6-7: Convergence
            writer.WriteLine("Convergence");
            writer.WriteLine(field.Convergence.ToString(CultureInfo.InvariantCulture));

            // Lines 8-9: StartFix (origin) - use F8 for consistent precision (8 decimal places ≈ 1mm)
            writer.WriteLine("StartFix");
            writer.WriteLine($"{field.Origin.Latitude.ToString("F8", CultureInfo.InvariantCulture)},{field.Origin.Longitude.ToString("F8", CultureInfo.InvariantCulture)}");
        }
    }

    /// <summary>
    /// Create an empty Field.txt for a new field
    /// </summary>
    public void CreateField(string fieldDirectory, Position originPosition)
    {
        var field = new Field
        {
            Name = Path.GetFileName(fieldDirectory),
            DirectoryPath = fieldDirectory,
            Origin = originPosition,
            CreatedDate = DateTime.Now,
            Convergence = 0,
            OffsetX = 0,
            OffsetY = 0
        };

        SaveField(field, fieldDirectory);
    }
}
