// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AgOpenWeb.Models;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services.Network;

/// <summary>
/// The web cards' "Search the network" (Plans/ADD_ON_CARDS_PLAN.md): asks every address of the
/// host's own networks for a page on port 80 and reads its title, so the modules' pages (an AiO's,
/// a machine module's, a Wi-Fi bridge's) can be added as bookmarks without typing an address. It
/// runs only when the operator asks, only on private networks, and on each at most the /24 around
/// the host's own address, so a wide or a carrier's network is never swept.
/// </summary>
public static class WebPageScanner
{
    public const int Port = 80;
    private const int Parallel = 64, MaxBody = 32 * 1024, MaxNetworks = 4;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);
    private static readonly Regex TitleRx = new(@"<title[^>]*>(.*?)</title\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    /// <summary>
    /// The addresses to ask, in order, and the networks they make up ("192.168.5.0/24"): each private
    /// IPv4 network the host is on (at most four), cut to the /24 around its address, without the
    /// network and broadcast addresses. The host's own addresses are in: an add-on on the same computer.
    /// </summary>
    public static (List<IPAddress> Targets, List<string> Networks) Targets(IEnumerable<LocalNetworkAddress> local)
    {
        var targets = new List<IPAddress>();
        var networks = new List<string>();
        var seenNets = new HashSet<(uint, int)>();
        var seenIps = new HashSet<uint>();
        foreach (var a in local)
        {
            if (a.Address.AddressFamily != AddressFamily.InterNetwork || !IsPrivate(a.Address)) continue;
            int prefix = Math.Clamp(a.PrefixLength, 24, 32);
            uint mask = uint.MaxValue << (32 - prefix);
            uint net = ToUInt(a.Address) & mask;
            if (!seenNets.Add((net, prefix))) continue;
            if (networks.Count == MaxNetworks) break;
            networks.Add($"{FromUInt(net)}/{prefix}");
            uint size = ~mask + 1;
            for (uint i = 0; i < size; i++)
            {
                if (size > 2 && (i == 0 || i == size - 1)) continue;   // the network and broadcast addresses
                if (seenIps.Add(net + i)) targets.Add(FromUInt(net + i));
            }
        }
        return (targets, networks);
    }

    /// <summary>
    /// Asks each address for "/" on <paramref name="port"/>, many at a time, and returns the ones that
    /// answer with a page (any reply below 400, or 401 for a page behind a login), in the order of
    /// <paramref name="targets"/>. A page on one of the host's own addresses gets <c>{host}</c> in its
    /// URL, as a bookmark for an add-on on the AgOpenWeb computer has, and is listed once.
    /// </summary>
    public static async Task<List<FoundWebPage>> ScanAsync(IReadOnlyList<IPAddress> targets, ISet<IPAddress> own,
        CancellationToken ct, int port = Port)
    {
        using var handler = new SocketsHttpHandler { ConnectTimeout = ConnectTimeout, AllowAutoRedirect = false, UseProxy = false };
        using var http = new HttpClient(handler, disposeHandler: false) { Timeout = RequestTimeout };
        using var gate = new SemaphoreSlim(Parallel);
        var found = new FoundWebPage?[targets.Count];
        await Task.WhenAll(targets.Select(async (ip, i) =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try { found[i] = await ProbeAsync(http, ip, own.Contains(ip), port, ct).ConfigureAwait(false); }
            finally { gate.Release(); }
        })).ConfigureAwait(false);
        // The host's own page answers on each of its addresses: it is listed once.
        return found.Where(f => f != null).Select(f => f!).DistinctBy(f => f.Url).ToList();
    }

    private static async Task<FoundWebPage?> ProbeAsync(HttpClient http, IPAddress ip, bool own, int port, CancellationToken ct)
    {
        string suffix = port == Port ? "/" : $":{port}/";
        try
        {
            using var res = await http.GetAsync($"http://{ip}{suffix}", HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            int code = (int)res.StatusCode;
            if (code >= 400 && code != 401) return null;   // a server, but no page at "/"
            string title = "";
            string type = res.Content.Headers.ContentType?.MediaType ?? "text/html";
            if (res.IsSuccessStatusCode && type.Contains("html", StringComparison.OrdinalIgnoreCase))
                title = TitleOf(await ReadHeadAsync(res.Content, ct).ConfigureAwait(false));
            return new FoundWebPage(title, "http://" + (own ? "{host}" : ip.ToString()) + suffix);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }   // nothing there, refused, timed out, or not HTTP
    }

    // The first bytes of the page, where its title is.
    private static async Task<string> ReadHeadAsync(HttpContent content, CancellationToken ct)
    {
        await using var s = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buf = new byte[MaxBody];
        int n = 0, r;
        while (n < buf.Length && (r = await s.ReadAsync(buf.AsMemory(n), ct).ConfigureAwait(false)) > 0) n += r;
        return Encoding.UTF8.GetString(buf, 0, n);
    }

    /// <summary>A page's title as a bookmark name: entities decoded, spaces collapsed, at most
    /// <see cref="WebCardBookmarks.MaxName"/> characters; "" when it has none.</summary>
    public static string TitleOf(string html)
    {
        try
        {
            var m = TitleRx.Match(html);
            if (!m.Success) return "";
            var t = Regex.Replace(WebUtility.HtmlDecode(m.Groups[1].Value), @"\s+", " ").Trim();
            return t.Length > WebCardBookmarks.MaxName ? t[..WebCardBookmarks.MaxName].TrimEnd() : t;
        }
        catch (RegexMatchTimeoutException) { return ""; }
    }

    private static bool IsPrivate(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }

    private static uint ToUInt(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return (uint)b[0] << 24 | (uint)b[1] << 16 | (uint)b[2] << 8 | b[3];
    }

    private static IPAddress FromUInt(uint v) =>
        new(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
}
