// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Interfaces;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// However the steer wizard ends, what its finished steps set is saved, as AgOpenGPS saves
/// when its wizard closes. Cancel kept Invert Motor and the zeros in the live settings, the
/// module ran them, and a restart lost them (Steer wizard scenarios, CANCEL_KEEPS_LIVE_NOT_SAVED).
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class SteerWizardCancelSavesTests
{
    private static IConfigurationService Config()
    {
        var c = Substitute.For<IConfigurationService>();
        c.Store.Returns(ConfigurationStore.Instance);
        return c;
    }

    [Test]
    public void Cancel_saves_what_the_steps_set()
    {
        var config = Config();
        var vm = new MainViewModelBuilder { ConfigurationService = config }.Build();
        vm.StartRemoteSteerWizard();
        config.ClearReceivedCalls();

        vm.SteerWizardViewModel!.CancelCommand.Execute(null);
        vm.EndRemoteSteerWizard();

        config.Received(1).SaveProfiles(Arg.Any<string>(), Arg.Any<string>());
        Assert.That(vm.IsSteerWizardOpen, Is.False);
    }

    [Test]
    public void No_wizard_open_saves_nothing()
    {
        var config = Config();
        var vm = new MainViewModelBuilder { ConfigurationService = config }.Build();
        config.ClearReceivedCalls();

        vm.EndRemoteSteerWizard();

        config.DidNotReceive().SaveProfiles(Arg.Any<string>(), Arg.Any<string>());
    }
}
