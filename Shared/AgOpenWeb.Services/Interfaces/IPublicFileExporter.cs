// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

namespace AgOpenWeb.Services.Interfaces;

/// <summary>
/// Platform-specific exporter to user-accessible public storage (e.g. Android Documents).
/// On desktop and iOS where AppDataRoot.Documents is already accessible, the default implementation
/// returns null so callers keep the direct file path.
/// </summary>
public interface IPublicFileExporter
{
    /// <summary>
    /// Exports a created file to a user-accessible public directory (e.g. Documents/AgOpenWeb/{subfolder}).
    /// Returns the user-visible destination path if exported, or null if direct filesystem path should be kept.
    /// </summary>
    string? ExportToPublicDocuments(string sourceFilePath, string subFolder, string fileName);
}
