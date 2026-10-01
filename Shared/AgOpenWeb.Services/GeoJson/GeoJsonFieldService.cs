// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
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
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.GeoJson;
using AgOpenWeb.Models.Guidance;
using AgOpenWeb.Models.Track;

namespace AgOpenWeb.Services.GeoJson;

/// <summary>
/// A field's one file, field.geojson: a FeatureCollection where every part of the field is a
/// Feature with a "role" property. <see cref="Save"/> writes the field itself (metadata,
/// boundaries, headland polygon); the background image, tracks, flags and headland lines are
/// saved on their own, when they change, and each save replaces only its own features.
/// </summary>
public partial class GeoJsonFieldService
{
    private const string FileName = "field.geojson";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Check whether a field directory contains a GeoJSON field file.
    /// </summary>
    public static bool Exists(string fieldDirectory)
    {
        return File.Exists(Path.Combine(fieldDirectory, FileName));
    }

    // Several savers read-modify-write the file at different times, some off the UI thread.
    private static readonly object FileLock = new();

    // The roles Save(field) writes. Everything else in the file is left as it is.
    private static readonly HashSet<string> FieldRoles = new()
    {
        FeatureRoles.Metadata, FeatureRoles.OuterBoundary, FeatureRoles.InnerBoundary,
        FeatureRoles.Headland,
    };

    /// <summary>
    /// Save the field itself (metadata, boundaries, headland polygon), keeping its background
    /// image, tracks, flags and headland lines. Passing <paramref name="tracks"/> also
    /// replaces the tracks.
    /// </summary>
    public static void Save(Models.Field field, IReadOnlyList<Models.Track.Track>? tracks)
    {
        if (string.IsNullOrWhiteSpace(field.DirectoryPath))
            throw new ArgumentException("Field.DirectoryPath must be set", nameof(field));

        if (!Directory.Exists(field.DirectoryPath))
            Directory.CreateDirectory(field.DirectoryPath);

        var geo = new Projection(field.Origin.Latitude, field.Origin.Longitude);
        var features = new List<GeoJsonFeature>
        {
            // Metadata feature -- a Point at the field origin, always first
            BuildMetadataFeature(field),
        };

        // Boundaries
        if (field.Boundary != null)
        {
            if (field.Boundary.OuterBoundary is { IsValid: true })
                features.Add(BuildBoundaryFeature(geo, field.Boundary.OuterBoundary, FeatureRoles.OuterBoundary));

            foreach (var inner in field.Boundary.InnerBoundaries)
            {
                if (inner.IsValid)
                    features.Add(BuildBoundaryFeature(geo, inner, FeatureRoles.InnerBoundary));
            }

            if (field.Boundary.HeadlandPolygon is { IsValid: true })
                features.Add(BuildBoundaryFeature(geo, field.Boundary.HeadlandPolygon, FeatureRoles.Headland));
        }

        if (tracks != null)
            features.AddRange(TrackFeatures(geo, tracks));

        var path = Path.Combine(field.DirectoryPath, FileName);
        lock (FileLock)
        {
            // Keep the parts this save doesn't own. An unreadable file has nothing to keep
            // (FieldService sets an unreadable one aside when it reads it).
            GeoJsonFeatureCollection? existing = null;
            if (File.Exists(path))
            {
                try { existing = ReadCollection(path); }
                catch (Exception ex) when (ex is InvalidDataException or JsonException) { }
            }
            if (existing != null)
                features.AddRange(existing.Features.Where(f =>
                {
                    var role = GetStringProp(f, FieldPropertyKeys.Role) ?? "";
                    return !FieldRoles.Contains(role) && !(tracks != null && role == FeatureRoles.Track);
                }));
            Write(path, new GeoJsonFeatureCollection { Features = features });
        }
    }

    /// <summary>Replace the field's tracks.</summary>
    public static void SaveTracks(string fieldDirectory, IReadOnlyList<Models.Track.Track> tracks) =>
        ReplaceRole(fieldDirectory, FeatureRoles.Track, geo => TrackFeatures(geo, tracks));

