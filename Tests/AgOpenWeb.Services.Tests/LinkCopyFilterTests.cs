// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Network;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// A tablet on cable and on Wi-Fi to the modules' router gets every broadcast twice, the Wi-Fi
/// copy late (0.3 s the day it was found, 1.5 to 1.7 s measured since). On a tester's Puma (20 Nov 2026) the late copies of the
/// position read as the tractor going back (a false reverse paused the steering and the
/// module in Button mode let go) and AutoSteer went off over and over. Each datagram is taken
/// from the link it comes in on first; its copy from the other link is dropped.
/// </summary>
[TestFixture]
public class LinkCopyFilterTests
{
    private const uint Teensy = 0xC0A8057E; // 192.168.5.126
    private const uint Machine = 0xC0A80578; // 192.168.5.120
    private const int Cable = 3, WiFi = 7;

    private static byte[] Fix(int n) => Encoding.ASCII.GetBytes(
        $"$GNGGA,1331{n:00}.00,3924.90,N,00731.80,W,4,12,0.7,341.9,M,48.0,M,2.8,0000*00\r\n");

    // PGN 253: the steer switch bit in byte 11 (switch byte), nothing else changing.
    private static byte[] Pgn253(bool steerSwitchOn) =>
        new byte[] { 0x80, 0x81, 0x7E, 0xFD, 8, 0, 0, 0, 0, 0, 0, (byte)(steerSwitchOn ? 0 : 2), 12, 0 };

    [Test]
    public void On_one_link_everything_is_taken_even_the_same_bytes_again()
    {
        var f = new LinkCopyFilter();
        for (int i = 0; i < 50; i++)
            Assert.That(f.IsCopy(Teensy, Cable, Pgn253(true), i * 100, out _, out _), Is.False, $"datagram {i}");
    }

    [Test]
    public void A_late_copy_of_an_older_fix_from_the_second_link_is_dropped()
    {
        // As on the Puma: fix N on the cable, fix N+1 on the cable, then fix N's Wi-Fi copy
        // 0.3 s late, which put the tractor 0.3-0.5 m back.
        var f = new LinkCopyFilter();
        var taken = new List<string>();
        void Arrive(byte[] d, int link, long t)
        {
            if (!f.IsCopy(Teensy, link, d, t, out _, out _)) taken.Add(Encoding.ASCII.GetString(d, 7, 9));
        }
        for (int n = 0; n < 20; n++)
        {
            Arrive(Fix(n), Cable, n * 100);
            if (n >= 3) Arrive(Fix(n - 3), WiFi, n * 100 + 5);
        }

        Assert.That(taken, Has.Count.EqualTo(20), "every fix once");
        for (int n = 0; n < 20; n++)
            Assert.That(taken[n], Is.EqualTo($"1331{n:00}.00"), "in order, none late");
    }

    [Test]
    public void A_module_repeating_the_same_bytes_on_both_links_is_taken_once_per_send()
    {
        // PGN 253 ten times a second with nothing changed, then the steer switch goes off: the
        // late Wi-Fi copies of "on" must not put the switch back on, and the cable's own
        // repeats of "on" must not be taken for copies.
        var f = new LinkCopyFilter();
        var states = new List<bool>();
        var sent = new List<(bool On, long T)>();
        for (int i = 0; i < 30; i++) sent.Add((i < 20, i * 100));

        var arrivals = new List<(bool On, int Link, long T)>();
        foreach (var (on, t) in sent)
        {
            arrivals.Add((on, Cable, t));
            arrivals.Add((on, WiFi, t + 300));
        }
        arrivals.Sort((a, b) => a.T.CompareTo(b.T));

        foreach (var (on, link, t) in arrivals)
            if (!f.IsCopy(Teensy, link, Pgn253(on), t, out _, out _)) states.Add(on);

        Assert.That(states, Has.Count.EqualTo(30), "each send taken once");
        Assert.That(states.FindIndex(on => !on), Is.EqualTo(20), "the switch goes off when it went off");
        Assert.That(states.GetRange(20, 10), Has.All.False, "and stays off: no late 'on' taken");
    }

