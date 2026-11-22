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
/// module in Button mode let go) and AutoSteer went off over and over. It is an installation
/// fault: nothing from a module heard on two links is used until one is disconnected, and the
/// detection is strict, since it stops the app.
/// </summary>
[TestFixture]
public class TwoLinkDetectorTests
{
    private const uint Teensy = 0xC0A8057E; // 192.168.5.126
    private const uint Machine = 0xC0A80578; // 192.168.5.120
    private const int Cable = 3, WiFi = 7;

    private static byte[] Fix(int n) => Encoding.ASCII.GetBytes(
        $"$GNGGA,1331{n:00}.00,3924.90,N,00731.80,W,4,12,0.7,341.9,M,48.0,M,2.8,0000*00\r\n");

    // PGN 253: the steer switch bit in byte 11 (switch byte), nothing else changing.
    private static byte[] Pgn253(bool steerSwitchOn) =>
        new byte[] { 0x80, 0x81, 0x7E, 0xFD, 8, 0, 0, 0, 0, 0, 0, (byte)(steerSwitchOn ? 0 : 2), 12, 0 };

    private static bool Use(TwoLinkDetector d, uint sender, int link, long t) => d.Take(sender, link, t, out _);

    [Test]
    public void On_one_link_everything_is_used()
    {
        var d = new TwoLinkDetector();
        for (int i = 0; i < 500; i++)
            Assert.That(Use(d, Teensy, Cable, i * 10), Is.True, $"datagram {i}");
    }

    [Test]
    public void A_few_datagrams_on_another_link_are_not_a_second_link()
    {
        var d = new TwoLinkDetector();
        int notUsed = 0;
        for (int i = 0; i < 1000; i++)
        {
            if (!Use(d, Teensy, Cable, i * 10)) notUsed++;
            // Nine in a row on another link, once: a stray burst.
            if (i >= 300 && i < 309 && !Use(d, Teensy, WiFi, i * 10 + 5)) notUsed++;
        }
        Assert.That(notUsed, Is.Zero);
    }

