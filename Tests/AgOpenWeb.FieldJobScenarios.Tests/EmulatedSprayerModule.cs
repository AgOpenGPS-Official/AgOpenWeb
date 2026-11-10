// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace AgOpenWeb.FieldJobScenarios.Tests;

/// <summary>
/// What a machine module keeps in its flash: it survives a restart and a power cut. The
/// defaults are those of a module that was never set up (a first install).
/// </summary>
public sealed class ModuleFlash
{
    /// <summary>Output functions per relay (PGN 236): 0 none, 1-16 a section, 17 hyd up,
    /// 18 hyd down, 19 tram right, 20 tram left, 21 geo stop (AgOpenGPS's numbers).</summary>
    public byte[] Pins = { 1, 2, 3, 4, 5, 6, 7, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
    public byte RaiseTime = 2, LowerTime = 4, HydEnable, InvertOutputs;
    public byte User1, User2, User3, User4;
    /// <summary>The tool's section count and the widths of sections 1-16, cm (PGN 235). 0 = never heard.</summary>
    public byte ToolSections;
    public ushort[] SectionCm = new ushort[16];
    public int Writes;

    public ModuleFlash Clone()
    {
        var c = (ModuleFlash)MemberwiseClone();
        c.Pins = (byte[])Pins.Clone();
        c.SectionCm = (ushort[])SectionCm.Clone();
        return c;
    }
}

/// <summary>
/// A machine module on the network, as the Sprayer_ESP32 0.10.1 program of the Boards repository
/// (renatorgr/Boards, folder Sprayer) behaves, which itself follows the official AgOpenGPS
/// Machine_UDP_v5 module (AgOpenGPS-Official/Boards): it listens on port 8888, answers the hello
/// (PGN 200) with PGN 123 carrying the sections it has open, takes the sections from PGN 239 (and
/// 229 above 16 sections), keeps the machine config (238), the output functions (236) and the
/// section widths (235) in its flash, and closes every section when the program has been silent
/// for 2 s. Every relay follows the function the program gave it, with AgOpenGPS's numbering
/// (17 hyd up drives the lowering timer, 19 tram right, 20 tram left: Machine_UDP_v5 relayState).
///
/// It talks real UDP on the loopback (127.0.0.1:8888 in, to 127.0.0.1:9999 out), where
/// AgOpenWeb's UdpCommunicationService always sends as well, so the host side under test is the
/// real one: discovery, the hello bookkeeping, the module status.
///
/// The harness can cut its power, restart it (RAM lost, flash kept), wipe its flash (a new
/// board) and take its Wi-Fi down (nothing in or out; the outputs still follow the watchdog).
/// </summary>
public sealed class EmulatedSprayerModule : IDisposable
{
    public const int NoDataTimeoutMs = 2000;   // Sprayer_ESP32 NO_DATA_TIMEOUT_MS
    public const int BootMs = 1500;             // from power on until it is on the network
    private static readonly IPEndPoint HostEp = new(IPAddress.Loopback, 9999);

    private readonly object _lock = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private UdpClient? _udp;
    private Thread? _rx;
    private Timer? _tick;
    private volatile bool _disposed;

    public ModuleFlash Flash { get; private set; } = new();

    // ---------------------------------------------------------------- power and network
    public bool Powered { get; private set; }
    /// <summary>Its Wi-Fi link: false = nothing in or out (the outputs keep running).</summary>
    public bool NetworkUp { get; set; } = true;
    private long _bootAtMs;
    /// <summary>The module's clock: the harness's simulated time when set (then the harness
    /// also drives <see cref="TickNow"/>), else the wall clock.</summary>
    public Func<long>? Clock { get; set; }
    public long NowMs => Clock?.Invoke() ?? _clock.ElapsedMilliseconds;
    /// <summary>The 50 ms firmware loop, run by the harness in simulated time.</summary>
    public void TickNow() => Tick();
    private bool Online => Powered && NetworkUp && NowMs - _bootAtMs >= BootMs;

