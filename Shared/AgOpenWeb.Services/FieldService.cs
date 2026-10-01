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
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Guidance;
using AgOpenWeb.Services.GeoJson;

namespace AgOpenWeb.Services;

/// <summary>
/// Implementation of field management service
/// Coordinates file I/O services to provide complete field management
/// </summary>
public class FieldService : IFieldService
{
    private readonly FieldPlaneFileService _fieldPlaneService;
    private readonly BoundaryFileService _boundaryService;

    public event EventHandler<Field?>? ActiveFieldChanged;
    public Field? ActiveField { get; private set; }

    public FieldService()
    {
        _fieldPlaneService = new FieldPlaneFileService();
        _boundaryService = new BoundaryFileService();
    }

    /// <summary>
    /// Get list of available field names in the Fields directory
    /// </summary>
    public List<string> GetAvailableFields(string fieldsRootDirectory)
    {
        if (!Directory.Exists(fieldsRootDirectory))
        {
            return new List<string>();
        }

        return Directory.GetDirectories(fieldsRootDirectory)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Cast<string>()
            .OrderBy(name => name)
            .ToList();
    }

    // AgOpenGPS's field-definition files. A folder that has them is imported into field.geojson
    // once and they are deleted: one-way, AgOpenWeb never writes them back. Headland.Txt is
    // AgOpenGPS's headland polygon; Headlines.txt (the headland line) is imported with the
    // tracks and flags below.
    private static readonly string[] AgOpenGpsFieldFiles = { "Field.txt", "Boundary.txt", "Headland.Txt", "Headland.txt" };

    /// <summary>
    /// Load a complete field from field.geojson, first importing any AgOpenGPS files in the
    /// folder (field, boundary, headland, tracks, flags, headland lines) and deleting them. Throws <see cref="FileNotFoundException"/> when the
    /// folder holds neither.
    /// </summary>
    public Field LoadField(string fieldDirectory)
    {
        if (HasAgOpenGpsFieldFiles(fieldDirectory))
        {
            var imported = ReadAgOpenGpsField(fieldDirectory);
            GeoJsonFieldService.Save(imported, tracks: null);
            foreach (var name in AgOpenGpsFieldFiles)
                DeleteIfPresent(fieldDirectory, name);
        }
        var field = ReadGeoJsonField(fieldDirectory);

        // The field's other AgOpenGPS files, now that field.geojson exists to take them.
        if (File.Exists(Path.Combine(fieldDirectory, TrackFilesService.FileName)))
        {
            GeoJsonFieldService.SaveTracks(fieldDirectory, TrackFilesService.Load(fieldDirectory));
            DeleteIfPresent(fieldDirectory, TrackFilesService.FileName);
        }
        if (File.Exists(Path.Combine(fieldDirectory, TrackFilesService.AbLinesFileName)))
        {
            // AgOpenGPS's older AB-line file: added to whatever tracks the field has.
            var tracks = GeoJsonFieldService.LoadTracks(fieldDirectory);
            tracks.AddRange(TrackFilesService.LoadAbLines(fieldDirectory));
            GeoJsonFieldService.SaveTracks(fieldDirectory, tracks);
            DeleteIfPresent(fieldDirectory, TrackFilesService.AbLinesFileName);
        }
        if (File.Exists(Path.Combine(fieldDirectory, FlagFilesService.FileName)))
        {
            GeoJsonFieldService.SaveFlags(fieldDirectory, FlagFilesService.Load(fieldDirectory));
            DeleteIfPresent(fieldDirectory, FlagFilesService.FileName);
        }
        if (File.Exists(Path.Combine(fieldDirectory, HeadlandLineSerializer.FileName)))
        {
            GeoJsonFieldService.SaveHeadlandLine(fieldDirectory, HeadlandLineSerializer.Load(fieldDirectory));
            DeleteIfPresent(fieldDirectory, HeadlandLineSerializer.FileName);
        }
        ImportBackPic(fieldDirectory, field.Origin);
        ImportRecordedData(fieldDirectory);

        // Written at field close by older builds and never read: tram lines are generated on
        // demand from the field's tram settings.
        DeleteIfPresent(fieldDirectory, "TramLines.txt");
        return field;
    }

