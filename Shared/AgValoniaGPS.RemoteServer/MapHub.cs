using Microsoft.AspNetCore.SignalR;

namespace AgValoniaGPS.RemoteServer;

/// <summary>
/// SignalR hub for the map feed. Commands (mark AB, select track, etc.) will be
/// added in Phase 3; for now it's a one-way feed — the client receives "scene"
/// and "tick" messages, and gets the current scene immediately on connect.
/// </summary>
public sealed class MapHub : Hub
{
    private readonly MapBroadcaster _broadcaster;

    public MapHub(MapBroadcaster broadcaster) => _broadcaster = broadcaster;

    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("scene", _broadcaster.CurrentScene());

        // Seed coverage: grid geometry + a full snapshot of what's painted so far.
        if (_broadcaster.CurrentCoverageInit() is { } init)
        {
            await Clients.Caller.SendAsync("coverageInit", init);
            if (_broadcaster.CoverageSnapshot() is { } snap)
                await Clients.Caller.SendAsync("coverageCells", snap);
        }

        await base.OnConnectedAsync();
    }
}
