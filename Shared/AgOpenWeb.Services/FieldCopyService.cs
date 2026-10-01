// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.IO;
using AgOpenWeb.Services.GeoJson;

namespace AgOpenWeb.Services
{
    /// <summary>
    /// Builds a new field directory from an existing one ("From Existing", #107), following
    /// AgOpenGPS FormFieldExisting: the new field keeps the source's origin, boundary,
    /// background and elevation; flags, applied area, headland and guidance lines are
    /// optional. The caller then opens it through the normal open path.
    /// </summary>
    public static class FieldCopyService
    {
        /// <summary>
        /// Create <paramref name="newDirectory"/> from <paramref name="sourceDirectory"/>. The
        /// field itself (boundary, background, field.geojson) is re-saved under the new name via
        /// <paramref name="fieldService"/>: a raw copy of field.geojson would carry the source's
        /// name, which wins over the directory name on load. The source is only read, so an
        /// AgOpenGPS source keeps its files until it is opened itself.
        /// </summary>
        public static void CreateFromExisting(IFieldService fieldService, string sourceDirectory,
            string newDirectory, string newName,
            bool copyFlags, bool copyMapping, bool copyHeadland, bool copyLines)
        {
            var field = fieldService.PeekField(sourceDirectory);
            Directory.CreateDirectory(newDirectory);
            field.Name = newName;
            field.DirectoryPath = newDirectory;
            // The headland polygon travels in field.geojson with the boundary.
            if (!copyHeadland && field.Boundary != null)
                field.Boundary.HeadlandPolygon = null;
            fieldService.SaveField(field);

            // Tracks, flags and headland lines live in field.geojson; read from the source in
            // either format (it isn't imported), written to the copy's field.geojson.
            GeoJsonFieldService.SaveHeadlandLine(newDirectory, fieldService.PeekHeadlandLine(sourceDirectory));
            if (copyFlags)
                GeoJsonFieldService.SaveFlags(newDirectory, fieldService.PeekFlags(sourceDirectory));
            if (copyLines)
                GeoJsonFieldService.SaveTracks(newDirectory, fieldService.PeekTracks(sourceDirectory));

            // The background image: its placement in field.geojson, the image file alongside.
            if (fieldService.PeekBackground(sourceDirectory) is { } background &&
                File.Exists(Path.Combine(sourceDirectory, background.ImageFile)))
            {
                File.Copy(Path.Combine(sourceDirectory, background.ImageFile),
                    Path.Combine(newDirectory, Models.FieldBackground.DefaultImageFile), overwrite: true);
                GeoJsonFieldService.SaveBackground(newDirectory,
                    background with { ImageFile = Models.FieldBackground.DefaultImageFile });
            }

            // Always (AgOpenGPS copies these regardless of the options).
            // The recorded-data files go across as they are; an AgOpenGPS source's own
            // (Elevation.txt, RecPath.txt, *.rec, Contour.txt) are imported when the copy opens.
            Copy(sourceDirectory, newDirectory, "field.origin");
            Copy(sourceDirectory, newDirectory, ElevationLogService.FileName);
            Copy(sourceDirectory, newDirectory, "Elevation.txt");

            if (copyHeadland)
                Copy(sourceDirectory, newDirectory, "HeadlandSegments.json");

            if (copyLines)
            {
                Copy(sourceDirectory, newDirectory, GeoJsonFieldService.RecordedPathsFileName);
                Copy(sourceDirectory, newDirectory, RecPathFileService.FileName);
                foreach (var rec in Directory.EnumerateFiles(sourceDirectory, "*.rec"))
                    Copy(sourceDirectory, newDirectory, Path.GetFileName(rec));
                Copy(sourceDirectory, newDirectory, "TramConfig.json");
                Copy(sourceDirectory, newDirectory, "TramSystems.json");
            }

            if (copyMapping)
            {
                // Applied area: per-job coverage lives under jobs/<task>/ (plus pre-jobs
                // legacy files, which the open path migrates).
                var jobs = Path.Combine(sourceDirectory, "jobs");
                if (Directory.Exists(jobs))
                    CopyDirectory(jobs, Path.Combine(newDirectory, "jobs"));
                Copy(sourceDirectory, newDirectory, "Sections.txt");
                Copy(sourceDirectory, newDirectory, GeoJsonFieldService.ContoursFileName);
                Copy(sourceDirectory, newDirectory, "Contour.txt");
            }
        }

        private static void Copy(string fromDir, string toDir, string fileName)
        {
            var src = Path.Combine(fromDir, fileName);
            if (File.Exists(src))
                File.Copy(src, Path.Combine(toDir, fileName), overwrite: true);
        }

        private static void CopyDirectory(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.EnumerateFiles(from))
                File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: true);
            foreach (var d in Directory.EnumerateDirectories(from))
                CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)));
        }
    }
}