    // ---------------------------------------------------------------- RAM (lost at a restart)
    private bool _have239, _have229;
    private long _last239Ms, _last229Ms;
    private ushort _sections239;
    private ulong _sections229;
    private byte _uTurn, _hydLift, _lastTrigger, _raiseTimer, _lowerTimer;
    public byte Tramline { get; private set; }
    public byte GeoStop { get; private set; }
    public double GpsKmh { get; private set; }
    public bool LinkOk { get; private set; }
    public bool IsLower { get; private set; }
    public bool IsRaise { get; private set; }
    /// <summary>Sections the relays really have open (bit 0 = section 1): what the module drives
    /// and reports back.</summary>
    public ulong SectionsOpen { get; private set; }
    /// <summary>Sections where product is coming out of the nozzles (bit 0 = section 1): each
    /// follows its relay after the valve's own time to open or shut (<see cref="ValveOnDelayS"/>,
    /// <see cref="ValveOffDelayS"/>), the time look-ahead is there to cover.</summary>
    public ulong SectionsSpraying { get; private set; }
    /// <summary>Seconds from a relay closing to product at the nozzles (valve, line pressure).</summary>
    public double ValveOnDelayS { get; set; }
    /// <summary>Seconds from a relay opening to the nozzles stopping.</summary>
    public double ValveOffDelayS { get; set; }
    private readonly long[] _relayChangedMs = new long[64];
    /// <summary>Sections the program asks for (239 / 229), before the relays.</summary>
    public ulong SectionsAsked { get; private set; }
    /// <summary>Relay coil states, relay 1 = bit 0 (8 relays, ESP32_Relay_X8).</summary>
    public ulong Relays { get; private set; }

    // ---------------------------------------------------------------- what came in
    public readonly Dictionary<byte, int> Received = new();
    public readonly Dictionary<byte, long> LastReceivedMs = new();
    public int ChecksumErrors { get; private set; }
    public int ShortPackets { get; private set; }
    public int HellosAnswered { get; private set; }
    public int Restarts { get; private set; }
    /// <summary>Log lines a developer would want (config received, restarts), newest last.</summary>
    public readonly List<string> Log = new();

    /// <summary>Relays on the board: 8 on the Sprayer_ESP32 relay board; 24 is the most PGN 236 can name.</summary>
    public int NumRelays { get; }
    /// <summary>Relay i drives section i + 1 straight from PGN 239/229, with no pin functions:
    /// the Seeder_ESP32 way for an implement with more than 16 sections.</summary>
    public bool DirectSections { get; }

    public EmulatedSprayerModule(int relays = 8, bool directSections = false)
    {
        NumRelays = Math.Clamp(relays, 1, 64);
        DirectSections = directSections;
    }