    [Test]
    public void When_the_first_link_goes_quiet_the_other_is_taken_at_once()
    {
        var f = new LinkCopyFilter();
        int takenOnWiFi = 0;
        for (int n = 0; n < 10; n++)
        {
            Assert.That(f.IsCopy(Teensy, Cable, Fix(n), n * 100, out _, out _), Is.False);
            Assert.That(f.IsCopy(Teensy, WiFi, Fix(n), n * 100 + 300, out _, out _), Is.True);
        }
        // The cable is pulled out after fix 9: fix 10 on is heard on Wi-Fi only.
        for (int n = 10; n < 20; n++)
            if (!f.IsCopy(Teensy, WiFi, Fix(n), n * 100 + 300, out _, out _)) takenOnWiFi++;

        Assert.That(takenOnWiFi, Is.EqualTo(10), "no fix lost to the switch");
    }

    // The fix sent `tenths` tenths of a second into the run, and the three sentences that go with it.
    private static byte[] FixAt(int tenths) => Encoding.ASCII.GetBytes(
        $"$GNGGA,13{31 + tenths / 600:00}{tenths / 10 % 60:00}.{tenths % 10}0,3924.90,N,00731.80,W,4,12,0.7,341.9,M,48.0,M,2.8,0000*00\r\n");

    private static byte[] WithFix(string name, int tenths) => Encoding.ASCII.GetBytes($"${name},{tenths}*00\r\n");

    /// <summary>
    /// What a module sends through a router whose Wi-Fi holds broadcasts, as measured on a
    /// tester's (22 Nov 2026, 10 minutes, a listener on port 9999 asking for each datagram's link):
    /// the cable has every datagram as sent; the Wi-Fi gets <paramref name="perBeacon"/> of them
    /// every <paramref name="everyMs"/>, at most <paramref name="waiting"/> wait and the rest are
    /// dropped. With 80 datagrams a second, 8 every 205 ms and 60 waiting, the copies are 1.5 s
    /// late and half of them never come. The cable is pulled out at <paramref name="cableUntilMs"/>.
    /// </summary>
    private static List<(byte[] Data, int Link, long T, long SentT)> ThroughHeldBroadcasts(
        int seconds, int everyMs, long cableUntilMs = long.MaxValue, int perBeacon = 8, int waiting = 60)
    {
        var arrivals = new List<(byte[] Data, int Link, long T, long SentT)>();
        var queue = new Queue<(byte[] Data, long SentT)>();
        long nextBeacon = everyMs;
        void Beacons(long until)
        {
            for (; nextBeacon <= until; nextBeacon += everyMs)
                for (int i = 0; i < perBeacon && queue.Count > 0; i++)
                {
                    var (data, sentT) = queue.Dequeue();
                    arrivals.Add((data, WiFi, nextBeacon, sentT));
                }
        }
        void Send(byte[] data, long t)
        {
            Beacons(t);
            if (t < cableUntilMs) arrivals.Add((data, Cable, t, t));
            if (queue.Count < waiting) queue.Enqueue((data, t));
        }
        for (int tenths = 0; tenths < seconds * 10; tenths++)
        {
            // Each tenth of a second: the position's four sentences, and the same PGN four times
            // (a module answering 20 hellos a second with two PGNs, nothing in them changing).
            long t = tenths * 100L;
            Send(FixAt(tenths), t);
            Send(WithFix("GNVTG", tenths), t + 1);
            Send(WithFix("GNTHS", tenths), t + 2);
            Send(WithFix("INHPR", tenths), t + 3);
            for (int k = 0; k < 4; k++) Send(Pgn253(true), t + 20 + k * 20);
        }
        Beacons(seconds * 1000L + 5000);
        // In the order they come in; those of one beacon keep the order they were sent in.
        return arrivals.OrderBy(a => a.T).ToList();
    }

    private static bool IsFix(byte[] d) => d.Length > 6 && d[3] == (byte)'G' && d[4] == (byte)'G' && d[5] == (byte)'A';

    [Test]
    public void Copies_a_second_and_a_half_late_with_half_of_them_lost_are_all_dropped()
    {
        var arrivals = ThroughHeldBroadcasts(seconds: 30, everyMs: 205);
        int onCable = arrivals.FindAll(a => a.Link == Cable).Count, onWiFi = arrivals.Count - onCable;
        long latest = 0;
        foreach (var a in arrivals) if (a.Link == WiFi) latest = Math.Max(latest, a.T - a.SentT);
        Assert.That(onWiFi / (double)onCable, Is.InRange(0.45, 0.55), "the router as measured: half the copies");
        Assert.That(latest, Is.InRange(1400, 1800), "the router as measured: the latest copy, ms");

        var f = new LinkCopyFilter();
        int takenOnWiFi = 0;
        var fixes = new List<long>();
        foreach (var a in arrivals)
        {
            if (f.IsCopy(Teensy, a.Link, a.Data, a.T, out _, out _)) continue;
            if (a.Link == WiFi) takenOnWiFi++;
            if (IsFix(a.Data)) fixes.Add(a.SentT);
        }

        Assert.That(takenOnWiFi, Is.Zero, "everything on Wi-Fi was a copy");
        Assert.That(fixes, Has.Count.EqualTo(300), "every fix once");
        Assert.That(fixes, Is.Ordered.Ascending, "none late");
    }

