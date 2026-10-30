// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using AgOpenWeb.Models.Timing;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services;

/// <summary>The time limits of one connection attempt and of the stream.</summary>
public sealed class NtripTimeouts
{
    /// <summary>Resolving the host and connecting.</summary>
    public TimeSpan Connect { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>Sending the request and reading the reply header.</summary>
    public TimeSpan Header { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>No data on an accepted connection for this long ends it (as the former stall watchdog did).</summary>
    public TimeSpan NoData { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>RTKLIB sends "ICY 200 OK" alone and then waits for the next epoch: after this pause the line is the whole header.</summary>
    public TimeSpan IcyGrace { get; init; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>
/// One attempt: resolve, connect, send the request, read the reply header and classify it.
/// A network problem never throws: <see cref="Outcome"/> says what happened (only cancellation
/// is an exception). When the caster accepts, the connection stays open and the caller reads the
/// body through <see cref="Reader"/>. The client and the Test button both open connections here.
/// </summary>
internal sealed class NtripConnection : IDisposable
{
    // A real header is well under 1 KB; the cap stops a caster, or a man in the middle, from
    // growing the buffer by streaming bytes without a blank line (#286).
    private const int MaxHeaderBytes = 8 * 1024;

    private Socket? _socket;
    private NetworkStream? _stream;
    private readonly bool _wantsSourcetable;   // asked for the list of mount points: that list is the answer

    public NtripOutcome Outcome { get; private set; } = new(NtripCategory.CasterUnreachable, "");
    public NtripVersion Version { get; }
    /// <summary>The bytes that arrived behind the header in the same reads.</summary>
    public byte[] InitialBody { get; private set; } = [];
    public TimedReader? Reader { get; private set; }

    private NtripConnection(NtripVersion version, bool wantsSourcetable)
    {
        Version = version;
        _wantsSourcetable = wantsSourcetable;
    }

    /// <summary>
    /// Opens a connection with <paramref name="first"/>; a caster that did not understand NTRIP 2.0
    /// gets the NTRIP 1.0 request once (<see cref="NtripOutcome.Ntrip2NotUnderstood"/>).
    /// </summary>
    /// <param name="gga">The position for the NTRIP 2.0 request header; returns null while unknown.</param>
    public static async Task<NtripConnection> OpenAsync(NtripConfiguration config, NtripVersion first,
        Func<string?> gga, NtripTimeouts timeouts, CancellationToken ct, bool sourcetable = false)
    {
        NtripConnection conn = await AttemptAsync(config, first, first == NtripVersion.V2 ? gga() : null, timeouts, ct, sourcetable).ConfigureAwait(false);
        if (first == NtripVersion.V2 && conn.Outcome.Ntrip2NotUnderstood)
        {
            conn.Dispose();
            conn = await AttemptAsync(config, NtripVersion.V1, null, timeouts, ct, sourcetable).ConfigureAwait(false);
        }
        return conn;
    }

    private static async Task<NtripConnection> AttemptAsync(NtripConfiguration config, NtripVersion version,
        string? gga, NtripTimeouts timeouts, CancellationToken ct, bool sourcetable)
    {
        var conn = new NtripConnection(version, sourcetable);
        try
        {
            await conn.RunAsync(config, gga, timeouts, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            conn.Dispose();
            throw;
        }
        catch (SocketException ex)
        {
            conn.Fail(ConnectionErrors.Classify(ex.SocketErrorCode, config.CasterAddress, config.CasterPort));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            conn.Fail(new NtripOutcome(NtripCategory.CasterUnreachable,
                config.CasterAddress + ":" + config.CasterPort + " closed the connection.", ex.GetType().Name));
        }
        catch
        {
            conn.Dispose();   // e.g. a port out of range: do not leave the socket open
            throw;
        }
        return conn;
    }

    private async Task RunAsync(NtripConfiguration c, string? gga, NtripTimeouts t, CancellationToken ct)
    {
        // The host is resolved on every attempt, so a caster that moves (dynamic DNS) is followed.
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            limit.CancelAfter(t.Connect);
            try
            {
                IPAddress ip = await ResolveAsync(c.CasterAddress, limit.Token).ConfigureAwait(false);
                _socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                await _socket.ConnectAsync(new IPEndPoint(ip, c.CasterPort), limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Fail(ConnectionErrors.Classify(SocketError.TimedOut, c.CasterAddress, c.CasterPort));
                return;
            }
        }

        _stream = new NetworkStream(_socket, ownsSocket: true);
        Reader = new TimedReader(_stream);
        await WriteAsync(NtripRequest.Build(c, Version, gga), ct).ConfigureAwait(false);

        // The header can arrive over several reads.
        var header = new List<byte>();
        long started = Clock.Current.GetTimestamp();
        while (true)
        {
            TimeSpan remaining = t.Header - TimeSpan.FromSeconds(Clock.Current.ElapsedSeconds(started, Clock.Current.GetTimestamp()));
            if (remaining <= TimeSpan.Zero)
            {
                Fail(ConnectionErrors.Classify(SocketError.TimedOut, c.CasterAddress, c.CasterPort));
                return;
            }
            bool icyOnly = NtripHeaderScanner.IsIcyStatusLineOnly(CollectionsMarshal.AsSpan(header));
            int n = await Reader.ReadAsync(icyOnly && t.IcyGrace < remaining ? t.IcyGrace : remaining, ct).ConfigureAwait(false);
            if (n < 0)
            {
                if (icyOnly) { Complete(header.ToArray(), header.Count, header.Count); return; }
                continue;
            }
            if (n == 0)
            {
                // Closed before the header was complete: judge what there is.
                if (header.Count == 0)
                    Fail(new NtripOutcome(NtripCategory.CasterUnreachable, c.CasterAddress + ":" + c.CasterPort + " closed the connection without answering."));
                else
                    Complete(header.ToArray(), header.Count, header.Count);
                return;
            }
            header.AddRange(Reader.Buffer.AsSpan(0, n));
            if (NtripHeaderScanner.TryFindEnd(CollectionsMarshal.AsSpan(header), out int headerLength, out int bodyOffset))
            {
                Complete(header.ToArray(), headerLength, bodyOffset);
                return;
            }
            // The cap is for a header that does not end. A read that holds a whole header and a large body
            // (a source table) was found above and is not held to it.
            if (header.Count > MaxHeaderBytes)
            {
                Fail(new NtripOutcome(NtripCategory.NotUnderstood, "The caster sent an invalid reply."));
                return;
            }
        }
    }

    /// <summary>A literal address, else the host's IPv4 address when it has one (else its first).</summary>
    private static async Task<IPAddress> ResolveAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out IPAddress? ip)) return ip;
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
            ?? addresses.FirstOrDefault()
            ?? throw new SocketException((int)SocketError.HostNotFound);
    }

    private void Complete(byte[] all, int headerLength, int bodyOffset)
    {
        byte[] body = all[bodyOffset..];
        string header = Encoding.ASCII.GetString(all, 0, headerLength);
        Outcome = NtripResponse.Classify(header, body);
        // Asked for the list of mount points, the list is the answer; elsewhere it means the mount point does not exist.
        if (_wantsSourcetable && NtripResponse.IsSourcetable(header, body))
            Outcome = new NtripOutcome(NtripCategory.Accepted, "", Outcome.FirstLine, Outcome.Chunked);
        if (Outcome.IsAccepted) InitialBody = body;
        else Close();
    }

    private void Fail(NtripOutcome outcome)
    {
        Outcome = outcome;
        Close();
    }

    public Task WriteAsync(string text, CancellationToken ct = default) =>
        _stream!.WriteAsync(Encoding.ASCII.GetBytes(text), ct).AsTask();

    private void Close()
    {
        Reader?.Abandon();
        if (_stream != null) _stream.Dispose();
        else _socket?.Dispose();
    }

    public void Dispose() => Close();
}
