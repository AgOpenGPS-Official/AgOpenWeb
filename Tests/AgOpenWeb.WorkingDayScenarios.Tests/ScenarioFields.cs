// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AgOpenWeb.WorkingDayScenarios.Tests;

/// <summary>One of Renato's real fields (TestData/fields.json, made from the field.geojson
/// files in the project's shared folder): outer ring, obstacles, and the AB lines he planned.</summary>
public sealed class ScenarioField
{
    public string Name { get; init; } = "";
    public (double Lat, double Lon) Origin { get; init; }
    public List<(double Lat, double Lon)> Outer { get; init; } = new();
    public List<(bool DriveThrough, List<(double Lat, double Lon)> Ring)> Inner { get; init; } = new();
    public List<(string Name, List<(double Lat, double Lon)> Points)> Tracks { get; init; } = new();

    public (double Lat, double Lon) Centroid
    {
        get
        {
            double la = 0, lo = 0;
            foreach (var p in Outer) { la += p.Lat; lo += p.Lon; }
            return (la / Outer.Count, lo / Outer.Count);
        }
    }

    /// <summary>A point well inside the field: the centroid of the outer ring when it is
    /// inside and clear of obstacles, else the midpoint of the longest chord through it.</summary>
    public (double Lat, double Lon) InsidePoint()
    {
        var c = Centroid;
        if (Geo.Inside(c, Outer) && !Inner.Any(i => Geo.Inside(c, i.Ring))) return c;
        // walk from each vertex towards the centroid and take the first point inside
        foreach (var v in Outer)
            for (double t = 0.1; t < 1; t += 0.1)
            {
                var p = (v.Lat + (c.Lat - v.Lat) * t, v.Lon + (c.Lon - v.Lon) * t);
                if (Geo.Inside(p, Outer) && !Inner.Any(i => Geo.Inside(p, i.Ring))) return p;
            }
        return c;
    }

    /// <summary>The obstacles big enough to drive round (ring perimeter at least 30 m).</summary>
    public IEnumerable<List<(double Lat, double Lon)>> BigObstacles() =>
        Inner.Where(i => !i.DriveThrough && Geo.PerimeterM(i.Ring) >= 30).Select(i => i.Ring);

    /// <summary>KML with the outer ring first and the obstacles after it, the shape the
    /// Import-KML picker expects (first polygon = outer, the rest = inner).</summary>
    public string ToKml(bool withObstacles, string? nameOverride = null)
    {
        var inv = CultureInfo.InvariantCulture;
        string Coords(IEnumerable<(double Lat, double Lon)> ring)
        {
            var list = ring.ToList();
            list.Add(list[0]);
            return string.Join(" ", list.Select(p => p.Lon.ToString("F9", inv) + "," + p.Lat.ToString("F9", inv) + ",0"));
        }
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<kml xmlns=\"http://www.opengis.net/kml/2.2\"><Document>\n");
        sb.Append("<name>").Append(nameOverride ?? Name).Append("</name>\n");
        sb.Append("<Placemark><name>").Append(nameOverride ?? Name).Append("</name><Polygon><outerBoundaryIs><LinearRing><coordinates>")
          .Append(Coords(Outer)).Append("</coordinates></LinearRing></outerBoundaryIs></Polygon></Placemark>\n");
        if (withObstacles)
        {
            int k = 1;
            foreach (var (_, ring) in Inner)
                sb.Append("<Placemark><name>Obstacle ").Append(k++.ToString(inv)).Append("</name><Polygon><outerBoundaryIs><LinearRing><coordinates>")
                  .Append(Coords(ring)).Append("</coordinates></LinearRing></outerBoundaryIs></Polygon></Placemark>\n");
        }
        sb.Append("</Document></kml>\n");
        return sb.ToString();
    }

    public static IReadOnlyList<ScenarioField> LoadAll()
    {
        var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestData", "fields.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var list = new List<ScenarioField>();
        foreach (var f in doc.RootElement.GetProperty("fields").EnumerateArray())
        {
            static List<(double, double)> Ring(JsonElement e) =>
                e.EnumerateArray().Select(p => (p[0].GetDouble(), p[1].GetDouble())).ToList();
            var o = f.GetProperty("origin");
            list.Add(new ScenarioField
            {
                Name = f.GetProperty("name").GetString()!,
                Origin = (o[0].GetDouble(), o[1].GetDouble()),
                Outer = Ring(f.GetProperty("outer")),
                Inner = f.GetProperty("inner").EnumerateArray()
                    .Select(i => (i.GetProperty("driveThrough").GetBoolean(), Ring(i.GetProperty("ring")))).ToList(),
                Tracks = f.GetProperty("tracks").EnumerateArray()
                    .Select(t => (t.GetProperty("name").GetString() ?? "", Ring(t.GetProperty("points")))).ToList(),
            });
        }
        return list;
    }
}

