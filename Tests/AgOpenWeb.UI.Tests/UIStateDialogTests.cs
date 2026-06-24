using AgOpenWeb.Models.State;

namespace AgOpenWeb.UI.Tests;

/// <summary>
/// Pure model tests for dialog visibility state — no rendering needed.
/// </summary>
[TestFixture]
public class UIStateDialogTests
{
    private UIState _ui = null!;

    [SetUp]
    public void SetUp() => _ui = new UIState();

    [Test]
    public void ShowDialog_SetsActiveDialog()
    {
        _ui.ShowDialog(DialogType.AppSettings);
        Assert.That(_ui.ActiveDialog, Is.EqualTo(DialogType.AppSettings));
    }

    [Test]
    public void CloseDialog_ResetsToNone()
    {
        _ui.ShowDialog(DialogType.AppSettings);
        _ui.CloseDialog();
        Assert.That(_ui.ActiveDialog, Is.EqualTo(DialogType.None));
    }

    [Test]
    public void IsAppSettingsDialogVisible_TrueWhenActive()
    {
        _ui.ShowDialog(DialogType.AppSettings);
        Assert.That(_ui.IsAppSettingsDialogVisible, Is.True);
    }

    [Test]
    public void IsAppSettingsDialogVisible_FalseWhenOtherDialogOpen()
    {
        _ui.ShowDialog(DialogType.Confirmation);
        Assert.That(_ui.IsAppSettingsDialogVisible, Is.False);
    }

    [Test]
    public void ShowDialog_RaisesPropertyChangedForVisibility()
    {
        var changed = new List<string>();
        _ui.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        _ui.ShowDialog(DialogType.AppSettings);

        Assert.That(changed, Contains.Item(nameof(UIState.IsAppSettingsDialogVisible)));
        Assert.That(changed, Contains.Item(nameof(UIState.IsDialogOpen)));
    }

    [Test]
    public void OnlyOneDialogVisibleAtATime()
    {
        _ui.ShowDialog(DialogType.AppSettings);

        Assert.That(_ui.IsAppSettingsDialogVisible, Is.True);
        Assert.That(_ui.IsConfirmationDialogVisible, Is.False);
        Assert.That(_ui.IsNtripProfilesDialogVisible, Is.False);
    }

    [Test]
    public void IsDialogOpen_FalseWhenNone()
    {
        Assert.That(_ui.IsDialogOpen, Is.False);
    }

    [Test]
    public void IsDialogOpen_TrueWhenAnyDialogShown()
    {
        _ui.ShowDialog(DialogType.AppSettings);
        Assert.That(_ui.IsDialogOpen, Is.True);
    }

    // --- Back-stack (chain navigation) ---

    [Test]
    public void PushDialog_StacksParentAndShowsChild()
    {
        _ui.PushDialog(DialogType.NtripProfiles);      // first level, nothing to stack
        _ui.PushDialog(DialogType.NtripProfileEditor); // parent (Profiles) pushed

        Assert.That(_ui.ActiveDialog, Is.EqualTo(DialogType.NtripProfileEditor));
    }

    [Test]
    public void GoBack_PopsToParentDialog_ReturnsTrue()
    {
        _ui.PushDialog(DialogType.NtripProfiles);
        _ui.PushDialog(DialogType.NtripProfileEditor);

        var surfacedParent = _ui.GoBack();

        Assert.That(surfacedParent, Is.True);
        Assert.That(_ui.ActiveDialog, Is.EqualTo(DialogType.NtripProfiles));
    }

    [Test]
    public void GoBack_AtBottomOfChain_ClosesAndReturnsFalse()
    {
        _ui.PushDialog(DialogType.NtripProfiles); // single level: stack empty

        var surfacedParent = _ui.GoBack();

        Assert.That(surfacedParent, Is.False);
        Assert.That(_ui.ActiveDialog, Is.EqualTo(DialogType.None));
    }

    [Test]
    public void GoBack_UnwindsFullChainOneLevelAtATime()
    {
        _ui.PushDialog(DialogType.NtripProfiles);
        _ui.PushDialog(DialogType.NtripProfileEditor);

        Assert.That(_ui.GoBack(), Is.True);   // -> Profiles
        Assert.That(_ui.ActiveDialog, Is.EqualTo(DialogType.NtripProfiles));
        Assert.That(_ui.GoBack(), Is.False);  // -> None (bottom)
        Assert.That(_ui.ActiveDialog, Is.EqualTo(DialogType.None));
    }

    [Test]
    public void CloseDialog_ClearsStack_SoBackDoesNotResurrectParents()
    {
        _ui.PushDialog(DialogType.NtripProfiles);
        _ui.PushDialog(DialogType.NtripProfileEditor);

        _ui.CloseDialog();                    // exit the whole chain
        Assert.That(_ui.ActiveDialog, Is.EqualTo(DialogType.None));

        var surfacedParent = _ui.GoBack();    // stack must be empty
        Assert.That(surfacedParent, Is.False);
        Assert.That(_ui.ActiveDialog, Is.EqualTo(DialogType.None));
    }
}
