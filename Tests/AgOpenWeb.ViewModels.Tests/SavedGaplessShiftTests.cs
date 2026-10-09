using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Track;
using AgOpenWeb.Services.GeoJson;
using AgOpenWeb.Services.Track;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;
[TestFixture,NonParallelizable]
public sealed class SavedGaplessShiftTests
{
    [TestCase(GuidanceShiftTarget.Base)] [TestCase(GuidanceShiftTarget.Guiding)]
    public void NativeSaveRetainsChosenShiftAfterReopening(GuidanceShiftTarget target)
    {
        var directory=Path.Combine(Path.GetTempPath(),"AgOpenWebGaplessTests-"+Guid.NewGuid().ToString("N"));
        ConfigurationStore.SetInstance(new());
        var builder=new MainViewModelBuilder();
        var field=new Field{Name="Test",DirectoryPath=directory,Origin=new Position{Latitude=52,Longitude=19}};
        var track=new Track{Name="AB",Type=TrackType.ABLine,Points=[new Vec3(0,0,0),new Vec3(0,100,0)]};
        try{
            GeoJsonFieldService.Save(field,[track]);builder.FieldService.ActiveField.Returns(field);
            var vm=builder.Build();vm.SavedTracks.Add(track);vm.SelectedTrack=track;
            vm.State.Guidance.ActiveTrack=track;vm.State.Guidance.IsHeadingSameWay=true;
            vm.State.Guidance.HowManyPathsAway=2;vm.State.Guidance.NudgeOffset=.2;
            var width=ConfigurationStore.Instance.ActualToolWidth-ConfigurationStore.Instance.Tool.Overlap;
            vm.SaveGaplessShift(track,target,.6);
            var saved=GeoJsonFieldService.LoadTracks(directory).Single();
            Assert.Multiple(()=>{
                Assert.That(saved.Points[0].Easting,Is.EqualTo(target==GuidanceShiftTarget.Base?.6:0).Within(.001));
                Assert.That(saved.NudgeDistance,Is.EqualTo(2*width+.2+(target==GuidanceShiftTarget.Guiding?.6:0)).Within(.001));
                Assert.That(vm.SelectedTrack,Is.Not.SameAs(track));Assert.That(track.Points[0].Easting,Is.Zero);
            });
        }finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
    [Test] public void FailedPersistenceDoesNotChangeRunningLine()
    {
        var root=Path.Combine(Path.GetTempPath(),"AgOpenWebGaplessTests-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);var blocked=Path.Combine(root,"blocked");File.WriteAllText(blocked,"file");
        try{
            ConfigurationStore.SetInstance(new());var builder=new MainViewModelBuilder();
            builder.FieldService.ActiveField.Returns(new Field{DirectoryPath=blocked});
            var vm=builder.Build();var track=new Track{Name="AB",Type=TrackType.ABLine,Points=[new Vec3(0,0,0),new Vec3(0,100,0)]};
            vm.SavedTracks.Add(track);vm.SelectedTrack=track;vm.State.Guidance.ActiveTrack=track;
            Assert.That(()=>vm.SaveGaplessShift(track,GuidanceShiftTarget.Base,.6),Throws.Exception);
            Assert.That(vm.SelectedTrack,Is.SameAs(track));Assert.That(vm.SavedTracks.Single(),Is.SameAs(track));Assert.That(track.Points[0].Easting,Is.Zero);
        }finally{Directory.Delete(root,true);}
    }
}