    public void Start()
    {
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Loopback, 8888));
        _rx = new Thread(ReceiveLoop) { IsBackground = true, Name = "machine module rx" };
        _rx.Start();
        _tick = new Timer(_ => { if (Clock == null) Tick(); }, null, 50, 50);
    }

    public void PowerOn()
    {
        lock (_lock)
        {
            if (Powered) return;
            ResetRam();
            Powered = true;
            _bootAtMs = NowMs;
            Note("power on");
        }
    }

    public void PowerOff()
    {
        lock (_lock)
        {
            Powered = false;
            ResetRam();
            Note("power off");
        }
    }

    /// <summary>A restart (watchdog, brown-out, the page's Restart button): RAM lost, flash kept.</summary>
    public void Restart()
    {
        lock (_lock)
        {
            ResetRam();
            Powered = true;
            _bootAtMs = NowMs;
            Restarts++;
            Note("restart");
        }
    }

    /// <summary>A board straight from the box, or one whose flash was erased.</summary>
    public void EraseFlash()
    {
        lock (_lock) { Flash = new ModuleFlash(); Note("flash erased"); }
    }

    public ModuleFlash FlashCopy() { lock (_lock) return Flash.Clone(); }

    public int Count(byte pgn) { lock (_lock) return Received.TryGetValue(pgn, out var n) ? n : 0; }

    private void ResetRam()
    {
        _have239 = _have229 = false;
        _sections239 = 0;
        _sections229 = 0;
        _uTurn = _hydLift = _lastTrigger = _raiseTimer = _lowerTimer = 0;
        Tramline = GeoStop = 0;
        LinkOk = IsLower = IsRaise = false;
        SectionsOpen = SectionsAsked = SectionsSpraying = 0;
        Relays = 0;
    }

    private void Note(string s)
    {
        if (Log.Count > 400) Log.RemoveRange(0, 100);
        Log.Add($"{NowMs / 1000.0:F1}s {s}");
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
                if (!Online) continue;   // off, booting, or its Wi-Fi is down: the packet is lost
                Handle(p, any);
            }
        }
    }

    private static byte Checksum(byte[] p, int length)
    {
        int sum = 0;
        for (int i = 2; i < length - 1; i++) sum += p[i];
        return (byte)sum;
    }

    private void Handle(byte[] rx, IPEndPoint from)
    {
        // Sprayer_ESP32 udpPoll: header, source 0x7F (only the program talks to a machine module),
        // the whole message there, the checksum right.
        if (rx.Length < 6 || rx[0] != 0x80 || rx[1] != 0x81) return;
        if (rx[2] != 0x7F) return;
        byte pgn = rx[3], dataLength = rx[4];
        if (rx.Length < dataLength + 6) { ShortPackets++; return; }
        if (Checksum(rx, dataLength + 6) != rx[dataLength + 5]) { ChecksumErrors++; return; }
        Received[pgn] = (Received.TryGetValue(pgn, out var n) ? n : 0) + 1;
        LastReceivedMs[pgn] = NowMs;
        var d = rx;
        switch (pgn)
        {
            case 239:
                if (dataLength < 8) return;
                _uTurn = d[5];
                GpsKmh = d[6] / 10.0;
                _hydLift = d[7];
                Tramline = d[8];
                GeoStop = d[9];
                _sections239 = (ushort)(d[11] | d[12] << 8);
                _last239Ms = NowMs;
                _have239 = true;
                ApplyOutputs();
                break;

            case 229:
            {
                if (dataLength < 8) return;
                ulong s = 0;
                for (int b = 0; b < 8; b++) s |= (ulong)d[5 + b] << (8 * b);
                _sections229 = s;
                _sections239 = (ushort)s;
                _last229Ms = _last239Ms = NowMs;
                _have229 = _have239 = true;
                ApplyOutputs();
                break;
            }

            case 200:
                HellosAnswered++;
                Send(new byte[] { 0x80, 0x81, 0x7B, 123, 5, (byte)SectionsOpen, (byte)(SectionsOpen >> 8), 0, 0, 0, 0 });
                break;

            case 238:
                if (dataLength < 8) return;
                Flash.RaiseTime = d[5];
                Flash.LowerTime = d[6];
                Flash.HydEnable = d[7];
                Flash.InvertOutputs = (byte)(d[8] & 1);
                Flash.User1 = d[9]; Flash.User2 = d[10]; Flash.User3 = d[11]; Flash.User4 = d[12];
                Flash.Writes++;
                Note($"238 machine config: raise {d[5]} s, lower {d[6]} s, lift {(d[7] != 0 ? "on" : "off")}, invert {d[8] & 1}");
                break;

            case 236:
                if (dataLength < 24) return;
                for (int i = 0; i < 24; i++) Flash.Pins[i] = d[5 + i] <= 21 ? d[5 + i] : (byte)0;
                Flash.Writes++;
                Note("236 output functions: " + string.Join(" ", Flash.Pins.Take(8)));
                break;

            case 235:
            {
                if (dataLength < 33) return;
                var cm = new ushort[16];
                bool same = d[37] == Flash.ToolSections;
                for (int i = 0; i < 16; i++)
                {
                    cm[i] = (ushort)(d[5 + 2 * i] | d[6 + 2 * i] << 8);
                    if (cm[i] != Flash.SectionCm[i]) same = false;
                }
                if (same) break;   // nothing new: the flash is left alone
                Flash.SectionCm = cm;
                Flash.ToolSections = d[37];
                Flash.Writes++;
                Note($"235 sections: {d[37]}, " + string.Join(" ", cm.Take(Math.Min((int)d[37], 16))) + " cm");
                break;
            }

            case 201:
                if (dataLength == 5 && d[5] == 201 && d[6] == 201)
                {
                    Note($"201 subnet {d[7]}.{d[8]}.{d[9]}: restarting");
                    Flash.Writes++;
                    // restarts 300 ms later on the new subnet; on the loopback it comes back here
                    ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(300); Restart(); });
                }
                break;

            case 202:
                if (dataLength == 3 && d[5] == 202 && d[6] == 202)
                    Send(new byte[] { 0x80, 0x81, 0x7B, 203, 7, 127, 0, 0, 1, 127, 0, 0, 0 });
                break;
        }
    }

    private void Send(byte[] msg)
    {
        if (!Online) return;
        msg[^1] = Checksum(msg, msg.Length);
        try { _udp!.Send(msg, msg.Length, HostEp); } catch { /* the host is gone */ }
    }

    // ---------------------------------------------------------------- the outputs
    private int _ticks;

    private void Tick()
    {
        if (_disposed) return;
        lock (_lock)
        {
            if (!Powered) return;
            ApplyOutputs();
            if (++_ticks % 4 == 0)
            {
                HydTick();
                // PGN 237 every 200 ms (AiO v26 layout): AgOpenWeb ignores it, it is here as the
                // network load the real module adds.
                Send(new byte[] { 0x80, 0x81, 0x7B, 237, 8, (byte)SectionsOpen, (byte)(SectionsOpen >> 8),
                    (byte)(LinkOk && IsLower ? 1 : LinkOk && IsRaise ? 2 : 0), (byte)(LinkOk ? Tramline & 3 : 0),
                    (byte)(LinkOk && GeoStop != 0 ? 1 : 0), (byte)(LinkOk ? 1 : 0), 0, 0, 0 });
            }
        }
    }

    private void HydTick()
    {
        if (_hydLift != _lastTrigger && (_hydLift == 1 || _hydLift == 2))
        {
            _lastTrigger = _hydLift;
            _lowerTimer = _raiseTimer = 0;
            if (_hydLift == 1) _lowerTimer = (byte)Math.Min(255, Flash.LowerTime * 5);
            if (_hydLift == 2) _raiseTimer = (byte)Math.Min(255, Flash.RaiseTime * 5);
        }
        if (_raiseTimer > 0) { _raiseTimer--; _lowerTimer = 0; }
        if (_lowerTimer > 0) _lowerTimer--;
        if ((_hydLift != 1 && _hydLift != 2) || !LinkOk) { _lowerTimer = _raiseTimer = 0; _lastTrigger = 0; }
        IsLower = Flash.HydEnable != 0 && _lowerTimer > 0;
        IsRaise = Flash.HydEnable != 0 && _raiseTimer > 0;
    }

    private void ApplyOutputs()
    {
        long now = NowMs;
        LinkOk = _have239 && now - _last239Ms < NoDataTimeoutMs;
        bool fresh229 = _have229 && now - _last229Ms < NoDataTimeoutMs;
        SectionsAsked = _sections239 | (fresh229 ? _sections229 & ~0xFFFFUL : 0);
        ulong req = LinkOk ? SectionsAsked : 0;   // masterOn: the switch cuts the valves' 12 V in the wiring
        bool[] other = { false, LinkOk && IsLower, LinkOk && IsRaise, LinkOk && (Tramline & 1) != 0,
                         LinkOk && (Tramline & 2) != 0, LinkOk && GeoStop != 0 };
        ulong bits = 0;
        ulong open = 0;
        for (int i = 0; i < NumRelays; i++)
        {
            int f = DirectSections ? i + 1 : RelayFunction(i);
            bool on = f is >= 1 and <= 64 ? ((req >> (f - 1)) & 1) != 0
                    : f > 100 && f <= 105 && other[f - 100];
            if (!on) continue;
            bits |= 1UL << i;
            if (f <= 64) open |= 1UL << (f - 1);
        }
        Relays = bits;
        ulong changed = open ^ SectionsOpen;
        for (int s = 0; s < 64; s++) if (((changed >> s) & 1) != 0) _relayChangedMs[s] = now;
        SectionsOpen = open;

        // The valves: each section's product follows its relay once the relay has held for the
        // valve's time (a relay that flicks back sooner never reaches the nozzles).
        ulong spray = SectionsSpraying;
        long onMs = (long)Math.Round(ValveOnDelayS * 1000), offMs = (long)Math.Round(ValveOffDelayS * 1000);
        for (int s = 0; s < 64; s++)
        {
            ulong bit = 1UL << s;
            bool relay = (open & bit) != 0, flowing = (spray & bit) != 0;
            if (relay == flowing) continue;
            if (now - _relayChangedMs[s] >= (relay ? onMs : offMs)) spray ^= bit;
        }
        SectionsSpraying = spray;
    }

    /// <summary>What relay i (0 = relay 1) does: 1-16 that section, 101 lower (pin 17), 102 raise
    /// (18), 103 tram right (19), 104 tram left (20), 105 geo stop (21), 0 nothing.</summary>
    public int RelayFunction(int i)
    {
        if (i >= 24) return 0;
        byte p = Flash.Pins[i];
        if (p is >= 17 and <= 21) return 100 + (p - 17 + 1);
        return p <= 16 ? p : 0;
    }

    public void Dispose()
    {
        _disposed = true;
        _tick?.Dispose();
        try { _udp?.Close(); } catch { }
        _udp?.Dispose();
    }
}
