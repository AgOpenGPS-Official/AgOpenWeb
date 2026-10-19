// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AgOpenWeb.Models.GeoJson;

/// <summary>
/// GeoJSON FeatureCollection (RFC 7946).
/// Top-level container for field data serialization.
/// </summary>
public class GeoJsonFeatureCollection
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "FeatureCollection";

    [JsonPropertyName("features")]
    public List<GeoJsonFeature> Features { get; set; } = new();
}

/// <summary>
/// GeoJSON Feature -- wraps a geometry with a properties bag.
/// </summary>
public class GeoJsonFeature
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "Feature";

    [JsonPropertyName("geometry")]
    public GeoJsonGeometry Geometry { get; set; } = new();

    [JsonPropertyName("properties")]
    public Dictionary<string, object?> Properties { get; set; } = new();
}

/// <summary>
/// GeoJSON Geometry -- supports Point, LineString, Polygon, MultiPolygon.
/// Coordinates use the raw JSON array form; callers convert via typed helpers.
/// </summary>
public class GeoJsonGeometry
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "Point";

    /// <summary>
    /// Raw coordinate data. Shape depends on Type:
    ///   Point       -> [lon, lat]                          (double[])
    ///   LineString  -> [[lon, lat], ...]                   (double[][])
    ///   Polygon     -> [[[lon, lat], ...], ...]            (double[][][])
    ///   MultiPolygon-> [[[[lon, lat], ...], ...], ...]     (double[][][][])
    /// Stored as object to allow System.Text.Json polymorphic round-trip.
    /// Use the typed accessors below for safe reading.
    /// </summary>
    [JsonPropertyName("coordinates")]
    public object? Coordinates { get; set; }
}

/// <summary>
/// Well-known GeoJSON geometry type strings.
/// </summary>
public static class GeoJsonTypes
{
    public const string Point = "Point";
    public const string LineString = "LineString";
    public const string Polygon = "Polygon";
    public const string MultiPolygon = "MultiPolygon";
}

/// <summary>
/// Well-known property keys used in AgOpenWeb field GeoJSON files.
/// </summary>
public static class FieldPropertyKeys
{
    public const string Role = "role";
    public const string Name = "name";
    public const string TrackType = "trackType";
    public const string IsClosed = "isClosed";
    public const string NoPassOffset = "noPassOffset";
    public const string NudgeDistance = "nudgeDistance";
    public const string IsVisible = "isVisible";
    public const string IsDriveThrough = "isDriveThrough";
    public const string IsHard = "isHard";
    public const string OriginLatitude = "originLatitude";
    public const string OriginLongitude = "originLongitude";
    public const string Convergence = "convergence";
    public const string AreaHectares = "areaHectares";
    public const string CreatedDate = "createdDate";
    public const string LastModifiedDate = "lastModifiedDate";
    public const string Color = "color";
    public const string Id = "id";
    public const string Notes = "notes";
    public const string MoveDistance = "moveDistance";
    public const string Mode = "mode";
    public const string APointIndex = "aPointIndex";
    public const string Image = "image";
    public const string MercatorMinX = "mercatorMinX";
    public const string MercatorMaxX = "mercatorMaxX";
    public const string MercatorMinY = "mercatorMinY";
    public const string MercatorMaxY = "mercatorMaxY";
    public const string Current = "current";
    public const string Speeds = "speeds";
    public const string AutoSteer = "autoSteer";
}

/// <summary>
/// Well-known role values stored in the "role" property.
/// </summary>
public static class FeatureRoles
{
    public const string Metadata = "metadata";
    public const string OuterBoundary = "outer-boundary";
    public const string InnerBoundary = "inner-boundary";
    public const string Headland = "headland";
    public const string Track = "track";
    public const string BackgroundImage = "background-image";
    public const string Flag = "flag";
    public const string HeadlandLine = "headland-line";
    public const string Contour = "contour";
    public const string RecordedPath = "recorded-path";
}