    /// <summary>Replace the field's flags.</summary>
    public static void SaveFlags(string fieldDirectory, IReadOnlyList<Flag> flags) =>
        ReplaceRole(fieldDirectory, FeatureRoles.Flag, geo => flags.Select(f => BuildFlagFeature(geo, f)));

    /// <summary>Replace the field's headland lines.</summary>
    public static void SaveHeadlandLine(string fieldDirectory, HeadlandLine headlandLine) =>
        ReplaceRole(fieldDirectory, FeatureRoles.HeadlandLine, geo => headlandLine.Tracks
            .Where(p => p.TrackPoints.Count > 0)
            .Select(p => BuildHeadlandPathFeature(geo, p)));

    /// <summary>Set or (with null) remove the field's background image placement.</summary>
    public static void SaveBackground(string fieldDirectory, FieldBackground? background) =>
        ReplaceRole(fieldDirectory, FeatureRoles.BackgroundImage, _ => background == null
            ? Array.Empty<GeoJsonFeature>()
            : new[] { BuildBackgroundFeature(background) });

    public static FieldBackground? LoadBackground(string fieldDirectory) =>
        ReadRole(fieldDirectory, FeatureRoles.BackgroundImage, ReadBackground).FirstOrDefault();

    public static List<Models.Track.Track> LoadTracks(string fieldDirectory) =>
        ReadRole(fieldDirectory, FeatureRoles.Track, ReadTrack);

    public static List<Flag> LoadFlags(string fieldDirectory) =>
        ReadRole(fieldDirectory, FeatureRoles.Flag, ReadFlag);

    public static HeadlandLine LoadHeadlandLine(string fieldDirectory) =>
        new() { Tracks = ReadRole(fieldDirectory, FeatureRoles.HeadlandLine, ReadHeadlandPath) };

    private static IEnumerable<GeoJsonFeature> TrackFeatures(Projection geo, IReadOnlyList<Models.Track.Track> tracks) =>
        tracks.Where(t => t.Points.Count >= 2).Select(t => BuildTrackFeature(geo, t)).ToList();

    private static void ReplaceRole(string fieldDirectory, string role, Func<Projection, IEnumerable<GeoJsonFeature>> build)
    {
        var path = Path.Combine(fieldDirectory, FileName);
        lock (FileLock)
        {
            var fc = ReadCollection(path);
            var geo = ProjectionOf(fc);
            fc.Features.RemoveAll(f => GetStringProp(f, FieldPropertyKeys.Role) == role);
            fc.Features.AddRange(build(geo));
            Write(path, fc);
        }
    }

    private static List<T> ReadRole<T>(string fieldDirectory, string role, Func<Projection, GeoJsonFeature, T?> read)
        where T : class
    {
        var fc = ReadCollection(Path.Combine(fieldDirectory, FileName));
        var geo = ProjectionOf(fc);
        return fc.Features
            .Where(f => GetStringProp(f, FieldPropertyKeys.Role) == role)
            .Select(f => read(geo, f))
            .OfType<T>()
            .ToList();
    }

    private static GeoJsonFeatureCollection ReadCollection(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("field.geojson not found", path);
        return JsonSerializer.Deserialize<GeoJsonFeatureCollection>(File.ReadAllText(path), SerializerOptions)
            ?? throw new InvalidDataException("Failed to deserialize GeoJSON");
    }

    private static GeoJsonFeature MetadataOf(GeoJsonFeatureCollection fc) =>
        fc.Features.FirstOrDefault(f => GetStringProp(f, FieldPropertyKeys.Role) == FeatureRoles.Metadata)
        ?? throw new InvalidDataException("GeoJSON missing metadata feature");

    private static Projection ProjectionOf(GeoJsonFeatureCollection fc)
    {
        var meta = MetadataOf(fc);
        return new Projection(GetDoubleProp(meta, FieldPropertyKeys.OriginLatitude),
                              GetDoubleProp(meta, FieldPropertyKeys.OriginLongitude));
    }

    private static void Write(string path, GeoJsonFeatureCollection fc)
    {
        var json = JsonSerializer.Serialize(fc, SerializerOptions);
        File.WriteAllText(path + ".tmp", json);
        File.Move(path + ".tmp", path, overwrite: true);
    }