    [Test]
    public void A_router_with_more_time_between_its_beacons_is_later_still()
    {
        // 8 every third beacon: the same 60 waiting take 2.3 s. Past a 2 s window those copies
        // were taken as news, each a position 2 s old among the cable's.
        var arrivals = ThroughHeldBroadcasts(seconds: 30, everyMs: 307);
        long latest = 0;
        foreach (var a in arrivals) if (a.Link == WiFi) latest = Math.Max(latest, a.T - a.SentT);
        Assert.That(latest, Is.InRange(2100, 2600), "the latest copy, ms");

        var f = new LinkCopyFilter();
        int takenOnWiFi = 0;
        var fixes = new List<long>();
        foreach (var a in arrivals)
        {
            if (f.IsCopy(Teensy, a.Link, a.Data, a.T, out _, out _)) continue;
            if (a.Link == WiFi) takenOnWiFi++;
            if (IsFix(a.Data)) fixes.Add(a.SentT);
        }

        Assert.That(takenOnWiFi, Is.Zero, "everything on Wi-Fi was a copy");
        Assert.That(fixes, Is.Ordered.Ascending, "none late");
    }

    [Test]
    public void The_cable_pulled_with_copies_lost_on_the_way_no_fix_is_taken_twice_or_late()
    {
        const long pulledAt = 20_000;
        var arrivals = ThroughHeldBroadcasts(seconds: 40, everyMs: 205, cableUntilMs: pulledAt);

        var f = new LinkCopyFilter();
        var fixes = new List<long>();
        int wiFiFixesAfter = 0, takenAfter = 0;
        long repeatsTakenAgainAt = -1;
        foreach (var a in arrivals)
        {
            bool copy = f.IsCopy(Teensy, a.Link, a.Data, a.T, out _, out _);
            if (IsFix(a.Data))
            {
                if (a.Link == WiFi && a.SentT >= pulledAt) { wiFiFixesAfter++; if (!copy) takenAfter++; }
                if (!copy) fixes.Add(a.SentT);
            }
            else if (!copy && a.Link == WiFi && a.Data[0] == 0x80 && repeatsTakenAgainAt < 0)
                repeatsTakenAgainAt = a.T;
        }

        Assert.That(fixes, Is.Ordered.Ascending, "no fix late");
        Assert.That(fixes, Is.Unique, "no fix twice");
        Assert.That(takenAfter, Is.EqualTo(wiFiFixesAfter), "every fix the Wi-Fi brings after the cable went is taken");
        Assert.That(wiFiFixesAfter, Is.GreaterThan(50));
        // The module's unchanged PGN: the Wi-Fi's are taken for the lost copies of the cable's
        // until those are forgotten, never longer.
        Assert.That(repeatsTakenAgainAt, Is.InRange(pulledAt, pulledAt + LinkCopyFilter.WindowMs + 500),
            "the module's repeated PGN is taken from Wi-Fi again within the window");
    }

    [Test]
    public void Whichever_link_is_first_for_a_datagram_wins_it()
    {
        var f = new LinkCopyFilter();
        Assert.That(f.IsCopy(Teensy, WiFi, Fix(1), 0, out _, out _), Is.False, "Wi-Fi first this time");
        Assert.That(f.IsCopy(Teensy, Cable, Fix(1), 2, out int first, out _), Is.True);
        Assert.That(first, Is.EqualTo(WiFi));
        Assert.That(f.IsCopy(Teensy, Cable, Fix(2), 100, out _, out _), Is.False, "cable first");
        Assert.That(f.IsCopy(Teensy, WiFi, Fix(2), 400, out first, out _), Is.True);
        Assert.That(first, Is.EqualTo(Cable));
    }

