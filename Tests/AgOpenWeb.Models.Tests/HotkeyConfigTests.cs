using AgOpenWeb.Models.Configuration;

namespace AgOpenWeb.Models.Tests;

/// <summary>#111 (#98): named keys (arrows, F-keys) keep their name and are found
/// case-insensitively, so they fire like letters do.</summary>
[TestFixture]
public class HotkeyConfigTests
{
    [Test]
    public void NamedKeys_KeepTheirName()
    {
        var hk = new HotkeyConfig();
        hk.SetKeyForAction(HotkeyAction.AutoSteer, "ArrowUp");
        Assert.That(hk.GetKeyForAction(HotkeyAction.AutoSteer), Is.EqualTo("ArrowUp"));
    }

    [Test]
    public void Letters_AreStoredUpperCase()
    {
        var hk = new HotkeyConfig();
        hk.SetKeyForAction(HotkeyAction.AutoSteer, "q");
        Assert.That(hk.GetKeyForAction(HotkeyAction.AutoSteer), Is.EqualTo("Q"));
    }

    [TestCase("ArrowUp")]
    [TestCase("ARROWUP")] // how older builds saved it
    [TestCase("arrowup")]
    public void Lookup_IsCaseInsensitive(string pressed)
    {
        var hk = new HotkeyConfig();
        hk.SetKeyForAction(HotkeyAction.Flag, "ArrowUp");
        Assert.That(hk.GetActionForKey(pressed), Is.EqualTo(HotkeyAction.Flag));
    }
}
