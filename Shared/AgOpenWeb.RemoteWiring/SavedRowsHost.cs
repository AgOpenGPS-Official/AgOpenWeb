using AgOpenWeb.Services.FieldAssistants.Core;
using AgOpenWeb.Services.FieldAssistants.Modules;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Models.State;
using AgOpenWeb.Models.Track;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services;
using AgOpenWeb.ViewModels;

namespace AgOpenWeb.RemoteWiring;
/// <summary>Owner-thread bridge. Geometry/selection enter the worker through an atomic intent.</summary>
internal sealed class SavedRowsHost(IUiDispatcher dispatcher, MainViewModel vm, ApplicationState state,
    IFieldService fields, IPipelineIntents intents) : ISavedRowsHost
{
    private IReadOnlyList<Track> tracks=[];
    private int manualSelection=-1;
    private string? configuredField;
    public Task<SavedRowsContext> CaptureAsync(CancellationToken ct)=>OnOwner(()=>
    {
        var field=fields.ActiveField;
        var selected=state.Guidance.ActiveTrack;
        return new SavedRowsContext(field?.DirectoryPath,field?.Origin.Latitude??0,field?.Origin.Longitude??0,
            vm.SelectedTrack?.IsIndividualRow==true,field?.DirectoryPath==configuredField?Enumerable.Range(0,tracks.Count).FirstOrDefault(i=>ReferenceEquals(tracks[i],selected),manualSelection):-1,
            vm.SelectedTrack?.IsIndividualRow==true&&selected?.IsIndividualRow==true,vm.IsAutoSteerEngaged);
    },ct);
    public Task ConfigureAsync(string directory,IndividualRowsRequest request,bool enabled,CallContext context)=>OnOwner(()=>
    {
        context.Cancellation.ThrowIfCancellationRequested();
        if(!context.HasAuthority())throw new InvalidOperationException("Control authority lost");
        if(fields.ActiveField?.DirectoryPath!=directory)throw new InvalidOperationException("Field changed; reopen saved rows");
        if(vm.IsAutoSteerEngaged)vm.ToggleAutoSteerCommand?.Execute(null);
        tracks=request.Rows;manualSelection=request.Selected;configuredField=directory;
        intents.RequestIndividualRows(request);intents.RequestClearYouTurn();
        if(enabled)
        {
            if(tracks.Count==0)throw new InvalidOperationException("Record and save a row first");
            vm.IsContourModeOn=false;vm.IsAutoTrackEnabled=false;vm.IsYouTurnEnabled=false;
            var selected=tracks[request.Selected>=0&&request.Selected<tracks.Count?request.Selected:0];
            // The native UI may edit its selected track. Keep those edits away from the
            // immutable actual row geometry owned by the cycle request and field file.
            vm.SelectedTrack=new Track{Name=selected.Name,Type=selected.Type,IsIndividualRow=true,NoPassOffset=true,
                Points=selected.Points.Select(p=>new AgOpenWeb.Models.Base.Vec3(p.Easting,p.Northing,p.Heading)).ToList()};
        }
        else if(vm.SelectedTrack?.IsIndividualRow==true)vm.SelectedTrack=null;
        return true;
    },context.Cancellation);
    private Task<T> OnOwner<T>(Func<T> action,CancellationToken ct)
    {
        var result=new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.Post(()=>{try{ct.ThrowIfCancellationRequested();result.TrySetResult(action());}catch(Exception ex){result.TrySetException(ex);}});
        return result.Task.WaitAsync(ct);
    }
}