    [Test]
    public void Bytes_seen_on_the_other_link_longer_ago_than_the_window_are_not_a_copy()
    {
        var f = new LinkCopyFilter();
        Assert.That(f.IsCopy(Teensy, Cable, Pgn253(true), 0, out _, out _), Is.False);
        Assert.That(f.IsCopy(Teensy, WiFi, Pgn253(true), LinkCopyFilter.WindowMs + 1, out _, out _), Is.False);
    }

    [Test]
    public void Two_modules_are_told_apart()
    {
        var f = new LinkCopyFilter();
        var hello = new byte[] { 0x80, 0x81, 0x7B, 0x7E, 5, 0, 0, 0, 0, 0, 0x47 };
        Assert.That(f.IsCopy(Teensy, Cable, hello, 0, out _, out _), Is.False);
        Assert.That(f.IsCopy(Machine, WiFi, hello, 10, out _, out _), Is.False, "another sender's bytes");
        Assert.That(f.IsCopy(Teensy, WiFi, hello, 300, out _, out _), Is.True);
        Assert.That(f.IsCopy(Machine, Cable, hello, 310, out _, out _), Is.True);
    }

    [Test]
    public void The_first_copy_in_a_while_is_flagged_for_the_log()
    {
        var f = new LinkCopyFilter();
        var starts = new List<int>();
        void Pair(int n, long t)
        {
            f.IsCopy(Teensy, Cable, Fix(n % 60), t, out _, out _);
            if (f.IsCopy(Teensy, WiFi, Fix(n % 60), t + 300, out _, out bool start) && start) starts.Add(n);
        }
        for (int n = 0; n < 50; n++) Pair(n, n * 100);              // Wi-Fi joins
        for (int n = 50; n < 400; n++)                               // Wi-Fi drops for 35 s
            f.IsCopy(Teensy, Cable, Fix(n % 60), n * 100, out _, out _);
        for (int n = 400; n < 420; n++) Pair(n, n * 100);           // and joins again

        Assert.That(starts, Is.EqualTo(new[] { 0, 400 }));
        Assert.That(f.CopiesFrom(Teensy), Is.EqualTo(70));
    }

    /// <summary>The system says which link a datagram came in on: the filter's input.</summary>
    [Test]
    public void A_socket_asked_for_it_tells_the_link_each_datagram_came_in_on()
    {
        using var rx = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            rx.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
        }
        catch (Exception ex)
        {
            Assert.Ignore($"no packet information on this system: {ex.Message}");
        }
        rx.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        rx.ReceiveTimeout = 2000;
        using var tx = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        tx.SendTo(Fix(1), rx.LocalEndPoint!);

        var buf = new byte[256];
        SocketFlags flags = SocketFlags.None;
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        int n = rx.ReceiveMessageFrom(buf, 0, buf.Length, ref flags, ref from, out IPPacketInformation info);

        Assert.That(n, Is.EqualTo(Fix(1).Length));
        Assert.That(info.Interface, Is.GreaterThan(0));
    }

    private sealed class Loopback : ILocalNetworkInfoProvider
    {
        public IReadOnlyList<LocalNetworkAddress> GetIPv4Addresses() =>
            new[] { new LocalNetworkAddress(IPAddress.Loopback, 8, "lo0") };
    }

    /// <summary>The app's own receive path: the same bytes again on the same link all arrive.</summary>
    [Test]
    [NonParallelizable] // real socket on the module port
    public async Task The_app_takes_every_repeat_on_one_link()
    {
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Bind(new IPEndPoint(IPAddress.Any, UdpCommunicationService.ModulePort));
        }
        catch (SocketException)
        {
            Assert.Ignore("port 9999 is held by another process on this machine");
        }

        var udp = new UdpCommunicationService(new Loopback());
        int got = 0;
        udp.DataReceived += (_, e) => { if (e.PGN == 0xFD) System.Threading.Interlocked.Increment(ref got); };
        await udp.StartAsync();
        try
        {
            using var sender = new UdpClient();
            var pgn = Pgn253(true);
            for (int i = 0; i < 10; i++)
            {
                sender.Send(pgn, pgn.Length, new IPEndPoint(IPAddress.Loopback, UdpCommunicationService.ModulePort));
                await Task.Delay(20);
            }
            for (int wait = 0; wait < 40 && System.Threading.Volatile.Read(ref got) < 10; wait++) await Task.Delay(25);
            Assert.That(got, Is.EqualTo(10));
        }
        finally
        {
            await udp.StopAsync();
            udp.Dispose();
        }
    }
}
