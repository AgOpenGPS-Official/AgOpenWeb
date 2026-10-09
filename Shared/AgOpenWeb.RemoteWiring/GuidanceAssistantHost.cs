using AgOpenWeb.Services.FieldAssistants;
using AgOpenWeb.Services.FieldAssistants.Core;
using AgOpenWeb.Services.FieldAssistants.Modules;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services;
using AgOpenWeb.Models.Track;
using System.Globalization;

namespace AgOpenWeb.RemoteWiring;
/// <summary>Only captures UI-owned mirrors or posts an intent. The expensive AOG analysis runs in the module worker.</summary>
internal sealed class GuidanceAssistantHost(IUiDispatcher dispatcher,ApplicationState state,ConfigurationStore config,
    IFieldService fields,ICoverageMapService coverage,IPipelineIntents intents,Func<string?> activeDirectory, AgOpenWeb.ViewModels.MainViewModel? vm = null) : IGuidanceAssistantHost
{
    private GaplessInput Capture()
    {
        var field=fields.ActiveField??throw new InvalidOperationException("Open a field first");
        if(state.Connections.IsAutoSteerEngaged)throw new InvalidOperationException("Disengage steering before aligning AB");
        var track=state.Guidance.ActiveTrack??throw new InvalidOperationException("Select an AB line");
        var line=state.Guidance.DisplayLine;
        if(track.Type!=TrackType.ABLine||line is not {Count:>=2})throw new InvalidOperationException("An active straight AB guidance line is required");
        var directory=field.DirectoryPath;
        var polygons=new List<AgOpenWeb.Models.BoundaryPolygon>();if(field.Boundary?.OuterBoundary is {} outer)polygons.Add(outer);if(field.Boundary!=null)polygons.AddRange(field.Boundary.InnerBoundaries);
        var boundaries=polygons.Select(p=>new AgOpenWeb.Models.BoundaryPolygon{Points=p.Points.ToList()}).ToList();
        var heading=Math.Atan2(line[^1].Easting-line[0].Easting,line[^1].Northing-line[0].Northing);
        var key=track.Name+"|"+string.Join('|',line.Take(2).SelectMany(p=>new[]{p.Easting,p.Northing}).Select(x=>x.ToString("R",CultureInfo.InvariantCulture)))+"|"+state.Guidance.IsHeadingSameWay;
        return new(directory,key,new(line[0].Easting,line[0].Northing),heading,state.Guidance.IsHeadingSameWay,config.ActualToolWidth,config.Tool.Offset,boundaries,coverage.GetCoverageBounds()!=null,
            p=>{if(activeDirectory()!=directory)throw new InvalidOperationException("Field changed during analysis");return coverage.IsPointCovered(p.Easting,p.Northing);})
        {SelectionKey=track.Name+"|"+string.Join('|',track.Points.Take(2).SelectMany(p=>new[]{p.Easting,p.Northing}).Select(x=>x.ToString("R",CultureInfo.InvariantCulture)))+"|"+state.Guidance.HowManyPathsAway+"|"+state.Guidance.IsHeadingSameWay};
    }
    public Task<GaplessInput> CaptureAsync(CancellationToken ct)
    {
        var result=new TaskCompletionSource<GaplessInput>(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.Post(()=>{try{ct.ThrowIfCancellationRequested();result.SetResult(Capture());}catch(Exception ex){result.SetException(ex);}});return result.Task.WaitAsync(ct);
    }
    public Task SaveShiftAsync(GaplessInput input, double delta, AgOpenWeb.Services.Track.GuidanceShiftTarget target, CallContext context)
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.Post(() => { try {
            context.Cancellation.ThrowIfCancellationRequested();
            if (!context.HasAuthority()) throw new InvalidOperationException("Control authority lost");
            var current = Capture();
            if (current.FieldKey != input.FieldKey || current.TrackKey != input.TrackKey || !double.IsFinite(delta)
                || Math.Abs(delta) > config.ActualToolWidth * .8 || current.SelectionKey != input.SelectionKey
                || current.ToolWidth != input.ToolWidth || current.ToolOffset != input.ToolOffset)
                throw new InvalidOperationException("Guidance context changed");
            (vm ?? throw new InvalidOperationException("Saved line support unavailable")).SaveGaplessShift(state.Guidance.ActiveTrack!, target, delta);
            result.SetResult();
        } catch (Exception ex) { result.SetException(ex); } });
        return result.Task.WaitAsync(context.Cancellation);
    }
    public Task ApplyNudgeAsync(GaplessInput input,double delta,CallContext context)
    {
        var result=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.Post(()=>{try{context.Cancellation.ThrowIfCancellationRequested();if(!context.HasAuthority())throw new InvalidOperationException("Control authority lost");
            var current=Capture();if(current.FieldKey!=input.FieldKey||current.TrackKey!=input.TrackKey||!double.IsFinite(delta)||Math.Abs(delta)>config.ActualToolWidth*.8)throw new InvalidOperationException("Guidance context changed");
            intents.RequestGuidanceNudge(delta);result.SetResult();}catch(Exception ex){result.SetException(ex);}});return result.Task.WaitAsync(context.Cancellation);
    }
}
