// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Sections;
using AgOpenWeb.Services.Coverage;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Section;
using AgOpenWeb.Services.Tool;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// A section is in the headland only with both its ends in it (AgOpenGPS WhereAreToolCorners /
/// WhereAreToolLookOnPoints). Where a pass meets the headland line at an angle the section stays
/// on until its last end crosses and comes on as its first end leaves, so no wedge inside the
/// line is left unsprayed. It used to switch on the section's centre: half of each wedge was
/// sprayed in the headland and half left unsprayed inside the line (field job scenarios
/// SPRAYED-IN-HEADLAND, 95 m² over and 128 m² short on Olival).
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class SectionHeadlandEndsTests
{
    // The headland line's top edge rises 1 m in 10 m: at x = ±3 (the 6 m section's ends) it is at
    // y = 99.7 and 100.3, at the section's centre 100.
    private static readonly List<Vec3> HeadlandLine = new()
    {
        new(-100, -100, 0), new(100, -100, 0), new(100, 110, 0), new(-100, 90, 0),
    };

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var config = ConfigurationStore.Instance;
        config.Vehicle.AntennaPivot = 0;
        config.Vehicle.AntennaOffset = 0;
        config.Vehicle.AntennaHeight = 0;
        config.Tool.Width = 6;
        config.NumSections = 1;
        config.Tool.SetSectionWidth(0, 600);
        config.Tool.IsHeadlandSectionControl = true;
        config.Tool.LookAheadOnSetting = 0;  // decide at the section itself
        config.Tool.LookAheadOffSetting = 0;
    }

    private static SectionControlService NewSections()
    {
        var config = ConfigurationStore.Instance;
        var coverage = new CoverageMapService(config);
        coverage.SetFieldBounds(-200, 200, -200, 200);
        var sections = new SectionControlService(new ToolPositionService(config), coverage, config);
        var poly = new BoundaryPolygon();
        foreach (var p in new[] { (-200.0, -200.0), (200.0, -200.0), (200.0, 200.0), (-200.0, 200.0) })
            poly.Points.Add(new BoundaryPoint(p.Item1, p.Item2, 0));
        poly.UpdateBounds();
        sections.SetFieldContext(new SectionFieldContext(new Models.Boundary { OuterBoundary = poly }, HeadlandLine, true, true));
        sections.MasterState = SectionMasterState.Auto;
        sections.SetAllAuto();
        return sections;
    }

    /// <summary>Drives the tool from <paramref name="from"/> to <paramref name="to"/> along x = 0
    /// at 1 m/s (1 cm a 100 Hz tick); the y where the section changed state.</summary>
    private static double DriveUntilChange(SectionControlService sections, double from, double to, bool startOn)
    {
        double heading = to > from ? 0 : Math.PI;
        double step = Math.Sign(to - from) * 0.01;
        bool? last = null;
        for (double y = from; Math.Sign(to - y) == Math.Sign(step); y += step)
        {
            sections.Update(new Vec3(0, y, heading), heading, heading, 1.0);
            bool on = sections.SectionStates[0].IsOn;
            if (last == startOn && on != startOn) return y;
            last = on;
        }
        return double.NaN;
    }

    [Test]
    public void Into_the_headland_at_an_angle_the_section_stays_on_until_both_ends_are_in()
    {
        var sections = NewSections();
        for (int i = 0; i < 200 && !sections.SectionStates[0].IsOn; i++)
            sections.Update(new Vec3(0, 80, 0), 0, 0, 1.0);
        Assume.That(sections.SectionStates[0].IsOn, Is.True, "the section comes on inside the line");

        double offAt = DriveUntilChange(sections, 80, 105, startOn: true);
        Assert.That(offAt, Is.GreaterThanOrEqualTo(100.29).And.LessThan(100.6),
            "the section turns off as its last end (y 100.3 on the line) enters the headland");
    }

    [Test]
    public void Out_of_the_headland_at_an_angle_the_section_comes_on_as_its_first_end_leaves()
    {
        var sections = NewSections();
        for (int i = 0; i < 50; i++)
            sections.Update(new Vec3(0, 105, Math.PI), Math.PI, Math.PI, 1.0);
        Assume.That(sections.SectionStates[0].IsOn, Is.False, "off in the headland");

        double onAt = DriveUntilChange(sections, 105, 90, startOn: false);
        Assert.That(onAt, Is.LessThanOrEqualTo(100.31).And.GreaterThan(100.0),
            "the section comes on as its first end (y 100.3 on the line) leaves the headland, not at its centre (y 100)");
    }
}
