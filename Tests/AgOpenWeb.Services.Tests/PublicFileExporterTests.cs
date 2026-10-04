// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System.IO;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Interfaces;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests;

[TestFixture]
public class PublicFileExporterTests
{
    [Test]
    public void NullPublicFileExporter_AlwaysReturnsNull()
    {
        IPublicFileExporter exporter = new NullPublicFileExporter();

        var tempFile = Path.GetTempFileName();
        try
        {
            var result = exporter.ExportToPublicDocuments(tempFile, "BugReports", "test.zip");
            Assert.That(result, Is.Null);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }
}
