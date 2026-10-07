using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Ntrip;
using AgOpenWeb.Services.Interfaces;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>#285: closing a field keeps the corrections flowing; only a field-specific
/// profile gives way to the default one.</summary>
[TestFixture]
public class NtripFieldCloseTests
{
    private static NtripProfile P(string host) =>
        new() { Name = host, CasterHost = host, CasterPort = 2101, MountPoint = "M", Username = "u", Password = "p" };

    private static (MainViewModelBuilder b, MainViewModel vm) Connected(string host, NtripProfile? defaultProfile)
    {
        var b = new MainViewModelBuilder();
        var vm = b.Build();
        ConfigurationStore.Instance.Connections.NtripEnabled = true;
        b.NtripProfileService.DefaultProfile.Returns(defaultProfile);
        b.NtripService.IsActive.Returns(true);
        b.NtripService.IsConnected.Returns(true);
        vm.NtripCasterAddress = host; vm.NtripCasterPort = 2101; vm.NtripMountPoint = "M";
        return (b, vm);
    }

    [Test]
    public async Task ClosingAField_OnTheDefaultProfile_KeepsTheConnection()
    {
        var (b, vm) = Connected("default.caster", P("default.caster"));

        await vm.RestoreDefaultNtripProfileAsync();

        await b.NtripService.DidNotReceive().DisconnectAsync();
        await b.NtripService.DidNotReceiveWithAnyArgs().ConnectAsync(default!);
    }

    [Test]
    public async Task ClosingAField_OnItsOwnProfile_ReturnsToTheDefault()
    {
        var (b, vm) = Connected("field.caster", P("default.caster"));

        await vm.RestoreDefaultNtripProfileAsync();

        await b.NtripService.Received(1).DisconnectAsync();
        await b.NtripService.ReceivedWithAnyArgs(1).ConnectAsync(default!);
        Assert.That(vm.NtripCasterAddress, Is.EqualTo("default.caster"));
    }

    [Test]
    public async Task ClosingAField_WithNoDefault_KeepsTheFieldProfile()
    {
        var (b, vm) = Connected("field.caster", null);

        await vm.RestoreDefaultNtripProfileAsync();

        await b.NtripService.DidNotReceive().DisconnectAsync();
    }

    [Test]
    public async Task ClosingAField_WhenNtripIsStopped_StartsNothing()
    {
        var (b, vm) = Connected("field.caster", P("default.caster"));
        b.NtripService.IsActive.Returns(false);
        b.NtripService.IsConnected.Returns(false);

        await vm.RestoreDefaultNtripProfileAsync();

        await b.NtripService.DidNotReceiveWithAnyArgs().ConnectAsync(default!);
    }

    [Test]
    public async Task CloseFieldCommand_DoesNotDisconnectNtrip()
    {
        var (b, vm) = Connected("default.caster", P("default.caster"));

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.CloseFieldCommand!).ExecuteAsync(null);

        await b.NtripService.DidNotReceive().DisconnectAsync();
    }
}