    /// <summary>
    /// Load a field from a GeoJSON FeatureCollection.
    /// Returns the field and any tracks found.
    /// </summary>
    public static (Models.Field field, List<Models.Track.Track> tracks) Load(string fieldDirectory)
    {
        var path = Path.Combine(fieldDirectory, FileName);
        if (!File.Exists(path))
            throw new FileNotFoundException("field.geojson not found", path);

        var fc = ReadCollection(path);

        // We need the origin first to set up coordinate conversion
        var metaFeature = MetadataOf(fc);

        double originLat = GetDoubleProp(metaFeature, FieldPropertyKeys.OriginLatitude);
        double originLon = GetDoubleProp(metaFeature, FieldPropertyKeys.OriginLongitude);

        var field = new Models.Field
        {
            Name = Path.GetFileName(fieldDirectory),
            DirectoryPath = fieldDirectory,
            Origin = new Position { Latitude = originLat, Longitude = originLon },
            Convergence = GetDoubleProp(metaFeature, FieldPropertyKeys.Convergence),
        };

        var nameProp = GetStringProp(metaFeature, FieldPropertyKeys.Name);
        if (!string.IsNullOrEmpty(nameProp))
            field.Name = nameProp;

        var createdStr = GetStringProp(metaFeature, FieldPropertyKeys.CreatedDate);
        if (DateTime.TryParse(createdStr, out var created))
            field.CreatedDate = created;

        var modifiedStr = GetStringProp(metaFeature, FieldPropertyKeys.LastModifiedDate);
        if (DateTime.TryParse(modifiedStr, out var modified))
            field.LastModifiedDate = modified;

        var geo = new Projection(originLat, originLon);
        var boundary = new Boundary();
        var tracks = new List<Models.Track.Track>();

        foreach (var feature in fc.Features)
        {
            var role = GetStringProp(feature, FieldPropertyKeys.Role);
            switch (role)
            {
                case FeatureRoles.OuterBoundary:
                    boundary.OuterBoundary = ReadBoundaryPolygon(geo, feature);
                    if (boundary.OuterBoundary != null)
                        boundary.OuterBoundary.IsHard = GetBoolProp(feature, FieldPropertyKeys.IsHard);
                    break;

                case FeatureRoles.InnerBoundary:
                    var inner = ReadBoundaryPolygon(geo, feature);
                    if (inner != null)
                    {
                        inner.IsDriveThrough = GetBoolProp(feature, FieldPropertyKeys.IsDriveThrough);
                        inner.IsHard = GetBoolProp(feature, FieldPropertyKeys.IsHard);
                        boundary.InnerBoundaries.Add(inner);
                    }
                    break;

                case FeatureRoles.Headland:
                    boundary.HeadlandPolygon = ReadBoundaryPolygon(geo, feature);
                    break;

                case FeatureRoles.Track:
                    var track = ReadTrack(geo, feature);
                    if (track != null)
                        tracks.Add(track);
                    break;

            }
        }

        if (boundary.OuterBoundary != null || boundary.InnerBoundaries.Count > 0 || boundary.HeadlandPolygon != null)
            field.Boundary = boundary;

        return (field, tracks);
    }

    // ---------------------------------------------------------------
    // Plane <-> WGS84
    // ---------------------------------------------------------------

    // The field plane <-> WGS84, with LocalPlane: longitude scaled at each point's own
    // latitude, the same conversion as live GPS and the AgOpenGPS import, so the coordinates
    // are right for GIS tools too.
    private sealed class Projection
    {
        private readonly LocalPlane _plane;

        public Projection(double originLat, double originLon) =>
            _plane = new LocalPlane(new Wgs84(originLat, originLon), new SharedFieldProperties());

        public (double lat, double lon) ToWgs84(Vec2 local)
        {
            var w = _plane.ConvertGeoCoordToWgs84(new GeoCoord(local.Northing, local.Easting));
            return (w.Latitude, w.Longitude);
        }

        public Vec2 ToLocal(double lat, double lon)
        {
            var c = _plane.ConvertWgs84ToGeoCoord(new Wgs84(lat, lon));
            return new Vec2(c.Easting, c.Northing);
        }

