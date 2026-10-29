// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Ntrip;
using AgOpenWeb.Services.Interfaces;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// The two NTRIP modes. One profile with a fallback (the default): a field load changes nothing and
/// the connection carries the profile's fallback. Profile per field: the main service only.
/// </summary>
[TestFixture, NonParallelizable]
public class NtripModeTests
{
    private static NtripProfile Profile(string host, NtripFallback? fallback = null) => new()
    {
        Name = host, CasterHost = host, CasterPort = 2101, MountPoint = "M", Username = "u", Password = "p",
        IsDefault = true, Fallback = fallback,
    };

    private static NtripFallback Fallback() => new()
    {
        CasterHost = "fallback.caster", CasterPort = 2102, MountPoint = "F", Username = "fu", Password = "fp",
        AutoSwitch = true, SwitchAfterFailures = 3,
    };

    private static (MainViewModelBuilder b, MainViewModel vm) Running(bool perField, NtripProfile? defaultProfile)
    {
        var b = new MainViewModelBuilder();
        var vm = b.Build();
        ConfigurationStore.Instance.Connections.NtripEnabled = true;
        ConfigurationStore.Instance.Connections.NtripProfilePerField = perField;
        b.NtripProfileService.DefaultProfile.Returns(defaultProfile);
        b.NtripService.IsActive.Returns(true);
        b.NtripService.IsConnected.Returns(true);
        vm.NtripCasterAddress = "field.caster"; vm.NtripCasterPort = 2101; vm.NtripMountPoint = "M";
        return (b, vm);
    }

    private static NtripConfiguration LastConnect(MainViewModelBuilder b) =>
        (NtripConfiguration)b.NtripService.ReceivedCalls()
            .Last(c => c.GetMethodInfo().Name == nameof(INtripClientService.ConnectAsync)).GetArguments()[0]!;

