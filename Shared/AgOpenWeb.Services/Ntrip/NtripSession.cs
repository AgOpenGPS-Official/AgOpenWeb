// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Globalization;
using AgOpenWeb.Models.Timing;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services;

/// <summary>Receives the stream's bytes (chunking not yet removed). The span is only valid during the call.</summary>
public delegate void NtripBodyHandler(ReadOnlySpan<byte> data);

/// <summary>
/// One NTRIP service from start to stop: connects, streams, and on any failure classifies it,
/// waits out the backoff and tries again, until stopped or until retrying cannot help (wrong
/// login or mount point). It only moves bytes and reports status; framing and forwarding the
/// RTCM stay with the caller. Runs on its own task, never on the UI thread.
/// </summary>
public sealed class NtripSession
{
    // Seconds to wait after consecutive failed attempts; the last step repeats. A small glitch
    // recovers quickly and a long outage does not hammer the caster.
    public static readonly int[] DefaultBackoff = [1, 2, 4, 8, 15];

    private readonly NtripConfiguration _config;
    private readonly NtripBodyHandler _onBody;
    private readonly Func<bool> _correctionsSeen;
    private readonly Func<string?> _gga;
    private readonly object _lock = new();
    private NtripSessionStatus _status = NtripSessionStatus.Idle;
    private NtripConnection? _open;
    private int _attempts;
    private long _lastData;
    private long _lastGga;
    private bool _streaming;

    /// <summary>Called when a stream starts, with whether the body is HTTP chunked.</summary>
    public Action<bool>? StreamStarted { get; init; }
    public NtripTimeouts Timeouts { get; init; } = new();
    /// <summary>Replaces <see cref="Task.Delay(TimeSpan, CancellationToken)"/> in tests.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;
    /// <summary>The task that completes when the network changes; it ends a backoff wait early (#385).</summary>
    public Func<Task>? NetworkChanged { get; init; }
    /// <summary>The NTRIP version that delivered corrections, per service, so a reconnect skips the probe.</summary>
    public ConcurrentDictionary<string, NtripVersion>? VersionMemory { get; init; }
    /// <summary>Seconds to wait after consecutive failed attempts, the last one repeating.</summary>
    public int[] Backoff { get; init; } = DefaultBackoff;

    public event Action<NtripSessionStatus>? StatusChanged;

    /// <param name="correctionsSeen">Whether the caller has framed any corrections yet; it tells "nothing came" from "it stopped".</param>
    /// <param name="gga">A complete GGA sentence for the caster, or null while the position is unknown. Not asked for when the config's GGA interval is 0.</param>
    public NtripSession(NtripConfiguration config, NtripBodyHandler onBody, Func<bool> correctionsSeen, Func<string?> gga)
    {
        _config = config;
        _onBody = onBody;
        _correctionsSeen = correctionsSeen;
        _gga = () =>
        {
            if (config.GgaIntervalSeconds <= 0) return null;
            try { return gga(); }
            catch (Exception) { return null; }   // a position that cannot be made must not end the session
        };
    }

    public NtripSessionStatus Status
    {
        get { lock (_lock) return _status; }
    }

