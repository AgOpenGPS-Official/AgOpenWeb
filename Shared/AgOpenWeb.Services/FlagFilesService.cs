// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Services
{
    /// <summary>
    /// Loads and saves a field's flags as Flags.txt, in AgOpenGPS's format (IO/FlagFiles.cs) so
    /// fields stay interchangeable:
    /// <code>
    /// $Flags
    /// &lt;count&gt;
    /// lat,lon,easting,northing,heading,color,id,notes
    /// </code>
    /// The flag's name rides in the notes column (AgOpenGPS has no separate name). Color ints
    /// match AgOpenGPS for 0 red / 1 green / 2 yellow; the rest are AgOpenWeb's extra colors.
    /// </summary>
    public static class FlagFilesService
    {
        public const string FileName = "Flags.txt";
        private const string Header = "$Flags";

        public static List<Flag> Load(string fieldDirectory)
        {
            var result = new List<Flag>();
            var path = Path.Combine(fieldDirectory, FileName);
            if (!File.Exists(path)) return result;

            var lines = File.ReadAllLines(path);
            if (lines.Length < 2 || !int.TryParse(lines[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
                return result;

            var inv = CultureInfo.InvariantCulture;
            for (int i = 0; i < count && i + 2 < lines.Length; i++)
            {
                var w = lines[i + 2].Split(',');
                if (w.Length < 6) continue;
                // AgOpenGPS: 8 columns (with heading + notes), or the old 6-column form.
                bool full = w.Length >= 8;
                if (!double.TryParse(w[2], NumberStyles.Float, inv, out double easting)
                    || !double.TryParse(w[3], NumberStyles.Float, inv, out double northing)
                    || !int.TryParse(w[full ? 5 : 4], NumberStyles.Integer, inv, out int color)
                    || !int.TryParse(w[full ? 6 : 5], NumberStyles.Integer, inv, out int id))
                    continue;

                var flagColor = Enum.IsDefined(typeof(FlagColor), color) ? (FlagColor)color : FlagColor.Red;
                string notes = full ? string.Join(",", w.Skip(7)).Trim() : "";
                result.Add(new Flag(easting, northing, flagColor, id, notes.Length > 0 ? notes : $"Flag {id}"));
            }
            return result;
        }

        /// <param name="originLat">Field origin (for the lat/lon columns); 0/0 writes 0/0.</param>
        public static void Save(string fieldDirectory, IReadOnlyList<Flag> flags, double originLat, double originLon)
        {
            var inv = CultureInfo.InvariantCulture;
            var geo = originLat != 0 || originLon != 0 ? new GeoConversion(originLat, originLon) : null;
            var lines = new List<string>(flags.Count + 2) { Header, flags.Count.ToString(inv) };
            foreach (var f in flags)
            {
                var (lat, lon) = geo?.ToWgs84(new Vec2(f.Easting, f.Northing)) ?? (0.0, 0.0);
                // Commas would split the notes column on load (AgOpenGPS reads a fixed column).
                string name = (f.Name ?? "").Replace(',', ' ');
                lines.Add(string.Join(",",
                    lat.ToString("F8", inv), lon.ToString("F8", inv),
                    f.Easting.ToString("F3", inv), f.Northing.ToString("F3", inv),
                    f.Heading.AngleInRadians.ToString("F5", inv),
                    ((int)f.FlagColor).ToString(inv), f.UniqueNumber.ToString(inv), name));
            }
            // Write-then-replace so a crash mid-save can't truncate the field's flags.
            var path = Path.Combine(fieldDirectory, FileName);
            var tmp = path + ".tmp";
            File.WriteAllLines(tmp, lines);
            File.Move(tmp, path, overwrite: true);
        }
    }
}
