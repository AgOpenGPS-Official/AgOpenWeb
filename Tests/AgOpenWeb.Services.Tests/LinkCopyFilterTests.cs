// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Network;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// A tablet on cable and on Wi-Fi to the modules' router gets every broadcast twice, the Wi-Fi
/// copy up to half a second late. On a tester's Puma (20 Nov 2026) the late copies of the
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
