using AgOpenWeb.Services.Interfaces;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>#111: zone buttons (AgOpenGPS btnZoneX_Click) set every section in the zone
/// to the next state of the zone's last section.</summary>
[TestFixture, NonParallelizable] // ConfigurationStore singleton
public class ZoneToggleTests
{
    [TearDown]
    public void Restore()
    {
        var t = AgOpenWeb.Models.Configuration.ConfigurationStore.Instance.Tool;
        t.Zones = 2; t.ZoneRanges = new[] { 0, 2, 4, 6, 8, 10, 12, 14, 16 };
    }

    [Test]
    public void Zone2_CyclesItsSections_FromTheLastOnesState()
    {
        var b = new MainViewModelBuilder();
        var states = Enumerable.Range(0, 8).Select(_ => new SectionControlState { ButtonState = SectionButtonState.Off }).ToList();
        states[5].ButtonState = SectionButtonState.Auto;   // last section of zone 2 (sections 4..5)
        b.SectionControlService.SectionStates.Returns(states);
        b.SectionControlService.NumSections.Returns(8);
        var vm = b.Build();
        AgOpenWeb.Models.Configuration.ConfigurationStore.Instance.Tool.Zones = 3;
        AgOpenWeb.Models.Configuration.ConfigurationStore.Instance.Tool.ZoneRanges = new[] { 0, 4, 6, 8, 0, 0, 0, 0, 0 };

        vm.ToggleZone(2);

        b.SectionControlService.Received(1).SetSectionState(4, SectionButtonState.On);
        b.SectionControlService.Received(1).SetSectionState(5, SectionButtonState.On);
        b.SectionControlService.DidNotReceive().SetSectionState(3, Arg.Any<SectionButtonState>());
        b.SectionControlService.DidNotReceive().SetSectionState(6, Arg.Any<SectionButtonState>());
    }

    [Test]
    public void ZoneOutsideTheConfiguredCount_DoesNothing()
    {
        var b = new MainViewModelBuilder();
        b.SectionControlService.NumSections.Returns(8);
        var vm = b.Build();
        AgOpenWeb.Models.Configuration.ConfigurationStore.Instance.Tool.Zones = 2;
        vm.ToggleZone(3);
        b.SectionControlService.DidNotReceiveWithAnyArgs().SetSectionState(default, default);
    }
}
