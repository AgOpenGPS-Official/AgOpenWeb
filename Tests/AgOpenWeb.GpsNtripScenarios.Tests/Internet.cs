// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace AgOpenWeb.GpsNtripScenarios.Tests;

/// <summary>
/// The tractor's internet, between AgOpenWeb and the casters: a TCP relay on the loopback.
/// AgOpenWeb's NTRIP profile points at the relay; the relay forwards to a caster. What a
/// mobile connection does when it fails is what the scenarios set:
/// <list type="bullet">
/// <item><see cref="Drop"/>: coverage lost. Nothing passes either way and nobody is told,
/// as on a real link (no FIN, no reset). A new connection is accepted locally but gets no
/// answer. When the link comes back, what the caster sent meanwhile arrives at once, as TCP
/// delivers it after retransmitting; a connection that waited longer than
/// <see cref="CasterGivesUpAfter"/> was dropped by the caster and is reset instead.</item>
/// <item><see cref="NoNetwork"/>: no network at all (Wi-Fi off): connections are refused at once.</item>
/// <item><see cref="SwitchNetwork"/>: the tablet moves from the tractor's Wi-Fi to the phone's
/// hotspot. Connections made before are dead for good (their packets go to an address that is
/// gone) and new ones work at once.</item>
/// </list>
/// </summary>
public sealed class Internet : IDisposable
{
    public enum State { Up, Down, NoNetwork }

    private readonly object _lock = new();
    private readonly List<Pipe> _pipes = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private TcpListener? _listener;
    private CancellationTokenSource _cts = new();
    private int _generation;   // bumped by a network switch: older pipes are dead
    private double _downSince = double.NaN;

    public int Port { get; }
    public int TargetPort { get; set; }
    public State Now { get; private set; } = State.Up;
    /// <summary>How long a caster keeps a silent client before it drops it (its TCP gives up).</summary>
    public TimeSpan CasterGivesUpAfter { get; set; } = TimeSpan.FromSeconds(60);
    public int Connections => Volatile.Read(ref _connections);
    private int _connections;
    public System.Collections.Concurrent.ConcurrentQueue<string> Log { get; } = new();

    public Internet(int targetPort)
    {
        TargetPort = targetPort;
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        Port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        Listen();
    }

    private void Note(string text) =>
        Log.Enqueue(string.Create(CultureInfo.InvariantCulture, $"{_clock.Elapsed.TotalSeconds,7:0.0} s  internet: {text}"));

