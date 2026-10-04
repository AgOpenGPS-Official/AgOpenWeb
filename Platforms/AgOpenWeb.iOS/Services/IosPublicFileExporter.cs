// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.IO;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.iOS.Services;

/// <summary>
/// Exports files (such as bug report dumps) to the public Documents folder on iOS.
/// When UIFileSharingEnabled and LSSupportsOpeningDocumentsInPlace are enabled in Info.plist,
/// this folder is visible and accessible in the iOS Files app under "On My iPad/iPhone -> AgOpenWeb".
/// </summary>
internal sealed class IosPublicFileExporter : IPublicFileExporter
{
    public string? ExportToPublicDocuments(string sourceFilePath, string subFolder, string fileName)
    {
        if (!File.Exists(sourceFilePath))
            return null;

        try
        {
            var docsDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(docsDir))
                return null;

            // In the iOS Files app, the app container's Documents folder is already presented
            // as "AgOpenWeb". Normalize subFolder to prevent redundant "AgOpenWeb/AgOpenWeb" nesting.
            string cleanSub = (subFolder ?? string.Empty).Trim('/', '\\');
            if (cleanSub.StartsWith("AgOpenWeb/", StringComparison.OrdinalIgnoreCase) ||
                cleanSub.StartsWith("AgOpenWeb\\", StringComparison.OrdinalIgnoreCase))
            {
                cleanSub = cleanSub["AgOpenWeb/".Length..].Trim('/', '\\');
            }

            var targetDir = string.IsNullOrEmpty(cleanSub)
                ? docsDir
                : Path.Combine(docsDir, cleanSub);

            Directory.CreateDirectory(targetDir);
            var targetPath = Path.Combine(targetDir, fileName);
            File.Copy(sourceFilePath, targetPath, overwrite: true);

            return targetPath;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[IosPublicFileExporter] Export to public documents failed: {ex.Message}");
            return null;
        }
    }
}
