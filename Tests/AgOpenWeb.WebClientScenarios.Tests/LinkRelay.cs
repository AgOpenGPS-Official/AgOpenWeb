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
/// sockets stay open and the relay stops reading them, so what is sent waits in the sockets'
/// buffers, as TCP keeps retrying over a dead radio link. The buffers are small
/// (<see cref="BufferBytes"/>), as a radio link's are, so a sender that keeps sending soon has
/// its sends wait too. When the link comes back the waiting bytes arrive, in order.</item>
/// <item><see cref="LinkMode.Cut"/>: the access point drops the tablet. The browser's
/// connections are reset (its Wi-Fi tells it), new ones are refused until the link is Up again,
/// and the server is told nothing: its side of each connection stays open and is never read
/// again, as a server's socket to a client that left the network goes quiet. Those sockets are
/// closed only when the relay is disposed.</item>
/// </list>
/// </summary>
public sealed class LinkRelay : IDisposable
{
    /// <summary>The send and receive buffers of the relay's sockets: about what a Wi-Fi link holds
    /// in flight, not the megabytes a loopback socket grows to.</summary>
    public const int BufferBytes = 32 * 1024;

    private readonly TcpListener _listener;
    private readonly int _target;
    private readonly object _lock = new();
    private readonly List<Pipe> _pipes = new();
    private readonly List<TcpClient> _held = new();
    private readonly CancellationTokenSource _cts = new();
    private LinkMode _mode = LinkMode.Up;

    public int Port { get; }

    public LinkRelay(int targetPort)
    {
        _target = targetPort;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        // Accepted sockets take these from the listener.
        _listener.Server.ReceiveBufferSize = BufferBytes;
        _listener.Server.SendBufferSize = BufferBytes;
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
                if (value == LinkMode.Cut) p.DropBrowser();
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
            // The server side's buffers are set before connecting, so the window it offers the
            // server is small from the start.
            var server = new TcpClient(AddressFamily.InterNetwork);
            server.ReceiveBufferSize = BufferBytes;
            server.SendBufferSize = BufferBytes;
            try { await server.ConnectAsync(IPAddress.Loopback, _target); }
            catch { Abort(client); server.Dispose(); continue; }
            client.ReceiveBufferSize = BufferBytes;
            client.SendBufferSize = BufferBytes;
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

    private void Hold(TcpClient server) { lock (_lock) _held.Add(server); }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        List<Pipe> pipes;
        List<TcpClient> held;
        lock (_lock) { pipes = _pipes.ToList(); held = _held.ToList(); _held.Clear(); }
        foreach (var p in pipes) p.Reset();
        foreach (var h in held) Abort(h);
    }

    /// <summary>One connection: two pumps that stop reading while the link is Silent.</summary>
    private sealed class Pipe
    {
        private readonly LinkRelay _relay;
        private readonly TcpClient _browser, _server;
        private readonly SemaphoreSlim _gate = new(0);
        private int _closed;

        public Pipe(LinkRelay relay, TcpClient browser, TcpClient server) { _relay = relay; _browser = browser; _server = server; }

        public void Start()
        {
            _ = Task.Run(() => PumpAsync(_browser.GetStream(), _server.GetStream()));
            _ = Task.Run(() => PumpAsync(_server.GetStream(), _browser.GetStream()));
        }

        public void Pulse() { try { _gate.Release(2); } catch { } }

        private async Task PumpAsync(NetworkStream from, NetworkStream to)
        {
            // Small chunks: a read that was already waiting when the link went silent takes at
            // most this much more out of the sender's way.
            var buf = new byte[16 * 1024];
            try
            {
                while (true)
                {
                    // Out of range: nothing is read, so the sender's bytes stay in its socket.
                    await WhileSilentAsync();
                    if (_relay.Mode == LinkMode.Cut) break;
                    int n = await from.ReadAsync(buf);
                    if (n <= 0) break;
                    // A chunk read just before the link went: hold it until the link is back.
                    await WhileSilentAsync();
                    if (_relay.Mode == LinkMode.Cut) break;
                    await to.WriteAsync(buf.AsMemory(0, n));
                }
            }
            catch { }
            if (_relay.Mode == LinkMode.Cut) DropBrowser();
            else Close();
        }

        private async Task WhileSilentAsync()
        {
            while (_relay.Mode == LinkMode.Silent) await _gate.WaitAsync(200);
        }

        /// <summary>The access point drops the tablet: the browser's side is reset, the server's
        /// side stays open and unread.</summary>
        public void DropBrowser()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            _relay.Hold(_server);
            Abort(_browser);
            _relay.Remove(this);
        }

        /// <summary>Both sides reset (the relay is disposed).</summary>
        public void Reset()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            Abort(_browser); Abort(_server);
            _relay.Remove(this);
        }

        private void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            try { _browser.Close(); } catch { }
            try { _server.Close(); } catch { }
            _relay.Remove(this);
        }
    }
}

public enum LinkMode { Up, Silent, Cut }
