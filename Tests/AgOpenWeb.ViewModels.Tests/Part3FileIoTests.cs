using System.IO.Compression;
using AgOpenWeb.Models.Ntrip;
using AgOpenWeb.Services.Interfaces;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>#111 part 3: KMZ files are read, and editing the connected NTRIP profile
/// reconnects with the new settings.</summary>
[TestFixture]
public class Part3FileIoTests
{
    [Test]
    public void Kmz_IsUnzipped_ToItsKml()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aow-" + Guid.NewGuid().ToString("N") + ".kmz");
        try
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            using (var w = new System.IO.StreamWriter(zip.CreateEntry("doc.kml").Open()))
                w.Write("<kml><coordinates>-87.1,32.5,0 -87.2,32.6,0</coordinates></kml>");

            using var r = MainViewModel.OpenKmlText(path);
            Assert.That(r.ReadToEnd(), Does.Contain("<coordinates>-87.1,32.5,0"));
        }
        finally { System.IO.File.Delete(path); }
    }

    private static NtripProfile P(string host, string mount = "M", string user = "u") =>
        new() { Name = "Rtk", CasterHost = host, CasterPort = 2101, MountPoint = mount, Username = user, Password = "p" };

    [Test]
    public async Task EditingTheConnectedProfile_Reconnects()
    {
        var b = new MainViewModelBuilder();
        var vm = b.Build();
        b.NtripService.IsConnected.Returns(true);
        vm.NtripCasterAddress = "old.caster"; vm.NtripCasterPort = 2101; vm.NtripMountPoint = "M";

        await vm.ReconnectNtripIfConnectedAsync(P("old.caster"), P("new.caster"));

        await b.NtripService.Received(1).DisconnectAsync();
        await b.NtripService.ReceivedWithAnyArgs(1).ConnectAsync(default!);
        Assert.That(vm.NtripCasterAddress, Is.EqualTo("new.caster"));
    }

    [Test]
    public async Task EditingAnotherProfile_OrNoChange_LeavesTheConnectionAlone()
    {
        var b = new MainViewModelBuilder();
        var vm = b.Build();
        b.NtripService.IsConnected.Returns(true);
        vm.NtripCasterAddress = "old.caster"; vm.NtripCasterPort = 2101; vm.NtripMountPoint = "M";

        await vm.ReconnectNtripIfConnectedAsync(P("other.caster"), P("changed.caster"));
        await vm.ReconnectNtripIfConnectedAsync(P("old.caster"), P("old.caster"));

        await b.NtripService.DidNotReceive().DisconnectAsync();
    }
}