    private static bool WaitFor(Func<bool> condition, int ms = 2000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (condition()) return true; Thread.Sleep(10); }
        return condition();
    }

    [Test]
    public async Task WithOneProfile_ClosingAField_ChangesNothing()
    {
        var (b, vm) = Running(perField: false, Profile("default.caster"));

        await vm.RestoreDefaultNtripProfileAsync();

        await b.NtripService.DidNotReceive().DisconnectAsync();
        await b.NtripService.DidNotReceiveWithAnyArgs().ConnectAsync(default!);
    }

    [Test]
    public void SwitchingToOneProfile_ConnectsTheDefaultProfile_WithItsFallback()
    {
        var (b, _) = Running(perField: true, Profile("default.caster", Fallback()));

        ConfigurationStore.Instance.Connections.NtripProfilePerField = false;

        Assert.That(WaitFor(() => b.NtripService.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(INtripClientService.ConnectAsync))), Is.True);
        NtripConfiguration config = LastConnect(b);
        Assert.That(config.CasterAddress, Is.EqualTo("default.caster"));
        Assert.That(config.Fallback, Is.Not.Null);
        Assert.That(config.Fallback!.CasterAddress, Is.EqualTo("fallback.caster"));
        Assert.That(config.Fallback.CasterPort, Is.EqualTo(2102));
        Assert.That(config.Fallback.MountPoint, Is.EqualTo("F"));
        Assert.That(config.Fallback.Username, Is.EqualTo("fu"));
        Assert.That(config.Fallback.AutoSwitch, Is.True);
        Assert.That(config.Fallback.SwitchAfterFailures, Is.EqualTo(3));
    }

    [Test]
    public void InTheProfilePerFieldMode_TheFallbackIsNotUsed()
    {
        var (b, _) = Running(perField: false, Profile("default.caster", Fallback()));

        ConfigurationStore.Instance.Connections.NtripProfilePerField = true;   // no field open: the default profile, main service only

        Assert.That(WaitFor(() => b.NtripService.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(INtripClientService.ConnectAsync))), Is.True);
        Assert.That(LastConnect(b).Fallback, Is.Null);
    }

    [Test]
    public void AnIncompleteFallback_IsLeftOut()
    {
        var (b, _) = Running(perField: true, Profile("default.caster", new NtripFallback { CasterHost = "fallback.caster" }));

        ConfigurationStore.Instance.Connections.NtripProfilePerField = false;

        Assert.That(WaitFor(() => b.NtripService.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(INtripClientService.ConnectAsync))), Is.True);
        Assert.That(LastConnect(b).Fallback, Is.Null, "no mount point: nothing to connect to");
    }

    private static int Connects(MainViewModelBuilder b) =>
        b.NtripService.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(INtripClientService.ConnectAsync));

    [Test]
    public void ChangingTheMode_ReconnectsEvenOnTheSameCaster_BecauseTheFallbackIsPartOfTheSession()
    {
        var (b, vm) = Running(perField: true, Profile("default.caster", Fallback()));
        vm.NtripCasterAddress = "default.caster";   // already connected to the profile's main service, without its fallback

        ConfigurationStore.Instance.Connections.NtripProfilePerField = false;
        Assert.That(WaitFor(() => Connects(b) == 1), Is.True);
        Assert.That(LastConnect(b).Fallback, Is.Not.Null);

        ConfigurationStore.Instance.Connections.NtripProfilePerField = true;   // and back: the main service alone
        Assert.That(WaitFor(() => Connects(b) == 2), Is.True);
        Assert.That(LastConnect(b).Fallback, Is.Null);
    }

    [Test]
    public void ChangingTheModeToWhatIsAlreadyRunning_ReconnectsNothing()
    {
        var (b, vm) = Running(perField: true, Profile("default.caster"));   // no fallback in the profile
        vm.NtripCasterAddress = "default.caster";

        ConfigurationStore.Instance.Connections.NtripProfilePerField = false;
        Thread.Sleep(300);

        Assert.That(Connects(b), Is.EqualTo(0));
    }

    [Test]
    public async Task ACasterTypedInByHand_DoesNotInheritTheFallbackOfTheLastProfile()
    {
        var (b, vm) = Running(perField: true, Profile("default.caster", Fallback()));
        ConfigurationStore.Instance.Connections.NtripProfilePerField = false;
        Assert.That(WaitFor(() => Connects(b) == 1), Is.True);
        Assert.That(LastConnect(b).Fallback, Is.Not.Null);

        await vm.ConnectToNtripAsync();   // the same caster again: still the profile's fallback
        Assert.That(LastConnect(b).Fallback, Is.Not.Null);

        vm.NtripCasterAddress = "typed.caster";
        await vm.ConnectToNtripAsync();
        Assert.That(LastConnect(b).Fallback, Is.Null, "that fallback belongs to the profile's main service");
    }

    [Test]
    public async Task EditingOnlyTheFallback_ReconnectsTheConnectedProfile()
    {
        var (b, vm) = Running(perField: false, Profile("field.caster", Fallback()));
        NtripProfile before = Profile("field.caster", Fallback());
        NtripProfile after = Profile("field.caster", Fallback());
        after.Fallback!.MountPoint = "OTHER";

        await vm.ReconnectNtripIfConnectedAsync(before, after);

        await b.NtripService.Received(1).DisconnectAsync();
        Assert.That(LastConnect(b).Fallback!.MountPoint, Is.EqualTo("OTHER"));
    }

    [Test]
    public async Task EditingNothingThatMatters_DoesNotReconnect()
    {
        var (b, vm) = Running(perField: false, Profile("field.caster", Fallback()));

        await vm.ReconnectNtripIfConnectedAsync(Profile("field.caster", Fallback()), Profile("field.caster", Fallback()));

        await b.NtripService.DidNotReceive().DisconnectAsync();
    }

    [TestCase(NtripService.Fallback)]
    [TestCase(NtripService.Main)]
    public async Task TheOperatorCanSwitchAtAnyTime(NtripService service)
    {
        var (b, vm) = Running(perField: false, null);

        await vm.SwitchNtripServiceAsync(service);

        await b.NtripService.Received(1).SwitchToAsync(service);
    }

    [Test]
    public void ANotice_ReachesTheState_AndTheServiceInUseFollows()
    {
        var (b, vm) = Running(perField: false, null);
        b.NtripService.Using.Returns(NtripService.Fallback);
        b.NtripService.MainIsBack.Returns(true);

        b.NtripService.Notice += Raise.EventWith(new NtripNoticeEventArgs { Kind = NtripNoticeKind.MainBack, Message = "Main is back" });

        Assert.That(WaitFor(() => vm.State.Connections.NtripNotice == "Main is back"), Is.True);
        Assert.That(vm.State.Connections.NtripUsingFallback, Is.True);
        Assert.That(vm.State.Connections.NtripMainIsBack, Is.True);
    }
}
