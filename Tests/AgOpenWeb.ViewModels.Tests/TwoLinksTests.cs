// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.State;
using AgOpenWeb.Services.Interfaces;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>A module heard on two network links (a tablet on cable and Wi-Fi to the same router):
/// nothing from it is used, and while that lasts the host holds AutoSteer, Free Drive and the
/// sections off and raises the flag the client's warning follows. It clears by itself when one
/// link is disconnected; nothing comes back on by itself.</summary>
[TestFixture]
public class TwoLinksTests
{
    private static readonly SecondLink[] OnBoth = { new("192.168.5.126", "Ethernet 2", LinkKind.Cable, "Wi-Fi", LinkKind.WiFi, 12, 1, true) };
    private static readonly SecondLink[] OnOneAgain = { new("192.168.5.126", "Ethernet 2", LinkKind.Cable, "Wi-Fi", LinkKind.WiFi, 400, 1, false) };

    [Test]
    public void A_module_on_two_links_stops_AutoSteer_and_the_sections_and_raises_the_flag()
    {
        var b = new MainViewModelBuilder();
        var vm = b.Build();
        vm.IsAutoSteerAvailable = true;
        vm.ToggleAutoSteerCommand!.Execute(null);
        vm.IsSectionMasterOn = true;
        Assume.That(vm.IsAutoSteerEngaged, Is.True);

        vm.UpdateSecondLinks(OnBoth);

        Assert.Multiple(() =>
        {
            Assert.That(vm.State.Connections.IsOnTwoLinks, Is.True);
            Assert.That(vm.State.Connections.SecondLinks, Is.EqualTo(OnBoth));
            Assert.That(vm.IsAutoSteerEngaged, Is.False);
            Assert.That(vm.IsSectionMasterOn, Is.False);
        });
    }

    [Test]
    public void Free_Drive_and_a_section_on_by_hand_go_off_too_and_again_if_turned_on()
    {
        var b = new MainViewModelBuilder();
        var states = new[]
        {
            new SectionControlState { Index = 0, ButtonState = SectionButtonState.Off },
            new SectionControlState { Index = 1, ButtonState = SectionButtonState.On },
        };
        b.SectionControlService.NumSections.Returns(2);
        b.SectionControlService.SectionStates.Returns(states);
        b.AutoSteerService.IsInFreeDriveMode.Returns(true);
        var vm = b.Build();

        vm.UpdateSecondLinks(OnBoth);
        b.SectionControlService.Received(1).SetSectionState(1, SectionButtonState.Off);
        b.AutoSteerService.Received(1).DisableFreeDrive();

        // Still on two links, and the section is switched on by hand again (the substitute keeps
        // its state On): off again at the next status tick.
        vm.UpdateSecondLinks(OnBoth);
        b.SectionControlService.Received(2).SetSectionState(1, SectionButtonState.Off);
    }

    [Test]
    public void One_link_again_clears_the_flag_and_turns_nothing_back_on()
    {
        var b = new MainViewModelBuilder();
        var vm = b.Build();
        vm.IsSectionMasterOn = true;
        vm.UpdateSecondLinks(OnBoth);

        vm.UpdateSecondLinks(OnOneAgain);

        Assert.That(vm.State.Connections.IsOnTwoLinks, Is.False);
        Assert.That(vm.State.Connections.SecondLinks, Is.EqualTo(OnOneAgain), "kept for Network IO and the bug report");
        Assert.That(vm.IsSectionMasterOn, Is.False);
    }

    [Test]
    public void Nothing_on_two_links_changes_nothing()
    {
        var b = new MainViewModelBuilder();
        var vm = b.Build();
        vm.IsSectionMasterOn = true;

        vm.UpdateSecondLinks(System.Array.Empty<SecondLink>());

        Assert.That(vm.State.Connections.IsOnTwoLinks, Is.False);
        Assert.That(vm.IsSectionMasterOn, Is.True);
    }
}
