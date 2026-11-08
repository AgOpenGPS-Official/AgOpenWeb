// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AgOpenWeb.IsobusScenarios.Tests;

/// <summary>
/// An ISOBUS implement on the task controller's bus, as far as the task controller and
/// AgOpenWeb can tell: a name, a section count, the sections it really has on (its actual
/// condensed work state, what PGN 240 carries), and how it behaves. In automatic section
/// control its sections follow the setpoints the task controller forwards from PGN 229, each
/// change taking <see cref="ValveDelayMs"/> (a valve, a clutch); a <see cref="Stuck"/> section
/// never comes on (a broken valve). In manual its sections are whatever its own switches say
/// (<see cref="ManualBits"/>).
/// </summary>
public sealed class Implement
{
    public string Name = "Implement";
    public int Sections = 8;
    public double WidthM = 12;
    public int ValveDelayMs = 300;
    public ulong Stuck;
    public ulong ManualBits;
    /// <summary>Its sections now, section 1 in bit 0.</summary>
    public ulong Actual;

    internal ulong Pending;
    internal long PendingAtMs = -1;
    internal bool Known;   // the task controller has sent its "Implement:" message

    public Implement(string name, int sections, double widthM = 12)
    {
        Name = name;
        Sections = sections;
        WidthM = widthM;
    }

    public ulong Mask => Sections >= 64 ? ulong.MaxValue : (1UL << Sections) - 1;
}

/// <summary>
/// AOG-TaskController as AgOpenWeb sees it, on the loopback: it listens on 127.0.0.1:8888 for
/// the program's frames and sends to 127.0.0.1:9999 with source 0x80, as the real one (its
/// <c>docs/PROTOCOL.md</c> and <c>src/app.cpp</c>, 2026-11): every 100 ms one PGN 240 per
/// implement with sections (mode, section count, the implement's actual section states) or,
/// with no such implement and <see cref="BeaconEnabled"/> (<c>aogHeartbeatEnabled</c>), a bare
/// {0, 0} beacon; PGN 221 hardware messages when an implement registers or is lost; it takes
/// PGN 229 (the 64 section setpoints), PGN 241 (section control mode) and PGN 242 (process
/// data) from source 0x7F only, and PGN 214 (fix quality) from source 0x7C; it does not check
/// the checksum (the real one has that code commented out) but this one counts the bad ones.
///
/// What the harness can do to it: stop and restart the process (its clients come back with
/// section control off, as after any restart), cut its network (nothing in or out, the
/// implements keep their state), connect and disconnect implements, make it ignore or delay a
/// mode request (an implement that refuses section control, or takes its time), switch the
/// mode itself (the operator did it on the terminal), slow or stop its heartbeat, and send any
/// raw frame.
/// </summary>
public sealed class EmulatedTaskController : IDisposable
{
    public const byte Source = 0x80;
    public const int HeartbeatIntervalMs = 100;
    private static readonly IPEndPoint HostEp = new(IPAddress.Loopback, 9999);

    private readonly object _lock = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private UdpClient? _udp;
    private Thread? _rx;
    private Timer? _tick;
    private volatile bool _disposed;
    private long _lastHeartbeatMs = -1000;

    /// <summary>The process is up (receives, sends, drives the implements).</summary>
    public bool Running { get; private set; }
    /// <summary>Its network: false = nothing in or out.</summary>
    public bool NetworkUp { get; set; } = true;
    /// <summary><c>aogHeartbeatEnabled</c>: the {0, 0} beacon with no implement.</summary>
    public bool BeaconEnabled { get; set; } = true;
    /// <summary>How often it sends PGN 240 (100 ms in the real one; 0 = never).</summary>
    public int HeartbeatMs { get; set; } = HeartbeatIntervalMs;
    /// <summary>Section control on the implements: automatic (true) or manual.</summary>
    public bool SectionControlEnabled { get; set; }

    public enum ModeRequestHandling { Obey, Ignore, ObeyLate }
    /// <summary>What it does with PGN 241.</summary>
    public ModeRequestHandling OnModeRequest { get; set; } = ModeRequestHandling.Obey;
    public int ObeyLateMs { get; set; } = 1500;
    private long _obeyAtMs = -1;
    private bool _obeyValue;

