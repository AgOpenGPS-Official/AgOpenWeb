// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;

namespace AgOpenWeb.WebClientScenarios.Tests;

/// <summary>
/// One screen's Wi-Fi: a TCP relay on the loopback between that browser and the AgOpenWeb web
/// server. Every connection the browser opens (the page, the WebSocket) goes through it.
/// <list type="bullet">
/// <item><see cref="LinkMode.Up"/>: bytes pass both ways.</item>
/// <item><see cref="LinkMode.Silent"/>: out of range. Nothing passes, but no side is told: the
/// sockets stay open and what is sent waits, as TCP keeps retrying over a dead radio link. When
/// the link comes back the waiting bytes arrive, in order.</item>
/// <item><see cref="LinkMode.Cut"/>: the access point drops the client. Every open connection is
/// reset and new ones are refused until the link is Up again.</item>
/// </list>
/// </summary>
public sealed class LinkRelay : IDisposable
{
    private readonly TcpListener _listener;
    private readonly int _target;
    private readonly object _lock = new();
    private readonly List<Pipe> _pipes = new();
    private readonly CancellationTokenSource _cts = new();
    private LinkMode _mode = LinkMode.Up;

    public int Port { get; }

    public LinkRelay(int targetPort)
    {
        _target = targetPort;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    public LinkMode Mode
    {
        get { lock (_lock) return _mode; }
        set
        {
            List<Pipe> pipes;
            lock (_lock) { _mode = value; pipes = _pipes.ToList(); }
            foreach (var p in pipes)
            {
                if (value == LinkMode.Cut) p.Reset();
                else p.Pulse();
            }
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { break; }
            if (Mode == LinkMode.Cut) { Abort(client); continue; }
            var server = new TcpClient();
            try { await server.ConnectAsync(IPAddress.Loopback, _target); }
            catch { Abort(client); server.Dispose(); continue; }
            client.NoDelay = true; server.NoDelay = true;
            var pipe = new Pipe(this, client, server);
            lock (_lock) _pipes.Add(pipe);
            pipe.Start();
        }
    }

    private static void Abort(TcpClient c)
    {
        try { c.Client.LingerState = new LingerOption(true, 0); c.Close(); } catch { }
    }

    private void Remove(Pipe p) { lock (_lock) _pipes.Remove(p); }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        List<Pipe> pipes;
        lock (_lock) pipes = _pipes.ToList();
        foreach (var p in pipes) p.Reset();
    }

    /// <summary>One connection: two pumps that wait while the link is Silent.</summary>
    private sealed class Pipe
    {
        private readonly LinkRelay _relay;
        private readonly TcpClient _a, _b;
        private readonly SemaphoreSlim _gate = new(0);
        private int _closed;

        public Pipe(LinkRelay relay, TcpClient a, TcpClient b) { _relay = relay; _a = a; _b = b; }

        public void Start()
        {
            _ = Task.Run(() => PumpAsync(_a.GetStream(), _b.GetStream()));
            _ = Task.Run(() => PumpAsync(_b.GetStream(), _a.GetStream()));
        }

        public void Pulse() { try { _gate.Release(2); } catch { } }

        private async Task PumpAsync(NetworkStream from, NetworkStream to)
        {
            var buf = new byte[64 * 1024];
            try
            {
                while (true)
                {
                    int n = await from.ReadAsync(buf);
                    if (n <= 0) break;
                    // Out of range: hold this chunk until the link is back (or cut).
                    while (_relay.Mode == LinkMode.Silent) await _gate.WaitAsync(200);
                    if (_relay.Mode == LinkMode.Cut) break;
                    await to.WriteAsync(buf.AsMemory(0, n));
                }
            }
            catch { }
            Close();
        }

        public void Reset()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            Abort(_a); Abort(_b);
            _relay.Remove(this);
        }

        private void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            try { _a.Close(); } catch { }
            try { _b.Close(); } catch { }
            _relay.Remove(this);
        }
    }
}

public enum LinkMode { Up, Silent, Cut }
