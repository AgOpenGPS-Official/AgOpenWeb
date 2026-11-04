// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AgOpenWeb.Services;

/// <summary>
/// The <c>Import</c> folder under the data root, where the user copies KML/KMZ files and
/// ISO-XML datasets for the From KML, From ISO-XML and Import KML Boundary pickers.
/// </summary>
public static class ImportFolder
{
    /// <summary>What the pickers offer: one entry per ISO-XML partfield, and the KML/KMZ file names.</summary>
    public sealed record Listing(IReadOnlyList<string> IsoXml, IReadOnlyList<string> Kml)
    {
        public static readonly Listing Empty = new(Array.Empty<string>(), Array.Empty<string>());

        public bool SameAs(Listing other) => IsoXml.SequenceEqual(other.IsoXml) && Kml.SequenceEqual(other.Kml);
    }

    /// <summary><c>&lt;data root&gt;/Import</c> (honors <c>AGOPENWEB_DATA</c>, like Fields).</summary>
    public static string Path => System.IO.Path.Combine(AppDataRoot.Documents, "Import");

    /// <summary>The folder, created if missing, so the user finds where to put files
    /// without making it by hand.</summary>
    public static string Ensure()
    {
        var dir = Path;
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// The datasets and files under <paramref name="dir"/>, subfolders included: every
    /// TASKDATA.XML's partfields (any case: ISOBUS writes TASKDATA.XML, which File.Exists
    /// misses on Linux, #111; a farm export holds many) and every .kml/.kmz. It walks the whole
    /// folder, so it runs when a picker asks for it (<see cref="ImportFolderScanner"/>), never
    /// on every broadcast. Empty when the folder can't be read.
    /// </summary>
    public static Listing Scan(string dir)
    {
        var iso = new List<string>();
        var kml = new List<string>();
        try
        {
            foreach (var e in IsoXml.IsoXmlImportCatalog.Scan(dir))
                iso.Add(e.Name);
            foreach (var fp in Directory.GetFiles(dir, "*.kml", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(dir, "*.kmz", SearchOption.AllDirectories)))
                kml.Add(System.IO.Path.GetFileName(fp));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Listing.Empty;
        }
        return new Listing(iso, kml);
    }
}
