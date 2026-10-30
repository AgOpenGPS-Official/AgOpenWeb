// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Coverage;
using AgOpenWeb.Services.Section;
using AgOpenWeb.Services.Tool;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// #446: the tool offset is applied once. The section spans carry it (as AgOpenGPS's section
/// positions do) and the tool position stays on the tractor's line, so the sections paint
/// exactly the offset beside the tractor. Both applied the offset before, and the coverage
/// landed at twice the offset: overlap on one side of each pass, a gap on the other.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class ToolOffsetCoverageTests
{
    private ConfigurationStore _config = null!;
    private ToolPositionService _toolPosition = null!;
    private SectionControlService _sections = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _config = ConfigurationStore.Instance;
        _config.Tool.HitchLength = 4;
        _config.Vehicle.HitchLength = 2;
        _config.Tool.TrailingHitchLength = 3;
        _config.Tool.Offset = 0.8; // tool 0.8 m right of the tractor
        _config.NumSections = 2;
        _config.Tool.SetSectionWidth(0, 300);
        _config.Tool.SetSectionWidth(1, 300);

        _toolPosition = new ToolPositionService(_config);
        _sections = new SectionControlService(_toolPosition, new CoverageMapService(_config), _config);
        _sections.RecalculateSectionPositions();
    }

    // Where the tool's two sections paint, across the tractor's line (m, + = right of travel).
    private (double left, double right) PaintedAcross(double headingDeg)
    {
        double h = headingDeg * Math.PI / 180.0;
        _toolPosition.Update(new Vec3(0, 0, h), h);
        double rightE = Math.Cos(h), rightN = -Math.Sin(h);
        var (l, _) = _sections.GetSectionWorldPosition(0, _toolPosition.ToolPosition, _toolPosition.ToolHeading);
        var (_, r) = _sections.GetSectionWorldPosition(1, _toolPosition.ToolPosition, _toolPosition.ToolHeading);
        return (l.Easting * rightE + l.Northing * rightN, r.Easting * rightE + r.Northing * rightN);
    }

    // Every tool type, both ways along the reporter's 50.6° line and north/south. The
    // reporter's tool is front-mounted.
    [Test]
    public void Sections_PaintTheOffsetBesideTheTractor_Once(
        [Values("front", "rear", "trailing", "tbt")] string toolType,
        [Values(0.0, 180.0, 50.6, 230.6)] double heading)
    {
        _config.Tool.SetToolType(toolType);
        var (left, right) = PaintedAcross(heading);

        Assert.Multiple(() =>
        {
            Assert.That(left, Is.EqualTo(0.8 - 3.0).Within(0.001), "left edge");
            Assert.That(right, Is.EqualTo(0.8 + 3.0).Within(0.001), "right edge");
        });
    }
}
