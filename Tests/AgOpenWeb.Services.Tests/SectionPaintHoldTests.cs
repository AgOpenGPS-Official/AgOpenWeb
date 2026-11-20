// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Section;
using NSubstitute;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// While the machine module is not answering, nothing is painted (MODULE-LOST-SILENT, Machine
/// Module harness K4, 20 Nov 2026: 495 m² painted in a 30 s Wi-Fi drop with the valves shut).
/// The sections keep their state and their bits, so the valves work again the moment the
/// module hears them, and painting resumes with them.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class SectionPaintHoldTests
{
    private ICoverageMapService _coverage = null!;
    private SectionControlService _sections = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var config = ConfigurationStore.Instance;
        config.NumSections = 2;
        config.Tool.SetSectionWidth(0, 300);
        config.Tool.SetSectionWidth(1, 300);
        config.Tool.LookAheadOnSetting = 0;
        config.Tool.LookAheadOffSetting = 0;
        _coverage = Substitute.For<ICoverageMapService>();
        _sections = new SectionControlService(Substitute.For<IToolPositionService>(), _coverage, config);
        _sections.SetAllSections(SectionButtonState.On);
    }

    private int _n;
    private void Drive(int ticks)
    {
        for (int i = 0; i < ticks; i++)
            _sections.Update(new Vec3(0, _n++ * 0.3, 0), 0, 0, 3.0);
    }

    private int Painted()
    {
        int n = _coverage.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(ICoverageMapService.AddCoveragePoint));
        _coverage.ClearReceivedCalls();
        return n;
    }

    [Test]
    public void Held_NothingIsPainted_TheSectionsStayOn_AndPaintingResumesWhenReleased()
    {
        Drive(10);
        Assume.That(Painted(), Is.GreaterThan(0), "painting while the module answers");

        _sections.HoldPainting = true;
        Drive(30);

        _coverage.Received().StopMapping(0);   // the strips end
        _coverage.Received().StopMapping(1);
        Assert.That(Painted(), Is.Zero, "the module is not answering: its valves are shut");
        Assert.That(_sections.SectionStates.Take(2).All(s => s.IsOn), Is.True, "the sections keep their state");
        Assert.That(_sections.GetSectionBits(), Is.Not.Zero, "their bits still go to the module");

        _sections.HoldPainting = false;
        Drive(10);
        Assert.That(Painted(), Is.GreaterThan(0), "the module answers again: painting resumes");
    }
}
