// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Section;
using NSubstitute;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// Checklist 4.5: sections are off inside every inner boundary, drive-through or not.
/// AgOpenGPS's section test (OpenGL.Designer.cs, FenceAreaService.IsPointInsideFenceArea)
/// has no drive-through exception: the flag only lets a U-turn pass through (CTurn). An inner
/// boundary is an obstacle (a creek, a wet hole, a pylon), and nothing is applied on it. Ours
/// skipped drive-through ones, so a sprayer crossing a waterway marked drive-through sprayed it.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class InnerBoundarySectionTests
{
    private SectionControlService _service = null!;

    private static BoundaryPolygon Square(double x0, double y0, double x1, double y1, bool driveThrough = false)
    {
        var poly = new BoundaryPolygon { IsDriveThrough = driveThrough };
        poly.Points.Add(new BoundaryPoint(x0, y0, 0));
        poly.Points.Add(new BoundaryPoint(x1, y0, 0));
        poly.Points.Add(new BoundaryPoint(x1, y1, 0));
        poly.Points.Add(new BoundaryPoint(x0, y1, 0));
        poly.UpdateBounds();
        return poly;
    }

    private void Field(bool driveThrough, bool offWhenOut)
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var config = ConfigurationStore.Instance;
        config.NumSections = 3;
        for (int i = 0; i < 3; i++) config.Tool.SetSectionWidth(i, 200); // 2 m each, 6 m tool
        config.Tool.Offset = 0;
        config.Tool.IsSectionOffWhenOut = offWhenOut;

        var boundary = new Boundary { OuterBoundary = Square(0, 0, 200, 200) };
        boundary.InnerBoundaries.Add(Square(90, 90, 110, 110, driveThrough));   // a 20 m island mid-field

        _service = new SectionControlService(Substitute.For<IToolPositionService>(),
            Substitute.For<ICoverageMapService>(), config);
        _service.SetFieldContext(new AgOpenWeb.Models.Sections.SectionFieldContext(boundary, null, true, false));
        _service.SetAllAuto();
        _service.MasterState = SectionMasterState.Auto;
    }

    private void Run(double e, double n, int ticks = 15)
    {
        for (int i = 0; i < ticks; i++) _service.Update(new Vec3(e, n, 0), 0, 0, 2.0);
    }

    private bool AnyOn => _service.SectionStates.Take(3).Any(s => s.IsOn);

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void OverAnInnerBoundary_EverySectionIsOff(bool driveThrough, bool offWhenOut)
    {
        Field(driveThrough, offWhenOut);

        Run(100, 50);
        Assert.That(AnyOn, Is.True, "working mid-field before the island");

        Run(100, 100);
        Assert.That(AnyOn, Is.False, $"over the island (drive-through {driveThrough}): nothing applied");

        Run(100, 150);
        Assert.That(AnyOn, Is.True, "working again past it");
    }

    [Test]
    public void BesideADriveThroughInnerBoundary_OnlyTheSectionsOverItGoOff()
    {
        Field(driveThrough: true, offWhenOut: true);
        Run(111, 100); // the tool spans 108..114: the west section (108..110) over the island, the east one (112..114) 2 m clear
        Assert.Multiple(() =>
        {
            Assert.That(_service.SectionStates[0].IsOn, Is.False, "west section over the island");
            Assert.That(_service.SectionStates[2].IsOn, Is.True, "east section clear of it");
        });
    }
}