    private readonly List<Implement> _implements = new();
    /// <summary>The implements on its bus now (a copy).</summary>
    public Implement[] Implements { get { lock (_lock) return _implements.ToArray(); } }

    // ---------------------------------------------------------------- what came in
    public readonly Dictionary<byte, int> Received = new();
    public readonly Dictionary<byte, long> LastReceivedMs = new();
    public int ChecksumErrors { get; private set; }
    public int ShortPackets { get; private set; }
    /// <summary>Frames with a source the task controller doesn't take (not 0x7F / 0x7C for PGN 214).</summary>
    public int OtherSourceDropped { get; private set; }
    /// <summary>The last PGN 229: the 64 setpoints, when, and how often they changed.</summary>
    public ulong LastSetpoints { get; private set; }
    public long LastSetpointsMs { get; private set; } = -1;
    public int SetpointChanges { get; private set; }
    /// <summary>Every PGN 241: when (its clock) and what was asked.</summary>
    public readonly List<(long Ms, bool Enabled)> ModeRequests = new();
    public string? LastSubnet { get; private set; }
    /// <summary>PGN 242 process data: DDI and value, newest last.</summary>
    public readonly List<(ushort Ddi, int Value)> ProcessData = new();
    /// <summary>Hardware messages (PGN 221) it sent: text, seconds, alert.</summary>
    public readonly List<(string Text, int Seconds, bool Alert)> MessagesSent = new();
    public int HeartbeatsSent { get; private set; }
    public int Restarts { get; private set; }
    /// <summary>Its own log, newest last.</summary>
    public readonly List<string> Log = new();

    public long NowMs => _clock.ElapsedMilliseconds;
    private bool Online => Running && NetworkUp;

