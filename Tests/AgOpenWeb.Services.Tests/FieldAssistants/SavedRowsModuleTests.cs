using AgOpenWeb.Services.FieldAssistants.Core;
using AgOpenWeb.Services.FieldAssistants.Modules;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Services.SavedRows;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.FieldAssistants;
[TestFixture]
public sealed class SavedRowsModuleTests
{
    private sealed class Host(string directory):ISavedRowsHost
    {
        public SavedRowsContext Current=new(directory,52.1,19.2,false,-1);
        public IndividualRowsRequest? Request;
        public Task<SavedRowsContext> CaptureAsync(CancellationToken ct)=>Task.FromResult(Current);
        public Task ConfigureAsync(string directory,IndividualRowsRequest request,bool enabled,CallContext context)
        {Request=request;Current=Current with{Enabled=enabled,Selected=request.Selected};return Task.CompletedTask;}
    }
    private string root=null!;private Host host=null!;private SavedRowsModule module=null!;
    private MachineSnapshot pose=null!;private CallContext owner=null!;
    [SetUp] public void Setup()
    {
        root=Path.Combine(Path.GetTempPath(),"SavedRows-web-"+Guid.NewGuid().ToString("N"));host=new(root);
        pose=new(root,"Test",52.1,19.2,0,0,0,1,3,false,true,true,DateTimeOffset.UtcNow);
        module=new(host,()=>pose);owner=new(Guid.NewGuid(),()=>true,CancellationToken.None);
    }
    [TearDown] public void Cleanup(){if(Directory.Exists(root))Directory.Delete(root,true);}
    private async Task<JObject> Call(string command,JObject? args=null)=>JObject.FromObject((await module.InvokeAsync(command,args??new(),owner))!);
    private async Task Record(double offset)
    {
        var time=DateTimeOffset.UtcNow-TimeSpan.FromSeconds(.8);
        pose=pose with{Easting=offset,Northing=0,ReceivedAt=time};await Call("begin");
        for(int i=1;i<=8;i++){pose=pose with{Easting=offset+.01*i*i,Northing=.4*i,ReceivedAt=time+TimeSpan.FromSeconds(.1*i)};await module.TickAsync(CancellationToken.None);}
        await Call("finish");
    }
    [Test] public async Task TwoActualPathsSurviveRestartAndKeepSourceMetadata()
    {
        await Record(0);pose=pose with{Simulation=false};await Record(.9);
        module=new(host,()=>pose);var state=await Call("state");
        Assert.That(state["rows"]!.Count(),Is.EqualTo(2));
        var loaded=SavedRowTracks.Load(root,52.1,19.2);
        Assert.That(loaded.Rows[0].Simulator,Is.True);Assert.That(loaded.Rows[1].Simulator,Is.False);
        Assert.That(loaded.Rows[1].Points[^1].Easting,Is.EqualTo(1.54).Within(1e-6));
        await Call("select",new(){{"id",loaded.Rows[1].Id}});await Call("mode",new(){{"enabled",true}});
        Assert.That(host.Request!.Selected,Is.EqualTo(1));
        Assert.That(host.Request.Rows.All(t=>t.IsIndividualRow&&t.NoPassOffset),Is.True);
    }
    [Test] public async Task FieldSwitchCancelsRecordingAndNeverCarriesRowsIntoAnotherField()
    {
        await Record(0);var bytes=File.ReadAllBytes(Path.Combine(root,"individual-rows.json"));await Call("begin");
        host.Current=host.Current with{Directory=Path.Combine(root,"Other"),Enabled=false};
        var state=await Call("state");
        Assert.That(state["recording"]!.Value<bool>(),Is.False);Assert.That(state["rows"],Is.Empty);
        Assert.That(File.ReadAllBytes(Path.Combine(root,"individual-rows.json")),Is.EqualTo(bytes));
    }
    [Test] public async Task GpsLossAndOperatorDisconnectCancelRecordingWithoutSaving()
    {
        await Call("begin");pose=pose with{PositionValid=false};await module.TickAsync(CancellationToken.None);
        Assert.That((await Call("state"))["recording"]!.Value<bool>(),Is.False);
        pose=pose with{PositionValid=true};await Call("begin");module.Drop(owner.Connection);await module.TickAsync(CancellationToken.None);
        Assert.That((await Call("state"))["recording"]!.Value<bool>(),Is.False);
        Assert.That(File.Exists(Path.Combine(root,"individual-rows.json")),Is.False);
    }
    [Test] public void ObserverCannotStartRecordingOrChangeRowMode()
    {
        var observer=owner with{HasAuthority=()=>false};
        Assert.ThrowsAsync<InvalidOperationException>(()=>module.InvokeAsync("begin",new(),observer));
        Assert.ThrowsAsync<InvalidOperationException>(()=>module.InvokeAsync("mode",new(){{"enabled",true}},observer));
    }
    [Test] public async Task SavedCaptureOptionsAreValidatedAndPersisted()
    {
        await Record(0);await Call("options",new(){{"autoSelect",false},{"captureCm",35}});
        var data=SavedRowTracks.Load(root,52.1,19.2);
        Assert.That(data.AutoSelect,Is.False);Assert.That(data.CaptureDistanceMetres,Is.EqualTo(.35));
        Assert.ThrowsAsync<ArgumentException>(()=>Call("options",new(){{"autoSelect",true},{"captureCm",500}}));
    }
}
