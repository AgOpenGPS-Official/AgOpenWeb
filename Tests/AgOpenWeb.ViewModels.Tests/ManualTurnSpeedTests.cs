using AgOpenWeb.Models.Configuration;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>#110: with "Manual turns" on, manual U-turns and lateral moves are refused
/// above the manual turns speed (AgOpenGPS vehicle.functionSpeedLimit).</summary>
[TestFixture, NonParallelizable] // ConfigurationStore singleton
public class ManualTurnSpeedTests
{
    [TearDown]
    public void Restore() => ConfigurationStore.Instance.AutoSteer.ManualTurnsEnabled = false;

    [TestCase(false, 20.0, false)] // off: no limit
    [TestCase(true, 8.0, false)]   // below 10 km/h
    [TestCase(true, 12.0, true)]   // above 10 km/h → refused
    public void ManualTurn_SpeedLimit(bool enabled, double kmh, bool refused)
    {
        var vm = new MainViewModelBuilder().Build();
        var a = ConfigurationStore.Instance.AutoSteer;
        a.ManualTurnsEnabled = enabled;
        a.ManualTurnsSpeed = 10;
        vm.Speed = kmh / 3.6;
        string? msg = null; vm.FailureReported += m => msg = m;

        Assert.That(vm.ManualTurnTooFast(), Is.EqualTo(refused));
        if (refused) Assert.That(msg, Does.StartWith("Too fast"));
    }
}
