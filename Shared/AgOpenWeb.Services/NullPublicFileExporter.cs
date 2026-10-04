// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services;

public sealed class NullPublicFileExporter : IPublicFileExporter
{
    public string? ExportToPublicDocuments(string sourceFilePath, string subFolder, string fileName) => null;
}
