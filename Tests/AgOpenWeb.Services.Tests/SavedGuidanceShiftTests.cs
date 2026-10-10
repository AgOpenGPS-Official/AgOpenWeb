using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Track;
using AgOpenWeb.Services.Track;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests;
[TestFixture]
public class SavedGuidanceShiftTests
{
    [TestCase(GuidanceShiftTarget.Base,true,.6,22)]
    [TestCase(GuidanceShiftTarget.Base,false,-.6,22)]
    [TestCase(GuidanceShiftTarget.Guiding,true,0,22.6)]
    [TestCase(GuidanceShiftTarget.Guiding,false,0,21.4)]
    public void BaseMovesPointsAndGuidingPreservesThem(GuidanceShiftTarget target,bool sameWay,double x,double savedOffset)
    {
        var source=new Models.Track.Track {Name="AB",Type=TrackType.ABLine,Points=[new Vec3(0,0,0),new Vec3(0,100,0)],WorkedPaths=[1]};
        var result=SavedGuidanceShift.Build(source,target,.6,sameWay,2,2,10);
        Assert.Multiple(()=>{
            Assert.That(result.Points[0].Easting,Is.EqualTo(x).Within(1e-9));
            Assert.That(result.Points[1].Easting,Is.EqualTo(x).Within(1e-9));
            Assert.That(result.NudgeDistance,Is.EqualTo(savedOffset).Within(1e-9));
            Assert.That(source.Points[0].Easting,Is.Zero);Assert.That(source.NudgeDistance,Is.Zero);
            Assert.That(result.WorkedPaths,Is.EquivalentTo(source.WorkedPaths));
            Assert.That(result.WorkedPaths,Is.Not.SameAs(source.WorkedPaths));
        });
    }
    [Test] public void InvalidInputIsRejected()
    {
        var source=new Models.Track.Track{Type=TrackType.Curve,Points=[new Vec3(0,0,0),new Vec3(1,1,0)]};
        Assert.Throws<InvalidOperationException>(()=>SavedGuidanceShift.Build(source,GuidanceShiftTarget.Base,.6,true,0,0,10));
        source.Type=TrackType.ABLine;
        Assert.Throws<InvalidOperationException>(()=>SavedGuidanceShift.Build(source,GuidanceShiftTarget.Base,double.NaN,true,0,0,10));
    }
}
