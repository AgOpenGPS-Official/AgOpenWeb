// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AgOpenWeb.WorkingDayScenarios.Tests;

/// <summary>
/// An NTRIP caster on the loopback, as the services farmers use answer:
/// <list type="bullet">
/// <item>NTRIP 2.0 requests get <c>HTTP/1.1 200 OK</c> and a chunked stream (as BKG and Emlid
/// casters do); NTRIP 1.0 requests get <c>ICY 200 OK</c> and a plain stream.</item>
/// <item>A wrong login gets <c>401 Unauthorized</c>; an unknown mount point gets the source
/// table (that is how most casters say it), as does a request for <c>/</c>.</item>
/// <item>Each second an observation epoch of four MSM7 messages (GPS, GLONASS, Galileo,
/// BeiDou), about 1.4 KB; every 10 s the base position (1005), antenna (1033) and GLONASS
/// biases (1230). The epoch time in each message is the caster's clock, so the receiver can
/// tell how old its corrections are.</item>
/// <item>A VRS mount point sends nothing until the client has sent a GGA position.</item>
/// </list>
/// The scenario can stop the caster (the port then refuses connections), let it stall (the
/// connection stays open and nothing comes), or close every client.
/// </summary>
public sealed class FakeCaster : IDisposable
{
    public const string User = "farmer";
    public const string Password = "s3cret-pw";
    public const string Mount = "VRS3";
    public const string VrsMount = "VRS_NEAR";
    public const string SecondMount = "BASE2";

    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly object _lock = new();
    private readonly List<Client> _clients = new();
    private TcpListener? _listener;
    private CancellationTokenSource _cts = new();
    private int _accepted, _streaming, _maxStreaming;
    private long _bytesSent;

    public int Port { get; }
    public string Name { get; }
    /// <summary>Milliseconds on the caster's clock: the epoch time written into each message.</summary>
    public double NowMs => _clock.Elapsed.TotalMilliseconds;
    public bool IsUp => _listener != null;
    /// <summary>Streams stay open but nothing is sent (a caster whose base station went quiet).</summary>
    public volatile bool Stalled;

    public int Accepted => Volatile.Read(ref _accepted);
    public int Streaming => Volatile.Read(ref _streaming);
    public int MaxStreaming => Volatile.Read(ref _maxStreaming);
    public long BytesSent => Interlocked.Read(ref _bytesSent);
    public ConcurrentQueue<string> Log { get; } = new();
    /// <summary>What each request asked for: version, mount point, user, how it was answered.</summary>
    public ConcurrentQueue<string> Requests { get; } = new();
    public int GgasReceived => Volatile.Read(ref _ggas);
    private int _ggas;

