using System.Linq;

using AgValoniaGPS.Models.Configuration;

namespace AgValoniaGPS.ViewModels.Tests;

[TestFixture]
public class SectionControlTests
{
    [Test]
    public void ToggleSectionMasterCommand_TogglesSectionMasterOn()
    {
        var vm = new MainViewModelBuilder().Build();

        bool initialState = vm.IsSectionMasterOn;

        vm.ToggleSectionMasterCommand!.Execute(null);

        Assert.That(vm.IsSectionMasterOn, Is.Not.EqualTo(initialState));
    }

    [Test]
    public void ToggleSectionCommand_WithValidIndex_DoesNotThrow()
    {
        var builder = new MainViewModelBuilder();
        var vm = builder.Build();

        // Execute with section index 0 (string param, matching real usage)
        Assert.DoesNotThrow(() => vm.ToggleSectionCommand!.Execute("0"));
    }

    [Test]
    public void SectionRows_ButtonsMatchSectionCount()
    {
        var vm = new MainViewModelBuilder().Build();

        // Whatever NumSections is, the rows together hold exactly that many
        // buttons, numbered 1..N in order.
        var buttons = vm.SectionRows.SelectMany(r => r.Buttons).ToList();
        Assert.That(buttons.Count, Is.EqualTo(vm.NumSections));
        for (int i = 0; i < buttons.Count; i++)
            Assert.That(buttons[i].Number, Is.EqualTo(i + 1));
    }

    [Test]
    public void SectionRows_SplitIntoEvenRows_TopRowLargerWhenOdd()
    {
        var store = ConfigurationStore.Instance;
        int original = store.NumSections;
        try
        {
            var vm = new MainViewModelBuilder().Build();

            // ≤16 sections: a single row.
            store.NumSections = 16;
            Assert.That(vm.NumSections, Is.EqualTo(16));
            Assert.That(vm.SectionRows.Count, Is.EqualTo(1));
            Assert.That(vm.SectionRows[0].Buttons.Count, Is.EqualTo(16));

            // 17 sections: two rows, top row holds the greater count (9 + 8).
            store.NumSections = 17;
            Assert.That(vm.SectionRows.Count, Is.EqualTo(2));
            Assert.That(vm.SectionRows[0].Buttons.Count, Is.EqualTo(9));
            Assert.That(vm.SectionRows[1].Buttons.Count, Is.EqualTo(8));

            // 64 sections: four even rows of 16, never more than 16 per row.
            store.NumSections = 64;
            Assert.That(vm.SectionRows.Count, Is.EqualTo(4));
            Assert.That(vm.SectionRows.All(r => r.Buttons.Count == 16), Is.True);
        }
        finally
        {
            store.NumSections = original;
        }
    }
}