    public void Start()
    {
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Loopback, 8888));
        _rx = new Thread(ReceiveLoop) { IsBackground = true, Name = "task controller rx" };
        _rx.Start();
        _tick = new Timer(_ => Tick(), null, 50, 50);
        lock (_lock) { Running = true; Note("started"); }
    }

    /// <summary>The process stops (killed, crashed, the HAT rebooted): no heartbeat, nothing read.</summary>
    public void Stop()
    {
        lock (_lock) { Running = false; Note("stopped"); }
    }

    /// <summary>The process starts again: section control off for every client, no setpoints,
    /// the implements register again (each gets its "Implement:" message).</summary>
    public void Restart()
    {
        lock (_lock)
        {
            Running = true;
            Restarts++;
            SectionControlEnabled = false;
            LastSetpoints = 0;
            LastSetpointsMs = -1;
            _obeyAtMs = -1;
            foreach (var i in _implements) { i.Known = false; i.Pending = 0; i.PendingAtMs = -1; }
            Note("restarted");
        }
    }

    /// <summary>An implement registers with the task controller (plugged in, powered on).</summary>
    public void Connect(Implement implement)
    {
        lock (_lock)
        {
            if (!_implements.Contains(implement)) _implements.Add(implement);
            implement.Known = false;
            Note($"implement {implement.Name} ({implement.Sections} sections) registering");
        }
    }

    /// <summary>An implement leaves the bus (unplugged, powered off).</summary>
    public void Disconnect(Implement implement)
    {
        lock (_lock)
        {
            _implements.Remove(implement);
            if (Running) SendHardwareMessage("Implement lost: " + implement.Name, 10, alert: true);
            Note($"implement {implement.Name} lost");
        }
    }

    public int Count(byte pgn) { lock (_lock) return Received.TryGetValue(pgn, out var n) ? n : 0; }

    /// <summary>Count of PGN 241 since the given clock time.</summary>
    public int ModeRequestsSince(long ms) { lock (_lock) return ModeRequests.Count(r => r.Ms >= ms); }

    // ---------------------------------------------------------------- frames

    /// <summary>A framed packet: 0x80 0x81, source, PGN, length, payload, checksum (sum of
    /// source..last payload byte).</summary>
    public static byte[] Frame(byte src, byte pgn, params byte[] payload)
    {
        var f = new byte[6 + payload.Length];
        f[0] = 0x80; f[1] = 0x81; f[2] = src; f[3] = pgn; f[4] = (byte)payload.Length;
        payload.CopyTo(f, 5);
        int sum = 0;
        for (int i = 2; i < f.Length - 1; i++) sum += f[i];
        f[^1] = (byte)sum;
        return f;
    }

    /// <summary>A PGN 240 as the task controller builds it: mode, section count, then one bit
    /// per section (section 1 in bit 0 of the first state byte).</summary>
    public static byte[] HeartbeatFrame(bool enabled, int sections, ulong states)
    {
        var payload = new List<byte> { (byte)(enabled ? 1 : 0), (byte)sections };
        int idx = 0;
        while (idx < sections)
        {
            byte b = 0;
            for (int i = 0; i < 8 && idx < sections; i++, idx++)
                if (idx < 64 && ((states >> idx) & 1) != 0) b |= (byte)(1 << i);
            payload.Add(b);
        }
        return Frame(Source, 0xF0, payload.ToArray());
    }

    /// <summary>Send any frame once, as it is (for malformed ones).</summary>
    public void SendRaw(byte[] frame)
    {
        lock (_lock)
        {
            if (!Online) return;
            try { _udp!.Send(frame, frame.Length, HostEp); } catch { }
        }
    }

    private void Send(byte[] frame)
    {
        if (!Online) return;
        try { _udp!.Send(frame, frame.Length, HostEp); } catch { /* the host is gone */ }
    }

    private void SendHardwareMessage(string text, int seconds, bool alert)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > 60) bytes = bytes[..60];
        var payload = new byte[2 + bytes.Length];
        payload[0] = (byte)seconds;
        payload[1] = (byte)(alert ? 0 : 1);
        bytes.CopyTo(payload, 2);
        MessagesSent.Add((text, seconds, alert));
        Send(Frame(Source, 0xDD, payload));
        Note("221 \"" + text + "\"");
    }

    private void Note(string s)
    {
        if (Log.Count > 400) Log.RemoveRange(0, 100);
        Log.Add(string.Create(CultureInfo.InvariantCulture, $"{NowMs / 1000.0:F1}s {s}"));
    }

    // ---------------------------------------------------------------- network in
    private void ReceiveLoop()
    {
        var any = new IPEndPoint(IPAddress.Any, 0);
        while (!_disposed)
        {
            byte[] p;
            try { p = _udp!.Receive(ref any); }
            catch { if (_disposed) return; continue; }
            lock (_lock)
            {
                if (!Online) continue;   // stopped, or its network is down: the packet is lost
                Handle(p);
            }
        }
    }

    private void Handle(byte[] rx)
    {
        // UdpConnections::receive: header, source, PGN, length, the payload, the checksum
        // (computed and compared in the real code, but the comparison is commented out).
        if (rx.Length < 6 || rx[0] != 0x80 || rx[1] != 0x81) return;
        byte src = rx[2], pgn = rx[3], len = rx[4];
        if (rx.Length < len + 6) { ShortPackets++; return; }
        int sum = 0;
        for (int i = 2; i < len + 5; i++) sum += rx[i];
        if ((byte)sum != rx[len + 5]) ChecksumErrors++;
        var d = rx.AsSpan(5, len);

        if (src == 0x7C && pgn == 0xD6)
        {
            Received[pgn] = (Received.TryGetValue(pgn, out var g) ? g : 0) + 1;
            LastReceivedMs[pgn] = NowMs;
            return;
        }
        if (src != 0x7F) { OtherSourceDropped++; return; }
        Received[pgn] = (Received.TryGetValue(pgn, out var n) ? n : 0) + 1;
        LastReceivedMs[pgn] = NowMs;

        switch (pgn)
        {
            case 0xE5 when len >= 8:   // 229: the 64 section setpoints
            {
                ulong s = 0;
                for (int b = 0; b < 8; b++) s |= (ulong)d[b] << (8 * b);
                if (LastSetpointsMs < 0 || s != LastSetpoints) SetpointChanges++;
                LastSetpoints = s;
                LastSetpointsMs = NowMs;
                foreach (var i in _implements) SetSetpoint(i, s);
                break;
            }
            case 0xF1 when len >= 1:   // 241: section control mode
            {
                bool on = d[0] == 1;
                ModeRequests.Add((NowMs, on));
                Note($"241 request: section control {(on ? "enabled" : "disabled")}" + (OnModeRequest == ModeRequestHandling.Obey ? "" : OnModeRequest == ModeRequestHandling.Ignore ? " (ignored)" : $" (in {ObeyLateMs} ms)"));
                switch (OnModeRequest)
                {
                    case ModeRequestHandling.Obey: ApplyMode(on); break;
                    case ModeRequestHandling.ObeyLate:
                        // A slow task controller works on the first request; the repeats (AgOpenWeb asks
                        // every 0.5 s) don't make it start over.
                        if (_obeyAtMs < 0 || _obeyValue != on) { _obeyValue = on; _obeyAtMs = NowMs + ObeyLateMs; }
                        break;
                }
                break;
            }
            case 0xF2 when len >= 6:   // 242: process data
                ProcessData.Add(((ushort)(d[0] | d[1] << 8), d[2] | d[3] << 8 | d[4] << 16 | d[5] << 24));
                if (ProcessData.Count > 200) ProcessData.RemoveRange(0, 100);
                break;
            case 0xC9 when len == 5 && d[0] == 0xC9 && d[1] == 0xC9:   // 201: subnet
                LastSubnet = string.Create(CultureInfo.InvariantCulture, $"{d[2]}.{d[3]}.{d[4]}");
                Note("201 subnet " + LastSubnet);
                break;
        }
    }

    private void ApplyMode(bool on)
    {
        SectionControlEnabled = on;
        foreach (var i in _implements) SetSetpoint(i, LastSetpoints);
    }

    private void SetSetpoint(Implement i, ulong setpoints)
    {
        ulong want = SectionControlEnabled ? setpoints & i.Mask & ~i.Stuck : i.ManualBits & i.Mask & ~i.Stuck;
        if (want == i.Actual && i.PendingAtMs < 0) return;
        if (i.PendingAtMs >= 0 && i.Pending == want) return;
        i.Pending = want;
        i.PendingAtMs = NowMs + i.ValveDelayMs;
    }

    // ---------------------------------------------------------------- the loop
    private void Tick()
    {
        if (_disposed) return;
        lock (_lock)
        {
            if (!Running) return;
            long now = NowMs;
            if (_obeyAtMs >= 0 && now >= _obeyAtMs) { _obeyAtMs = -1; ApplyMode(_obeyValue); Note($"section control {(_obeyValue ? "enabled" : "disabled")} (late)"); }

            foreach (var i in _implements)
            {
                if (!i.Known && Online)
                {
                    i.Known = true;
                    SendHardwareMessage(string.Create(CultureInfo.InvariantCulture, $"Implement: {i.Name} ({i.Sections} sections, {i.WidthM:0.#} m)"), 5, alert: false);
                    SetSetpoint(i, LastSetpoints);
                }
                // In manual the implement does what its switches say, whatever the setpoints.
                if (!SectionControlEnabled && i.PendingAtMs < 0 && i.Actual != (i.ManualBits & i.Mask & ~i.Stuck))
                    SetSetpoint(i, LastSetpoints);
                if (i.PendingAtMs >= 0 && now >= i.PendingAtMs)
                {
                    i.Actual = i.Pending;
                    i.PendingAtMs = -1;
                }
            }

            if (HeartbeatMs > 0 && now - _lastHeartbeatMs >= HeartbeatMs)
            {
                _lastHeartbeatMs = now;
                bool any = false;
                foreach (var i in _implements)
                {
                    if (i.Sections == 0) continue;
                    any = true;
                    Send(HeartbeatFrame(SectionControlEnabled, i.Sections, i.Actual));
                    HeartbeatsSent++;
                }
                if (!any && BeaconEnabled) { Send(Frame(Source, 0xF0, 0, 0)); HeartbeatsSent++; }
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        lock (_lock) Running = false;
        _tick?.Dispose();
        try { _udp?.Dispose(); } catch { }
    }
}