/// <summary>Small WGS84 helpers (equirectangular, plenty for fields of a few hundred metres).</summary>
public static class Geo
{
    private const double R = 6371000.0;

    public static double DistanceM((double Lat, double Lon) a, (double Lat, double Lon) b)
    {
        double dLat = (b.Lat - a.Lat) * Math.PI / 180;
        double dLon = (b.Lon - a.Lon) * Math.PI / 180 * Math.Cos((a.Lat + b.Lat) / 2 * Math.PI / 180);
        return R * Math.Sqrt(dLat * dLat + dLon * dLon);
    }

    public static double BearingDeg((double Lat, double Lon) a, (double Lat, double Lon) b)
    {
        double dN = (b.Lat - a.Lat) * Math.PI / 180 * R;
        double dE = (b.Lon - a.Lon) * Math.PI / 180 * R * Math.Cos((a.Lat + b.Lat) / 2 * Math.PI / 180);
        double h = Math.Atan2(dE, dN) * 180 / Math.PI;
        return h < 0 ? h + 360 : h;
    }

    public static (double Lat, double Lon) Offset((double Lat, double Lon) a, double eastM, double northM) =>
        (a.Lat + northM / R * 180 / Math.PI,
         a.Lon + eastM / (R * Math.Cos(a.Lat * Math.PI / 180)) * 180 / Math.PI);

    public static double PerimeterM(IReadOnlyList<(double Lat, double Lon)> ring)
    {
        double s = 0;
        for (int i = 0; i < ring.Count; i++) s += DistanceM(ring[i], ring[(i + 1) % ring.Count]);
        return s;
    }

    public static bool Inside((double Lat, double Lon) p, IReadOnlyList<(double Lat, double Lon)> ring)
    {
        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            if ((ring[i].Lat > p.Lat) != (ring[j].Lat > p.Lat)
                && p.Lon < (ring[j].Lon - ring[i].Lon) * (p.Lat - ring[i].Lat) / (ring[j].Lat - ring[i].Lat) + ring[i].Lon)
                inside = !inside;
        }
        return inside;
    }

    /// <summary>A ring pushed outwards by <paramref name="m"/> metres around its centroid
    /// (for driving round an obstacle without touching it).</summary>
    public static List<(double Lat, double Lon)> Grow(IReadOnlyList<(double Lat, double Lon)> ring, double m)
    {
        double la = ring.Average(p => p.Lat), lo = ring.Average(p => p.Lon);
        var c = (la, lo);
        return ring.Select(p =>
        {
            double d = DistanceM(c, p);
            if (d < 0.01) return p;
            double k = (d + m) / d;
            return (la + (p.Lat - la) * k, lo + (p.Lon - lo) * k);
        }).ToList();
    }
}

public static class ScenarioFields
{
    private static IReadOnlyList<ScenarioField>? _all;
    public static IReadOnlyList<ScenarioField> All => _all ??= ScenarioField.LoadAll();
    public static ScenarioField Get(string name) => All.First(f => f.Name == name);
}
