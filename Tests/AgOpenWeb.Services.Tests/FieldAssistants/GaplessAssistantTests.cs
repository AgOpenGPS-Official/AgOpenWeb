using AgOpenWeb.Services.FieldAssistants;
using AgOpenWeb.Services.FieldAssistants.Core;
using AgOpenWeb.Services.FieldAssistants.Modules;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using NSubstitute;

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Models.State;
using AgOpenWeb.Models.Track;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Pipeline;

namespace AgOpenWeb.Services.Tests.FieldAssistants;
[TestFixture]
public sealed class GaplessAssistantTests
{
    private static GaplessInput Input(Func<Vec2,bool>? covered=null,bool sameWay=true) => new("field","AB",new(0,0),0,sameWay,10,0,
        [new BoundaryPolygon{Points=[new(-30,0,0),new(30,0,0),new(30,100,0),new(-30,100,0)]}],true,
        covered??(p=>p.Easting>=5.5&&p.Easting<=15.5));
    [TestCase(true,1)] [TestCase(false,-1)]
    public void ContinuousStripProposesDirectionWithTenCentimetresMinimumOverlap(bool sameWay,int direction)
    {
        var result=new GaplessAlignment(Input(sameWay:sameWay)).Analyze();
        Assert.Multiple(()=>{Assert.That(result.Success,Is.True);Assert.That(Math.Sign(result.NudgeCommandM),Is.EqualTo(direction));
            Assert.That(Math.Abs(result.NudgeCommandM),Is.InRange(.5,1));Assert.That(result.MinimumOverlapM,Is.EqualTo(.1).Within(1e-6));Assert.That(result.AnalyzedLengthM,Is.GreaterThan(90));});
    }
    [Test] public void CoverageOnBothSidesIsRejected() => Assert.That(new GaplessAlignment(Input(p=>Math.Abs(p.Easting)>=5.5&&Math.Abs(p.Easting)<=15.5)).Analyze().Success,Is.False);
    [Test] public void ShortOrInterruptedCoverageIsRejected()
    {
        Assert.That(new GaplessAlignment(Input(p=>p.Easting>=5.5&&p.Easting<=15.5&&p.Northing<30)).Analyze().Success,Is.False);
        Assert.That(new GaplessAlignment(Input(p=>p.Easting>=5.5&&p.Easting<=15.5&&(p.Northing<35||p.Northing>60))).Analyze().Success,Is.False);
    }
    [Test] public void MissingCoverageAndBoundaryDoNotProduceProposal()
    {
        Assert.That(new GaplessAlignment(Input() with{HasCoverage=false}).Analyze().Success,Is.False);
        Assert.That(new GaplessAlignment(Input() with{Boundaries=[]}).Analyze().Success,Is.False);
    }
    private sealed class Host : IGuidanceAssistantHost
    {
        public GaplessInput Current=Input();public double? Applied;
        public Task<GaplessInput> CaptureAsync(CancellationToken ct)=>Task.FromResult(Current);
        public Task ApplyNudgeAsync(GaplessInput input,double delta,CallContext context){Applied=delta;return Task.CompletedTask;}
        public Task SaveShiftAsync(GaplessInput input,double delta,AgOpenWeb.Services.Track.GuidanceShiftTarget target,CallContext context){Applied=delta;return Task.CompletedTask;}
    }
    private static CallContext Owner()=>new(Guid.NewGuid(),()=>true,CancellationToken.None);
    private static async Task<JObject> Analyze(GuidanceModule module,CallContext owner)=>JObject.FromObject((await module.InvokeAsync("analyze",new(),owner))!);
    [Test] public async Task AnalysisNeverAppliesAndOneConfirmedProposalCannotBeAppliedTwice()
    {
        var host=new Host();var module=new GuidanceModule(host);var owner=Owner();var proposal=await Analyze(module,owner);
        Assert.That(host.Applied,Is.Null);
        await module.InvokeAsync("apply",new JObject{{"id",proposal["id"]},{"target","guiding"}},owner);Assert.That(host.Applied,Is.GreaterThan(.5));
        Assert.ThrowsAsync<InvalidOperationException>(()=>module.InvokeAsync("apply",new JObject{{"id",proposal["id"]},{"target","guiding"}},owner));
    }
    [Test] public async Task FieldChangeAndCoverageChangeInvalidateProposal()
    {
        var host=new Host();var module=new GuidanceModule(host);var owner=Owner();var proposal=await Analyze(module,owner);
        host.Current=host.Current with{FieldKey="another field"};
        Assert.ThrowsAsync<InvalidOperationException>(()=>module.InvokeAsync("apply",new JObject{{"id",proposal["id"]},{"target","guiding"}},owner));
        host.Current=Input();proposal=await Analyze(module,owner);host.Current=Input(p=>p.Easting>=7&&p.Easting<=17);
        Assert.ThrowsAsync<InvalidOperationException>(()=>module.InvokeAsync("apply",new JObject{{"id",proposal["id"]},{"target","guiding"}},owner));Assert.That(host.Applied,Is.Null);
    }
    [Test] public async Task ObserverAndAnotherConnectionCannotApplyAnOperatorsProposal()
    {
        var host=new Host();var module=new GuidanceModule(host);var owner=Owner();var proposal=await Analyze(module,owner);
        Assert.ThrowsAsync<InvalidOperationException>(()=>module.InvokeAsync("apply",new JObject{{"id",proposal["id"]},{"target","guiding"}},new(owner.Connection,()=>false,CancellationToken.None)));
        Assert.ThrowsAsync<InvalidOperationException>(()=>module.InvokeAsync("apply",new JObject{{"id",proposal["id"]},{"target","guiding"}},Owner()));Assert.That(host.Applied,Is.Null);
    }
}
