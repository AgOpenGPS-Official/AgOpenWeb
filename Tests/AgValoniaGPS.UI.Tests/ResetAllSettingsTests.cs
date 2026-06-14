using AgValoniaGPS.Models.Configuration;
using AgValoniaGPS.Models.State;
using NSubstitute;

namespace AgValoniaGPS.UI.Tests;

/// <summary>
/// Tests for the Reset All Settings command.
/// </summary>
[TestFixture]
public class ResetAllSettingsTests
{
    [Test]
    public void ResetCommand_ShowsConfirmationDialog()
    {
        var vm = new MainViewModelBuilder().Build();

        vm.ResetAllSettingsCommand!.Execute(null);

        Assert.That(vm.State.UI.ActiveDialog, Is.EqualTo(DialogType.Confirmation));
    }

    [Test]
    public void ResetCommand_WhenConfirmed_ResetsSettingsService()
    {
        var builder = new MainViewModelBuilder();
        var vm = builder.Build();

        // Clear any calls from constructor/initialization
        builder.SettingsService.ClearReceivedCalls();

        // Open and confirm
        vm.ResetAllSettingsCommand!.Execute(null);
        vm.ConfirmConfirmationDialogCommand!.Execute(null);

        builder.SettingsService.Received(1).ResetToDefaults();
        builder.SettingsService.Received(1).Save();
    }

    [Test]
    public void ResetCommand_WhenConfirmed_ReloadsStoreInPlace()
    {
        var original = ConfigurationStore.Instance;
        var vm = new MainViewModelBuilder().Build();

        vm.ResetAllSettingsCommand!.Execute(null);
        vm.ConfirmConfirmationDialogCommand!.Execute(null);

        // §11.2: reset is IN PLACE — the store object identity is preserved
        // (every service/VM now holds an injected reference to it), and defaults
        // are re-applied via ResetToDefaults + Save + LoadAppSettings rather than
        // swapping the singleton out from under those injected references.
        Assert.That(ConfigurationStore.Instance, Is.SameAs(original));
    }

    [Test]
    public void ResetCommand_WhenCancelled_DoesNotReset()
    {
        var builder = new MainViewModelBuilder();
        var vm = builder.Build();

        // Clear any calls from constructor/initialization
        builder.SettingsService.ClearReceivedCalls();

        vm.ResetAllSettingsCommand!.Execute(null);
        vm.CancelConfirmationDialogCommand!.Execute(null);

        builder.SettingsService.DidNotReceive().ResetToDefaults();
        builder.SettingsService.DidNotReceive().Save();
    }

    [Test]
    public void ResetCommand_WhenCancelled_ClosesDialog()
    {
        var vm = new MainViewModelBuilder().Build();

        vm.ResetAllSettingsCommand!.Execute(null);
        vm.CancelConfirmationDialogCommand!.Execute(null);

        Assert.That(vm.State.UI.ActiveDialog, Is.EqualTo(DialogType.None));
    }
}
