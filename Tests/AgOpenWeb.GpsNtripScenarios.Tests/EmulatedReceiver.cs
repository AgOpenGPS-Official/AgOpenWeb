// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace AgOpenWeb.GpsNtripScenarios.Tests;

/// <summary>
/// The GPS receiver and the board it is wired to (AiO), as AgOpenWeb sees them on the network:
/// <list type="bullet">
/// <item>It listens on UDP 2233 for the RTCM AgOpenWeb forwards and re-frames it as a receiver
/// does (whole messages with a good checksum only). It keeps what the AiO v26 firmware cannot
/// take: datagrams over 512 bytes, and datagrams back to back (under 5 ms as received; the firmware
/// keeps one datagram between polls, and the app keeps them 10 ms apart).</item>
/// <item>RTK depends on the corrections it has, as on a real receiver: RTK fixed while the
/// newest observation epoch is at most <see cref="FixHoldSeconds"/> old and the base position
/// (1005) has come; then float up to <see cref="FloatHoldSeconds"/>; then a single fix. With
/// fresh corrections again it is float at once and fixed after <see cref="RefixFromFloatSeconds"/>
/// (from float) or <see cref="RefixFromSingleSeconds"/> (from single). The differential age it
/// reports is the age of its newest epoch.</item>
/// <item>It sends its position ten times a second to AgOpenWeb's UDP port 9999, from its own
/// address: $PAOGI with the dual-antenna heading, or $PANDA for a single antenna. The position
/// carries the error of its solution (a few cm fixed, decimetres float, metres single).</item>
/// </list>
/// The numbers are within what u-blox F9P and Unicore UM982 receivers show; they shape when the
/// receiver loses RTK, not when AgOpenWeb gets the corrections back to it, which is what the
/// scenarios measure.
/// </summary>
public sealed class EmulatedReceiver : IDisposable
{
    public double FixHoldSeconds = 10;
    public double FloatHoldSeconds = 30;
    public double RefixFromFloatSeconds = 5;
    public double RefixFromSingleSeconds = 20;

    private static readonly (double Lat, double Lon) Origin = (41.4987, -5.5012); // made-up place
    private const double MetresPerDegLat = 111_320.0;

    private readonly UdpClient _rtcmIn;
    private readonly UdpClient _nmeaOut;
    private readonly IPEndPoint _host = new(IPAddress.Loopback, 9999);
    private readonly Func<double> _casterNowMs;
    private readonly CancellationTokenSource _cts = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly Rtcm.Framer _framer = new();
    private readonly object _lock = new();
    private Task? _rx, _tx;

    // the tractor (truth), field-local metres
    public double E, N;
    public double SpeedKmh = 8;
    public double HeadingDeg;
    /// <summary>Position sentences are sent (false = the GPS is unplugged or the module is off).</summary>
    public volatile bool Sending = true;
    public bool Dual = true;
    /// <summary>The second antenna is lost: how the receiver/firmware says so.</summary>
    public DualLoss DualLost = DualLoss.None;
    /// <summary>The receiver's own quality, whatever its corrections (trees, a hedge, a shed): null = from the corrections.</summary>
    public int? ForcedQuality;
    /// <summary>A shift of the reported position from the truth (a wrong integer fix, another base).</summary>
    public double ShiftE, ShiftN;

    public enum DualLoss { None, EmptyHeading, PandaInstead }

    // corrections
    private double _newestEpochCasterMs = double.NaN;   // caster time of the newest observation epoch held
    private double _newestEpochAtLocal = double.NaN;    // when it arrived (receiver clock, s)
    private bool _haveStation;
    private int _quality = 1;
    private double _freshSince = double.NaN;            // receiver clock: fresh corrections since

    // what came in
    public long Datagrams, Bytes, Oversize, CloseGaps, StaleEpochs, Messages, StationMessages;
    public double MinGapMs = double.PositiveInfinity;
    private double _lastDatagramAt = double.NaN;
    public ConcurrentQueue<(double At, int Bytes)> DatagramLog { get; } = new();
    public ConcurrentQueue<(double At, int Type, double EpochAgeS)> MessageLog { get; } = new();
    public ConcurrentQueue<string> Log { get; } = new();
    public long SentencesSent;
    public double LastSentenceAt = double.NaN;

    public IPAddress NmeaAddress { get; }

    /// <param name="nmeaFrom">The address the position sentences leave from (another box than the
    /// one listening for RTCM when they differ).</param>
    /// <param name="rtcmOn">The address the board listens on for RTCM (port 2233).</param>
    public EmulatedReceiver(Func<double> casterNowMs, IPAddress? nmeaFrom = null, IPAddress? rtcmOn = null)
    {
        _casterNowMs = casterNowMs;
        NmeaAddress = nmeaFrom ?? IPAddress.Loopback;
        _rtcmIn = new UdpClient(new IPEndPoint(rtcmOn ?? IPAddress.Loopback, 2233));
        _nmeaOut = new UdpClient(new IPEndPoint(NmeaAddress, 0));
    }

