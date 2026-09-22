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
    private void WorkSwitch(bool closed) => Module(workClosed: closed, moduleSteering: false);

    // PGN 253 byte 11 bit 1 is the module's steer-switch state: 1 = not steering. The parser
    // stores the raw bit as SteerSwitchActive.
    private void ModuleSteering(bool steering) => Module(workClosed: false, moduleSteering: steering);

    private void Module(bool workClosed, bool moduleSteering)
    {
        _builder.AutoSteerService.LastSteerData.Returns(new SteerModuleData(
            ActualSteerAngle: 0, ImuHeading: 0, ImuRoll: 0, WorkSwitchActive: workClosed,
            SteerSwitchActive: !moduleSteering, RemoteButtonPressed: false, VwasFusionActive: false, PwmDisplay: 0));
        typeof(MainViewModel).GetMethod("UpdateModuleSwitches", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_vm, null);
    }

    private void WithTrack() => _vm.SelectedTrack = new AgOpenWeb.Models.Track.Track
    {
        Name = "AB",
        Points = new List<AgOpenWeb.Models.Base.Vec3> { new(0, 0, 0), new(0, 100, 0) },
    };

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

    [TestCase(1)] // switch
    [TestCase(2)] // button
    public void SteerSwitchOrButton_EngagesAndDisengagesAutoSteer(int externalEnable)
    {
        ConfigurationStore.Instance.AutoSteer.ExternalEnable = externalEnable;
        WithTrack();

        ModuleSteering(false);                 // module idle
        Assert.That(_vm.IsAutoSteerEngaged, Is.False);

        ModuleSteering(true);                  // button pressed / switch on at the module
        Assert.That(_vm.IsAutoSteerEngaged, Is.True, "the module switch engages AutoSteer");

        ModuleSteering(false);                 // switch off, button again, or a sensor kickout
        Assert.That(_vm.IsAutoSteerEngaged, Is.False, "the module stopping disengages AutoSteer");
    }

    [Test]
    public void TabletEngage_IsNotUndoneByTheModuleFollowing()
    {
        ConfigurationStore.Instance.AutoSteer.ExternalEnable = 2;
        WithTrack();
        ModuleSteering(false);

        _vm.ToggleAutoSteerCommand!.Execute(null); // engaged from the screen
        ModuleSteering(true);                      // the module follows (steer bit clears)

        Assert.That(_vm.IsAutoSteerEngaged, Is.True);
    }

    [Test]
    public void NoSteerSwitch_TheDefault_IgnoresTheModule()
    {
        Assert.That(ConfigurationStore.Instance.AutoSteer.ExternalEnable, Is.EqualTo(0), "default is no switch/button");
        WithTrack();

        ModuleSteering(false);
        ModuleSteering(true);

        Assert.That(_vm.IsAutoSteerEngaged, Is.False);
    }
}