        /// <summary>Local points to GeoJSON [lon, lat] (Vec2) or [lon, lat, heading] (Vec3).</summary>
        public List<double[]> ToGeoJsonCoordinates(IReadOnlyList<Vec2> points) =>
            points.Select(p => { var (lat, lon) = ToWgs84(p); return new[] { lon, lat }; }).ToList();

        public List<double[]> ToGeoJsonCoordinates(IReadOnlyList<Vec3> points) =>
            points.Select(p => { var (lat, lon) = ToWgs84(new Vec2(p.Easting, p.Northing)); return new[] { lon, lat, p.Heading }; }).ToList();

        /// <summary>GeoJSON [lon, lat(, heading)] back to local points; heading defaults to 0.</summary>
        public List<Vec3> FromGeoJsonCoordinatesVec3(IReadOnlyList<double[]> coords) =>
            coords.Where(c => c.Length >= 2)
                  .Select(c => { var l = ToLocal(c[1], c[0]); return new Vec3(l.Easting, l.Northing, c.Length >= 3 ? c[2] : 0); })
                  .ToList();
    }

    // ---------------------------------------------------------------
    // Feature builders (local -> GeoJSON)
    // ---------------------------------------------------------------

    private static GeoJsonFeature BuildMetadataFeature(Models.Field field)
    {
        return new GeoJsonFeature
        {
            Geometry = new GeoJsonGeometry
            {
                Type = GeoJsonTypes.Point,
                Coordinates = new[] { field.Origin.Longitude, field.Origin.Latitude }
            },
            Properties = new Dictionary<string, object?>
            {
                [FieldPropertyKeys.Role] = FeatureRoles.Metadata,
                [FieldPropertyKeys.Name] = field.Name,
                [FieldPropertyKeys.OriginLatitude] = field.Origin.Latitude,
                [FieldPropertyKeys.OriginLongitude] = field.Origin.Longitude,
                [FieldPropertyKeys.Convergence] = field.Convergence,
                [FieldPropertyKeys.AreaHectares] = field.TotalArea,
                [FieldPropertyKeys.CreatedDate] = field.CreatedDate.ToString("O"),
                [FieldPropertyKeys.LastModifiedDate] = field.LastModifiedDate.ToString("O"),
            }
        };
    }

    private static GeoJsonFeature BuildBoundaryFeature(Projection geo, BoundaryPolygon polygon, string role)
    {
        var ring = BoundaryToGeoJsonRing(geo, polygon);
        return new GeoJsonFeature
        {
            Geometry = new GeoJsonGeometry
            {
                Type = GeoJsonTypes.Polygon,
                Coordinates = new[] { ring }
            },
            Properties = new Dictionary<string, object?>
            {
                [FieldPropertyKeys.Role] = role,
                [FieldPropertyKeys.IsDriveThrough] = polygon.IsDriveThrough,
                [FieldPropertyKeys.IsHard] = polygon.IsHard,
                [FieldPropertyKeys.AreaHectares] = polygon.AreaHectares,
            }
        };
    }

    private static GeoJsonFeature BuildTrackFeature(Projection geo, Models.Track.Track track)
    {
        var coords = geo.ToGeoJsonCoordinates(track.Points);
        return new GeoJsonFeature
        {
            Geometry = new GeoJsonGeometry
            {
                Type = GeoJsonTypes.LineString,
                Coordinates = coords.Select(c => (object)c).ToArray()
            },
            Properties = new Dictionary<string, object?>
            {
                [FieldPropertyKeys.Role] = FeatureRoles.Track,
                [FieldPropertyKeys.Name] = track.Name,
                [FieldPropertyKeys.TrackType] = (int)track.Type,
                [FieldPropertyKeys.IsClosed] = track.IsClosed,
                [FieldPropertyKeys.NoPassOffset] = track.NoPassOffset,
                [FieldPropertyKeys.NudgeDistance] = track.NudgeDistance,
                [FieldPropertyKeys.IsVisible] = track.IsVisible,
            }
        };
    }

