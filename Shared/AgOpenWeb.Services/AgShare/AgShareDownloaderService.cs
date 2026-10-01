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

using System.Globalization;
using Newtonsoft.Json;
using AgOpenWeb.Models;
using AgOpenWeb.Models.AgShare;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Services.AgShare
{
    /// <summary>
    /// Service for downloading field data from AgShare cloud service.
    /// </summary>
    public class AgShareDownloaderService(AgShareClient agShareClient)
    {
        /// <summary>
        /// Downloads a field and saves it to disk
        /// </summary>
        public async Task<(bool success, string message)> DownloadAndSaveAsync(Guid fieldId, string fieldsDirectory)
        {
            try
            {
                string json = await agShareClient.DownloadFieldAsync(fieldId);
                var dto = JsonConvert.DeserializeObject<AgShareFieldDto>(json);
                var model = AgShareFieldParser.Parse(dto!);
                string fieldDir = Path.Combine(fieldsDirectory, model.Name);
                await FieldFileWriter.WriteAllFilesAsync(model, fieldDir);
                return (true, "Download successful");
            }
            catch (Exception ex)
            {
                return (false, $"Download failed: {ex.GetType().Name} - {ex.Message}");
            }
        }

        /// <summary>
        /// Retrieves a list of user-owned fields
        /// </summary>
        public async Task<List<AgShareGetOwnFieldDto>> GetOwnFieldsAsync()
        {
            return await agShareClient.GetOwnFieldsAsync();
        }

        /// <summary>
        /// Downloads a field DTO for preview only
        /// </summary>
        public async Task<AgShareFieldDto?> DownloadFieldPreviewAsync(Guid fieldId)
        {
            string json = await agShareClient.DownloadFieldAsync(fieldId);
            return JsonConvert.DeserializeObject<AgShareFieldDto>(json);
        }

        /// <summary>
        /// Downloads all user fields with progress reporting
        /// </summary>
        public async Task<(int Downloaded, int Skipped)> DownloadAllAsync(
            string fieldsDirectory,
            bool forceOverwrite = false,
            IProgress<int>? progress = null)
        {
            var fields = await GetOwnFieldsAsync();
            int skipped = 0, downloaded = 0;

            foreach (var field in fields)
            {
                string dir = Path.Combine(fieldsDirectory, field.Name);
                string agsharePath = Path.Combine(dir, "agshare.txt");

                bool alreadyExists = false;
                if (File.Exists(agsharePath))
                {
                    try
                    {
                        var id = (await File.ReadAllTextAsync(agsharePath)).Trim();
                        alreadyExists = Guid.TryParse(id, out Guid guid) && guid == field.Id;
                    }
                    catch
                    {
                        //If the ID file is unreadable, treat it as non-existent
                    }
                }

                if (alreadyExists && !forceOverwrite)
                {
                    skipped++;
                }
                else
                {
                    var preview = await DownloadFieldPreviewAsync(field.Id);
                    if (preview != null)
                    {
                        var model = AgShareFieldParser.Parse(preview);
                        await FieldFileWriter.WriteAllFilesAsync(model, dir);
                        downloaded++;
                    }
                }

                progress?.Report(downloaded + skipped);
            }

            return (downloaded, skipped);
        }
    }

    /// <summary>
    /// Writes a downloaded LocalFieldModel into a field folder: field.geojson for the field
    /// itself, plus the per-feature files AgOpenWeb still keeps in AgOpenGPS formats.
    /// </summary>
    public static class FieldFileWriter
    {
        /// <summary>
        /// Writes all files required for a field
        /// </summary>
        public static async Task WriteAllFilesAsync(LocalFieldModel field, string fieldDir)
        {
            if (!Directory.Exists(fieldDir))
                Directory.CreateDirectory(fieldDir);

            await WriteAgShareIdAsync(fieldDir, field.FieldId);
            WriteFieldGeoJson(fieldDir, field.Origin, field.Boundaries);
            await WriteTrackLinesTxtAsync(fieldDir, field.AbLines);
            await WriteStaticFilesAsync(fieldDir); // Flags, Contour (only if missing)
        }

        /// <summary>
        /// Writes agshare.txt with the field ID
        /// </summary>
        private static async Task WriteAgShareIdAsync(string fieldDir, Guid fieldId)
        {
            await File.WriteAllTextAsync(Path.Combine(fieldDir, "agshare.txt"), fieldId.ToString());
        }

        /// <summary>
        /// Writes the origin and boundary rings to field.geojson. A re-download over an existing
        /// field replaces those and keeps the rest (headland, background image); with no rings
        /// in the download the existing boundary stays. An earlier download still in AgOpenGPS
        /// files is imported first, so it can't win over this one on the next open.
        /// </summary>
        private static void WriteFieldGeoJson(string fieldDir, Wgs84 origin, List<List<LocalPoint>>? boundaries)
        {
            var fields = new FieldService();
            Field field;
            try
            {
                field = fields.LoadField(fieldDir);
            }
            catch (FileNotFoundException)
            {
                field = new Field { Name = Path.GetFileName(fieldDir), CreatedDate = DateTime.Now };
            }
            field.DirectoryPath = fieldDir;
            field.Origin = new Position { Latitude = origin.Latitude, Longitude = origin.Longitude };
            field.LastModifiedDate = DateTime.Now;

            if (boundaries is { Count: > 0 })
            {
                var boundary = field.Boundary ?? new Boundary();
                boundary.OuterBoundary = null;
                boundary.InnerBoundaries.Clear();
                for (int i = 0; i < boundaries.Count; i++)
                {
                    var polygon = new BoundaryPolygon();
                    foreach (var pt in BoundaryUtils.WithHeadings(ConvertToVec3List(boundaries[i])))
                        polygon.Points.Add(new BoundaryPoint(pt.Easting, pt.Northing, pt.Heading));
                    polygon.UpdateBounds();
                    if (i == 0)
                        boundary.OuterBoundary = polygon;
                    else
                    {
                        // Holes were written drive-through, as before.
                        polygon.IsDriveThrough = true;
                        boundary.InnerBoundaries.Add(polygon);
                    }
                }
                field.Boundary = boundary;
            }

            fields.SaveField(field);
        }

        /// <summary>
        /// Writes AB-lines and optional curve points to TrackLines.txt
        /// </summary>
        private static async Task WriteTrackLinesTxtAsync(string fieldDir, List<AbLineLocal> abLines)
        {
            var lines = new List<string> { "$TrackLines" };

            foreach (var ab in abLines)
            {
                lines.Add(ab.Name ?? "Unnamed");

                bool isCurve = ab.CurvePoints is { Count: > 1 };

                LocalPoint ptA = ab.PtA;
                LocalPoint ptB = ab.PtB;
                double heading = ab.Heading;

                if (isCurve)
                {
                    ptA = ab.CurvePoints![0];
                    ptB = ab.CurvePoints[ab.CurvePoints!.Count - 1];
                    heading = GeoConversion.HeadingFromPoints(
                        new Vec2(ptA.Easting, ptA.Northing),
                        new Vec2(ptB.Easting, ptB.Northing)
                    );
                }

                lines.Add(heading.ToString("0.###", CultureInfo.InvariantCulture));
                lines.Add(ptA.Easting.ToString("0.###", CultureInfo.InvariantCulture) + "," + ptA.Northing.ToString("0.###", CultureInfo.InvariantCulture));
                lines.Add(ptB.Easting.ToString("0.###", CultureInfo.InvariantCulture) + "," + ptB.Northing.ToString("0.###", CultureInfo.InvariantCulture));
                lines.Add("0"); // Nudge

                if (isCurve)
                {
                    lines.Add("4"); // Curve mode
                    lines.Add("True");
                    lines.Add(ab.CurvePoints!.Count.ToString(CultureInfo.InvariantCulture));

                    foreach (var pt in ab.CurvePoints)
                    {
                        lines.Add(
                            pt.Easting.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                            pt.Northing.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                            pt.Heading.ToString("0.#####", CultureInfo.InvariantCulture)
                        );
                    }
                }
                else
                {
                    lines.Add("2"); // AB mode
                    lines.Add("True");
                    lines.Add("0");
                }
            }

            await File.WriteAllLinesAsync(Path.Combine(fieldDir, "TrackLines.txt"), lines);
        }

        /// <summary>
        /// Empty Flags.txt and Contour.txt for a new field. These are local work,
        /// not part of what AgShare stores, so a re-download never overwrites them; Sections.txt
        /// (the applied area) isn't touched at all (AgOpenGPS #1203).
        /// </summary>
        private static async Task WriteStaticFilesAsync(string fieldDir)
        {
            await WriteIfMissingAsync(fieldDir, "Flags.txt", ["$Flags", "0"]);
            await WriteIfMissingAsync(fieldDir, "Contour.txt", ["$Contour", "0"]);
        }

        private static async Task WriteIfMissingAsync(string fieldDir, string name, string[] lines)
        {
            string path = Path.Combine(fieldDir, name);
            if (!File.Exists(path)) await File.WriteAllLinesAsync(path, lines);
        }

        /// <summary>
        /// Helper to convert LocalPoint list to Vec3 list
        /// </summary>
        private static List<Vec3> ConvertToVec3List(List<LocalPoint> points)
        {
            var result = new List<Vec3>();
            foreach (var pt in points)
            {
                result.Add(pt);
            }
            return result;
        }
    }
}
