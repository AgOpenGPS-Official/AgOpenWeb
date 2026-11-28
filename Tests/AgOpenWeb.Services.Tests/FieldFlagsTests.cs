// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;

namespace AgOpenWeb.Services.Tests;

[TestFixture]
public class FieldFlagsTests
{
    [Test]
    public void Add_NumbersOn_AndTakesAnUnusedColorFirst()
    {
        var flags = new FieldFlags(new AgOpenWeb.Services.Threading.InlineUiDispatcher());
        int colors = Enum.GetValues<FlagColor>().Length;
        for (int i = 0; i < colors; i++) flags.Add(i, i);

        Assert.That(flags.Items.Select(f => f.FlagColor), Is.EquivalentTo(Enum.GetValues<FlagColor>()));
        Assert.That(flags.Add(0, 0).Name, Is.EqualTo($"Flag {colors + 1}"));
    }

    [Test]
    public void Replace_NumbersAfterTheHighest_AndAnEmptyListStartsAtOne()
    {
        var flags = new FieldFlags(new AgOpenWeb.Services.Threading.InlineUiDispatcher());
        flags.Replace([new Flag(0, 0, FlagColor.Red, 7, "Stone")]);
        Assert.That(flags.Add(1, 1).UniqueNumber, Is.EqualTo(8));

        flags.Replace([]);
        Assert.That(flags.Add(1, 1).UniqueNumber, Is.EqualTo(1));
    }

    [TestCase(-1)]
    [TestCase(1)]
    public void AnIndexOutsideTheList_ChangesNothing(int index)
    {
        var flags = new FieldFlags(new AgOpenWeb.Services.Threading.InlineUiDispatcher());
        var flag = flags.Add(1, 2, FlagColor.Red);

        Assert.That(flags.Rename(index, "x"), Is.False);
        Assert.That(flags.SetColor(index, FlagColor.Blue), Is.False);
        Assert.That(flags.RemoveAt(index), Is.Null);
        Assert.That(flags.Items, Is.EqualTo(new[] { flag }));
        Assert.That(flag.FlagColor, Is.EqualTo(FlagColor.Red));
    }

    [Test]
    public void Rename_TrimsTheName_AndRefusesABlankOne()
    {
        var flags = new FieldFlags(new AgOpenWeb.Services.Threading.InlineUiDispatcher());
        flags.Add(1, 2);

        Assert.That(flags.Rename(0, "  "), Is.False);
        Assert.That(flags.Rename(0, " Stone "), Is.True);
        Assert.That(flags.Items[0].Name, Is.EqualTo("Stone"));
    }

    [Test]
    public void A_write_off_the_host_loop_throws_in_a_debug_build()
    {
        var flags = new FieldFlags(new OtherThread());
        Assert.Throws<InvalidOperationException>(() => flags.Add(1, 2));
    }

    /// <summary>A host loop the caller is not on.</summary>
    private sealed class OtherThread : AgOpenWeb.Services.Interfaces.IUiDispatcher
    {
        public bool CheckAccess() => false;
        public void Post(Action action, AgOpenWeb.Services.Interfaces.UiDispatcherPriority priority = default) => action();
        public System.Threading.Tasks.Task InvokeAsync(Action action, AgOpenWeb.Services.Interfaces.UiDispatcherPriority priority = default) { action(); return System.Threading.Tasks.Task.CompletedTask; }
    }
}
