using AgOpenWeb.Models;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>#109: Drive In with 2+ fields within 0.5 km offers a pick list (AgOpenGPS
/// FormDrivePicker) instead of opening a native dialog nothing shows.</summary>
[TestFixture]
public class DriveInPickTests
{
    private static (MainViewModel vm, MainViewModelBuilder b) Build(params NearbyField[] nearby)
    {
        var b = new MainViewModelBuilder();
        b.FieldService.FindFieldsNear(Arg.Any<string>(), Arg.Any<double>(), Arg.Any<double>(), Arg.Any<double>())
            .Returns(nearby);
        var vm = b.Build();
        vm.Latitude = 40.7;
        vm.Longitude = -74.0;
        return (vm, b);
    }

    [Test]
    public void TwoNearbyFields_RaisesThePickList()
    {
        var (vm, _) = Build(new NearbyField("North", "/f/North", 0.1, 5),
                            new NearbyField("South", "/f/South", 0.3, 7));
        IReadOnlyList<NearbyField>? offered = null;
        vm.DriveInPickRequested += l => offered = l;

        vm.DriveInCommand!.Execute(null);

        Assert.That(offered?.Select(f => f.Name), Is.EqualTo(new[] { "North", "South" }));
    }

    [Test]
    public void OneNearbyField_OpensItWithoutAPickList()
    {
        var (vm, _) = Build(new NearbyField("North", "/f/North", 0.1, 5));
        bool offered = false;
        vm.DriveInPickRequested += _ => offered = true;

        vm.DriveInCommand!.Execute(null);

        Assert.That(offered, Is.False);
    }

    [Test]
    public void DriveInOpen_OnlyAcceptsAFieldFromTheList()
    {
        var (vm, _) = Build(new NearbyField("North", "/f/North", 0.1, 5),
                            new NearbyField("South", "/f/South", 0.3, 7));
        string? failure = null;
        vm.FailureReported += m => failure = m;

        vm.DriveInOpen("North"); // no Drive In yet → nothing offered
        Assert.That(failure, Is.EqualTo("Press Drive In again"));

        failure = null;
        vm.DriveInCommand!.Execute(null);
        vm.DriveInOpen("Elsewhere");
        Assert.That(failure, Is.EqualTo("Press Drive In again"));

        failure = null;
        vm.DriveInOpen("south"); // case-insensitive, like field names elsewhere
        Assert.That(failure, Is.Null);
    }
}