    /// <summary>Runs until cancelled or until the caster keeps rejecting the login or mount point.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        string where = $"{_config.CasterAddress}:{_config.CasterPort.ToString(CultureInfo.InvariantCulture)}/{_config.MountPoint}";
        try
        {
            while (!ct.IsCancellationRequested)
            {
                NtripVersion first = VersionMemory != null && VersionMemory.TryGetValue(where, out NtripVersion known) ? known : NtripVersion.V2;
                Update(s => s with { State = NtripState.Connecting, Message = $"Connecting to {where}…", RetryInSeconds = 0, Version = first });

                NtripOutcome outcome;
                using (NtripConnection conn = await NtripConnection.OpenAsync(_config, first, _gga, Timeouts, ct).ConfigureAwait(false))
                {
                    outcome = conn.Outcome;
                    if (outcome.IsAccepted)
                    {
                        Update(s => s with { State = NtripState.Connected, Message = $"Connected to {where}", LastError = null, FirstLine = outcome.FirstLine, Version = conn.Version });
                        outcome = await StreamAsync(conn, where, ct).ConfigureAwait(false);
                    }
                }

                _attempts++;
                double wait = Backoff[Math.Min(_attempts, Backoff.Length) - 1];
                if (outcome.IsFinal)
                {
                    Update(s => s with { State = NtripState.GaveUp, Message = outcome.Message, LastError = outcome.Category, FirstLine = outcome.FirstLine, Attempts = _attempts, RetryInSeconds = 0 });
                    return;
                }

                Update(s => s with
                {
                    State = NtripState.Waiting,
                    Message = $"{outcome.Message} Trying again in {wait.ToString("0", CultureInfo.InvariantCulture)} s.",
                    LastError = outcome.Category,
                    FirstLine = outcome.FirstLine,
                    Attempts = _attempts,
                    RetryInSeconds = wait,
                });
                await WaitAsync(TimeSpan.FromSeconds(wait), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped by the caller.
        }
        catch (Exception ex)
        {
            // A setting no connection can use (a port out of range): say so instead of ending silently.
            Update(s => s with { State = NtripState.GaveUp, Message = "Cannot connect: " + ex.Message, LastError = NtripCategory.NotUnderstood, RetryInSeconds = 0 });
        }
        finally
        {
            if (Status.State != NtripState.GaveUp)
                Update(s => s with { State = NtripState.Stopped, Message = "Stopped", RetryInSeconds = 0 });
        }
    }

    /// <summary>Sends text to the caster, for a position the caller made itself. Nothing is sent without an open connection.</summary>
    public async Task SendAsync(string text)
    {
        NtripConnection? conn = _open;
        if (conn == null) return;
        try
        {
            await conn.WriteAsync(text).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The read side reports the broken connection.
        }
    }

    // Reads the body until the stream ends, goes silent or the caller cancels. Returns why it ended.
    private async Task<NtripOutcome> StreamAsync(NtripConnection conn, string where, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        StreamStarted?.Invoke(conn.Outcome.Chunked);
        _streaming = false;
        _lastData = _lastGga = Clock.Current.GetTimestamp();
        _open = conn;
        try
        {
            // NTRIP 1.0 has no request header for the position: send it right after the answer.
            if (conn.Version == NtripVersion.V1) await SendGgaAsync(conn, ct).ConfigureAwait(false);

            if (conn.InitialBody.Length > 0 && Deliver(conn.InitialBody, conn.Version, where) is { } failed) return failed;

            TimeSpan ggaEvery = TimeSpan.FromSeconds(_config.GgaIntervalSeconds);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                TimeSpan wait = Timeouts.NoData - Since(_lastData);
                if (ggaEvery > TimeSpan.Zero && ggaEvery - Since(_lastGga) < wait) wait = ggaEvery - Since(_lastGga);

                int n;
                try { n = await conn.Reader!.ReadAsync(wait, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    ct.ThrowIfCancellationRequested();   // closed by Stop, not by the caster
                    n = 0;
                }

                if (n > 0)
                {
                    ct.ThrowIfCancellationRequested();   // a stopped session hands nothing on
                    if (Deliver(conn.Reader!.Buffer.AsSpan(0, n), conn.Version, where) is { } lost) return lost;
                }
                else if (n == 0)
                {
                    return _correctionsSeen()
                        ? new NtripOutcome(NtripCategory.StreamLost, "The caster closed the connection.")
                        : new NtripOutcome(NtripCategory.NoCorrections, "The caster closed the connection before sending corrections (mount point or account rejected?).");
                }

                if (ggaEvery > TimeSpan.Zero && Since(_lastGga) >= ggaEvery) await SendGgaAsync(conn, ct).ConfigureAwait(false);

                if (Since(_lastData) >= Timeouts.NoData)
                {
                    return _correctionsSeen()
                        ? new NtripOutcome(NtripCategory.StreamLost, $"No corrections for {Since(_lastData).TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s.")
                        : new NtripOutcome(NtripCategory.NoCorrections, "Connected, but nothing is coming in. Some casters need your position: check that the GPS position is available.");
                }
            }
        }
        finally
        {
            _open = null;
        }
    }

    // Hands the bytes on. Null when that worked; when the caller's handler throws, the stream
    // counts as lost and is retried, so a bug there does not end the session for good.
    private NtripOutcome? Deliver(ReadOnlySpan<byte> data, NtripVersion version, string where)
    {
        _lastData = Clock.Current.GetTimestamp();
        try
        {
            _onBody(data);
        }
        catch (Exception ex)
        {
            return new NtripOutcome(NtripCategory.StreamLost, "Handling the corrections failed: " + ex.Message);
        }
        if (_streaming || !_correctionsSeen()) return null;

        _streaming = true;
        _attempts = 0;   // corrections flow: the next outage starts at the first backoff step
        if (VersionMemory != null) VersionMemory[where] = version;
        Update(s => s with { State = NtripState.Streaming, LastError = null, Attempts = 0 });
        return null;
    }

    private async Task SendGgaAsync(NtripConnection conn, CancellationToken ct)
    {
        _lastGga = Clock.Current.GetTimestamp();
        string? gga = _gga();
        if (gga == null) return;
        try
        {
            await conn.WriteAsync(gga + "\r\n", ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The read side reports the broken connection.
        }
    }

    private async Task WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await Task.WhenAny(Delay(delay, stop.Token), NetworkChanged?.Invoke() ?? Task.Delay(Timeout.Infinite, stop.Token)).ConfigureAwait(false);
        stop.Cancel();   // ends the wait that is left
        ct.ThrowIfCancellationRequested();
    }

    private static TimeSpan Since(long timestamp) =>
        TimeSpan.FromSeconds(Clock.Current.ElapsedSeconds(timestamp, Clock.Current.GetTimestamp()));

    private void Update(Func<NtripSessionStatus, NtripSessionStatus> change)
    {
        NtripSessionStatus next;
        lock (_lock) next = _status = change(_status);
        StatusChanged?.Invoke(next);
    }
}