    // The image's corners as a WGS84 polygon (NW, NE, SE, SW, closed), so GIS tools place it.
    private static GeoJsonFeature BuildBackgroundFeature(FieldBackground bg)
    {
        double n = bg.NwLatitude, w = bg.NwLongitude, s = bg.SeLatitude, e = bg.SeLongitude;
        var props = new Dictionary<string, object?>
        {
            [FieldPropertyKeys.Role] = FeatureRoles.BackgroundImage,
            [FieldPropertyKeys.Image] = bg.ImageFile,
        };
        if (bg.Mercator is { } m)
        {
            props[FieldPropertyKeys.MercatorMinX] = m.MinX;
            props[FieldPropertyKeys.MercatorMaxX] = m.MaxX;
            props[FieldPropertyKeys.MercatorMinY] = m.MinY;
            props[FieldPropertyKeys.MercatorMaxY] = m.MaxY;
        }
        return new GeoJsonFeature
        {
            Geometry = new GeoJsonGeometry
            {
                Type = GeoJsonTypes.Polygon,
                Coordinates = new[] { new object[] { new[] { w, n }, new[] { e, n }, new[] { e, s }, new[] { w, s }, new[] { w, n } } },
            },
            Properties = props,
        };
    }

    /// <summary>
    /// Convert a BoundaryPolygon to a GeoJSON ring (closed array of [lon, lat, heading]).
    /// </summary>
    private static object[] BoundaryToGeoJsonRing(Projection geo, BoundaryPolygon polygon)
    {
        var ring = new List<object>(polygon.Points.Count + 1);
        foreach (var pt in polygon.Points)
        {
            var (lat, lon) = geo.ToWgs84(new Vec2(pt.Easting, pt.Northing));
            ring.Add(new[] { lon, lat, pt.Heading });
        }
        // Close the ring
        if (polygon.Points.Count > 0)
        {
            var first = polygon.Points[0];
            var (lat, lon) = geo.ToWgs84(new Vec2(first.Easting, first.Northing));
            ring.Add(new[] { lon, lat, first.Heading });
        }
        return ring.ToArray();
    }

    private static GeoJsonFeature BuildFlagFeature(Projection geo, Flag flag)
    {
        var (lat, lon) = geo.ToWgs84(new Vec2(flag.Easting, flag.Northing));
        var props = new Dictionary<string, object?>
        {
            [FieldPropertyKeys.Role] = FeatureRoles.Flag,
            [FieldPropertyKeys.Name] = flag.Name,
            [FieldPropertyKeys.Color] = (int)flag.FlagColor,
            [FieldPropertyKeys.Id] = flag.UniqueNumber,
        };
        if (!string.IsNullOrEmpty(flag.Notes))
            props[FieldPropertyKeys.Notes] = flag.Notes;
        return new GeoJsonFeature
        {
            Geometry = new GeoJsonGeometry { Type = GeoJsonTypes.Point, Coordinates = new[] { lon, lat } },
            Properties = props,
        };
    }

    private static GeoJsonFeature BuildHeadlandPathFeature(Projection geo, HeadlandPath path)
    {
        return new GeoJsonFeature
        {
            Geometry = new GeoJsonGeometry
            {
                Type = GeoJsonTypes.LineString,
                Coordinates = geo.ToGeoJsonCoordinates(path.TrackPoints).Select(c => (object)c).ToArray(),
            },
            Properties = new Dictionary<string, object?>
            {
                [FieldPropertyKeys.Role] = FeatureRoles.HeadlandLine,
                [FieldPropertyKeys.Name] = path.Name,
                [FieldPropertyKeys.MoveDistance] = path.MoveDistance,
                [FieldPropertyKeys.Mode] = path.Mode,
                [FieldPropertyKeys.APointIndex] = path.APointIndex,
            }
        };
    }

    // ---------------------------------------------------------------
    // Feature readers (GeoJSON -> local)
    // ---------------------------------------------------------------