    // AgOpenGPS's contour strips, recorded paths (RecPath.txt in use, named *.rec) and elevation
    // log, into contours.geojson, recorded-paths.geojson and elevation.csv.
    private static void ImportRecordedData(string fieldDirectory)
    {
        if (File.Exists(Path.Combine(fieldDirectory, Contour.ContourFilesService.FileName)))
        {
            GeoJsonFieldService.AppendContours(fieldDirectory, Contour.ContourFilesService.Load(fieldDirectory));
            DeleteIfPresent(fieldDirectory, Contour.ContourFilesService.FileName);
        }
        if (RecPathFileService.LoadRecPathPoints(fieldDirectory) is { Count: > 0 } current)
            GeoJsonFieldService.SaveCurrentRecordedPath(fieldDirectory, current);
        DeleteIfPresent(fieldDirectory, RecPathFileService.FileName);
        foreach (var rec in Directory.EnumerateFiles(fieldDirectory, "*.rec").ToList())
        {
            if (RecPathFileService.LoadRecPathPointsFromFile(rec) is { Count: > 0 } points)
                GeoJsonFieldService.SaveRecordedPath(fieldDirectory, Path.GetFileNameWithoutExtension(rec), points);
            File.Delete(rec);
        }
        ElevationLogService.ImportAgOpenGpsFile(fieldDirectory);
    }

    // AgOpenGPS's background image: BackPic.png, placed by BackPic.txt (older builds: BackPic.Txt)
    // as "$BackPic", "True", then max E, min E, max N, min N in field-local metres.
    private static readonly string[] BackPicTextFiles = { "BackPic.txt", "BackPic.Txt" };
    private const string BackPicImageFile = "BackPic.png";

    private static FieldBackground? ReadBackPic(string fieldDirectory, Position origin)
    {
        foreach (var name in BackPicTextFiles)
        {
            var path = Path.Combine(fieldDirectory, name);
            if (!File.Exists(path) || !File.Exists(Path.Combine(fieldDirectory, BackPicImageFile)))
                continue;
            var lines = File.ReadAllLines(path);
            // Exactly AgOpenGPS's six lines; anything else isn't its layout.
            if (lines.Length < 6 || lines.Skip(6).Any(l => l.Trim().Length > 0) ||
                lines[0].Trim() != "$BackPic" ||
                !bool.TryParse(lines[1].Trim(), out bool geoMap) || !geoMap)
                return null;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var style = System.Globalization.NumberStyles.Float | System.Globalization.NumberStyles.AllowThousands; // written "N3"
            if (!double.TryParse(lines[2], style, inv, out double maxE) || !double.TryParse(lines[3], style, inv, out double minE) ||
                !double.TryParse(lines[4], style, inv, out double maxN) || !double.TryParse(lines[5], style, inv, out double minN))
                return null;
            var plane = new LocalPlane(new Wgs84(origin.Latitude, origin.Longitude), new SharedFieldProperties());
            var nw = plane.ConvertGeoCoordToWgs84(new GeoCoord(maxN, minE));
            var se = plane.ConvertGeoCoordToWgs84(new GeoCoord(minN, maxE));
            return new FieldBackground(BackPicImageFile, nw.Latitude, nw.Longitude, se.Latitude, se.Longitude, null);
        }
        return null;
    }

    private static void ImportBackPic(string fieldDirectory, Position origin)
    {
        if (!BackPicTextFiles.Any(n => File.Exists(Path.Combine(fieldDirectory, n))))
            return;
        var background = ReadBackPic(fieldDirectory, origin);
        if (background != null)
        {
            File.Move(Path.Combine(fieldDirectory, BackPicImageFile),
                      Path.Combine(fieldDirectory, FieldBackground.DefaultImageFile), overwrite: true);
            GeoJsonFieldService.SaveBackground(fieldDirectory, background with { ImageFile = FieldBackground.DefaultImageFile });
        }
        // Not AgOpenGPS's layout (or switched off): nothing to place the image with.
        foreach (var name in BackPicTextFiles)
            DeleteIfPresent(fieldDirectory, name);
        DeleteIfPresent(fieldDirectory, BackPicImageFile);
    }