    public double Now => _clock.Elapsed.TotalSeconds;

    public void Start()
    {
        _rx = Task.Run(ReceiveLoop);
        _tx = Task.Run(SendLoop);
    }

    private void Note(string text) =>
        Log.Enqueue(string.Create(CultureInfo.InvariantCulture, $"{Now,7:0.0} s  receiver: {text}"));

    private async Task ReceiveLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await _rtcmIn.ReceiveAsync(_cts.Token); }
            catch { return; }
            double t = Now;
            lock (_lock)
            {
                Datagrams++;
                Bytes += r.Buffer.Length;
                DatagramLog.Enqueue((t, r.Buffer.Length));
                if (r.Buffer.Length > 512) { Oversize++; continue; }   // the AiO drops it whole
                if (!double.IsNaN(_lastDatagramAt))
                {
                    double gap = (t - _lastDatagramAt) * 1000;
                    MinGapMs = Math.Min(MinGapMs, gap);
                    if (gap < 5) CloseGaps++;   // back to back (receive timestamps jitter by a few ms)
                }
                _lastDatagramAt = t;
                _framer.Feed(r.Buffer, (type, frame) =>
                {
                    Messages++;
                    if (Rtcm.IsStation(type)) { _haveStation = true; StationMessages++; }
                    double ageS = double.NaN;
                    if (Rtcm.IsObservation(type))
                    {
                        double epochMs = Rtcm.Bits(frame.AsSpan(3), 24, 30);
                        ageS = (_casterNowMs() - epochMs) / 1000.0;
                        if (!double.IsNaN(_newestEpochCasterMs) && epochMs < _newestEpochCasterMs - 1) StaleEpochs++;
                        if (double.IsNaN(_newestEpochCasterMs) || epochMs > _newestEpochCasterMs)
                        {
                            _newestEpochCasterMs = epochMs;
                            _newestEpochAtLocal = t;
                        }
                    }
                    MessageLog.Enqueue((t, type, ageS));
                });
            }
        }
    }

    public long ChecksumFailures { get { lock (_lock) return _framer.ChecksumFailures; } }
    public long BytesSkipped { get { lock (_lock) return _framer.BytesSkipped; } }

    /// <summary>Age of the newest observation epoch the receiver holds, s; NaN with none yet.</summary>
    public double CorrectionAge
    {
        get
        {
            lock (_lock)
                return double.IsNaN(_newestEpochCasterMs) ? double.NaN : (_casterNowMs() - _newestEpochCasterMs) / 1000.0;
        }
    }

    public int Quality { get { lock (_lock) return ForcedQuality ?? _quality; } }
    public bool HaveStation { get { lock (_lock) return _haveStation; } }

    // The RTK state from the corrections, once per position.
    private void UpdateQuality(double t)
    {
        double age = CorrectionAge;
        bool fresh = !double.IsNaN(age) && age <= 2.5 && _haveStation;
        int before = _quality;
        if (fresh)
        {
            if (double.IsNaN(_freshSince)) _freshSince = t;
            double since = t - _freshSince;
            if (_quality == 4) { }
            else if (_quality == 5 && since >= RefixFromFloatSeconds) _quality = 4;
            else if (_quality is 1 or 2 && since >= RefixFromSingleSeconds) _quality = 4;
            else if (_quality is 1 or 2) _quality = 5;
        }
        else
        {
            _freshSince = double.NaN;
            if (double.IsNaN(age) || !_haveStation) _quality = 1;
            else if (age > FloatHoldSeconds) _quality = 1;
            else if (age > FixHoldSeconds && _quality == 4) _quality = 5;
        }
        if (_quality != before) Note($"RTK {Name(before)} -> {Name(_quality)} (age {(double.IsNaN(age) ? "none" : age.ToString("0.0", CultureInfo.InvariantCulture))} s)");
    }

    public static string Name(int q) => q switch { 4 => "fixed", 5 => "float", 2 => "DGPS", 1 => "single", 0 => "no fix", _ => q.ToString(CultureInfo.InvariantCulture) };

    private async Task SendLoop()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long next = 0;
        var rnd = new Random(7);
        // The solution's error: a smooth wander toward a target redrawn every few seconds
        // (receivers smooth with the carrier phase, so the position does not jitter by
        // decimetres between epochs), plus a little noise per epoch. On RTK fixed the wander
        // goes back to zero within a few epochs: the jump a re-fix makes.
        double wanderE = 0, wanderN = 0, targetE = 0, targetN = 0, nextTarget = 0;
        while (!_cts.IsCancellationRequested)
        {
            double v = SpeedKmh / 3.6 * 0.1;
            double h = HeadingDeg * Math.PI / 180;
            E += v * Math.Sin(h);
            N += v * Math.Cos(h);
            double t = Now;
            int q;
            double age;
            lock (_lock) { UpdateQuality(t); }
            q = Quality;
            age = CorrectionAge;
            if (Sending)
            {
                double spread = q == 4 ? 0 : q == 5 ? 0.15 : q == 2 ? 0.5 : 1.2;    // m, how far it wanders
                double rate = q == 4 ? 0.5 : q == 5 ? 0.05 : q == 2 ? 0.15 : 0.3;   // m/s, how fast
                double noise = q == 4 ? 0.005 : q == 5 ? 0.01 : q == 2 ? 0.02 : 0.03; // m per epoch
                if (t >= nextTarget || q == 4)
                {
                    targetE = Gauss(rnd) * spread;
                    targetN = Gauss(rnd) * spread;
                    nextTarget = t + 4 + rnd.NextDouble() * 6;
                }
                double dE = targetE - wanderE, dN = targetN - wanderN, d = Math.Sqrt(dE * dE + dN * dN);
                double step = rate * 0.1;
                if (d <= step) { wanderE = targetE; wanderN = targetN; }
                else { wanderE += dE / d * step; wanderN += dN / d * step; }
                double ex = wanderE + Gauss(rnd) * noise + ShiftE;
                double nx = wanderN + Gauss(rnd) * noise + ShiftN;
                var (lat, lon) = LatLon(E + ex, N + nx);
                string ageText = q is 4 or 5 or 2 && !double.IsNaN(age) ? Math.Max(0, age).ToString("0.0", CultureInfo.InvariantCulture) : "";
                string s = Sentence(lat, lon, HeadingDeg, q, ageText);
                var bytes = System.Text.Encoding.ASCII.GetBytes(s);
                try { _nmeaOut.Send(bytes, bytes.Length, _host); SentencesSent++; LastSentenceAt = t; }
                catch { }
            }
            next += 100;
            long wait = next - sw.ElapsedMilliseconds;
            if (wait > 0) { try { await Task.Delay((int)wait, _cts.Token); } catch { return; } }
        }
    }

    private static double Gauss(Random r)
    {
        double u1 = 1.0 - r.NextDouble(), u2 = r.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    public static (double Lat, double Lon) LatLon(double e, double n) =>
        (Origin.Lat + n / MetresPerDegLat, Origin.Lon + e / (MetresPerDegLat * Math.Cos(Origin.Lat * Math.PI / 180)));

    private string Sentence(double lat, double lon, double headingDeg, int fix, string age)
    {
        var inv = CultureInfo.InvariantCulture;
        string Ll(double v, bool isLat)
        {
            double a = Math.Abs(v);
            int deg = (int)a;
            double min = (a - deg) * 60.0;
            return isLat ? deg.ToString("00", inv) + min.ToString("00.0000000", inv)
                         : deg.ToString("000", inv) + min.ToString("00.0000000", inv);
        }
        string time = DateTime.UtcNow.ToString("HHmmss.ff", inv);
        double knots = SpeedKmh / 1.852;
        double hd = ((headingDeg % 360) + 360) % 360;
        int sats = fix == 4 ? 24 : fix == 5 ? 22 : 18;
        string hdop = fix == 4 ? "0.7" : "0.9";
        bool paogi = Dual && DualLost != DualLoss.PandaInstead;
        string heading = paogi ? (DualLost == DualLoss.EmptyHeading ? "" : hd.ToString("F3", inv)) : "65535";
        string roll = paogi ? (DualLost == DualLoss.EmptyHeading ? "" : "0.000") : "0";
        string body = string.Join(",", paogi ? "PAOGI" : "PANDA", time, Ll(lat, true), lat >= 0 ? "N" : "S", Ll(lon, false), lon >= 0 ? "E" : "W",
            fix.ToString(inv), sats.ToString(inv), hdop, "300.00", age, knots.ToString("F3", inv), heading, roll, paogi ? "0.00" : "0", "0");
        byte cs = 0;
        foreach (char c in body) cs ^= (byte)c;
        return "$" + body + "*" + cs.ToString("X2", inv) + "\r\n";
    }

    /// <summary>Datagrams and bytes in a window of the receiver's clock.</summary>
    public (int Datagrams, int Bytes) Window(double from, double to)
    {
        int d = 0, b = 0;
        foreach (var (at, bytes) in DatagramLog) if (at >= from && at < to) { d++; b += bytes; }
        return (d, b);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _rtcmIn.Dispose(); } catch { }
        try { _nmeaOut.Dispose(); } catch { }
    }
}
