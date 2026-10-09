// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Sections;
using AgOpenWeb.Models.State;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// The services that used to read the UI-bound mirror (<c>ApplicationState</c>) get their
/// inputs pushed from the ViewModel instead, and the mirror is written only by
/// <c>ApplyGpsCycleResult</c> (CONTRIBUTING, Threading Model).
/// </summary>
[TestFixture]
public class PipelineInputPushTests
{
    [Test]
    public void CycleResult_WritesTheRollMirror()
    {
        var vm = new MainViewModelBuilder().Build();

        vm.ApplyGpsCycleResult(new GpsCycleResult { RollDegrees = 2.5, FixQuality = 4 });

        Assert.That(vm.State.Vehicle.Roll, Is.EqualTo(2.5), "the projector and roll-zero read State.Vehicle.Roll");
    }

    [Test]
    public void CommittedLocalPlane_IsPushedToThePipeline()
    {
        var builder = new MainViewModelBuilder();
        var vm = builder.Build();
        var plane = new LocalPlane(new Wgs84(43.7128, -74.006), new SharedFieldProperties());

        vm.State.Field.LocalPlane = plane;
        builder.GpsPipelineService.Received().SetLocalPlane(plane);

        vm.State.Field.LocalPlane = null; // field closed
        builder.GpsPipelineService.Received().SetLocalPlane(null);
    }

    [Test]
    public void HeadlandToggle_ReachesThePipelineAndSectionControl()
    {
        var builder = new MainViewModelBuilder();
        var vm = builder.Build();
        builder.GpsPipelineService.ClearReceivedCalls();
        builder.SectionControlService.ClearReceivedCalls();

        vm.IsHeadlandOn = true;

        builder.GpsPipelineService.Received().SetHeadlandOn(true);
        builder.SectionControlService.Received().SetFieldContext(Arg.Is<SectionFieldContext>(c => c.IsHeadlandOn));
    }

    [Test]
    public void BoundaryAndHeadlandLine_ReachSectionControlAsOneRecord()
    {
        var builder = new MainViewModelBuilder();
        var vm = builder.Build();
        var poly = new BoundaryPolygon();
        poly.Points.Add(new BoundaryPoint(0, 0, 0));
        poly.Points.Add(new BoundaryPoint(100, 0, 0));
        poly.Points.Add(new BoundaryPoint(100, 100, 0));
        poly.UpdateBounds();
        var boundary = new Boundary { OuterBoundary = poly };
        var headland = new List<Vec3> { new(10, 10, 0), new(90, 10, 0), new(90, 90, 0) };
        builder.SectionControlService.ClearReceivedCalls();

        vm.State.Field.CurrentBoundary = boundary;
        vm.State.Field.HeadlandLine = headland;

        builder.SectionControlService.Received().SetFieldContext(Arg.Is<SectionFieldContext>(c =>
            ReferenceEquals(c.Boundary, boundary) && ReferenceEquals(c.HeadlandLine, headland)));
    }

    [Test]
    public void SimulatorToggle_ReachesThePipeline()
    {
        var builder = new MainViewModelBuilder();
        var vm = builder.Build();
        builder.GpsPipelineService.ClearReceivedCalls();

        vm.IsSimulatorEnabled = !vm.IsSimulatorEnabled;

        builder.GpsPipelineService.Received().SetSimulatorEnabled(vm.IsSimulatorEnabled);
    }
}
