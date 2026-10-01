// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.GeoJson;
using AgOpenWeb.Models.Track;

namespace AgOpenWeb.Services.GeoJson;

/// <summary>
/// A field's recorded data that grows as it's worked, kept in GeoJSON files next to
/// field.geojson so appending to them never rewrites the field: contour strips
/// (contours.geojson) and recorded paths (recorded-paths.geojson). Coordinates are WGS84,
/// converted with the field's origin from field.geojson.
/// </summary>
public partial class GeoJsonFieldService
{
    public const string ContoursFileName = "contours.geojson";
    public const string RecordedPathsFileName = "recorded-paths.geojson";

    // ---------- contours ----------

    /// <summary>The field's contour strips (empty when it has none).</summary>
    public static List<List<Vec3>> LoadContours(string fieldDirectory)
    {
        var path = Path.Combine(fieldDirectory, ContoursFileName);
        if (!File.Exists(path))
            return new();
        var geo = ProjectionOfField(fieldDirectory);
        return ReadCollection(path).Features
            .Where(f => GetStringProp(f, FieldPropertyKeys.Role) == FeatureRoles.Contour)
            .Select(f => ReadLineStringCoords(f.Geometry))
            .Where(c => c is { Count: > 0 })
            .Select(c => geo.FromGeoJsonCoordinatesVec3(c!))
            .ToList();
    }

    /// <summary>Add finished contour strips (as AgOpenGPS appends them as they finish).</summary>
    public static void AppendContours(string fieldDirectory, IEnumerable<IReadOnlyList<Vec3>> strips)
    {
        var list = strips.Where(s => s.Count > 0).ToList();
        if (list.Count == 0)
            return;
        var path = Path.Combine(fieldDirectory, ContoursFileName);
        var geo = ProjectionOfField(fieldDirectory);
        lock (FileLock)
        {
            var fc = File.Exists(path) ? ReadCollection(path) : new GeoJsonFeatureCollection();
            fc.Features.AddRange(list.Select(strip => new GeoJsonFeature
            {
                Geometry = new GeoJsonGeometry
                {
                    Type = GeoJsonTypes.LineString,
                    Coordinates = geo.ToGeoJsonCoordinates(strip).Select(c => (object)c).ToArray(),
                },
                Properties = new Dictionary<string, object?> { [FieldPropertyKeys.Role] = FeatureRoles.Contour },
            }));
            Write(path, fc);
        }
    }

