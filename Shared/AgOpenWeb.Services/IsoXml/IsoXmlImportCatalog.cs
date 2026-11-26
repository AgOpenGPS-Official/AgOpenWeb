// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Services.IsoXml;

/// <summary>One field the From ISO-XML picker offers: a partfield (PFD) of a TASKDATA.XML under
/// the Import folder. <see cref="Name"/> is what the picker shows and sends back.</summary>
public sealed record IsoXmlImportEntry(string Name, string Folder, string TaskDataPath, int FieldIndex, string FieldName, double AreaHectares);

/// <summary>
/// The From ISO-XML picker's list. A TASKDATA.XML holds any number of partfields (a farm
/// management program exports the whole farm); AgOpenGPS lists them all, with their area, and
/// imports the one picked. A dataset with one field is listed by its folder, as it always was;
/// one with several is listed once per field, "folder › field (area)".
/// </summary>
public static class IsoXmlImportCatalog
{
    public const string TaskDataFileName = "TASKDATA.XML";

    /// <summary>The TASKDATA.XML of a dataset folder, whatever its case (ISO 11783 writes it in
    /// upper case; Linux file systems care).</summary>
    public static string? FindTaskData(string folder)
        => Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder).FirstOrDefault(f =>
                string.Equals(Path.GetFileName(f), TaskDataFileName, StringComparison.OrdinalIgnoreCase))
            : null;

    public static List<IsoXmlImportEntry> Scan(string importDir)
    {
        var entries = new List<IsoXmlImportEntry>();
        if (!Directory.Exists(importDir)) return entries;
        foreach (var dir in Directory.GetDirectories(importDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var taskData = FindTaskData(dir);
            if (taskData == null) continue;
            var folder = new DirectoryInfo(dir).Name;
            List<(string name, double areaHa)> fields;
            try { fields = Fields(taskData); }
            catch { fields = new(); } // unreadable: offered by its folder, the import reports what is wrong
            if (fields.Count <= 1)
            {
                entries.Add(new IsoXmlImportEntry(folder, dir, taskData, 0, fields.Count == 1 ? fields[0].name : "", fields.Count == 1 ? fields[0].areaHa : 0));
                continue;
            }
            for (int i = 0; i < fields.Count; i++)
                entries.Add(new IsoXmlImportEntry(
                    string.Create(CultureInfo.InvariantCulture, $"{folder} › {fields[i].name} ({fields[i].areaHa:0.00} ha)"),
                    dir, taskData, i, fields[i].name, fields[i].areaHa));
        }
        return entries;
    }

    /// <summary>The partfields of a TASKDATA.XML, in file order: name and area in hectares, the
    /// area from the PFD's own figure (D, m²) or else from its boundary, as AgOpenGPS lists them.</summary>
    public static List<(string name, double areaHa)> Fields(string taskDataPath)
    {
        var doc = new XmlDocument();
        doc.Load(taskDataPath);
        var list = new List<(string, double)>();
        foreach (XmlNode pfd in doc.GetElementsByTagName("PFD"))
        {
            string name = pfd.Attributes?["C"]?.Value ?? "Unnamed";
            double areaSqm;
            if (!(double.TryParse(pfd.Attributes?["D"]?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out areaSqm) && areaSqm >= 1))
                areaSqm = BoundaryAreaSqm(pfd);
            list.Add((name, Math.Round(areaSqm * 0.0001, 2, MidpointRounding.AwayFromZero)));
        }
        return list;
    }

    private static double BoundaryAreaSqm(XmlNode pfd)
    {
        foreach (XmlNode pln in pfd.SelectNodes("PLN")!)
        {
            if (pln.Attributes?["A"]?.Value != "1") continue;
            var pts = pln.SelectSingleNode("LSG[@A='1']")?.SelectNodes("PNT");
            if (pts == null || pts.Count < 3) continue;
            LocalPlane? plane = null;
            var ring = new List<Vec2>(pts.Count);
            foreach (XmlNode pnt in pts)
            {
                if (!double.TryParse(pnt.Attributes?["C"]?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double lat)) continue;
                if (!double.TryParse(pnt.Attributes?["D"]?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double lon)) continue;
                plane ??= new LocalPlane(new Wgs84(lat, lon), new SharedFieldProperties());
                ring.Add(new Vec2(plane.ConvertWgs84ToGeoCoord(new Wgs84(lat, lon))));
            }
            double area = 0;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
                area += (ring[j].Easting + ring[i].Easting) * (ring[j].Northing - ring[i].Northing);
            return Math.Abs(area / 2.0);
        }
        return 0;
    }
}