    [Test]
    public void Datagrams_on_another_link_now_and_then_are_never_a_second_link()
    {
        // One every 1.5 s for a minute: never TwoLinkDetector.Repeats in a row.
        var d = new TwoLinkDetector();
        int notUsed = 0;
        for (long t = 0; t < 60_000; t += 10)
        {
            if (!Use(d, Teensy, Cable, t)) notUsed++;
            if (t % 1500 == 0 && !Use(d, Teensy, WiFi, t + 5)) notUsed++;
        }
        Assert.That(notUsed, Is.Zero);
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
    /// late and half of them never come. The cable is plugged in at <paramref name="cableFromMs"/>
    /// and pulled out at <paramref name="cableUntilMs"/>.
    /// </summary>
    private static List<(byte[] Data, int Link, long T, long SentT)> ThroughHeldBroadcasts(
        int seconds, int everyMs, long cableUntilMs = long.MaxValue, int perBeacon = 8, int waiting = 60,
        long cableFromMs = 0)
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
            if (t >= cableFromMs && t < cableUntilMs) arrivals.Add((data, Cable, t, t));
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
    public void The_testers_router_is_found_within_half_a_second_and_nothing_is_used_after()
    {
        var arrivals = ThroughHeldBroadcasts(seconds: 30, everyMs: 205).FindAll(a => a.T < 30_000);
        int onCable = arrivals.FindAll(a => a.Link == Cable).Count, onWiFi = arrivals.Count - onCable;
        long latest = 0;
        foreach (var a in arrivals) if (a.Link == WiFi) latest = Math.Max(latest, a.T - a.SentT);
        Assert.That(onWiFi / (double)onCable, Is.InRange(0.45, 0.55), "the router as measured: half the copies");
        Assert.That(latest, Is.InRange(1400, 1800), "the router as measured: the latest copy, ms");

        var d = new TwoLinkDetector();
        long foundAt = -1;
        int changes = 0, usedAfter = 0;
        foreach (var a in arrivals)
        {
            bool use = d.Take(Teensy, a.Link, a.T, out var change);
            if (change != TwoLinkDetector.Change.None) changes++;
            if (change == TwoLinkDetector.Change.Detected) foundAt = a.T;
            if (foundAt >= 0 && use) usedAfter++;
        }

        Assert.That(foundAt, Is.InRange(0, 500), "the second beacon's 8 copies make 10 in a row on the Wi-Fi");
        Assert.That(changes, Is.EqualTo(1), "found once, never cleared while both links carry it");
        Assert.That(usedAfter, Is.Zero, "nothing used from either link");
    }

    [Test]
    public void A_router_with_more_time_between_its_beacons_is_found_too()
    {
        var arrivals = ThroughHeldBroadcasts(seconds: 30, everyMs: 307).FindAll(a => a.T < 30_000);
        var d = new TwoLinkDetector();
        long foundAt = -1;
        int usedAfter = 0;
        foreach (var a in arrivals)
        {
            bool use = d.Take(Teensy, a.Link, a.T, out var change);
            if (change == TwoLinkDetector.Change.Detected) foundAt = a.T;
            if (foundAt >= 0 && use) usedAfter++;
        }
        Assert.That(foundAt, Is.InRange(0, 700));
        Assert.That(usedAfter, Is.Zero);
    }

    [Test]
    public void The_cable_pulled_the_module_is_used_again_from_the_wifi_a_second_later()
    {
        const long pulledAt = 20_000;
        var arrivals = ThroughHeldBroadcasts(seconds: 40, everyMs: 205, cableUntilMs: pulledAt);

        var d = new TwoLinkDetector();
        long clearedAt = -1, firstUsedAfterPull = -1;
        int usedBetween = 0;
        foreach (var a in arrivals)
        {
            bool use = d.Take(Teensy, a.Link, a.T, out var change);
            if (change == TwoLinkDetector.Change.Cleared) clearedAt = a.T;
            if (a.T > 1000 && a.T < pulledAt && use) usedBetween++;
            if (a.T >= pulledAt && use && firstUsedAfterPull < 0) firstUsedAfterPull = a.T;
        }

        Assert.That(usedBetween, Is.Zero, "nothing used while on both");
        Assert.That(clearedAt, Is.InRange(pulledAt + TwoLinkDetector.GapMs - 100, pulledAt + TwoLinkDetector.GapMs + 300),
            "the cable quiet for a second: one link again");
        Assert.That(firstUsedAfterPull, Is.EqualTo(clearedAt), "and the Wi-Fi is used from then on");
    }

    [Test]
    public void The_cable_plugged_in_while_on_wifi_is_found_at_once()
    {
        const long pluggedAt = 10_000;
        var arrivals = ThroughHeldBroadcasts(seconds: 30, everyMs: 205, cableFromMs: pluggedAt).FindAll(a => a.T < 30_000);
        var d = new TwoLinkDetector();
        long foundAt = -1;
        int usedBefore = 0, usedAfter = 0;
        foreach (var a in arrivals)
        {
            bool use = d.Take(Teensy, a.Link, a.T, out var change);
            if (change == TwoLinkDetector.Change.Detected) foundAt = a.T;
            if (foundAt < 0 && use) usedBefore++;
            if (foundAt >= 0 && use) usedAfter++;
        }
        Assert.That(foundAt, Is.InRange(pluggedAt, pluggedAt + 200), "10 datagrams in a row on the cable");
        Assert.That(usedAfter, Is.Zero);
        Assert.That(usedBefore, Is.GreaterThan(100), "the Wi-Fi alone was used, as today");
    }

    [Test]
    public void Two_modules_are_told_apart()
    {
        var d = new TwoLinkDetector();
        int machineNotUsed = 0;
        for (int i = 0; i < 100; i++)
        {
            Use(d, Teensy, Cable, i * 10);
            Use(d, Teensy, WiFi, i * 10 + 3);
            if (!Use(d, Machine, Cable, i * 10 + 5)) machineNotUsed++;
        }
        Assert.That(Use(d, Teensy, Cable, 1000), Is.False, "the Teensy is on two links");
        Assert.That(machineNotUsed, Is.Zero, "the machine module, on one, is used");
    }

    [Test]
    public void Each_finding_and_its_end_is_reported_once_and_counted()
    {
        var d = new TwoLinkDetector();
        var changes = new List<(int I, TwoLinkDetector.Change C)>();
        void Arrive(int link, int i)
        {
            d.Take(Teensy, link, i * 100L + (link == WiFi ? 50 : 0), out var c);
            if (c != TwoLinkDetector.Change.None) changes.Add((i, c));
        }
        for (int i = 0; i < 50; i++) { Arrive(Cable, i); Arrive(WiFi, i); }   // Wi-Fi joins
        for (int i = 50; i < 400; i++) Arrive(Cable, i);                     // Wi-Fi drops for 35 s
        for (int i = 400; i < 420; i++) { Arrive(Cable, i); Arrive(WiFi, i); } // and joins again

        Assert.That(changes, Is.EqualTo(new[]
        {
            (9, TwoLinkDetector.Change.Detected), (60, TwoLinkDetector.Change.Cleared), (409, TwoLinkDetector.Change.Detected),
        }));
        var list = new List<TwoLinkDetector.ModuleOnTwoLinks>();
        d.AddTo(list, 419 * 100 + 50);
        Assert.That(list, Has.Count.EqualTo(1));
        Assert.That(list[0], Is.EqualTo(new TwoLinkDetector.ModuleOnTwoLinks(Teensy, Cable, WiFi,
            NotUsed: 1 + 2 * 40 + 10 + 1 + 2 * 10, Times: 2, Now: true)));
        list.Clear();
        d.AddTo(list, 419 * 100 + 50 + TwoLinkDetector.GapMs + 1);
        Assert.That(list[0].Now, Is.False, "both links quiet: no longer");
    }

    [Test]
    public void A_wifi_that_comes_back_as_another_link_is_found_again()
    {
        const int WiFi2 = 9;
        var d = new TwoLinkDetector();
        for (int i = 0; i < 20; i++) { Use(d, Teensy, Cable, i * 100); Use(d, Teensy, WiFi, i * 100 + 50); }
        for (int i = 20; i < 40; i++) Use(d, Teensy, Cable, i * 100);
        for (int i = 40; i < 60; i++) { Use(d, Teensy, Cable, i * 100); Use(d, Teensy, WiFi2, i * 100 + 50); }

        Assert.That(Use(d, Teensy, Cable, 6000), Is.False);
        Assert.That(d.Links(Teensy), Is.EqualTo((Cable, WiFi2)));
    }

    [Test]
    public void The_app_reports_no_module_on_two_links_without_allocating()
    {
        var udp = new UdpCommunicationService(new Loopback());
        Assert.That(udp.GetSecondLinks(), Is.Empty);
        Assert.That(udp.GetSecondLinks(), Is.SameAs(udp.GetSecondLinks()), "the same empty list");
    }

    /// <summary>The system says which link a datagram came in on: the detector's input.</summary>
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