    private void Listen()
    {
        lock (_lock)
        {
            if (_listener != null) return;
            _cts = new CancellationTokenSource();
            var l = new TcpListener(IPAddress.Loopback, Port);
            l.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            l.Start();
            _listener = l;
            var ct = _cts.Token;
            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    TcpClient inbound;
                    try { inbound = await l.AcceptTcpClientAsync(ct); }
                    catch { return; }
                    Interlocked.Increment(ref _connections);
                    var p = new Pipe(this, inbound, _generation);
                    lock (_lock) _pipes.Add(p);
                    _ = Task.Run(p.RunAsync);
                }
            });
        }
    }

    private void StopListening()
    {
        lock (_lock)
        {
            if (_listener == null) return;
            _cts.Cancel();
            _listener.Stop();
            _listener = null;
        }
    }

    /// <summary>Coverage lost: nothing passes, nobody is told.</summary>
    public void Drop()
    {
        Listen();
        lock (_lock) { Now = State.Down; _downSince = _clock.Elapsed.TotalSeconds; }
        Note("down (packets lost)");
    }

    /// <summary>No network at all: new connections are refused at once; open ones are as in <see cref="Drop"/>.</summary>
    public void NoNetwork()
    {
        lock (_lock) { Now = State.NoNetwork; _downSince = _clock.Elapsed.TotalSeconds; }
        StopListening();
        Note("no network (connections refused)");
    }

    /// <summary>The link is back.</summary>
    public void Restore()
    {
        double downFor;
        lock (_lock)
        {
            downFor = double.IsNaN(_downSince) ? 0 : _clock.Elapsed.TotalSeconds - _downSince;
            Now = State.Up;
            _downSince = double.NaN;
        }
        Listen();
        Note(string.Create(CultureInfo.InvariantCulture, $"up again after {downFor:0.0} s"));
    }

    /// <summary>Wi-Fi to phone hotspot: the connections made so far are dead for good; new ones work.</summary>
    public void SwitchNetwork()
    {
        lock (_lock) _generation++;
        Note("network switched: older connections are dead");
    }

    /// <summary>Seconds the link has been down, 0 when up.</summary>
    public double DownFor
    {
        get { lock (_lock) return double.IsNaN(_downSince) ? 0 : _clock.Elapsed.TotalSeconds - _downSince; }
    }

    private bool Passes(int generation) { lock (_lock) return Now == State.Up && generation == _generation; }

    private sealed class Pipe
    {
        private readonly Internet _net;
        private readonly TcpClient _in;
        private readonly int _generation;
        private TcpClient? _out;
        private volatile bool _closed;

        public Pipe(Internet net, TcpClient inbound, int generation)
        {
            _net = net;
            _in = inbound;
            _generation = generation;
        }

        public async Task RunAsync()
        {
            try
            {
                // A connection opened while the link is down waits, as a SYN that is retransmitted.
                while (!_net.Passes(_generation))
                {
                    if (_closed || _net._generation != _generation) { await DeadUntilClosedAsync(); return; }
                    await Task.Delay(50);
                }
                _out = new TcpClient();
                try { await _out.ConnectAsync(IPAddress.Loopback, _net.TargetPort); }
                catch
                {
                    // The caster is not there: the remote end refuses, so does the relay.
                    Reset(_in);
                    return;
                }
                var up = Copy(_in, _out, toCaster: true);
                var down = Copy(_out, _in, toCaster: false);
                await Task.WhenAny(up, down);
            }
            catch { }
            finally
            {
                Close();
            }
        }

        // A connection from before a network switch: its packets go nowhere, for good. It ends
        // when AgOpenWeb closes it (nothing reaches the caster either way).
        private async Task DeadUntilClosedAsync()
        {
            var buf = new byte[4096];
            try
            {
                while (true)
                {
                    int n = await _in.GetStream().ReadAsync(buf);
                    if (n <= 0) return;
                }
            }
            catch { }
        }

        private async Task Copy(TcpClient from, TcpClient to, bool toCaster)
        {
            var buf = new byte[16384];
            var src = from.GetStream();
            var dst = to.GetStream();
            while (!_closed)
            {
                // While the link is down nothing is read: the data waits in the sender's
                // buffers, as TCP holds it for retransmission.
                if (!_net.Passes(_generation))
                {
                    if (_net._generation != _generation)
                    {
                        // Switched network: the caster's side times out on its own later;
                        // AgOpenWeb's side hears nothing until it gives up itself.
                        if (!toCaster) { try { to.Client.Shutdown(SocketShutdown.Send); } catch { } }
                        await DeadUntilClosedAsync();
                        return;
                    }
                    if (_net.DownFor > _net.CasterGivesUpAfter.TotalSeconds && _out != null && _out.Connected)
                    {
                        // The caster dropped this silent client; AgOpenWeb learns it when the link returns.
                        try { _out.Close(); } catch { }
                        while (!_net.Passes(_generation) && !_closed && _net._generation == _generation) await Task.Delay(50);
                        Reset(_in);
                        return;
                    }
                    await Task.Delay(50);
                    continue;
                }
                // A read that completes while the link is down is held back until it is up.
                int n = await src.ReadAsync(buf);
                if (n <= 0) return;
                while (!_net.Passes(_generation))
                {
                    if (_net._generation != _generation) { await DeadUntilClosedAsync(); return; }
                    if (_net.DownFor > _net.CasterGivesUpAfter.TotalSeconds)
                    {
                        try { _out?.Close(); } catch { }
                        while (!_net.Passes(_generation) && !_closed && _net._generation == _generation) await Task.Delay(50);
                        Reset(_in);
                        return;
                    }
                    await Task.Delay(50);
                }
                await dst.WriteAsync(buf.AsMemory(0, n));
            }
        }

        private static void Reset(TcpClient c)
        {
            try { c.Client.LingerState = new LingerOption(true, 0); } catch { }
            try { c.Close(); } catch { }
        }

        public void Close()
        {
            _closed = true;
            try { _in.Close(); } catch { }
            try { _out?.Close(); } catch { }
        }
    }

    public void Dispose()
    {
        StopListening();
        List<Pipe> all;
        lock (_lock) { all = _pipes.ToList(); _pipes.Clear(); }
        foreach (var p in all) p.Close();
    }
}
