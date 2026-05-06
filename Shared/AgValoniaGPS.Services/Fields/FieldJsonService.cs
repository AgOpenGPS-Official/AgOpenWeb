// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
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
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgValoniaGPS.Models;

namespace AgValoniaGPS.Services.Fields;

/// <summary>
/// Reads and writes <c>field.json</c> — slim per-field metadata
/// (name, origin, timestamps) introduced by the Fields &amp; Jobs split.
/// Geometry (boundary, headland, tracks) is unaffected and continues to
/// live in <c>field.geojson</c> / per-component files.
/// </summary>
public static class FieldJsonService
{
    private const string FileName = "field.json";

    /// <summary>
    /// Bumped whenever the on-disk shape changes in a non-additive way.
    /// New optional fields don't require a bump.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static bool Exists(string fieldDirectory) =>
        File.Exists(Path.Combine(fieldDirectory, FileName));

    public static string PathFor(string fieldDirectory) =>
        Path.Combine(fieldDirectory, FileName);

    /// <summary>
    /// Write metadata extracted from <paramref name="field"/> to
    /// <c>&lt;fieldDirectory&gt;/field.json</c>. Heavy state (boundary,
    /// background image) is not written here.
    /// </summary>
    public static void Save(Models.Field field, string fieldDirectory)
    {
        if (string.IsNullOrWhiteSpace(fieldDirectory))
            throw new ArgumentException("fieldDirectory must be set", nameof(fieldDirectory));

        if (!Directory.Exists(fieldDirectory))
            Directory.CreateDirectory(fieldDirectory);

        var dto = new FieldMetadataDto
        {
            SchemaVersion = CurrentSchemaVersion,
            Id = field.Id,
            Name = field.Name,
            Origin = new OriginDto
            {
                Latitude = field.Origin.Latitude,
                Longitude = field.Origin.Longitude
            },
            Convergence = field.Convergence,
            OffsetX = field.OffsetX,
            OffsetY = field.OffsetY,
            CreatedDate = field.CreatedDate,
            LastModifiedDate = field.LastModifiedDate,
            LastOpenedDate = field.LastOpenedDate
        };

        var json = JsonSerializer.Serialize(dto, SerializerOptions);
        File.WriteAllText(PathFor(fieldDirectory), json);
    }

    /// <summary>
    /// Read <c>field.json</c> and return a <see cref="Models.Field"/>
    /// populated with metadata only. Geometry must be loaded separately
    /// by the caller.
    /// </summary>
    /// <returns>The field, or null if no <c>field.json</c> exists.</returns>
    public static Models.Field? Load(string fieldDirectory)
    {
        var path = PathFor(fieldDirectory);
        if (!File.Exists(path)) return null;

        var json = File.ReadAllText(path);
        var dto = JsonSerializer.Deserialize<FieldMetadataDto>(json, SerializerOptions)
                  ?? throw new InvalidDataException($"field.json at {path} deserialized to null");

        return new Models.Field
        {
            Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id,
            Name = dto.Name ?? Path.GetFileName(fieldDirectory.TrimEnd(Path.DirectorySeparatorChar)),
            DirectoryPath = fieldDirectory,
            Origin = new Position
            {
                Latitude = dto.Origin?.Latitude ?? 0,
                Longitude = dto.Origin?.Longitude ?? 0
            },
            Convergence = dto.Convergence,
            OffsetX = dto.OffsetX,
            OffsetY = dto.OffsetY,
            CreatedDate = dto.CreatedDate ?? DateTime.Now,
            LastModifiedDate = dto.LastModifiedDate ?? DateTime.Now,
            LastOpenedDate = dto.LastOpenedDate ?? DateTime.Now
        };
    }

    private sealed class FieldMetadataDto
    {
        public int SchemaVersion { get; set; }
        public Guid Id { get; set; }
        public string? Name { get; set; }
        public OriginDto? Origin { get; set; }
        public double Convergence { get; set; }
        public double OffsetX { get; set; }
        public double OffsetY { get; set; }
        public DateTime? CreatedDate { get; set; }
        public DateTime? LastModifiedDate { get; set; }
        public DateTime? LastOpenedDate { get; set; }
    }

    private sealed class OriginDto
    {
        public double Latitude { get; set; }
        public double Longitude { get; set; }
    }
}
