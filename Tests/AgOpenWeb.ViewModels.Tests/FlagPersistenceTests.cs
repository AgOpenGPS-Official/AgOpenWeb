using AgOpenWeb.Models;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>#107: flags are saved with the field and don't carry over to the next one.</summary>
[TestFixture]
public class FlagPersistenceTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aow-vmflags-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown() { try { System.IO.Directory.Delete(_dir, true); } catch { } }

    private MainViewModel OpenFieldVm()
    {
        var builder = new MainViewModelBuilder();
        builder.FieldService.ActiveField.Returns(new Field { Name = "F", DirectoryPath = _dir });
        var vm = builder.Build();
        vm.IsFieldOpen = true;
        return vm;
    }

    [Test]
    public void PlacingAndEditingFlags_SavesFlagsTxt()
    {
        var vm = OpenFieldVm();

        vm.PlaceFlagAtWorldPosition(10, 20, FlagColor.Blue);
        vm.PlaceFlagAtWorldPosition(-5, 7, FlagColor.Red);
        vm.RenameFlagAt(0, "Stone");
        vm.DeleteFlagAt(1);

        var saved = AgOpenWeb.Services.FlagFilesService.Load(_dir);
        Assert.That(saved, Has.Count.EqualTo(1));
        Assert.That(saved[0].Name, Is.EqualTo("Stone"));
        Assert.That(saved[0].FlagColor, Is.EqualTo(FlagColor.Blue));
    }

    [Test]
    public async Task ClosingTheField_ClearsFlags_ButKeepsTheFile()
    {
        var vm = OpenFieldVm();
        vm.PlaceFlagAtWorldPosition(10, 20, FlagColor.Blue);

        await vm.CloseFieldAsync();

        Assert.That(vm.Flags, Is.Empty, "flags must not carry over into the next field");
        Assert.That(AgOpenWeb.Services.FlagFilesService.Load(_dir), Has.Count.EqualTo(1),
            "closing must not overwrite the saved flags with an empty list");
    }
}
