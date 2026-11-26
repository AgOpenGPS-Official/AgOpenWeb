// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using AgOpenWeb.Models;

namespace AgOpenWeb.UpdateScenarios.Tests;

/// <summary>
/// Writes a field folder as AgOpenGPS 6.x leaves it (the formats of AgOpen_Snapshot's streamers:
/// Field.txt, Boundary.txt, Headland.Txt, TrackLines.txt, Flags.txt, Sections.txt), from a
/// <see cref="FieldSpec"/>: what a farmer copies from the AgOpenGPS Fields folder into
/// AgOpenWeb's. AgOpenWeb imports these once, on open, and deletes them.
/// </summary>
public static class AgOpenGpsFiles
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static void Write(string fieldDir, FieldSpec fs, bool withCoverage)
    {
        Directory.CreateDirectory(fieldDir);
        var plane = new LocalPlane(new Wgs84(fs.Lat, fs.Lon), new SharedFieldProperties());
        GeoCoord G(double lat, double lon) => plane.ConvertWgs84ToGeoCoord(new Wgs84(lat, lon));

        File.WriteAllLines(Path.Combine(fieldDir, "Field.txt"), new[]
        {
            DateTime.Now.ToString("yyyy-MMM-dd hh:mm:ss tt", Inv),
            "$FieldDir", fs.Name, "$Offsets", "0,0", "Convergence", "0", "StartFix",
            fs.Lat.ToString("0.#######", Inv) + "," + fs.Lon.ToString("0.#######", Inv),
        });

        if (fs.Boundary.Count >= 3)
        {
            var ring = fs.Boundary.Select(p => G(p.Lat, p.Lon)).ToList();
            var lines = new List<string> { "$Boundary", "False", ring.Count.ToString(Inv) };
            lines.AddRange(WithHeadings(ring));
            File.WriteAllLines(Path.Combine(fieldDir, "Boundary.txt"), lines);
            if (fs.HeadlandOffset is { } off)
            {
                // A headland polygon: the boundary shrunk towards its centre by the offset.
                double ce = ring.Average(p => p.Easting), cn = ring.Average(p => p.Northing);
                var inner = ring.Select(p =>
                {
                    double dx = p.Easting - ce, dy = p.Northing - cn, len = Math.Sqrt(dx * dx + dy * dy);
                    double k = Math.Max(0.1, (len - off * 1.4) / len);
                    return new GeoCoord(cn + dy * k, ce + dx * k);
                }).ToList();
                var hl = new List<string> { "$Headland", inner.Count.ToString(Inv) };
                hl.AddRange(WithHeadings(inner));
                File.WriteAllLines(Path.Combine(fieldDir, "Headland.Txt"), hl);
            }
        }

        if (fs.Tracks.Count > 0)
        {
            var lines = new List<string> { "$TrackLines" };
            foreach (var t in fs.Tracks)
            {
                var pts = t.Points.Select(p => G(p.Lat, p.Lon)).ToList();
                var a = pts[0]; var b = pts[^1];
                double heading = Math.Atan2(b.Easting - a.Easting, b.Northing - a.Northing);
                if (heading < 0) heading += 2 * Math.PI;
                lines.Add(t.Name);
                lines.Add(heading.ToString("0.###", Inv));
                lines.Add(En(a));
                lines.Add(En(b));
                lines.Add("0");
                if (t.Curve)
                {
                    // AgOpenGPS curves are dense: a point every few metres along the drawn line.
                    var dense = Densify(pts, 3.0);
                    lines.Add("4"); lines.Add("True"); lines.Add(dense.Count.ToString(Inv));
                    lines.AddRange(WithHeadings(dense, closed: false));
                }
                else { lines.Add("2"); lines.Add("True"); lines.Add("0"); }
            }
            File.WriteAllLines(Path.Combine(fieldDir, "TrackLines.txt"), lines);
        }

        {
            var lines = new List<string> { "$Flags", fs.Flags.Count.ToString(Inv) };
            int id = 1;
            foreach (var f in fs.Flags)
            {
                var g = G(f.Lat, f.Lon);
                int color = (int)Enum.Parse<FlagColor>(f.Color);
                // lat,lon,easting,northing,heading,color,id,notes (AgOpenGPS 6.x)
                lines.Add(string.Join(",", f.Lat.ToString("0.#######", Inv), f.Lon.ToString("0.#######", Inv),
                    g.Easting.ToString("0.###", Inv), g.Northing.ToString("0.###", Inv), "0", color.ToString(Inv), (id++).ToString(Inv), f.Name));
            }
            File.WriteAllLines(Path.Combine(fieldDir, "Flags.txt"), lines);
        }

        if (withCoverage)
        {
            // One quad strip 4 m wide driven 60 m north from 20 m south of the origin: AgOpenGPS
            // writes the pair count line (1 + 2 × pairs, odd: a colour line follows), the colour,
            // then left/right "e,n, 0.0" pairs.
            var lines = new List<string>();
            const int pairs = 13;
            lines.Add((1 + 2 * pairs).ToString(Inv));
            lines.Add("0,255,0");
            for (int i = 0; i < pairs; i++)
            {
                double n = -20 + i * 5.0;
                lines.Add(Kit.F($"{-2.0:0.###},{n:0.###}, 0.0"));
                lines.Add(Kit.F($"{2.0:0.###},{n:0.###}, 0.0"));
            }
            File.WriteAllLines(Path.Combine(fieldDir, "Sections.txt"), lines);
        }
    }

    /// <summary>The worked area the Sections.txt above describes, m².</summary>
    public const double CoverageSqM = 4.0 * 60.0;

    private static string En(GeoCoord g) => g.Easting.ToString("0.###", Inv) + "," + g.Northing.ToString("0.###", Inv);

    private static IEnumerable<string> WithHeadings(List<GeoCoord> pts, bool closed = true)
    {
        for (int i = 0; i < pts.Count; i++)
        {
            var p = pts[i];
            var q = closed ? pts[(i + 1) % pts.Count] : pts[Math.Min(i + 1, pts.Count - 1)];
            double h = Math.Atan2(q.Easting - p.Easting, q.Northing - p.Northing);
            if (h < 0) h += 2 * Math.PI;
            yield return En(p) + "," + h.ToString("0.#####", Inv);
        }
    }

    private static List<GeoCoord> Densify(List<GeoCoord> pts, double step)
    {
        var o = new List<GeoCoord>();
        for (int i = 0; i < pts.Count - 1; i++)
        {
            var a = pts[i]; var b = pts[i + 1];
            double len = Math.Sqrt(Math.Pow(b.Easting - a.Easting, 2) + Math.Pow(b.Northing - a.Northing, 2));
            int n = Math.Max(1, (int)(len / step));
            for (int k = 0; k < n; k++)
            {
                double f = (double)k / n;
                o.Add(new GeoCoord(a.Northing + (b.Northing - a.Northing) * f, a.Easting + (b.Easting - a.Easting) * f));
            }
        }
        o.Add(pts[^1]);
        return o;
    }

    /// <summary>A field folder that looks like AgOpenGPS's (Field.txt with Boundary.txt).</summary>
    public static bool LooksLikeAgOpenGps(string dir) =>
        File.Exists(Path.Combine(dir, "Field.txt")) && !File.Exists(Path.Combine(dir, "field.geojson"));
}