    private static BoundaryPolygon? ReadBoundaryPolygon(Projection geo, GeoJsonFeature feature)
    {
        var coords = ReadPolygonRing(feature.Geometry, 0);
        if (coords == null || coords.Count < 3)
            return null;

        var polygon = new BoundaryPolygon();
        foreach (var coord in coords)
        {
            var local = geo.ToLocal(coord[1], coord[0]);
            double heading = coord.Length >= 3 ? coord[2] : 0;
            polygon.Points.Add(new BoundaryPoint(local.Easting, local.Northing, heading));
        }

        // Remove closing duplicate if present
        if (polygon.Points.Count > 1)
        {
            var first = polygon.Points[0];
            var last = polygon.Points[^1];
            if (Math.Abs(first.Easting - last.Easting) < 0.001 &&
                Math.Abs(first.Northing - last.Northing) < 0.001)
            {
                polygon.Points.RemoveAt(polygon.Points.Count - 1);
            }
        }

        polygon.UpdateBounds();
        return polygon;
    }

    private static Models.Track.Track? ReadTrack(Projection geo, GeoJsonFeature feature)
    {
        var coords = ReadLineStringCoords(feature.Geometry);
        if (coords == null || coords.Count < 2)
            return null;

        var points = geo.FromGeoJsonCoordinatesVec3(coords);

        var trackTypeInt = GetIntProp(feature, FieldPropertyKeys.TrackType);
        var trackType = Enum.IsDefined(typeof(TrackType), trackTypeInt)
            ? (TrackType)trackTypeInt
            : TrackType.ABLine;

        return new Models.Track.Track
        {
            Name = GetStringProp(feature, FieldPropertyKeys.Name) ?? string.Empty,
            Points = points,
            Type = trackType,
            IsClosed = GetBoolProp(feature, FieldPropertyKeys.IsClosed),
            NoPassOffset = GetBoolProp(feature, FieldPropertyKeys.NoPassOffset),
            NudgeDistance = GetDoubleProp(feature, FieldPropertyKeys.NudgeDistance),
            IsVisible = GetBoolPropDefault(feature, FieldPropertyKeys.IsVisible, true),
        };
    }

    private static Flag? ReadFlag(Projection geo, GeoJsonFeature feature)
    {
        if (feature.Geometry.Coordinates is not JsonElement je || je.ValueKind != JsonValueKind.Array)
            return null;
        var c = je.EnumerateArray().Select(x => x.GetDouble()).ToArray();
        if (c.Length < 2)
            return null;
        var local = geo.ToLocal(c[1], c[0]);
        int id = GetIntProp(feature, FieldPropertyKeys.Id);
        int color = GetIntProp(feature, FieldPropertyKeys.Color);
        var flag = new Flag(local.Easting, local.Northing,
            Enum.IsDefined(typeof(FlagColor), color) ? (FlagColor)color : FlagColor.Red, id,
            GetStringProp(feature, FieldPropertyKeys.Name) is { Length: > 0 } name ? name : $"Flag {id}");
        if (GetStringProp(feature, FieldPropertyKeys.Notes) is { Length: > 0 } notes)
            flag.Notes = notes;
        return flag;
    }

    private static HeadlandPath? ReadHeadlandPath(Projection geo, GeoJsonFeature feature)
    {
        var coords = ReadLineStringCoords(feature.Geometry);
        if (coords == null || coords.Count == 0)
            return null;
        return new HeadlandPath
        {
            Name = GetStringProp(feature, FieldPropertyKeys.Name) ?? string.Empty,
            TrackPoints = geo.FromGeoJsonCoordinatesVec3(coords),
            MoveDistance = GetDoubleProp(feature, FieldPropertyKeys.MoveDistance),
            Mode = GetIntProp(feature, FieldPropertyKeys.Mode),
            APointIndex = GetIntProp(feature, FieldPropertyKeys.APointIndex),
        };
    }

