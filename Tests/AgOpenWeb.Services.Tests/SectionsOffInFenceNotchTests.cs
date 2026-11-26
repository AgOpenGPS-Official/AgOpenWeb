// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Sections;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Section;
using NSubstitute;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// Auto sections driven across a notch of the fence shut in the notch, with "Off outside
/// boundary" on or off and no headland to shut them first. With it off they stayed on: a point
/// more than 50 m inside the fence's bounding box was taken as inside the field (a tester's
/// field: a boom steered by hand across a 60 m notch sprayed 464 m² outside the fence).
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class SectionsOffInFenceNotchTests
{
    private SectionControlService _service = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var config = ConfigurationStore.Instance;
        config.NumSections = 3;
        for (int i = 0; i < 3; i++) config.Tool.SetSectionWidth(i, 200);
        config.Tool.Offset = 0;
        config.Tool.LookAheadOnSetting = 0;
        config.Tool.LookAheadOffSetting = 0;

        _service = new SectionControlService(Substitute.For<IToolPositionService>(), Substitute.For<ICoverageMapService>(), config);

        // 400 x 400 m with a notch cut in from the east side: E 150..400, N 150..250.
        var fence = new BoundaryPolygon();
        foreach (var (e, n) in new[] { (0.0, 0.0), (400.0, 0.0), (400.0, 150.0), (150.0, 150.0), (150.0, 250.0), (400.0, 250.0), (400.0, 400.0), (0.0, 400.0) })
            fence.Points.Add(new BoundaryPoint(e, n, 0));
        fence.UpdateBounds();
        _service.SetFieldContext(new SectionFieldContext(new Boundary { OuterBoundary = fence }, null, true, false));
        _service.SetAllAuto();
        _service.MasterState = SectionMasterState.Auto;
    }

    private bool AnyOnAfterDriving(double fromN, double toN)
    {
        // North along E 250 at 2 m/s, 0.2 m a tick.
        for (double n = fromN; n <= toN; n += 0.2)
            _service.Update(new Vec3(250, n, 0), 0, 0, 2.0);
        return _service.SectionStates.Take(3).Any(s => s.IsOn);
    }

    [TestCase(false, TestName = "Off outside boundary off")]
    [TestCase(true, TestName = "Off outside boundary on")]
    public void Sections_shut_in_the_notch_and_open_again_past_it(bool offWhenOut)
    {
        ConfigurationStore.Instance.Tool.IsSectionOffWhenOut = offWhenOut;

        Assert.That(AnyOnAfterDriving(60, 100), Is.True, "in the field south of the notch");
        Assert.That(AnyOnAfterDriving(100, 200), Is.False, "in the middle of the notch, 50 m outside the fence");
        Assert.That(AnyOnAfterDriving(200, 240), Is.False, "still in the notch");
        Assert.That(AnyOnAfterDriving(240, 300), Is.True, "in the field north of the notch");
    }
}
