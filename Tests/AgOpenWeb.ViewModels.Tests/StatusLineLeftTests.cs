// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using NSubstitute;
using NUnit.Framework;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// The status line's Stats page, "Done ... Left ...%" (<see cref="MainViewModel.RemainingPercent"/>, and the
/// same sum in overlays.js <c>rotatingLineText</c>): the field less its inner boundaries, as AgOpenGPS
/// <c>CFieldData.WorkedAreaRemainPercentage</c> and the field data card's Remaining, never under 0. It took
/// the headland's area while the worked area counts the headland passes too, and a tester's field read
/// "Left -14%" with a fifth of it still to do.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class StatusLineLeftTests
{
    private MainViewModelBuilder _b = null!;
    private MainViewModel _vm = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _b = new MainViewModelBuilder();
        _vm = _b.Build();
        // A 100 x 100 m field with a 20 x 20 m obstacle (9600 m²) and a headland 10 m wide (80 x 80 m inside it).
        _vm.State.Field.CurrentBoundary = new Boundary
        {
            OuterBoundary = Square(0, 100),
            InnerBoundaries = { Square(40, 60) },
        };
        _vm.State.Field.HeadlandLine = new List<Vec3>
        {
            new(10, 10, 0), new(90, 10, 0), new(90, 90, 0), new(10, 90, 0),
        };
    }

    [TearDown]
    public void TearDown() => ConfigurationStore.SetInstance(new ConfigurationStore());

    private static BoundaryPolygon Square(double min, double max)
    {
        var p = new BoundaryPolygon();
        p.Points.Add(new BoundaryPoint(min, min, 0));
        p.Points.Add(new BoundaryPoint(max, min, 0));
        p.Points.Add(new BoundaryPoint(max, max, 0));
        p.Points.Add(new BoundaryPoint(min, max, 0));
        p.UpdateBounds();
        return p;
    }

    [Test]
    public void Left_is_from_the_field_less_its_obstacles_not_the_headland()
    {
        // Headland passes and the cultivated area worked: more than the headland's 6400 m².
        _b.CoverageMapService.TotalWorkedArea.Returns(7000.0);

        Assert.That(_vm.RemainingPercent, Is.EqualTo((9600 - 7000) * 100.0 / 9600).Within(1e-9),
            "27% of the field left, not (6400 - 7000) / 6400 = -9%");
    }

    [Test]
    public void Left_is_never_under_zero()
    {
        // Overlap counted twice: worked more than the field (AgOpenGPS reads -4.2% here).
        _b.CoverageMapService.TotalWorkedArea.Returns(10000.0);

        Assert.That(_vm.RemainingPercent, Is.Zero);
    }
}
