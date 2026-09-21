using System.Reflection;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// #106: the work / steer switch settings were read by nothing — nothing fed PGN 253's switch
/// bits to ModuleCommunicationService or called its CheckSwitches. Now each GPS cycle does,
/// like AgOpenGPS (CModuleComm.CheckWorkAndSteerSwitch). Edge-triggered, as there.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore singleton.
public class WorkSwitchTests
{
    private MainViewModelBuilder _builder = null!;
    private MainViewModel _vm = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _builder = new MainViewModelBuilder();
        _builder.ModuleCommunicationService = new ModuleCommunicationService(ConfigurationStore.Instance);
        _vm = _builder.Build();
    }

    // PGN 253 byte 11 bit 0 is the work switch, active low: closed → bit 0 = 0, which the
    // parser stores as WorkSwitchActive = true.
    private void WorkSwitch(bool closed)
    {
        _builder.AutoSteerService.LastSteerData.Returns(new SteerModuleData(
            ActualSteerAngle: 0, ImuHeading: 0, ImuRoll: 0, WorkSwitchActive: closed,
            SteerSwitchActive: false, RemoteButtonPressed: false, VwasFusionActive: false, PwmDisplay: 0));
        typeof(MainViewModel).GetMethod("UpdateModuleSwitches", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_vm, null);
    }

    [Test]
    public void WorkSwitch_TurnsAutoSectionsOnAndOff()
    {
        var tool = ConfigurationStore.Instance.Tool;
        tool.IsWorkSwitchEnabled = true;
        tool.IsWorkSwitchActiveLow = true;
        tool.IsWorkSwitchManualSections = false;

        WorkSwitch(closed: false);
        Assert.That(_vm.IsSectionMasterOn, Is.False);

        WorkSwitch(closed: true);
        Assert.That(_vm.IsSectionMasterOn, Is.True, "closing the work switch turns auto sections on");

        WorkSwitch(closed: false);
        Assert.That(_vm.IsSectionMasterOn, Is.False, "opening it turns them off");
    }

    [Test]
    public void WorkSwitch_ManualSectionsMode_UsesTheManualButton()
    {
        var tool = ConfigurationStore.Instance.Tool;
        tool.IsWorkSwitchEnabled = true;
        tool.IsWorkSwitchActiveLow = true;
        tool.IsWorkSwitchManualSections = true;

        WorkSwitch(closed: false);
        WorkSwitch(closed: true);

        Assert.That(_vm.IsManualSectionMode, Is.True);
        Assert.That(_vm.IsSectionMasterOn, Is.False);
    }

    [Test]
    public void WorkSwitch_Disabled_DoesNothing()
    {
        ConfigurationStore.Instance.Tool.IsWorkSwitchEnabled = false;

        WorkSwitch(closed: false);
        WorkSwitch(closed: true);

        Assert.That(_vm.IsSectionMasterOn, Is.False);
    }
}
