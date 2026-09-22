using AgOpenWeb.Models.Base;
using AgOpenWeb.Services.Interfaces;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>#110: boundary player "Section control" and "Antenna / Tool" (AgOpenGPS
/// bnd.isRecBoundaryWhenSectionOn / isDrawAtPivot), and the Rec paths map toggle.</summary>
[TestFixture]
public class BoundaryRecordOptionsTests
{
    [Test]
    public void SectionControl_RecordsOnlyWhileSectionsWork()
    {
        var vm = new MainViewModelBuilder().Build();
        vm.IsBoundarySectionControlOn = false;
        Assert.That(vm.BoundaryRecordingSectionsAllow(), Is.True);

        vm.IsBoundarySectionControlOn = true;
        Assert.That(vm.BoundaryRecordingSectionsAllow(), Is.EqualTo(vm.IsManualSectionMode || vm.IsSectionMasterOn));
    }

    [Test]
    public void Tool_RecordsAtTheToolsOuterEdge_Pivot_AtTheAntennaPlusOffset()
    {
        var b = new MainViewModelBuilder();
        b.SectionControlService.NumSections.Returns(3);
        b.ToolPositionService.ToolPosition.Returns(new Vec3(10, 20, 0));
        b.SectionControlService.GetSectionWorldPosition(0, Arg.Any<Vec3>(), Arg.Any<double>())
            .Returns((new Vec2(7, 20), new Vec2(9, 20)));
        b.SectionControlService.GetSectionWorldPosition(2, Arg.Any<Vec3>(), Arg.Any<double>())
            .Returns((new Vec2(11, 20), new Vec2(13, 20)));
        var vm = b.Build();

        vm.IsDrawAtPivot = false;
        vm.IsDrawRightSide = true;
        Assert.That(vm.BoundaryRecordPoint(0, 0, 0), Is.EqualTo((13.0, 20.0)), "right side = last section's right point");
        vm.IsDrawRightSide = false;
        Assert.That(vm.BoundaryRecordPoint(0, 0, 0), Is.EqualTo((7.0, 20.0)), "left side = first section's left point");

        vm.IsDrawAtPivot = true;
        vm.BoundaryOffset = 0;
        Assert.That(vm.BoundaryRecordPoint(1, 2, 0), Is.EqualTo((1.0, 2.0)), "pivot, no offset");
    }

    [Test]
    public void RecPathsToggle_ReachesTheWebState()
    {
        var vm = new MainViewModelBuilder().Build();
        vm.ShowRecordedPaths = true;
        Assert.That(vm.State.FieldTools.ShowRecordedPaths, Is.True);
    }
}
