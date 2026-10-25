using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.FieldAssistants.Core;
using AgOpenWeb.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace AgOpenWeb.RemoteWiring;
/// <summary>Captures owner-loop mirrors. Recording/analysis runs on its own worker;
/// pipeline changes use intents and native configuration handoffs.</summary>
internal sealed partial class FieldAssistantsRuntime
{
    private readonly ModuleRegistry registry=new();
    private readonly CancellationTokenSource lifetime=new();
    private readonly Timer captureTimer;
    private readonly Task worker;
    private MachineSnapshot snapshot=new(null,"",0,0,0,0,0,0,0,false,false,false,DateTimeOffset.MinValue);
    private int queued,stopped;
    public ModuleRegistry Registry=>registry;
    public FieldAssistantsRuntime(IServiceProvider sp,MainViewModel vm)
    {
        AddGapless(sp,vm);AddSavedRows(sp,vm);
        var dispatcher=sp.GetRequiredService<IUiDispatcher>();
        var state=sp.GetRequiredService<ApplicationState>();
        var fields=sp.GetRequiredService<IFieldService>();
        var gps=sp.GetRequiredService<IGpsService>();
        captureTimer=new Timer(_=>{
            if(Volatile.Read(ref stopped)!=0||Interlocked.Exchange(ref queued,1)!=0)return;
            dispatcher.Post(()=>{try{
                if(Volatile.Read(ref stopped)!=0)return;
                var v=state.Vehicle;var f=fields.ActiveField;
                bool valid=v.HasValidFix&&(state.Simulator.IsEnabled||gps.IsGpsLive&&gps.IsGpsDataOk());
                Volatile.Write(ref snapshot,new(f?.DirectoryPath,f?.Name??"",v.Latitude,v.Longitude,v.Easting,v.Northing,v.Heading*Math.PI/180,v.Speed*3.6,0,false,state.Simulator.IsEnabled,valid,valid?DateTimeOffset.UtcNow:DateTimeOffset.MinValue));
            }finally{Volatile.Write(ref queued,0);}});
        },null,0,100);
        worker=Task.Run(async()=>{
            using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            try{while(await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false)){
                try{await registry.TickAsync(lifetime.Token).ConfigureAwait(false);}
                catch(OperationCanceledException)when(lifetime.IsCancellationRequested){break;}
                catch(Exception ex){System.Diagnostics.Trace.TraceError("Field assistant tick: {0}",ex);}
            }}catch(OperationCanceledException)when(lifetime.IsCancellationRequested){}
        });
    }
    partial void AddGapless(IServiceProvider sp,MainViewModel vm);
    partial void AddSavedRows(IServiceProvider sp,MainViewModel vm);
    public async Task StopAsync()
    {
        if(Interlocked.Exchange(ref stopped,1)!=0)return;
        captureTimer.Dispose();lifetime.Cancel();await worker.ConfigureAwait(false);
        await registry.StopAsync().ConfigureAwait(false);
    }
}
