using System;
using System.Collections.Generic;
using System.IO;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.IsoXml;
using AgOpenWeb.Services.IsoXml;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests;

/// <summary>#110: coupling types go into the ISOXML export as DDI 157 on a connector element.</summary>
[TestFixture]
public class IsoXmlDeviceExportTests
{
    [Test]
    public void Devices_CarryTheirConnectorType()
    {
        var dir = Path.Combine(Path.GetTempPath(), "isoxml-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var plane = new LocalPlane(new Wgs84(32.59, -87.17), new SharedFieldProperties());
            var bnd = new IsoXmlBoundary { FenceLine = new List<Vec3> { new(0, 0, 0), new(100, 0, 0), new(100, 100, 0), new(0, 100, 0) } };
            IsoXmlExporter.Export(dir, "Test", 10000, new List<IsoXmlBoundary> { bnd }, new List<List<Vec3>>(),
                new List<IsoXmlTrack>(), plane, IsoXmlExporter.IsoXmlVersion.V4, "test",
                new[]
                {
                    new IsoXmlDevice { Designator = "Tractor", ConnectorType = 1 },  // ISO 6489-3 drawbar
                    new IsoXmlDevice { Designator = "Sprayer", ConnectorType = -1 }, // not available
                });

            string xml = File.ReadAllText(Path.Combine(dir, "TASKDATA.XML"));
            Assert.That(xml, Does.Contain("<DVC").And.Contain("Tractor").And.Contain("Sprayer"));
            Assert.That(xml, Does.Match("<DPT [^>]*B=\"009D\"[^>]*C=\"1\""), "DDI 157 = 1 on the tractor");
            Assert.That(System.Text.RegularExpressions.Regex.Matches(xml, "<DPT ").Count, Is.EqualTo(1),
                "no connector for a coupling that's not available");
        }
        finally { Directory.Delete(dir, true); }
    }
}
