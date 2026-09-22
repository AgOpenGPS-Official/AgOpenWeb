// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Services.Contour;

/// <summary>
/// A field's contour strips as Contour.txt, in AgOpenGPS's format (IO/ContourFiles.cs) (#110):
/// <code>
/// $Contour
/// &lt;count&gt;
/// easting,northing,heading   (× count, heading in radians)
/// </code>
/// repeated per strip. Strips are appended as they finish, like AgOpenGPS FileSaveContour.
/// </summary>
public static class ContourFilesService
{
    public const string FileName = "Contour.txt";
    private const string Header = "$Contour";

    public static List<List<Vec3>> Load(string fieldDirectory)
    {
        var result = new List<List<Vec3>>();
        var path = Path.Combine(fieldDirectory, FileName);
        if (!File.Exists(path)) return result;

        var inv = CultureInfo.InvariantCulture;
        var lines = File.ReadAllLines(path);
        if (lines.Length == 0 || !lines[0].TrimStart().StartsWith('$')) return result;

        int i = 1;
        while (i < lines.Length)
        {
            var countLine = lines[i++].Trim();
            if (countLine.Length == 0) continue;
            if (!int.TryParse(countLine, NumberStyles.Integer, inv, out int count) || count <= 0) continue;

            var strip = new List<Vec3>(count);
            for (int k = 0; k < count && i < lines.Length; k++, i++)
            {
                var w = lines[i].Split(',');
                if (w.Length < 3
                    || !double.TryParse(w[0], NumberStyles.Float, inv, out double e)
                    || !double.TryParse(w[1], NumberStyles.Float, inv, out double n)
                    || !double.TryParse(w[2], NumberStyles.Float, inv, out double h))
                    continue;
                strip.Add(new Vec3(e, n, h));
            }
            if (strip.Count > 0) result.Add(strip);
        }
        return result;
    }

    /// <summary>Append finished strips, creating the file with its header if needed.</summary>
    public static void Append(string fieldDirectory, IEnumerable<IReadOnlyList<Vec3>> strips)
    {
        var path = Path.Combine(fieldDirectory, FileName);
        bool isNew = !File.Exists(path);
        var inv = CultureInfo.InvariantCulture;
        using var writer = new StreamWriter(path, append: true);
        if (isNew) writer.WriteLine(Header);
        foreach (var strip in strips)
        {
            writer.WriteLine(strip.Count.ToString(inv));
            foreach (var p in strip)
                writer.WriteLine($"{p.Easting.ToString("F3", inv)},{p.Northing.ToString("F3", inv)},{p.Heading.ToString("F5", inv)}");
        }
    }
}