    /// <summary>
    /// A field's background image placement, read like <see cref="PeekField"/> (either format,
    /// no changes). <see cref="FieldBackground.ImageFile"/> names the image in that folder.
    /// </summary>
    public FieldBackground? PeekBackground(string fieldDirectory)
    {
        try
        {
            if (BackPicTextFiles.Any(n => File.Exists(Path.Combine(fieldDirectory, n))))
                return ReadBackPic(fieldDirectory, PeekField(fieldDirectory).Origin);
            return GeoJsonFieldService.Exists(fieldDirectory) ? GeoJsonFieldService.LoadBackground(fieldDirectory) : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return null;
        }
    }

    private static void DeleteIfPresent(string fieldDirectory, string name)
    {
        var path = Path.Combine(fieldDirectory, name);
        if (File.Exists(path))
            File.Delete(path);
    }

    // Read-only views of a field's tracks, flags and headland lines in either format, like
    // PeekField: from its AgOpenGPS file while that hasn't been imported, else field.geojson.
    // Empty when the field has none (or isn't a field).
    public List<Models.Track.Track> PeekTracks(string fieldDirectory)
    {
        var tracks = File.Exists(Path.Combine(fieldDirectory, TrackFilesService.FileName))
            ? TrackFilesService.Load(fieldDirectory)
            : GeoJsonFieldService.Exists(fieldDirectory) ? GeoJsonFieldService.LoadTracks(fieldDirectory) : new();
        tracks.AddRange(TrackFilesService.LoadAbLines(fieldDirectory));
        return tracks;
    }

    public List<Flag> PeekFlags(string fieldDirectory) =>
        File.Exists(Path.Combine(fieldDirectory, FlagFilesService.FileName))
            ? FlagFilesService.Load(fieldDirectory)
            : GeoJsonFieldService.Exists(fieldDirectory) ? GeoJsonFieldService.LoadFlags(fieldDirectory) : new();

    public HeadlandLine PeekHeadlandLine(string fieldDirectory) =>
        File.Exists(Path.Combine(fieldDirectory, HeadlandLineSerializer.FileName))
            ? HeadlandLineSerializer.Load(fieldDirectory)
            : GeoJsonFieldService.Exists(fieldDirectory) ? GeoJsonFieldService.LoadHeadlandLine(fieldDirectory) : new();

    /// <summary>
    /// Read a field without changing its folder: field.geojson, or the AgOpenGPS files when
    /// they haven't been imported yet. For callers that only look at a field (lists, origins,
    /// copying from it), so browsing fields never converts them.
    /// </summary>
    public Field PeekField(string fieldDirectory) =>
        HasAgOpenGpsFieldFiles(fieldDirectory)
            ? ReadAgOpenGpsField(fieldDirectory)
            : ReadGeoJsonField(fieldDirectory);

    private static bool HasAgOpenGpsFieldFiles(string fieldDirectory) =>
        File.Exists(Path.Combine(fieldDirectory, "Field.txt"));

    private Field ReadAgOpenGpsField(string fieldDirectory)
    {
        var field = _fieldPlaneService.LoadField(fieldDirectory);
        field.Boundary = _boundaryService.LoadBoundary(fieldDirectory);
        return field;
    }

    private Field ReadGeoJsonField(string fieldDirectory)
    {
        if (!GeoJsonFieldService.Exists(fieldDirectory))
            throw new FileNotFoundException("No field.geojson (and no AgOpenGPS field to import)",
                Path.Combine(fieldDirectory, "field.geojson"));
        Field field;
        try
        {
            (field, _) = GeoJsonFieldService.Load(fieldDirectory);
        }
        catch (Exception ex)
        {
            // Unreadable (truncated write, power loss): set it aside for inspection so the next
            // save starts a fresh one, and report it.
            var path = Path.Combine(fieldDirectory, "field.geojson");
            try
            {
                File.Move(path, Path.Combine(fieldDirectory, $"field.geojson.corrupt.{DateTime.UtcNow:yyyyMMdd_HHmmss}"));
            }
            catch
            {
                // Couldn't rename it; still report the failure.
            }
            throw new InvalidDataException($"field.geojson in '{fieldDirectory}' is unreadable: {ex.Message}", ex);
        }
        return field;
    }