    private static FieldBackground? ReadBackground(Projection geo, GeoJsonFeature feature)
    {
        var coords = ReadPolygonRing(feature.Geometry, 0);
        var image = GetStringProp(feature, FieldPropertyKeys.Image);
        if (coords == null || coords.Count < 4 || string.IsNullOrEmpty(image))
            return null;
        MercatorBounds? mercator = HasProp(feature, FieldPropertyKeys.MercatorMinX)
            ? new MercatorBounds(GetDoubleProp(feature, FieldPropertyKeys.MercatorMinX), GetDoubleProp(feature, FieldPropertyKeys.MercatorMaxX),
                                 GetDoubleProp(feature, FieldPropertyKeys.MercatorMinY), GetDoubleProp(feature, FieldPropertyKeys.MercatorMaxY))
            : null;
        return new FieldBackground(image,
            coords.Max(c => c[1]), coords.Min(c => c[0]),
            coords.Min(c => c[1]), coords.Max(c => c[0]),
            mercator);
    }

    // ---------------------------------------------------------------
    // JSON coordinate extraction helpers
    // ---------------------------------------------------------------

    /// <summary>
    /// Read the outer ring (index 0) of a Polygon geometry.
    /// Coordinates arrive as a JsonElement after deserialization.
    /// </summary>
    private static List<double[]>? ReadPolygonRing(GeoJsonGeometry geometry, int ringIndex)
    {
        if (geometry.Coordinates is not JsonElement je)
            return null;

        if (je.ValueKind != JsonValueKind.Array)
            return null;

        int idx = 0;
        foreach (var ringElem in je.EnumerateArray())
        {
            if (idx == ringIndex)
                return ParseCoordArray(ringElem);
            idx++;
        }
        return null;
    }

    private static List<double[]>? ReadLineStringCoords(GeoJsonGeometry geometry)
    {
        if (geometry.Coordinates is not JsonElement je)
            return null;

        if (je.ValueKind != JsonValueKind.Array)
            return null;

        return ParseCoordArray(je);
    }

    private static List<double[]> ParseCoordArray(JsonElement arrayElem)
    {
        var result = new List<double[]>();
        foreach (var ptElem in arrayElem.EnumerateArray())
        {
            if (ptElem.ValueKind != JsonValueKind.Array)
                continue;

            var values = new List<double>();
            foreach (var v in ptElem.EnumerateArray())
            {
                if (v.TryGetDouble(out double d))
                    values.Add(d);
            }
            if (values.Count >= 2)
                result.Add(values.ToArray());
        }
        return result;
    }

    // ---------------------------------------------------------------
    // Property helpers
    // ---------------------------------------------------------------

    private static string? GetStringProp(GeoJsonFeature f, string key)
    {
        if (f.Properties.TryGetValue(key, out var val))
        {
            if (val is JsonElement je && je.ValueKind == JsonValueKind.String)
                return je.GetString();
            return val?.ToString();
        }
        return null;
    }

    private static bool HasProp(GeoJsonFeature f, string key) =>
        f.Properties.TryGetValue(key, out var val) && val is not null &&
        !(val is JsonElement je && je.ValueKind == JsonValueKind.Null);

    private static double GetDoubleProp(GeoJsonFeature f, string key)
    {
        if (f.Properties.TryGetValue(key, out var val))
        {
            if (val is JsonElement je && je.ValueKind == JsonValueKind.Number)
                return je.GetDouble();
            if (val is double d)
                return d;
            if (double.TryParse(val?.ToString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double parsed)) // #112
                return parsed;
        }
        return 0;
    }

    private static int GetIntProp(GeoJsonFeature f, string key)
    {
        if (f.Properties.TryGetValue(key, out var val))
        {
            if (val is JsonElement je && je.ValueKind == JsonValueKind.Number)
                return je.GetInt32();
            if (val is int i)
                return i;
            if (int.TryParse(val?.ToString(), out int parsed))
                return parsed;
        }
        return 0;
    }

    private static bool GetBoolProp(GeoJsonFeature f, string key)
    {
        return GetBoolPropDefault(f, key, false);
    }

    private static bool GetBoolPropDefault(GeoJsonFeature f, string key, bool defaultValue)
    {
        if (f.Properties.TryGetValue(key, out var val))
        {
            if (val is JsonElement je)
            {
                if (je.ValueKind == JsonValueKind.True) return true;
                if (je.ValueKind == JsonValueKind.False) return false;
            }
            if (val is bool b)
                return b;
            if (bool.TryParse(val?.ToString(), out bool parsed))
                return parsed;
        }
        return defaultValue;
    }
}
