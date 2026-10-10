using System;
using System.Linq;
using AgOpenWeb.Models.Track;
using AgOpenWeb.Services.GeoJson;
using AgOpenWeb.Services.Track;

namespace AgOpenWeb.ViewModels;

public partial class MainViewModel
{
    /// <summary>Owner-loop command. Persist a complete replacement first; a failed save
    /// leaves the running track and guidance unchanged. Selection uses the native
    /// configuration handoff; no pipeline-owned UI mirror is written here.</summary>
    public void SaveGaplessShift(Track expected, GuidanceShiftTarget target, double driverDelta)
    {
        var field = _fieldService.ActiveField ?? throw new InvalidOperationException("Open a field first");
        if (IsAutoSteerEngaged || !ReferenceEquals(SelectedTrack, expected)
            || !ReferenceEquals(State.Guidance.ActiveTrack, expected))
            throw new InvalidOperationException("Guidance context changed");
        int index = SavedTracks.IndexOf(expected);
        if (index < 0 || expected.IsClosed)
            throw new InvalidOperationException("A saved straight AB line is required");
        var replacement = SavedGuidanceShift.Build(expected, target, driverDelta,
            State.Guidance.IsHeadingSameWay, State.Guidance.HowManyPathsAway,
            State.Guidance.NudgeOffset, ConfigStore.ActualToolWidth - Tool.Overlap);
        var persisted = SavedTracks.ToList(); persisted[index] = replacement;
        GeoJsonFieldService.SaveTracks(field.DirectoryPath, persisted);
        SavedTracks[index] = replacement;
        int stateIndex = State.Field.Tracks.IndexOf(expected);
        if (stateIndex >= 0) State.Field.Tracks[stateIndex] = replacement;
        SelectedTrack = replacement;
        StatusMessage = target == GuidanceShiftTarget.Base ? "Base AB shift saved" : "Guiding AB shift saved";
    }
}
