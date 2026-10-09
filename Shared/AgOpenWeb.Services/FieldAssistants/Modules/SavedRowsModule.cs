using AgOpenWeb.Services.FieldAssistants.Core;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Models.Track;
using AgOpenWeb.Services.SavedRows;
using Newtonsoft.Json.Linq;

namespace AgOpenWeb.Services.FieldAssistants.Modules;
public sealed record SavedRowsContext(string? Directory, double Latitude, double Longitude, bool Enabled, int Selected, bool Captured=false, bool AutoSteer=false);
public interface ISavedRowsHost
{
    Task<SavedRowsContext> CaptureAsync(CancellationToken ct);
    Task ConfigureAsync(string directory, IndividualRowsRequest request, bool enabled, CallContext context);
}
/// <summary>Field-local persistence and recording, serialized off the host dispatcher.
/// Selection/guidance belong to the native cycle worker; this module never engages steering.</summary>
public sealed class SavedRowsModule : FieldAssistantModule
{
    private readonly ISavedRowsHost host;
    private readonly Func<MachineSnapshot> snapshot;
    private readonly SemaphoreSlim gate = new(1);
    private readonly SavedRowRecorder recorder = new();
    private SavedRowTracks? data;
    private string? directory;
    private Guid recordingOwner;
    private int cancelRecording;
    private int revision;
    private string status = "A starts a row; B saves its actual path";

