// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Coverage;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Section;
using NSubstitute;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// Checklist 4.12: sections of different widths, where each one sits on the tool and what each
/// one paints, held to AgOpenGPS. Its positions come from the widths (ConfigTool
/// CalculateSectionPositions: the left edge at minus half the total, then each width in turn,
/// plus the tool offset, Sections.Designer SectionSetPosition), its edge points from the tool
/// position square to the heading (Position.designer CalculateSectionLookAhead), and each
/// section's patch is the quad between one fix's edges and the next (CPatches), whose area is
/// the worked area. The real SectionControlService and CoverageMapService are driven here.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class UnequalSectionsCoverageMatchesAgOpenGpsTests
{
    private static readonly double[] WidthsCm = { 200, 500, 300 }; // 2, 5, 3 m: 10 m
    private const double ToolOffset = 0.5;

    private ConfigurationStore _config = null!;
    private CoverageMapService _coverage = null!;
    private SectionControlService _sections = null!;

    [SetUp]
    public void SetUp()
    {
        _config = new ConfigurationStore();
        ConfigurationStore.SetInstance(_config);
        _config.NumSections = WidthsCm.Length;
        for (int i = 0; i < WidthsCm.Length; i++) _config.Tool.SetSectionWidth(i, WidthsCm[i]);
        _config.Tool.Offset = ToolOffset;
        _config.Tool.IsSectionsNotZones = true;
        _coverage = new CoverageMapService(_config);
        _coverage.SetFieldBounds(-100, 100, -100, 100);
        _sections = new SectionControlService(Substitute.For<IToolPositionService>(), _coverage, _config);
    }

    [TearDown]
    public void TearDown() => ConfigurationStore.SetInstance(new ConfigurationStore());

    // ───────────── AgOpenGPS ─────────────

    /// <summary>ConfigTool.CalculateSectionPositions + SectionSetPosition: positionLeft/Right per section.</summary>
    private static (double positionLeft, double positionRight)[] AgOpenGpsPositions(double[] sectionWidthArr, double toolOffset)
    {
        int i = sectionWidthArr.Length;
        var sectionPositionArr = new double[17];
        //add up the set widths
        double setWidth = 0;
        for (int j = 0; j < i; j++)
        {
            setWidth += sectionWidthArr[j];
        }
        //leftmost position.
        setWidth *= -0.5;
        sectionPositionArr[0] = setWidth;
        for (int j = 1; j < 17; j++)
        {
            if (j <= i) sectionPositionArr[j] = sectionPositionArr[j - 1] + sectionWidthArr[j - 1];
            else sectionPositionArr[j] = 0;
        }
        var result = new (double, double)[i];
        for (int j = 0; j < i; j++)
            result[j] = (sectionPositionArr[j] + toolOffset, sectionPositionArr[j + 1] + toolOffset);
        return result;
    }

    /// <summary>Position.designer: sinSectionHeading = sin(-heading), cosSectionHeading = cos(-heading);
    /// CalculateSectionLookAhead's leftPoint = (cosHeading * positionLeft + easting, sinHeading * positionLeft + northing).</summary>
    private static (Vec2 left, Vec2 right) AgOpenGpsEdges((double positionLeft, double positionRight) section, double easting, double northing, double heading)
    {
        double sinHeading = Math.Sin(-heading), cosHeading = Math.Cos(-heading);
        return (new Vec2(cosHeading * section.positionLeft + easting, sinHeading * section.positionLeft + northing),
                new Vec2(cosHeading * section.positionRight + easting, sinHeading * section.positionRight + northing));
    }

    [Test]
    public void SectionsOfDifferentWidths_SitWhereAgOpenGpsPutsThem()
    {
        var theirs = AgOpenGpsPositions(WidthsCm.Select(w => w / 100.0).ToArray(), ToolOffset);
        Assume.That(theirs[0].positionLeft, Is.EqualTo(-5 + ToolOffset).Within(1e-12));
        Assume.That(theirs[2].positionRight, Is.EqualTo(5 + ToolOffset).Within(1e-12));
        foreach (double heading in new[] { 0, 0.7, Math.PI / 2, 2.5, -1.2 })
            for (int i = 0; i < WidthsCm.Length; i++)
            {
                var tool = new Vec3(30, -20, heading);
                var (ourLeft, ourRight) = _sections.GetSectionWorldPosition(i, tool, heading);
                var (theirLeft, theirRight) = AgOpenGpsEdges(theirs[i], tool.Easting, tool.Northing, heading);
                Assert.That(ourLeft.Easting, Is.EqualTo(theirLeft.Easting).Within(1e-9), $"section {i} left E at {heading}");
                Assert.That(ourLeft.Northing, Is.EqualTo(theirLeft.Northing).Within(1e-9), $"section {i} left N at {heading}");
                Assert.That(ourRight.Easting, Is.EqualTo(theirRight.Easting).Within(1e-9), $"section {i} right E at {heading}");
                Assert.That(ourRight.Northing, Is.EqualTo(theirRight.Northing).Within(1e-9), $"section {i} right N at {heading}");
            }
    }

    /// <summary>All sections on by hand, driven north from (0, 0) at 2 m/s, a fix every 0.1 s.</summary>
    private (double metres, List<AgOpenGpsFieldDataOracle.CPatches> theirs) Drive(double metres, double heading = 0)
    {
        _sections.SetAllAuto();
        for (int i = 0; i < WidthsCm.Length; i++) _sections.SetSectionState(i, SectionButtonState.On);
        var positions = AgOpenGpsPositions(WidthsCm.Select(w => w / 100.0).ToArray(), ToolOffset);
        var theirs = positions.Select(_ => new AgOpenGpsFieldDataOracle.CPatches()).ToList();
        const double speed = 2.0, dt = 0.1;
        int steps = (int)Math.Round(metres / (speed * dt));
        for (int k = 0; k <= steps; k++)
        {
            double d = k * speed * dt;
            var tool = new Vec3(Math.Sin(heading) * d, Math.Cos(heading) * d, heading);
            _sections.Update(tool, heading, heading, speed);
            for (int i = 0; i < positions.Length; i++)
            {
                var (l, r) = AgOpenGpsEdges(positions[i], tool.Easting, tool.Northing, heading);
                if (k == 0) theirs[i].TurnMappingOn(l, r); else theirs[i].AddMappingPoint(l, r);
            }
        }
        return (metres, theirs);
    }

    [Test]
    public void EachSectionPaintsItsOwnWidth_AndTheWorkedAreaIsTheirSum_AsAgOpenGps()
    {
        var (metres, theirs) = Drive(40);
        double theirWorked = theirs.Sum(p => p.workedAreaTotal);
        Assume.That(theirWorked, Is.EqualTo(10 * metres).Within(1e-6), "AgOpenGPS: 2 + 5 + 3 m over 40 m");

        TestContext.Out.WriteLine($"worked ours {_coverage.TotalWorkedArea:F2} theirs {theirWorked:F2}; zones " +
            string.Join(", ", Enumerable.Range(0, 3).Select(z => _coverage.GetZoneArea(z).ToString("F2"))) +
            $"; actual {_coverage.ActualAreaCovered:F2}");
        Assert.Multiple(() =>
        {
            for (int i = 0; i < WidthsCm.Length; i++)
                Assert.That(_coverage.GetZoneArea(i) * 10000, Is.EqualTo(theirs[i].workedAreaTotal).Within(0.3 * metres),
                    $"section {i}'s painted cells, {WidthsCm[i] / 100} m wide: painted with the 5 cm margin and a cell's edge each side");
            Assert.That(_coverage.TotalWorkedArea, Is.EqualTo(theirWorked).Within(1e-6), "the worked area is the sections' sum");
            Assert.That(_coverage.ActualAreaCovered, Is.EqualTo(10 * metres).Within(10 * metres * 0.02), "the ground painted once");
        });
    }

    [Test]
    public void TheSectionsPaintSideBySide_NoGapAndNoDoubleCount()
    {
        Drive(40);
        // Across the tool at mid-drive: every 10 cm cell from the left edge to the right is painted
        // once, nothing outside beyond a cell.
        double n = 20;
        int painted = 0, outside = 0;
        for (double e = -5 + ToolOffset + 0.05; e < 5 + ToolOffset; e += 0.1)
            if (_coverage.IsPointCovered(e, n)) painted++;
        for (double e = -5 + ToolOffset - 0.25; e > -8; e -= 0.1)
            if (_coverage.IsPointCovered(e, n)) outside++;
        for (double e = 5 + ToolOffset + 0.25; e < 8; e += 0.1)
            if (_coverage.IsPointCovered(e, n)) outside++;
        Assert.That(painted, Is.EqualTo(100), "100 cells of 10 cm across the 10 m tool");
        Assert.That(outside, Is.Zero, "nothing beyond the tool's edges");
    }
}
