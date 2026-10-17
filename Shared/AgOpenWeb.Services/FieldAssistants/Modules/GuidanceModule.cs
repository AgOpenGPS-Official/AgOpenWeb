using System.Collections.Concurrent;
using AgOpenWeb.Services.FieldAssistants.Core;
using AgOpenWeb.Services.Track;
using Newtonsoft.Json.Linq;

namespace AgOpenWeb.Services.FieldAssistants.Modules;
public interface IGuidanceAssistantHost
{
    Task<GaplessInput> CaptureAsync(CancellationToken ct);
    Task ApplyNudgeAsync(GaplessInput input, double delta, CallContext context);
    Task SaveShiftAsync(GaplessInput input, double delta, GuidanceShiftTarget target, CallContext context);
}
public sealed class GuidanceModule : FieldAssistantModule
{
    public sealed record Proposal(string Id, GaplessInput Input, GaplessAlignment.GaplessAbResult Result, DateTimeOffset Created);
    private readonly ConcurrentDictionary<Guid, Proposal> proposals = new();
    private readonly SemaphoreSlim analysis = new(1);
    private readonly IGuidanceAssistantHost host;
    public GuidanceModule(IGuidanceAssistantHost host) : base("guidance", "AB alignment without missed strips")
    {
        this.host = host;
        Async("context", false, async (_, c) => {
            try { var input = await host.CaptureAsync(c.Cancellation).ConfigureAwait(false);
                return (object)new { ready = input.HasCoverage && input.Boundaries.Count > 0, key = FormattableString.Invariant($"{input.FieldKey}|{input.SelectionKey ?? input.TrackKey}|{input.ToolWidth:R}|{input.ToolOffset:R}") }; }
            catch (InvalidOperationException) { return new { ready = false, key = "" }; }
        });
        Async("analyze", false, async (_, c) => {
            await analysis.WaitAsync(c.Cancellation).ConfigureAwait(false);
            try {
                var input = await host.CaptureAsync(c.Cancellation).ConfigureAwait(false);
                var result = new GaplessAlignment(input).Analyze();
                var proposal = new Proposal(Guid.NewGuid().ToString("N"), input, result, DateTimeOffset.UtcNow);
                proposals[c.Connection] = proposal; return new { id = proposal.Id, result };
            } finally { analysis.Release(); }
        });
        Async("apply", true, async (a, c) => {
            var target = a.Value<string>("target") switch {
                "base" => GuidanceShiftTarget.Base, "guiding" => GuidanceShiftTarget.Guiding,
                _ => throw new ArgumentException("Choose the base or guiding line")
            };
            return await SaveAsync(c, a.Value<string>("id") ?? "", target).ConfigureAwait(false);
        });
        Async("dismiss", false, (a, c) => {
            if (proposals.TryGetValue(c.Connection, out var current) && current.Id == a.Value<string>("id")) proposals.TryRemove(c.Connection, out _);
            return Task.FromResult<object?>(new { dismissed = true });
        });
    }
    private async Task<Proposal> Review(CallContext context, string id)
    {
        if (!proposals.TryGetValue(context.Connection, out var proposal) || proposal.Id != id
            || DateTimeOffset.UtcNow - proposal.Created > TimeSpan.FromMinutes(2) || !proposal.Result.Success || proposal.Result.AlreadyAligned)
            throw new InvalidOperationException("Analyze the current line first");
        var input = await host.CaptureAsync(context.Cancellation).ConfigureAwait(false);
        if (input.FieldKey != proposal.Input.FieldKey || input.TrackKey != proposal.Input.TrackKey
            || input.SelectionKey != proposal.Input.SelectionKey || input.ToolWidth != proposal.Input.ToolWidth || input.ToolOffset != proposal.Input.ToolOffset)
            throw new InvalidOperationException("Field or guidance line changed; analyze again");
        var current = new GaplessAlignment(input).Analyze();
        if (!current.Success || current.AlreadyAligned || Math.Sign(current.NudgeCommandM) != Math.Sign(proposal.Result.NudgeCommandM)
            || Math.Abs(current.NudgeCommandM - proposal.Result.NudgeCommandM) > .05)
            throw new InvalidOperationException("Coverage changed; review a new proposal");
        return proposal with { Input = input, Result = current };
    }
    public async Task<Proposal> ReviewAsync(CallContext context, string id)
    {
        await analysis.WaitAsync(context.Cancellation).ConfigureAwait(false);
        try { return await Review(context, id).ConfigureAwait(false); } finally { analysis.Release(); }
    }
    public async Task<object> SaveAsync(CallContext context, string id, GuidanceShiftTarget target)
    {
        if (!context.HasAuthority()) throw new InvalidOperationException("Control authority lost");
        await analysis.WaitAsync(context.Cancellation).ConfigureAwait(false);
        try {
            var proposal = await Review(context, id).ConfigureAwait(false);
            await host.SaveShiftAsync(proposal.Input, proposal.Result.NudgeCommandM, target, context).ConfigureAwait(false);
            proposals.TryRemove(context.Connection, out _);
            return new { saved = true, target = target == GuidanceShiftTarget.Base ? "base" : "guiding", proposal.Result.NudgeCommandM };
        } finally { analysis.Release(); }
    }
    public override void Drop(Guid connection) => proposals.TryRemove(connection, out _);
}
