using AgOpenWeb.Services.Interfaces;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>GPS data lost: after 2 s without position sentences the host switches autosteer
/// and the sections off and raises the flag the client's warning follows; data coming back
/// clears the flag and nothing re-engages by itself.</summary>
[TestFixture]
public class GpsLostTests
{
    private static readonly DateTime T0 = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public void ADroppedPacket_DoesNotRaiseTheFlag()
    {
        var vm = new MainViewModelBuilder().Build();

        vm.UpdateGpsLost(false, T0);
        vm.UpdateGpsLost(false, T0.AddMilliseconds(MainViewModel.GpsLostAfterMs - 100));
        vm.UpdateGpsLost(true, T0.AddMilliseconds(MainViewModel.GpsLostAfterMs + 100)); // the gap is reset by data

        Assert.That(vm.State.Connections.IsGpsLost, Is.False);
    }

    [Test]
    public void TwoSecondsWithoutData_StopsAutosteerAndSections()
    {
        var b = new MainViewModelBuilder();
        var vm = b.Build();
        vm.IsAutoSteerEngaged = true;
        vm.IsSectionMasterOn = true;

        vm.UpdateGpsLost(false, T0);
        vm.UpdateGpsLost(false, T0.AddMilliseconds(MainViewModel.GpsLostAfterMs));

        Assert.Multiple(() =>
        {
            Assert.That(vm.State.Connections.IsGpsLost, Is.True);
            Assert.That(vm.IsAutoSteerEngaged, Is.False);
            Assert.That(vm.IsSectionMasterOn, Is.False);
        });
        b.AutoSteerService.Received(1).Disengage();
        b.SectionControlService.Received(1).SetAllSections(SectionButtonState.Off);
    }

    [Test]
    public void DataBack_ClearsTheFlag_AndLeavesAutosteerOff()
    {
        var b = new MainViewModelBuilder();
        var vm = b.Build();
        vm.IsAutoSteerEngaged = true;
        vm.UpdateGpsLost(false, T0);
        vm.UpdateGpsLost(false, T0.AddSeconds(5));
        Assume.That(vm.State.Connections.IsGpsLost, Is.True);

        vm.UpdateGpsLost(true, T0.AddSeconds(6));

        Assert.That(vm.State.Connections.IsGpsLost, Is.False);
        Assert.That(vm.IsAutoSteerEngaged, Is.False);
        b.AutoSteerService.Received(1).Disengage(); // once, on the loss; nothing on the return
    }

    [Test]
    public void StillLost_DoesNotRepeatTheStop()
    {
        var b = new MainViewModelBuilder();
        var vm = b.Build();
        vm.IsSectionMasterOn = true;

        for (int s = 0; s < 10; s++) vm.UpdateGpsLost(false, T0.AddSeconds(s));

        b.SectionControlService.Received(1).SetAllSections(SectionButtonState.Off);
    }
}