    public SavedRowsModule(ISavedRowsHost host, Func<MachineSnapshot> snapshot) : base("savedrows", "Saved rows")
    {
        this.host=host; this.snapshot=snapshot;
        Async("state",false,(a,c)=>Run(a,c,null));
        foreach(var operation in new[]{"begin","finish","cancel","select","mode","options"})
        { var command=operation; Async(command,true,(a,c)=>Run(a,c,command)); }
    }
    private async Task<object?> Run(JObject args, CallContext context, string? command)
    {
        await gate.WaitAsync(context.Cancellation).ConfigureAwait(false);
        try
        {
            var current=await EnsureAsync(context.Cancellation).ConfigureAwait(false);
            if(command!=null)
            {
                context.Cancellation.ThrowIfCancellationRequested();
                if(!context.HasAuthority())throw new InvalidOperationException("Control authority lost");
                if(data==null||directory==null)throw new InvalidOperationException("Open a field first");
                if(command=="begin")
                {
                    if(recorder.Recording)throw new InvalidOperationException("Finish or cancel the current row first");
                    var pose=ReadyPose();recordingOwner=context.Connection;Interlocked.Exchange(ref cancelRecording,0);
                    recorder.Start(pose.Easting,pose.Northing,pose.Heading,Seconds(pose),"Manual",pose.Simulation);
                    status="Recording row; B saves it";
                }
                else if(command=="finish")
                {
                    if(!recorder.Recording)throw new InvalidOperationException("Start a row with A first");
                    if(recordingOwner!=context.Connection)throw new InvalidOperationException("Recording belongs to another operator");
                    var pose=ReadyPose();recorder.Add(pose.Easting,pose.Northing,pose.Heading,Seconds(pose),pose.Simulation);
                    var row=recorder.Finish("Row "+(data.Rows.Count+1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    data.Rows.Add(row);
                    try{data.Save(directory);}catch{data.Rows.Remove(row);throw;}
                    recorder.Cancel();revision++;status="Row saved in the open field";
                    if(current.Enabled)await Configure(context,current.Selected,true).ConfigureAwait(false);
                }
                else if(command=="cancel") {recorder.Cancel();status="Recording cancelled";}
                else if(command=="mode")
                {
                    var enabled=args.Value<bool>("enabled");
                    if(enabled&&data.Rows.Count==0)throw new InvalidOperationException("Record and save a row first");
                    await Configure(context,current.Selected,enabled).ConfigureAwait(false);
                    current=current with{Enabled=enabled};
                }
                else if(command=="select")
                {
                    var index=data.Rows.FindIndex(row=>row.Id==args.Value<string>("id"));
                    if(index<0)throw new InvalidOperationException("Saved row no longer exists");
                    await Configure(context,index,current.Enabled).ConfigureAwait(false);
                    current=current with{Selected=index};
                }
                else if(command=="options")
                {
                    var distance=Number(args,"captureCm",10,200)/100;
                    var auto=args.Value<bool>("autoSelect");
                    var oldAuto=data.AutoSelect;var oldDistance=data.CaptureDistanceMetres;
                    data.AutoSelect=auto;data.CaptureDistanceMetres=distance;
                    try{data.Save(directory);}catch{data.AutoSelect=oldAuto;data.CaptureDistanceMetres=oldDistance;throw;}
                    if(current.Enabled)await Configure(context,current.Selected,true).ConfigureAwait(false);
                    status="Saved row settings saved";
                }
            }
            return View(args,current);
        }
        finally{gate.Release();}
    }
    private Task Configure(CallContext context,int selected,bool enabled)
    {
        var tracks=data!.Rows.Select(row=>new Models.Track.Track{Name=row.Name,Type=TrackType.Curve,NoPassOffset=true,IsIndividualRow=true,
            Points=row.Points.Select(p=>new Vec3(p.Easting,p.Northing,p.Heading)).ToList()}).ToArray();
        return host.ConfigureAsync(directory!,new(tracks,data.AutoSelect,data.CaptureDistanceMetres,selected),enabled,context);
    }
    private async Task<SavedRowsContext> EnsureAsync(CancellationToken ct)
    {
        var current=await host.CaptureAsync(ct).ConfigureAwait(false);
        if(current.Directory!=directory)
        {
            recorder.Cancel();data=null;directory=null;revision++;
            status="A starts a row; B saves its actual path";
        }
        if(current.Directory!=null&&data==null)
        { data=SavedRowTracks.Load(current.Directory,current.Latitude,current.Longitude);directory=current.Directory; }
        return current;
    }
    private MachineSnapshot ReadyPose()
    {
        var pose=snapshot();
        if(!pose.PositionValid||pose.FieldDirectory!=directory||DateTimeOffset.UtcNow-pose.ReceivedAt>TimeSpan.FromSeconds(1)
            ||!double.IsFinite(pose.Easting)||!double.IsFinite(pose.Northing)||!double.IsFinite(pose.Heading))
            throw new InvalidOperationException("Open a field and provide a current GPS position");
        return pose;
    }
    private static double Seconds(MachineSnapshot pose)=>pose.ReceivedAt.UtcTicks/(double)TimeSpan.TicksPerSecond;
    public override async Task TickAsync(CancellationToken ct)
    {
        if(!recorder.Recording)return;
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if(Interlocked.Exchange(ref cancelRecording,0)!=0){recorder.Cancel();status="Recording cancelled: operator disconnected";return;}
            await EnsureAsync(ct).ConfigureAwait(false);
            if(!recorder.Recording)return;
            try{var pose=ReadyPose();recorder.Add(pose.Easting,pose.Northing,pose.Heading,Seconds(pose),pose.Simulation);}
            catch(InvalidOperationException ex){recorder.Cancel();status=ex.Message;}
        }
        finally{gate.Release();}
    }
    public override void Drop(Guid connection){if(connection==recordingOwner)Interlocked.Exchange(ref cancelRecording,1);}
    private object View(JObject args,SavedRowsContext current)=>new
    {
        fieldOpen=data!=null,fieldName=snapshot().FieldName,enabled=current.Enabled,selected=current.Selected,recording=recorder.Recording,
        recordedCount=recorder.Count,revision,status=data==null?"Open a field first":current.Enabled?(current.Captured?(current.AutoSteer?"Following captured saved row":"Saved row captured; press AUTO to follow"):"Drive into a saved row to capture it"):status,
        autoSelect=data?.AutoSelect??true,captureCm=(data?.CaptureDistanceMetres??.5)*100,
        rows=data?.Rows.Select((row,index)=>new{row.Id,row.Name,row.Source,row.Simulator,index,points=row.Points.Count,
            lengthM=row.Points.Zip(row.Points.Skip(1),(a,b)=>Math.Sqrt(Math.Pow(a.Easting-b.Easting,2)+Math.Pow(a.Northing-b.Northing,2))).Sum()}).ToArray(),
        paths=args.Value<int?>("revision")==revision?null:data?.Rows.Select(row=>new{row.Id,points=row.Points.Select(p=>new{e=p.Easting,n=p.Northing})}).ToArray(),
        recordingPoints=recorder.Points.Skip(Math.Clamp(args.Value<int?>("recordedCount")??0,0,recorder.Count)).Select(p=>new{e=p.Easting,n=p.Northing}).ToArray()
    };
}