    /// <summary>
    /// Save a complete field to field.geojson. Tracks are saved separately by the caller.
    /// </summary>
    public void SaveField(Field field)
    {
        if (string.IsNullOrWhiteSpace(field.DirectoryPath))
        {
            throw new ArgumentException("Field.DirectoryPath must be set", nameof(field));
        }

        // A null Boundary means "not loaded", not "no boundary" (that's an empty Boundary):
        // keep the one on disk rather than wiping it, which a close-save from a field object
        // that never had its boundary set once did.
        if (field.Boundary == null && GeoJsonFieldService.Exists(field.DirectoryPath))
        {
            try
            {
                field.Boundary = GeoJsonFieldService.Load(field.DirectoryPath).field.Boundary;
            }
            catch
            {
                // Unreadable: nothing to keep.
            }
        }

        // Only field.geojson. The background image's files (BackPic.png / BackPic.txt) are
        // written by whoever sets the image, not on every save of the field.
        GeoJsonFieldService.Save(field, tracks: null);
    }

    /// <summary>
    /// Create a new empty field
    /// </summary>
    public Field CreateField(string fieldsRootDirectory, string fieldName, Position originPosition)
    {
        var fieldDirectory = Path.Combine(fieldsRootDirectory, fieldName);

        if (Directory.Exists(fieldDirectory))
        {
            throw new InvalidOperationException($"Field '{fieldName}' already exists");
        }

        Directory.CreateDirectory(fieldDirectory);

        var field = new Field
        {
            Name = fieldName,
            DirectoryPath = fieldDirectory,
            Origin = originPosition,
            CreatedDate = DateTime.Now,
            LastModifiedDate = DateTime.Now
        };

        SaveField(field);

        return field;
    }

    /// <summary>
    /// Delete a field (removes entire directory)
    /// </summary>
    public void DeleteField(string fieldDirectory)
    {
        if (Directory.Exists(fieldDirectory))
        {
            Directory.Delete(fieldDirectory, true);
        }
    }

    /// <summary>
    /// Check if a field exists (GeoJSON or legacy)
    /// </summary>
    public bool FieldExists(string fieldDirectory)
    {
        return Directory.Exists(fieldDirectory) &&
               (GeoJsonFieldService.Exists(fieldDirectory) ||
                File.Exists(Path.Combine(fieldDirectory, "Field.txt")));
    }

    /// <summary>
    /// Set the active field
    /// </summary>
    public void SetActiveField(Field? field)
    {
        if (ActiveField != field)
        {
            ActiveField = field;
            ActiveFieldChanged?.Invoke(this, field);
        }
    }

    public IReadOnlyList<NearbyField> FindFieldsNear(
        string fieldsRootDirectory, double latitude, double longitude, double maxKm)
    {
        if (!Directory.Exists(fieldsRootDirectory))
            return Array.Empty<NearbyField>();

        var query = new Wgs84(latitude, longitude);
        var results = new List<NearbyField>();

        foreach (var dir in Directory.GetDirectories(fieldsRootDirectory))
        {
            var origin = TryReadFieldOrigin(dir);
            if (origin == null) continue;

            // (0,0) origin = field never georeferenced. Including it would
            // distort the "near me" filter for every field that hasn't yet
            // been opened in the world.
            if (origin.Latitude == 0 && origin.Longitude == 0) continue;

            var distKm = query.DistanceInKiloMeters(new Wgs84(origin.Latitude, origin.Longitude));
            if (distKm > maxKm) continue;

            var areaHa = TryReadBoundaryAreaHectares(dir);
            results.Add(new NearbyField(
                Name: Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar)) ?? string.Empty,
                DirectoryPath: dir,
                DistanceKm: distKm,
                BoundaryAreaHectares: areaHa));
        }

        results.Sort((a, b) => a.DistanceKm.CompareTo(b.DistanceKm));
        return results;
    }

    private Position? TryReadFieldOrigin(string fieldDirectory)
    {
        try
        {
            return PeekField(fieldDirectory).Origin;
        }
        catch
        {
            return null;
        }
    }

    private double TryReadBoundaryAreaHectares(string fieldDirectory)
    {
        try
        {
            return PeekField(fieldDirectory).Boundary?.AreaHectares ?? 0;
        }
        catch
        {
            return 0;
        }
    }
}