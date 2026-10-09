using AgOpenWeb.Services.SavedRows;
using System;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.FieldAssistants
{
    [TestFixture]
    public sealed class SavedRowTracksTests
    {
        private static SavedRowTrack Row(double offset, double bend = 0) => new SavedRowTrack
        {
            Name = "Rząd",
            Source = "Manual",
            Points = Enumerable.Range(0, 121).Select(i => new SavedRowPoint(offset + bend * Math.Sin(i * .025), i * .25, 0)).ToList()
        };
        [Test]
        public void EachIrregularRowIsStoredIndependentlyWithOriginAndSource()
        {
            string path = Path.Combine(Path.GetTempPath(), "SavedRows-" + Guid.NewGuid().ToString("N"));
            try
            {
                var rows = new SavedRowTracks { OriginLatitude = 52.1, OriginLongitude = 19.2 };
                rows.Rows.Add(Row(0, .3)); var second = Row(.8, -.2); second.Source = "Camera"; second.Simulator = true; rows.Rows.Add(second);
                rows.Save(path); rows.CaptureDistanceMetres = .4; rows.Save(path);
                var loaded = SavedRowTracks.Load(path, 52.1, 19.2);
                Assert.That(loaded.Rows[1].Points[40].Easting, Is.EqualTo(second.Points[40].Easting));
                Assert.That(loaded.Rows[1].Source, Is.EqualTo("Camera")); Assert.That(loaded.Rows[1].Simulator, Is.True);
                Assert.That(loaded.CaptureDistanceMetres, Is.EqualTo(.4)); Assert.That(File.Exists(Path.Combine(path, "individual-rows.json.bak")), Is.True);
                Assert.Throws<InvalidDataException>(() => SavedRowTracks.Load(path, 52.11, 19.2));
                Assert.That(SavedRowTracks.Load(Path.Combine(path, "AnotherField"), 52.1, 19.2).Rows, Is.Empty);
            }
            finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
        }
        [Test]
        public void LegacyImportPreservesTheOriginalAndWritesOnlyNativeRowsFile()
        {
            string path=Path.Combine(Path.GetTempPath(),"SavedRows-import-"+Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(path);var data=new SavedRowTracks{OriginLatitude=52.1,OriginLongitude=19.2};data.Rows.Add(Row(0));
                var legacy=Path.Combine(path,"SavedRows.json");File.WriteAllText(legacy,Newtonsoft.Json.JsonConvert.SerializeObject(data));
                var bytes=File.ReadAllBytes(legacy);var loaded=SavedRowTracks.Load(path,52.1,19.2);loaded.Save(path);
                Assert.That(File.ReadAllBytes(legacy),Is.EqualTo(bytes));
                Assert.That(File.Exists(Path.Combine(path,"individual-rows.json")),Is.True);
                Assert.That(SavedRowTracks.Load(path,52.1,19.2).Rows[0].Id,Is.EqualTo(data.Rows[0].Id));
            }
            finally{if(Directory.Exists(path))Directory.Delete(path,true);}
        }
        [Test]
        public void NearbyRowSwitchRequiresDistanceHeadingAndStableCandidate()
        {
            var data = new SavedRowTracks(); data.Rows.Add(Row(0)); data.Rows.Add(Row(.75));
            var selector = new SavedRowSelector();
            Assert.That(selector.Update(data, .1, 10, 0, 0), Is.Null);
            Assert.That(selector.Update(data, .1, 10, 0, .4).RowIndex, Is.Zero);
            Assert.That(selector.Update(data, .38, 10, 0, .5).RowIndex, Is.Zero, "Hysteresis holds the current row between two rows");
            Assert.That(selector.Update(data, .75, 10, 0, .6), Is.Null);
            Assert.That(selector.Update(data, .75, 10, 0, .8), Is.Null);
            Assert.That(selector.Update(data, .75, 10, 0, 1).RowIndex, Is.EqualTo(1));
            Assert.That(selector.Update(data, 3, 10, 0, 2), Is.Null);
            Assert.That(selector.Update(data, .75, 10, Math.PI / 2, 3), Is.Null, "Crossing rows does not capture a track");
        }
        [Test]
        public void MatchingUsesFiniteSegmentsAndAcceptsOppositeTravelDirection()
        {
            var data = new SavedRowTracks(); data.Rows.Add(Row(0));
            Assert.That(SavedRowSelector.Match(data, 0, .2, 10.12, Math.PI).Distance, Is.EqualTo(.2).Within(.00001));
            Assert.That(SavedRowSelector.Match(data, 0, 0, -1, 0), Is.Null);
            Assert.That(SavedRowSelector.Match(data, 0, 0, 31, 0), Is.Null);
            Assert.That(SavedRowSelector.Match(data, 0, double.NaN, 10, 0), Is.Null);
        }
        [Test]
        public void ManualSelectionDoesNotSwitchToAnotherRowWhenAutomaticChoiceIsOff()
        {
            var data = new SavedRowTracks { AutoSelect = false }; data.Rows.Add(Row(0)); data.Rows.Add(Row(.75));
            var selector = new SavedRowSelector(); selector.Reset(0);
            Assert.That(selector.Update(data, .75, 10, 0, 1), Is.Null); Assert.That(selector.Selected, Is.Zero);
            selector.Reset(1); Assert.That(selector.Update(data, .75, 10, 0, 2).RowIndex, Is.EqualTo(1));
        }
        [Test]
        public void RecorderRejectsGapsJumpsAndPositionSourceChanges()
        {
            var recorder = new SavedRowRecorder(); recorder.Start(0, 0, 0, 0, "Manual", false);
            recorder.Add(0, .4, 0, .1, false);
            Assert.Throws<InvalidOperationException>(() => recorder.Add(0, .8, 0, 2, false)); Assert.That(recorder.Recording, Is.False);
            recorder.Start(0, 0, 0, 0, "Camera", true);
            Assert.Throws<InvalidOperationException>(() => recorder.Add(0, 8, 0, .1, true));
            recorder.Start(0, 0, 0, 0, "Camera", true);
            Assert.Throws<InvalidOperationException>(() => recorder.Add(0, .4, 0, .1, false));
        }
        [Test]
        public void RecorderKeepsActualPassAndCanRetrySaveWithoutLosingIt()
        {
            var recorder = new SavedRowRecorder(); recorder.Start(0, 0, 0, 0, "Camera", true);
            for (int i = 1; i < 12; i++) recorder.Add(.02 * i * i, .3 * i, .1, .1 * i, true);
            var row = recorder.Finish("Rząd 1"); Assert.That(row.Source, Is.EqualTo("Camera")); Assert.That(row.Simulator, Is.True);
            Assert.That(row.Points.Last().Easting, Is.EqualTo(2.42).Within(.00001));
            Assert.That(recorder.Recording, Is.True); Assert.That(recorder.Finish("Powtórz").Points.Count, Is.EqualTo(row.Points.Count));
            recorder.Cancel(); Assert.That(recorder.Recording, Is.False);
        }
    }
}
