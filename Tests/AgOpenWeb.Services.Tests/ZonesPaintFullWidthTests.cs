// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Coverage;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Section;
using NSubstitute;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// Zones instead of sections (tool.isSectionsNotZones off) group the section buttons; they do not
/// change what is painted. AgOpenGPS paints each run of mapping sections as one strip from the
/// run's left edge to its right edge (OpenGL.Designer, the triStrip loop; CPatches.AddMappingPoint
/// takes section[currentStartSectionNum].leftPoint and section[currentEndSectionNum].rightPoint),
/// whatever the zones. Here the sections of one zone shared one coverage stream, each section's
/// point overwrote the last one's, so the quads ran across the tool between neighbouring sections
/// and a strip worked with every section on painted about a quarter of it (harness finding
/// SECTIONS-PAINT-SHORT: 196 of 800 m²).
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class ZonesPaintFullWidthTests
{
    private ConfigurationStore _config = null!;
    private CoverageMapService _coverage = null!;
    private SectionControlService _sections = null!;

    [SetUp]
    public void SetUp()
    {
        _config = new ConfigurationStore();
        ConfigurationStore.SetInstance(_config);
        _config.NumSections = 8;
        for (int i = 0; i < 8; i++) _config.Tool.SetSectionWidth(i, 100); // 8 × 1 m
        _config.Tool.IsSectionsNotZones = false;
        _config.Tool.Zones = 2;
        _config.Tool.SetZoneEndSection(1, 4);
        _config.Tool.SetZoneEndSection(2, 8);
        _coverage = new CoverageMapService(_config);
        _coverage.SetFieldBounds(-100, 100, -100, 100);
        _sections = new SectionControlService(Substitute.For<IToolPositionService>(), _coverage, _config);
    }

    [TearDown]
    public void TearDown() => ConfigurationStore.SetInstance(new ConfigurationStore());

    [Test]
    public void InZoneMode_EverySectionOn_PaintsTheWholeToolWidth()
    {
        _sections.SetAllAuto();
        for (int i = 0; i < 8; i++) _sections.SetSectionState(i, SectionButtonState.On);
        const double speed = 2.0, dt = 0.1, metres = 40;
        int steps = (int)System.Math.Round(metres / (speed * dt));
        for (int k = 0; k <= steps; k++)
        {
            var tool = new Vec3(0, k * speed * dt, 0);
            _sections.Update(tool, 0, 0, speed);
        }

        // Across the tool at mid-drive: every 10 cm cell from -4 m to +4 m is painted.
        int painted = 0;
        for (double e = -4 + 0.05; e < 4; e += 0.1)
            if (_coverage.IsPointCovered(e, 20)) painted++;

        Assert.Multiple(() =>
        {
            Assert.That(painted, Is.EqualTo(80), "80 cells of 10 cm across the 8 m tool");
            // The worked area is the sections' quads: the drive minus the mapping-on delay.
            Assert.That(_coverage.TotalWorkedArea, Is.EqualTo(8 * metres).Within(8 * 2.0),
                "the worked area is the full 8 m width");
            Assert.That(_coverage.ActualAreaCovered, Is.EqualTo(8 * metres).Within(8 * metres * 0.05),
                "the ground painted once, the full width");
        });
    }
}