    public static void DeleteContours(string fieldDirectory)
    {
        lock (FileLock)
        {
            var path = Path.Combine(fieldDirectory, ContoursFileName);
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    // ---------- recorded paths ----------
    // One feature per path: the one in use (current: true, what playback follows) and the
    // named ones saved alongside it. Per-point speed and section-master auto state ride in
    // properties, one entry per vertex.

    /// <summary>The recorded path in use, or null.</summary>
    public static List<RecPathPoint>? LoadCurrentRecordedPath(string fieldDirectory) =>
        ReadRecordedPath(fieldDirectory, f => GetBoolProp(f, FieldPropertyKeys.Current));

    public static void SaveCurrentRecordedPath(string fieldDirectory, IReadOnlyList<RecPathPoint> points) =>
        ReplaceRecordedPath(fieldDirectory, f => GetBoolProp(f, FieldPropertyKeys.Current), "", true, points);

    public static void DeleteCurrentRecordedPath(string fieldDirectory) =>
        ReplaceRecordedPath(fieldDirectory, f => GetBoolProp(f, FieldPropertyKeys.Current), "", true, null);

    /// <summary>Names of the saved recorded paths, sorted.</summary>
    public static List<string> ListRecordedPaths(string fieldDirectory)
    {
        var path = Path.Combine(fieldDirectory, RecordedPathsFileName);
        if (!File.Exists(path))
            return new();
        // The web projector asks every broadcast tick: re-read only when the file changes.
        var stamp = File.GetLastWriteTimeUtc(path);
        lock (RecordedPathNamesCache)
        {
            if (RecordedPathNamesCache.TryGetValue(path, out var cached) && cached.Stamp == stamp)
                return cached.Names.ToList();
        }
        var names = ReadCollection(path).Features
            .Where(f => GetStringProp(f, FieldPropertyKeys.Role) == FeatureRoles.RecordedPath && !GetBoolProp(f, FieldPropertyKeys.Current))
            .Select(f => GetStringProp(f, FieldPropertyKeys.Name) ?? "")
            .Where(n => n.Length > 0)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        lock (RecordedPathNamesCache)
            RecordedPathNamesCache[path] = (stamp, names);
        return names.ToList();
    }

    private static readonly Dictionary<string, (DateTime Stamp, List<string> Names)> RecordedPathNamesCache = new();

    public static List<RecPathPoint>? LoadRecordedPath(string fieldDirectory, string name) =>
        ReadRecordedPath(fieldDirectory, f => !GetBoolProp(f, FieldPropertyKeys.Current) && GetStringProp(f, FieldPropertyKeys.Name) == name);

    public static void SaveRecordedPath(string fieldDirectory, string name, IReadOnlyList<RecPathPoint> points) =>
        ReplaceRecordedPath(fieldDirectory,
            f => !GetBoolProp(f, FieldPropertyKeys.Current) && GetStringProp(f, FieldPropertyKeys.Name) == name, name, false, points);

    /// <summary>Remove a saved recorded path. False if there was none by that name.</summary>
    public static bool DeleteRecordedPath(string fieldDirectory, string name)
    {
        if (LoadRecordedPath(fieldDirectory, name) == null)
            return false;
        ReplaceRecordedPath(fieldDirectory,
            f => !GetBoolProp(f, FieldPropertyKeys.Current) && GetStringProp(f, FieldPropertyKeys.Name) == name, name, false, null);
        return true;
    }

    private static List<RecPathPoint>? ReadRecordedPath(string fieldDirectory, Func<GeoJsonFeature, bool> match)
    {
        var path = Path.Combine(fieldDirectory, RecordedPathsFileName);
        if (!File.Exists(path))
            return null;
        var feature = ReadCollection(path).Features
            .FirstOrDefault(f => GetStringProp(f, FieldPropertyKeys.Role) == FeatureRoles.RecordedPath && match(f));
        var coords = feature == null ? null : ReadLineStringCoords(feature.Geometry);
        if (feature == null || coords == null)
            return null;
        var geo = ProjectionOfField(fieldDirectory);
        var speeds = ReadNumberArray(feature, FieldPropertyKeys.Speeds);
        var auto = ReadBoolArray(feature, FieldPropertyKeys.AutoSteer);
        var points = new List<RecPathPoint>(coords.Count);
        for (int i = 0; i < coords.Count; i++)
        {
            var local = geo.ToLocal(coords[i][1], coords[i][0]);
            points.Add(new RecPathPoint(local.Easting, local.Northing, coords[i].Length > 2 ? coords[i][2] : 0,
                i < speeds.Count ? speeds[i] : 0, i < auto.Count && auto[i]));
        }
        return points;
    }

    // Replace the matching feature with (name, current, points), or remove it when points is null.
    private static void ReplaceRecordedPath(string fieldDirectory, Func<GeoJsonFeature, bool> match,
        string name, bool current, IReadOnlyList<RecPathPoint>? points)
    {
        var path = Path.Combine(fieldDirectory, RecordedPathsFileName);
        var geo = points == null ? null : ProjectionOfField(fieldDirectory);
        lock (FileLock)
        {
            var fc = File.Exists(path) ? ReadCollection(path) : new GeoJsonFeatureCollection();
            fc.Features.RemoveAll(f => GetStringProp(f, FieldPropertyKeys.Role) == FeatureRoles.RecordedPath && match(f));
            if (points != null && geo != null)
            {
                var props = new Dictionary<string, object?>
                {
                    [FieldPropertyKeys.Role] = FeatureRoles.RecordedPath,
                    [FieldPropertyKeys.Speeds] = points.Select(p => p.Speed).ToArray(),
                    [FieldPropertyKeys.AutoSteer] = points.Select(p => p.AutoBtnState).ToArray(),
                };
                if (current) props[FieldPropertyKeys.Current] = true;
                else props[FieldPropertyKeys.Name] = name;
                fc.Features.Add(new GeoJsonFeature
                {
                    Geometry = new GeoJsonGeometry
                    {
                        Type = GeoJsonTypes.LineString,
                        Coordinates = geo.ToGeoJsonCoordinates(points.Select(p => new Vec3(p.Easting, p.Northing, p.Heading)).ToList())
                            .Select(c => (object)c).ToArray(),
                    },
                    Properties = props,
                });
            }
            if (fc.Features.Count == 0)
            {
                if (File.Exists(path)) File.Delete(path);
            }
            else
                Write(path, fc);
        }
    }

    private static List<double> ReadNumberArray(GeoJsonFeature f, string key) =>
        f.Properties.TryGetValue(key, out var v) && v is JsonElement { ValueKind: JsonValueKind.Array } je
            ? je.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetDouble() : 0).ToList()
            : new();

    private static List<bool> ReadBoolArray(GeoJsonFeature f, string key) =>
        f.Properties.TryGetValue(key, out var v) && v is JsonElement { ValueKind: JsonValueKind.Array } je
            ? je.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.True).ToList()
            : new();

    // The field's origin, from field.geojson's metadata.
    private static Projection ProjectionOfField(string fieldDirectory) =>
        ProjectionOf(ReadCollection(Path.Combine(fieldDirectory, FileName)));
}
