using System.Linq;
using AgOpenWeb.Models.State;

namespace AgOpenWeb.UI.Tests;

/// <summary>
/// App Directories are folded into the App Settings dialog (the standalone
/// directories dialog was removed). Opening App Settings must refresh and expose
/// the directory list.
/// </summary>
[TestFixture]
public class AppSettingsDirectoriesTests
{
    [AvaloniaTest]
    public void ShowAppSettings_OpensDialog_AndPopulatesDirectories()
    {
        var vm = new MainViewModelBuilder().Build();

        vm.ShowAppSettingsDialogCommand!.Execute(null);

        Assert.That(vm.State.UI.ActiveDialog, Is.EqualTo(DialogType.AppSettings));
        Assert.That(vm.State.UI.IsAppSettingsDialogVisible, Is.True);

        Assert.That(vm.AppDirectories, Has.Count.EqualTo(4),
            "App Settings should expose Settings, Fields, Vehicles, NTRIP paths");
        Assert.That(vm.AppDirectories.Select(d => d.Name),
            Is.EquivalentTo(new[] { "Settings", "Fields", "Vehicle Profiles", "NTRIP Profiles" }));
    }
}