    public FakeCaster(string name = "caster")
    {
        Name = name;
        Port = FreePort();
        Start();
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private void Note(string text) =>
        Log.Enqueue(string.Create(CultureInfo.InvariantCulture, $"{_clock.Elapsed.TotalSeconds,7:0.0} s  {Name}: {text}"));

    /// <summary>The caster process starts (again): the port accepts connections.</summary>
    public void Start()
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
            _ = Task.Run(() => AcceptLoop(l, ct));
        }
        Note("up");
    }

    /// <summary>The caster process stops: every client is cut and the port refuses connections.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (_listener == null) return;
            _cts.Cancel();
            _listener.Stop();
            _listener = null;
        }
        CloseAll();
        Note("down");
    }

    /// <summary>Every client connection closed by the caster (a restart, a kick).</summary>
    public void CloseAll()
    {
        List<Client> all;
        lock (_lock) { all = _clients.ToList(); _clients.Clear(); }
        foreach (var c in all) c.Close();
        if (all.Count > 0) Note($"closed {all.Count} client(s)");
    }

    private async Task AcceptLoop(TcpListener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await l.AcceptTcpClientAsync(ct); }
            catch { return; }
            Interlocked.Increment(ref _accepted);
            var c = new Client(tcp);
            lock (_lock) _clients.Add(c);
            _ = Task.Run(() => Serve(c, ct));
        }
    }

    private sealed class Client
    {
        public readonly TcpClient Tcp;
        public readonly NetworkStream Stream;
        public volatile bool Closed;
        public Client(TcpClient tcp) { Tcp = tcp; Stream = tcp.GetStream(); }
        public void Close()
        {
            Closed = true;
            try { Tcp.Client.Shutdown(SocketShutdown.Both); } catch { }
            try { Tcp.Close(); } catch { }
        }
    }

    private async Task Serve(Client c, CancellationToken ct)
    {
        bool counted = false;
        try
        {
            var (header, extra) = await ReadHeaderAsync(c.Stream, ct);
            if (header == null) { Note("client closed before its request"); return; }
            string[] lines = header.Split("\r\n");
            string[] first = lines[0].Split(' ');
            string path = first.Length > 1 ? first[1] : "";
            string mount = Uri.UnescapeDataString(path.TrimStart('/'));
            bool v2 = lines.Any(x => x.StartsWith("Ntrip-Version:", StringComparison.OrdinalIgnoreCase) && x.Contains("2.0"));
            string? auth = lines.FirstOrDefault(x => x.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase));
            string user = "", pass = "";
            if (auth != null)
            {
                try
                {
                    string b64 = auth.Split(' ', StringSplitOptions.RemoveEmptyEntries).Last();
                    string up = Encoding.ASCII.GetString(Convert.FromBase64String(b64));
                    int i = up.IndexOf(':');
                    user = i < 0 ? up : up[..i];
                    pass = i < 0 ? "" : up[(i + 1)..];
                }
                catch { }
            }
            bool ggaInHeader = lines.Any(x => x.StartsWith("Ntrip-GGA:", StringComparison.OrdinalIgnoreCase));
            string request = $"{(v2 ? "NTRIP 2.0" : "NTRIP 1.0")} '{lines[0]}' user '{user}'";

            if (mount.Length == 0 || (mount != Mount && mount != VrsMount && mount != SecondMount))
            {
                Requests.Enqueue(request + " -> source table");
                await SendText(c.Stream, SourceTable(v2), ct);
                return;
            }
            if (user != User || pass != Password)
            {
                Requests.Enqueue(request + " -> 401");
                await SendText(c.Stream, v2
                    ? "HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Basic realm=\"" + mount + "\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                    : "ERROR - Bad Password\r\n", ct);
                return;
            }
            Requests.Enqueue(request + " -> 200" + (ggaInHeader ? " (GGA in header)" : ""));
            await SendText(c.Stream, v2
                ? "HTTP/1.1 200 OK\r\nNtrip-Version: Ntrip/2.0\r\nServer: FakeCaster/1.0\r\nContent-Type: gnss/data\r\nCache-Control: no-store\r\nTransfer-Encoding: chunked\r\n\r\n"
                : "ICY 200 OK\r\n\r\n", ct);

            int n = Interlocked.Increment(ref _streaming);
            counted = true;
            int max;
            while ((max = Volatile.Read(ref _maxStreaming)) < n && Interlocked.CompareExchange(ref _maxStreaming, n, max) != max) { }
            Note($"streaming {mount} to a client ({n} open)");

            bool ggaSeen = ggaInHeader || extra.Contains("GGA", StringComparison.Ordinal);
            if (ggaSeen) Interlocked.Increment(ref _ggas);
            // Reads what the client sends afterwards (GGA lines) so the caster knows its position.
            var ggaFlag = new StrongBox(ggaSeen);
            _ = Task.Run(() => ReadGgas(c, ggaFlag, ct));

            var rnd = new Random(Port + Accepted);
            long nextMs = (long)NowMs;
            int epoch = 0;
            while (!ct.IsCancellationRequested && !c.Closed)
            {
                long wait = nextMs - (long)NowMs;
                if (wait > 0) await Task.Delay((int)wait, ct);
                nextMs += 1000;
                if (Stalled) continue;
                if (mount == VrsMount && !ggaFlag.Value) continue;
                uint ms = (uint)NowMs;
                var msgs = new List<byte[]>();
                if (epoch % 10 == 0)
                {
                    msgs.Add(Rtcm.Message(1005, 19, ms, rnd));
                    msgs.Add(Rtcm.Message(1033, 40, ms, rnd));
                    msgs.Add(Rtcm.Message(1230, 12, ms, rnd));
                }
                msgs.Add(Rtcm.Message(1077, 420, ms, rnd));
                msgs.Add(Rtcm.Message(1087, 310, ms, rnd));
                msgs.Add(Rtcm.Message(1097, 330, ms, rnd));
                msgs.Add(Rtcm.Message(1127, 300, ms, rnd));
                epoch++;
                byte[] body = msgs.SelectMany(m => m).ToArray();
                byte[] wire = v2 ? Chunk(body) : body;
                await c.Stream.WriteAsync(wire, ct);
                Interlocked.Add(ref _bytesSent, wire.Length);
            }
        }
        catch (Exception) { /* the client went away, or the caster stopped */ }
        finally
        {
            if (counted)
            {
                int left = Interlocked.Decrement(ref _streaming);
                Note($"a stream ended ({left} open)");
            }
            lock (_lock) _clients.Remove(c);
            c.Close();
        }
    }

    private sealed class StrongBox(bool value) { public volatile bool Value = value; }

    private async Task ReadGgas(Client c, StrongBox seen, CancellationToken ct)
    {
        var buf = new byte[2048];
        var text = new StringBuilder();
        try
        {
            while (!ct.IsCancellationRequested && !c.Closed)
            {
                int n = await c.Stream.ReadAsync(buf, ct);
                if (n <= 0) { c.Close(); return; }   // the client closed: end the stream too
                text.Append(Encoding.ASCII.GetString(buf, 0, n));
                string s = text.ToString();
                int count = 0, i = 0;
                while ((i = s.IndexOf("GGA,", i, StringComparison.Ordinal)) >= 0) { count++; i += 4; }
                if (count > 0)
                {
                    Interlocked.Add(ref _ggas, count);
                    seen.Value = true;
                    text.Clear();
                }
                if (text.Length > 4096) text.Clear();
            }
        }
        catch { }
    }

    private static byte[] Chunk(byte[] body)
    {
        byte[] head = Encoding.ASCII.GetBytes(body.Length.ToString("X", CultureInfo.InvariantCulture) + "\r\n");
        var all = new byte[head.Length + body.Length + 2];
        head.CopyTo(all, 0);
        body.CopyTo(all, head.Length);
        all[^2] = (byte)'\r';
        all[^1] = (byte)'\n';
        return all;
    }

    private static string SourceTable(bool v2)
    {
        string body =
            $"STR;{Mount};Near base;RTCM 3.2;1005(10),1077(1),1087(1),1097(1),1127(1),1230(10);2;GPS+GLO+GAL+BDS;SNIP;PRT;41.50;-5.50;0;0;sNTRIP;none;B;N;2400;\r\n" +
            $"STR;{VrsMount};VRS;RTCM 3.2;1005(10),1077(1),1087(1),1097(1),1127(1);2;GPS+GLO+GAL+BDS;SNIP;PRT;41.50;-5.50;1;1;sNTRIP;none;B;N;2400;\r\n" +
            $"STR;{SecondMount};Second base;RTCM 3.2;1005(10),1077(1),1087(1);2;GPS+GLO;SNIP;PRT;41.40;-5.40;0;0;sNTRIP;none;B;N;2400;\r\n" +
            "ENDSOURCETABLE\r\n";
        return v2
            ? "HTTP/1.1 200 OK\r\nNtrip-Version: Ntrip/2.0\r\nContent-Type: gnss/sourcetable\r\nContent-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\nConnection: close\r\n\r\n" + body
            : "SOURCETABLE 200 OK\r\nContent-Type: text/plain\r\nContent-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\n\r\n" + body;
    }

    private static async Task SendText(NetworkStream s, string text, CancellationToken ct) =>
        await s.WriteAsync(Encoding.ASCII.GetBytes(text), ct);

    private static async Task<(string? Header, string Extra)> ReadHeaderAsync(NetworkStream s, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new byte[1024];
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(20));
        while (true)
        {
            int n;
            try { n = await s.ReadAsync(buf, limit.Token); }
            catch { return (null, ""); }
            if (n <= 0) return (null, "");
            sb.Append(Encoding.ASCII.GetString(buf, 0, n));
            string all = sb.ToString();
            int end = all.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0) return (all[..end], all[(end + 4)..]);
            if (sb.Length > 16384) return (null, "");
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
