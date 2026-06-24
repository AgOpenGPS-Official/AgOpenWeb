// Remote actuation authority (Phase 2 safety layer). A single client may hold
// "control" at a time; only the fresh holder's Tier-2 (live actuation) commands
// are honored by the hub. Presence must be refreshed (heartbeat) within the
// deadman window or the hold is revoked. A socket drop or a stale hold fires
// Revoked → the host runs its failsafe (Phase 3: disengage what the remote
// actuated). All state behind a lock; touched from the hub (per-connection) and
// the broadcaster sweep.

namespace AgOpenWeb.RemoteServer;

public sealed class ControlAuthority
{
    // No presence for this long (ms) while holding → deadman revoke. The client
    // heartbeats at ~2 Hz, so 1.5 s tolerates a missed beat without nuisance loss.
    private const long DeadmanMs = 1500;

    private readonly object _lock = new();
    private Guid? _holder;
    private string _holderName = "";
    private long _lastPresenceTicks;

    /// <summary>Authority state changed (acquired / released / revoked). Carries the
    /// new state so the host can broadcast it and drive the native banner.</summary>
    public event Action<ControlStateDto>? Changed;

    /// <summary>Authority lost involuntarily (disconnect / deadman). The host runs
    /// its failsafe. Not raised on a voluntary Release.</summary>
    public event Action<string>? Revoked;

    public ControlStateDto Snapshot()
    {
        lock (_lock)
            return new ControlStateDto(_holder.HasValue, _holder?.ToString() ?? "", _holderName);
    }

    /// <summary>True only when <paramref name="conn"/> holds control AND its presence
    /// is fresh — the gate the hub applies to every Tier-2 command.</summary>
    public bool HoldsFresh(Guid conn)
    {
        lock (_lock)
            return _holder == conn && (Environment.TickCount64 - _lastPresenceTicks) <= DeadmanMs;
    }

    /// <summary>Take control. Granted if free or already held by this connection;
    /// denied if another connection holds it (first-come, single authority).</summary>
    public bool Acquire(Guid conn, string name)
    {
        ControlStateDto? snap = null;
        lock (_lock)
        {
            if (_holder.HasValue && _holder != conn) return false;
            _holder = conn;
            _holderName = string.IsNullOrWhiteSpace(name) ? "Remote" : name.Trim();
            _lastPresenceTicks = Environment.TickCount64;
            snap = new ControlStateDto(true, _holder.ToString()!, _holderName);
        }
        Changed?.Invoke(snap);
        return true;
    }

    /// <summary>Heartbeat — keeps a hold alive (deadman reset). No-op for non-holders.</summary>
    public void Refresh(Guid conn)
    {
        lock (_lock) { if (_holder == conn) _lastPresenceTicks = Environment.TickCount64; }
    }

    /// <summary>Voluntary release. No failsafe (the holder chose to let go).</summary>
    public void Release(Guid conn)
    {
        if (ClearIfHolder(conn)) Changed?.Invoke(Snapshot());
    }

    /// <summary>Socket dropped. If it held control, fire the failsafe.</summary>
    public void Drop(Guid conn)
    {
        if (ClearIfHolder(conn))
        {
            Revoked?.Invoke("client disconnected");
            Changed?.Invoke(Snapshot());
        }
    }

    /// <summary>Called periodically by the broadcaster. Revokes a stale holder
    /// (deadman) and fires the failsafe. Returns true if it revoked one.</summary>
    public bool SweepStale()
    {
        bool revoked;
        lock (_lock)
        {
            revoked = _holder.HasValue && (Environment.TickCount64 - _lastPresenceTicks) > DeadmanMs;
            if (revoked) { _holder = null; _holderName = ""; }
        }
        if (revoked)
        {
            Revoked?.Invoke("presence lost (deadman)");
            Changed?.Invoke(Snapshot());
        }
        return revoked;
    }

    private bool ClearIfHolder(Guid conn)
    {
        lock (_lock)
        {
            if (_holder != conn) return false;
            _holder = null; _holderName = "";
            return true;
        }
    }
}
