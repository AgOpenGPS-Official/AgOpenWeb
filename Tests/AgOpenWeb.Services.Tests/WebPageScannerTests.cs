// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using System.Text;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Network;

namespace AgOpenWeb.Services.Tests;

/// <summary>The web cards' "Search the network": which addresses it asks, and what counts as a page.</summary>
[TestFixture]
public class WebPageScannerTests
{
    private static LocalNetworkAddress Local(string ip, int prefix) => new(IPAddress.Parse(ip), prefix, "eth0");

    // ── Which addresses ──────────────────────────────────────────────────

    [Test]
    public void ATractorNetwork_IsAskedWholeWithoutItsNetworkAndBroadcastAddresses()
    {
        var (targets, networks) = WebPageScanner.Targets([Local("192.168.5.10", 24)]);

        Assert.That(networks, Is.EqualTo(new[] { "192.168.5.0/24" }));
        Assert.That(targets, Has.Count.EqualTo(254));
        Assert.That(targets[0].ToString(), Is.EqualTo("192.168.5.1"));
        Assert.That(targets[^1].ToString(), Is.EqualTo("192.168.5.254"));
        Assert.That(targets.Select(t => t.ToString()), Does.Contain("192.168.5.10"), "an add-on on the same computer");
    }

    [Test]
    public void AWideNetwork_IsCutToThe24AroundTheHost()
    {
        var (targets, networks) = WebPageScanner.Targets([Local("10.20.7.30", 16)]);

        Assert.That(networks, Is.EqualTo(new[] { "10.20.7.0/24" }));
        Assert.That(targets, Has.Count.EqualTo(254));
        Assert.That(targets.All(t => t.ToString().StartsWith("10.20.7.", StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public void ANarrowNetwork_IsAskedAsItIs()
    {
        var (targets, networks) = WebPageScanner.Targets([Local("172.16.0.5", 30)]);

        Assert.That(networks, Is.EqualTo(new[] { "172.16.0.4/30" }));
        Assert.That(targets.Select(t => t.ToString()), Is.EqualTo(new[] { "172.16.0.5", "172.16.0.6" }));
    }

    [Test]
    public void PublicLinkLocalLoopbackAndIpv6Addresses_AreNeverSwept()
    {
        var (targets, networks) = WebPageScanner.Targets([
            Local("8.8.8.8", 24), Local("100.64.1.2", 10), Local("169.254.3.4", 16),
            Local("127.0.0.1", 8), Local("172.32.0.1", 24), new(IPAddress.IPv6Loopback, 64)]);

        Assert.That(targets, Is.Empty);
        Assert.That(networks, Is.Empty);
    }

    [Test]
    public void TwoAddressesOnOneNetwork_AskItOnce_AndAtMostFourNetworksAreAsked()
    {
        var (targets, networks) = WebPageScanner.Targets([
            Local("192.168.5.10", 24), Local("192.168.5.11", 24),
            Local("192.168.1.2", 24), Local("10.0.0.2", 24), Local("10.0.1.2", 24), Local("10.0.2.2", 24)]);

        Assert.That(networks, Is.EqualTo(new[] { "192.168.5.0/24", "192.168.1.0/24", "10.0.0.0/24", "10.0.1.0/24" }));
        Assert.That(targets, Has.Count.EqualTo(4 * 254));
        Assert.That(targets.Distinct().Count(), Is.EqualTo(targets.Count));
    }

    // ── A page's title ───────────────────────────────────────────────────

    [Test]
    public void TheTitle_IsDecodedAndOnOneLine()
    {
        Assert.That(WebPageScanner.TitleOf("<html><head><TITLE lang=\"en\">\n  AiO &amp; Teensy\n\t4.1 </TITLE></head>"),
            Is.EqualTo("AiO & Teensy 4.1"));
    }

    [TestCase("")]
    [TestCase("<html><body>No title</body></html>")]
    [TestCase("<title></title>")]
    public void APageWithoutATitle_HasAnEmptyOne(string html) =>
        Assert.That(WebPageScanner.TitleOf(html), Is.Empty);

    [Test]
    public void ALongTitle_IsCutToABookmarkName()
    {
        var t = WebPageScanner.TitleOf("<title>" + new string('x', 100) + "</title>");
        Assert.That(t, Has.Length.EqualTo(AgOpenWeb.Models.WebCardBookmarks.MaxName));
    }

    // ── Asking ───────────────────────────────────────────────────────────

    [Test]
    public async Task AnAddressWithAPage_IsFoundWithItsTitle()
    {
        using var server = new OneReplyServer("HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\n",
            "<!doctype html><html><head><title>Sprayer</title></head><body>…</body></html>");

        var found = await WebPageScanner.ScanAsync([IPAddress.Loopback], new HashSet<IPAddress>(),
            CancellationToken.None, server.Port);

        Assert.That(found, Has.Count.EqualTo(1));
        Assert.That(found[0].Title, Is.EqualTo("Sprayer"));
        Assert.That(found[0].Url, Is.EqualTo($"http://127.0.0.1:{server.Port}/"));
    }

    [Test]
    public async Task APageOnTheHostItself_GetsHostInItsAddress()
    {
        using var server = new OneReplyServer("HTTP/1.1 200 OK\r\nContent-Type: text/html\r\n", "<title>HAT</title>");

        var found = await WebPageScanner.ScanAsync([IPAddress.Loopback], new HashSet<IPAddress> { IPAddress.Loopback },
            CancellationToken.None, server.Port);

        Assert.That(found.Single().Url, Is.EqualTo($"http://{{host}}:{server.Port}/"));
    }

    [Test]
    public async Task APageBehindALogin_IsFoundWithoutATitle()
    {
        using var server = new OneReplyServer("HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Basic realm=\"x\"\r\nContent-Type: text/html\r\n",
            "<title>Login</title>");

        var found = await WebPageScanner.ScanAsync([IPAddress.Loopback], new HashSet<IPAddress>(),
            CancellationToken.None, server.Port);

        Assert.That(found.Single().Title, Is.Empty);
    }

    [Test]
    public async Task AServerWithoutAPage_OrSomethingThatIsNotHttp_IsNotFound()
    {
        using (var notFound = new OneReplyServer("HTTP/1.1 404 Not Found\r\nContent-Type: text/html\r\n", "<title>404</title>"))
            Assert.That(await WebPageScanner.ScanAsync([IPAddress.Loopback], new HashSet<IPAddress>(),
                CancellationToken.None, notFound.Port), Is.Empty);

        using (var notHttp = new OneReplyServer(null, "$GPGGA,123519,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,*47\r\n"))
            Assert.That(await WebPageScanner.ScanAsync([IPAddress.Loopback], new HashSet<IPAddress>(),
                CancellationToken.None, notHttp.Port), Is.Empty);
    }

    [Test]
    public async Task NothingListening_IsNotFound()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var found = await WebPageScanner.ScanAsync([IPAddress.Loopback], new HashSet<IPAddress>(),
            CancellationToken.None, port);

        Assert.That(found, Is.Empty);
    }

    // Answers every connection with one reply: a status line and headers (null = the body alone, as a
    // device that is no web server would), then the body, and closes.
    private sealed class OneReplyServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        public int Port { get; }

        public OneReplyServer(string? head, string body)
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            var bytes = Encoding.UTF8.GetBytes(body);
            var reply = head == null ? bytes
                : Encoding.ASCII.GetBytes(head + $"Content-Length: {bytes.Length}\r\nConnection: close\r\n\r\n").Concat(bytes).ToArray();
            _ = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                    catch (Exception) { return; }
                    using (client)
                    {
                        var s = client.GetStream();
                        var buf = new byte[4096];
                        var got = new StringBuilder();
                        try
                        {
                            while (!got.ToString().Contains("\r\n\r\n"))
                            {
                                int n = await s.ReadAsync(buf, _cts.Token);
                                if (n == 0) break;
                                got.Append(Encoding.ASCII.GetString(buf, 0, n));
                            }
                            await s.WriteAsync(reply, _cts.Token);
                        }
                        catch (Exception) { }
                    }
                }
            });
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }
    }
}
