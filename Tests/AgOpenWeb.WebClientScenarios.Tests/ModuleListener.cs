// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace AgOpenWeb.WebClientScenarios.Tests;

/// <summary>
/// What the steer module and the machine module are told, read where they read it: UDP port
/// 8888 on the loopback, where AgOpenWeb's UdpCommunicationService always sends its PGNs (the rig
/// gives it no other network, so it sends nowhere else). The port is bound without sharing: a
/// module simulator or another harness already on it makes the rig fail at the start, instead of
/// the datagrams being split between the two. Keeps every change of PGN 254 (AutoSteer data: the status byte the
/// firmware steers on, the steer angle, the speed, sections 1-16) with its time.
/// </summary>
public sealed class ModuleListener : IDisposable
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    /// <summary>The harness's one clock, in milliseconds (the browsers' events are stamped with it too).</summary>
    public static long NowMs => Clock.ElapsedMilliseconds;

    public readonly record struct Steer(long Ms, int Status, double AngleDeg, double SpeedKmh, int Sections)
    {
        public bool Steering => Status != 0;
        public override string ToString() => string.Create(CultureInfo.InvariantCulture,
            $"status {Status}, angle {AngleDeg:0.0}°, speed {SpeedKmh:0.0} km/h, sections {Convert.ToString(Sections, 2).PadLeft(8, '0')}");
    }

    private readonly UdpClient _udp;
    private readonly object _lock = new();
    private readonly List<Steer> _changes = new();
    private Steer? _last;
    private long _lastHeardMs = -1;
    private Task? _loop;
    private readonly CancellationTokenSource _cts = new();

    public ModuleListener()
    {
        _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 8888));
    }

    public void Start() => _loop = Task.Run(() => LoopAsync(_cts.Token));

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await _udp.ReceiveAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }
            var d = r.Buffer;
            if (d.Length < 13 || d[0] != 0x80 || d[1] != 0x81 || d[3] != 254) continue;
            var s = new Steer(NowMs, d[7], BitConverter.ToInt16(d, 8) / 100.0, BitConverter.ToUInt16(d, 5) / 10.0, d[11] | (d[12] << 8));
            lock (_lock)
            {
                _lastHeardMs = s.Ms;
                if (_last is { } l && l.Status == s.Status && Math.Abs(l.AngleDeg - s.AngleDeg) < 0.05 && l.Sections == s.Sections
                    && Math.Abs(l.SpeedKmh - s.SpeedKmh) < 0.5)
                    continue;
                _last = s;
                _changes.Add(s);
            }
        }
    }

    /// <summary>The last PGN 254 heard (null before the first).</summary>
    public Steer? Last { get { lock (_lock) return _last; } }

    /// <summary>When the last PGN 254 arrived (any content), harness milliseconds.</summary>
    public long LastHeardMs { get { lock (_lock) return _lastHeardMs; } }

    /// <summary>Every change since <paramref name="fromMs"/>.</summary>
    public List<Steer> Since(long fromMs) { lock (_lock) return _changes.Where(c => c.Ms >= fromMs).ToList(); }

    /// <summary>The first PGN 254 at or after <paramref name="fromMs"/> that matches, or null.</summary>
    public Steer? First(long fromMs, Func<Steer, bool> match)
    {
        lock (_lock)
        {
            // The state in force at fromMs counts too: the last change before it.
            int i = _changes.FindLastIndex(c => c.Ms < fromMs);
            if (i >= 0 && match(_changes[i])) return _changes[i] with { Ms = fromMs };
            for (int k = i + 1; k < _changes.Count; k++)
                if (match(_changes[k])) return _changes[k];
            return null;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _udp.Dispose(); } catch { }
        try { _loop?.Wait(1000); } catch { }
    }
}
