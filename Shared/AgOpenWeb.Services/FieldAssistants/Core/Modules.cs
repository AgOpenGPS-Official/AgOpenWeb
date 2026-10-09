using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.Text;

namespace AgOpenWeb.Services.FieldAssistants.Core;

public sealed record ModuleInfo(string Id, string Title, string Version, string[] Operations);
public sealed record MachineSnapshot(string? FieldDirectory, string FieldName, double Latitude,
    double Longitude, double Easting, double Northing, double Heading, double SpeedKmh,
    double ToolWidthM, bool Working, bool Simulation, bool PositionValid, DateTimeOffset ReceivedAt)
{
    public double ToolEasting { get; init; }
    public double ToolNorthing { get; init; }
    public bool ToolPositionValid { get; init; }
    public double WorkedAreaM2 { get; init; }
    public string SectionMask { get; init; } = "0";
    public double FieldAreaM2 { get; init; }
}

/// <summary>Per-call authority must be evaluated when an output is sent, not cached at request receipt.</summary>
public sealed record CallContext(Guid Connection, Func<bool> HasAuthority, CancellationToken Cancellation);
public sealed record RpcRequest(string Id, string Module, string Operation, JObject? Args);
public sealed record RpcResponse(string Id, bool Ok, object? Data, string? Error);

public interface IFieldAssistantModule
{
    ModuleInfo Info { get; }
    Task<object?> InvokeAsync(string operation, JObject args, CallContext context);
    Task TickAsync(CancellationToken ct);
    void Drop(Guid connection);
}

public abstract class FieldAssistantModule(string id, string title) : IFieldAssistantModule
{
    private readonly Dictionary<string, (bool Write, Func<JObject, CallContext, Task<object?>> Run)> operations = new(StringComparer.Ordinal);
    public ModuleInfo Info => new(id, title, "1.0", operations.Keys.ToArray());
    protected void Read(string name, Func<JObject, object?> run) => operations.Add(name, (false, (a, _) => Task.FromResult(run(a))));
    protected void Write(string name, Func<JObject, object?> run) => operations.Add(name, (true, (a, _) => Task.FromResult(run(a))));
    protected void Async(string name, bool write, Func<JObject, CallContext, Task<object?>> run) => operations.Add(name, (write, run));
    public Task<object?> InvokeAsync(string operation, JObject args, CallContext context)
    {
        if (!operations.TryGetValue(operation, out var entry)) throw new ArgumentException("Unknown operation");
        if (entry.Write && !context.HasAuthority()) throw new InvalidOperationException("Control authority required");
        context.Cancellation.ThrowIfCancellationRequested();
        return entry.Run(args, context);
    }
    public virtual Task TickAsync(CancellationToken ct) => Task.CompletedTask;
    public virtual void Drop(Guid connection) { }
    protected static double Number(JObject a, string key, double min, double max)
    {
        if (!double.TryParse(a[key]?.ToString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var x) || !double.IsFinite(x) || x < min || x > max)
            throw new ArgumentException($"Invalid {key}: {min}..{max}");
        return x;
    }
}

public sealed class ModuleRegistry
{
    private readonly Dictionary<string, IFieldAssistantModule> modules = new(StringComparer.Ordinal);
    private readonly object lifecycleGate = new();
    private readonly CancellationTokenSource lifetime = new();
    private int active;
    private bool stopping;
    private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ModuleInfo[] Info => modules.Values.Select(x => x.Info).ToArray();
    public void Add(IFieldAssistantModule module) => modules.Add(module.Info.Id, module);
    public async Task<string> HandleAsync(Guid connection, Func<bool> authority, string json, CancellationToken ct)
    {
        lock(lifecycleGate){if(stopping)return Encode(new("",false,null,"Host is stopping"));active++;}
        using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(ct,lifetime.Token);
        var id = "";
        try
        {
            var request = JsonConvert.DeserializeObject<RpcRequest>(json) ?? throw new ArgumentException("Invalid request");
            if (string.IsNullOrEmpty(request.Id) || request.Id.Length > 64) throw new ArgumentException("Invalid request id");
            id = request.Id;
            if (request.Module == "system" && request.Operation == "modules") return Encode(new(id, true, Info, null));
            if (!modules.TryGetValue(request.Module, out var module)) throw new ArgumentException("Unknown module");
            var result = await module.InvokeAsync(request.Operation, request.Args ?? new(), new(connection, authority, cancellation.Token)).ConfigureAwait(false);
            return Encode(new(id, true, result, null));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException)
        { return Encode(new(id, false, null, ex.Message)); }
        catch (OperationCanceledException) { return Encode(new(id, false, null, "Cancelled")); }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError("Field assistant RPC {0}: {1}",id,ex); return Encode(new(id, false, null, "Module failed; check local diagnostics")); }
        finally {lock(lifecycleGate){active--;if(stopping&&active==0)drained.TrySetResult();}}
    }
    public void Drop(Guid connection) { foreach(var module in modules.Values) module.Drop(connection); }
    public async Task TickAsync(CancellationToken ct) { foreach(var module in modules.Values) await module.TickAsync(ct).ConfigureAwait(false); }
    public async Task StopAsync()
    {
        lock(lifecycleGate){stopping=true;if(active==0)drained.TrySetResult();}
        lifetime.Cancel();await drained.Task.ConfigureAwait(false);
    }
    private static string Encode(RpcResponse r) => JsonConvert.SerializeObject(r, new JsonSerializerSettings { ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver() });
}
