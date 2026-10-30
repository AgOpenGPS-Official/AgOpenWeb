// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using AgOpenWeb.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// The main and the fallback service: when the client switches by itself, that it never goes back
/// by itself, and the one rule that keeps the fallback from pushing the main off.
/// </summary>
[TestFixture, NonParallelizable]
public class NtripFailoverTests
{
    private const string Busy = "HTTP/1.1 503 Service Unavailable\r\n\r\n";
    private const string Denied = "HTTP/1.1 401 Unauthorized\r\n\r\n";

    private static NtripConfiguration Config(int mainPort, int fallbackPort, bool autoSwitch, string fallbackUser = "fu", int after = 2,
        string mount = "MAIN", string fallbackMount = "FALLBACK") => new()
    {
        CasterAddress = "127.0.0.1", CasterPort = mainPort, MountPoint = mount, Username = "mu", Password = "mp",
        SubnetAddress = "127.0.0", UdpForwardPort = 0, GgaIntervalSeconds = 0,
        Fallback = new NtripFallbackConfiguration
        {
            CasterAddress = "127.0.0.1", CasterPort = fallbackPort, MountPoint = fallbackMount, Username = fallbackUser, Password = "fp",
            AutoSwitch = autoSwitch, SwitchAfterFailures = after,
        },
    };

    // Accepts and sends corrections: a service counts as working once bytes arrive, not at the 200.
    private static Task Accept(System.Net.Sockets.NetworkStream s) =>
        NtripFakeCaster.SendAsync(s, "ICY 200 OK\r\n\r\nrtcm").ContinueWith(_ => Task.Delay(5000)).Unwrap();

    private static bool WaitFor(Func<bool> condition, int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (condition()) return true; Thread.Sleep(10); }
        return condition();
    }

    private static (NtripClientService svc, ConcurrentQueue<NtripNoticeEventArgs> notices) Client()
    {
        var svc = new NtripClientService(Substitute.For<IGpsService>(), NullLogger<NtripClientService>.Instance);
        var notices = new ConcurrentQueue<NtripNoticeEventArgs>();
        svc.Notice += (_, e) => notices.Enqueue(e);
        return (svc, notices);
    }

    // ── The decisions ────────────────────────────────────────────────────

    private static NtripSessionStatus Status(NtripState state, int attempts) =>
        NtripSessionStatus.Idle with { State = state, Attempts = attempts };

    [TestCase(NtripState.Waiting, 1, true, 2, false)]
    [TestCase(NtripState.Waiting, 2, true, 2, true)]
    [TestCase(NtripState.Waiting, 5, true, 2, true)]
    [TestCase(NtripState.Waiting, 2, false, 2, false)]
    [TestCase(NtripState.GaveUp, 1, true, 5, true)]     // a wrong login or mount point counts at once
    [TestCase(NtripState.GaveUp, 1, false, 5, false)]
    [TestCase(NtripState.Streaming, 9, true, 2, false)]
    [TestCase(NtripState.Connecting, 9, true, 2, false)]
    [TestCase(NtripState.Waiting, 1, true, 0, true)]    // a nonsense count still means "after a failure"
    public void TheSwitchHappensOnlyWhenAutoSwitchIsOn_AndTheMainServiceFailedEnough(
        NtripState state, int attempts, bool auto, int after, bool expected)
    {
        var fallback = new NtripFallbackConfiguration { AutoSwitch = auto, SwitchAfterFailures = after };

        Assert.That(NtripFailover.ShouldSwitch(Status(state, attempts), fallback), Is.EqualTo(expected));
    }

    [TestCase("127.0.0.1", 2101, "u", "127.0.0.1", 2101, "u", true)]
    [TestCase("Caster.Example.com", 2101, "u", "caster.example.com", 2101, "u", true)]
    [TestCase("127.0.0.1", 2101, "u", "127.0.0.1", 2102, "u", false)]
    [TestCase("127.0.0.1", 2101, "u", "127.0.0.1", 2101, "other", false)]
    [TestCase("a.example.com", 2101, "u", "b.example.com", 2101, "u", false)]
    public void TheSameLoginOnTheSameCaster_IsOneAccount(string host, int port, string user, string fHost, int fPort, string fUser, bool same)
    {
        var main = new NtripConfiguration { CasterAddress = host, CasterPort = port, Username = user };
        var fallback = new NtripFallbackConfiguration { CasterAddress = fHost, CasterPort = fPort, Username = fUser };

        Assert.That(NtripFailover.SameAccount(main, fallback), Is.EqualTo(same));
    }

    [Test]
    public void TheFallbackConfiguration_KeepsTheForwardingSettings_ButHasNoFallbackOfItsOwn()
    {
        NtripConfiguration main = Config(2101, 2102, autoSwitch: true);
        main.SubnetAddress = "10.1.2";
        main.UdpForwardPort = 2233;

        NtripConfiguration fallback = NtripFailover.ForFallback(main, main.Fallback!);

        Assert.That(fallback.CasterPort, Is.EqualTo(2102));
        Assert.That(fallback.MountPoint, Is.EqualTo("FALLBACK"));
        Assert.That(fallback.Username, Is.EqualTo("fu"));
        Assert.That(fallback.SubnetAddress, Is.EqualTo("10.1.2"));
        Assert.That(fallback.UdpForwardPort, Is.EqualTo(2233));
        Assert.That(fallback.Fallback, Is.Null);
        Assert.That(main.CasterPort, Is.EqualTo(2101), "the main configuration is not changed");
    }

    [Test]
    public void ASwitchSaysThatRtkMayBeLost()
    {
        Assert.That(NtripFailover.NoticeText(NtripNoticeKind.SwitchedToFallback, true), Does.Contain("main service failed").And.Contain("RTK"));
        Assert.That(NtripFailover.NoticeText(NtripNoticeKind.SwitchedToFallback, false), Does.Contain("RTK"));
        Assert.That(NtripFailover.NoticeText(NtripNoticeKind.SwitchedToMain, false), Does.Contain("RTK"));
        Assert.That(NtripFailover.NoticeText(NtripNoticeKind.MainBack, false), Does.Contain("until you switch back"));
    }

    // ── The client ───────────────────────────────────────────────────────

    [Test]
    public async Task AMainServiceThatKeepsFailing_IsReplacedByTheFallback_WhenAutoSwitchIsOn()
    {
        using var main = new NtripFakeCaster((_, _, s) => NtripFakeCaster.SendAsync(s, Busy));
        using var fallback = new NtripFakeCaster((_, _, s) => Accept(s));
        var (svc, notices) = Client();

        await svc.ConnectAsync(Config(main.Port, fallback.Port, autoSwitch: true, after: 2));

        Assert.That(WaitFor(() => svc.Using == NtripService.Fallback), Is.True);
        Assert.That(WaitFor(() => svc.Status.State == NtripState.Connected), Is.True);
        Assert.That(main.Accepted, Is.GreaterThanOrEqualTo(2), "it tried the main service the set number of times first");
        Assert.That(fallback.Requests.First(), Does.StartWith("GET /FALLBACK "));
        NtripNoticeEventArgs notice = notices.Single(n => n.Kind == NtripNoticeKind.SwitchedToFallback);
        Assert.That(notice.Automatic, Is.True);
        Assert.That(notice.Message, Does.Contain("RTK"));
        Assert.That(svc.Events.Select(e => e.Message), Has.Some.Contain("The main service failed"));
        svc.Dispose();
    }

    [Test]
    public async Task AWrongLogin_SwitchesAtOnce_WithoutWaitingForTheCount()
    {
        using var main = new NtripFakeCaster((_, _, s) => NtripFakeCaster.SendAsync(s, Denied));
        using var fallback = new NtripFakeCaster((_, _, s) => Accept(s));
        var (svc, _) = Client();

        await svc.ConnectAsync(Config(main.Port, fallback.Port, autoSwitch: true, after: 5));

        Assert.That(WaitFor(() => svc.Using == NtripService.Fallback, 4000), Is.True);
        Assert.That(svc.Events.Select(e => e.Message), Has.None.Contain("Trying again"), "no retry came first: a wrong login is final");
        svc.Dispose();
    }

    [Test]
    public async Task WithAutoSwitchOff_TheFailureIsReported_AndTheMainServiceStaysInUse()
    {
        using var main = new NtripFakeCaster((_, _, s) => NtripFakeCaster.SendAsync(s, Denied));
        using var fallback = new NtripFakeCaster((_, _, s) => Accept(s));
        var (svc, notices) = Client();

        await svc.ConnectAsync(Config(main.Port, fallback.Port, autoSwitch: false));

        Assert.That(WaitFor(() => svc.Status.State == NtripState.GaveUp), Is.True);
        Thread.Sleep(500);
        Assert.That(svc.Using, Is.EqualTo(NtripService.Main));
        Assert.That(fallback.Accepted, Is.EqualTo(0));
        Assert.That(notices, Is.Empty);
        svc.Dispose();
    }

    [Test]
    public async Task TheOperatorSwitchesAtAnyTime_InBothDirections()
    {
        using var main = new NtripFakeCaster((_, _, s) => Accept(s));
        using var fallback = new NtripFakeCaster((_, _, s) => Accept(s));
        var (svc, notices) = Client();
        await svc.ConnectAsync(Config(main.Port, fallback.Port, autoSwitch: false));
        Assert.That(WaitFor(() => svc.Status.State == NtripState.Connected), Is.True);

        await svc.SwitchToAsync(NtripService.Fallback);
        Assert.That(WaitFor(() => fallback.Accepted == 1 && svc.Status.State == NtripState.Connected), Is.True);
        Assert.That(svc.Using, Is.EqualTo(NtripService.Fallback));

        await svc.SwitchToAsync(NtripService.Main);
        Assert.That(WaitFor(() => main.Accepted == 2 && svc.Status.State == NtripState.Connected), Is.True);
        Assert.That(svc.Using, Is.EqualTo(NtripService.Main));

        Assert.That(notices.Select(n => (n.Kind, n.Automatic)), Is.EqualTo(new[]
        {
            (NtripNoticeKind.SwitchedToFallback, false), (NtripNoticeKind.SwitchedToMain, false),
        }));
        svc.Dispose();
    }

    [Test]
    public async Task ASwitchToAFallbackThatIsNotThere_DoesNothing()
    {
        using var main = new NtripFakeCaster((_, _, s) => Accept(s));
        var (svc, notices) = Client();
        NtripConfiguration config = Config(main.Port, 1, autoSwitch: false);
        config.Fallback = null;
        await svc.ConnectAsync(config);
        Assert.That(WaitFor(() => svc.Status.State == NtripState.Connected), Is.True);

        await svc.SwitchToAsync(NtripService.Fallback);

        Assert.That(svc.Using, Is.EqualTo(NtripService.Main));
        Assert.That(notices, Is.Empty);
        svc.Dispose();
    }

    [Test]
    public async Task WhenTheMainServiceAnswersAgain_TheOperatorIsTold_AndTheFallbackStaysInUse()
    {
        // The main service fails twice, then recovers: the background try (the third connection) is accepted.
        using var main = new NtripFakeCaster((connection, _, s) => connection <= 2 ? NtripFakeCaster.SendAsync(s, Busy) : Accept(s));
        using var fallback = new NtripFakeCaster((_, _, s) => Accept(s));
        var (svc, notices) = Client();

        await svc.ConnectAsync(Config(main.Port, fallback.Port, autoSwitch: true, after: 2));

        Assert.That(WaitFor(() => svc.MainIsBack), Is.True);
        Assert.That(notices.Select(n => n.Kind), Is.EqualTo(new[] { NtripNoticeKind.SwitchedToFallback, NtripNoticeKind.MainBack }));
        Thread.Sleep(500);
        Assert.That(svc.Using, Is.EqualTo(NtripService.Fallback), "never back by itself");
        Assert.That(main.Accepted, Is.EqualTo(3), "the probe closes once the main service has answered");

        await svc.SwitchToAsync(NtripService.Main);
        Assert.That(svc.MainIsBack, Is.False, "the notice is over once the operator switched");
        svc.Dispose();
    }

    [Test]
    public async Task TheSameLoginOnTheSameCaster_IsNotTriedInTheBackground()
    {
        // One subscription with two mount points: a second connection could push the fallback's off.
        using var caster = new NtripFakeCaster((_, request, s) =>
            request.StartsWith("GET /MAIN ") ? NtripFakeCaster.SendAsync(s, Busy) : Accept(s));
        var (svc, _) = Client();
        NtripConfiguration config = Config(caster.Port, caster.Port, autoSwitch: true, fallbackUser: "mu", after: 2);

        await svc.ConnectAsync(config);

        Assert.That(WaitFor(() => svc.Using == NtripService.Fallback && svc.Status.State == NtripState.Connected), Is.True);
        Thread.Sleep(500);
        Assert.That(caster.Requests.Count(r => r.StartsWith("GET /MAIN ")), Is.EqualTo(2), "no background try of the main mount point");
        Assert.That(svc.MainIsBack, Is.False);
        svc.Dispose();
    }

    [Test]
    public async Task Disconnecting_StopsTheBackgroundTry_AndForgetsTheSwitch()
    {
        using var main = new NtripFakeCaster((_, _, s) => NtripFakeCaster.SendAsync(s, Denied));
        using var fallback = new NtripFakeCaster((_, _, s) => Accept(s));
        var (svc, _) = Client();
        await svc.ConnectAsync(Config(main.Port, fallback.Port, autoSwitch: true));
        Assert.That(WaitFor(() => svc.Using == NtripService.Fallback), Is.True);

        await svc.DisconnectAsync();
        int after = main.Accepted;
        Thread.Sleep(500);

        Assert.That(svc.Using, Is.EqualTo(NtripService.Main));
        Assert.That(main.Accepted, Is.EqualTo(after), "nothing tries the main service any more");
        Assert.That(svc.IsActive, Is.False);
        svc.Dispose();
    }

    [Test]
    public async Task OnceDisconnected_NoReaderSeesTheFallbackAnyMore()
    {
        using var main = new NtripFakeCaster((_, _, s) => NtripFakeCaster.SendAsync(s, Denied));
        using var fallback = new NtripFakeCaster((_, _, s) => Accept(s));
        var (svc, _) = Client();
        NtripService? seen = null;
        svc.ConnectionStatusChanged += (_, e) => { if (e.Message == "Disconnected") seen = svc.Using; };
        await svc.ConnectAsync(Config(main.Port, fallback.Port, autoSwitch: true));
        Assert.That(WaitFor(() => svc.Using == NtripService.Fallback), Is.True);

        await svc.DisconnectAsync();

        Assert.That(seen, Is.EqualTo(NtripService.Main), "the 'Disconnected' event already shows the main service");
        svc.Dispose();
    }

    [Test]
    public async Task TheBackgroundTry_SendsThePositionToo_AsAVrsMainServiceNeedsIt()
    {
        using var main = new NtripFakeCaster((_, _, s) => NtripFakeCaster.SendAsync(s, Busy));
        using var fallback = new NtripFakeCaster((_, _, s) => Accept(s));
        var (svc, _) = Client();
        NtripConfiguration config = Config(main.Port, fallback.Port, autoSwitch: true, after: 1);
        config.GgaIntervalSeconds = 10;
        config.FallbackPositionProvider = () => (52.0, 5.0);

        await svc.ConnectAsync(config);

        Assert.That(WaitFor(() => svc.Using == NtripService.Fallback && main.Accepted >= 2), Is.True, "the session and then the background try");
        Assert.That(main.Requests.Last(), Does.Contain("Ntrip-GGA: $GPGGA"));
        svc.Dispose();
    }

    [Test]
    public async Task AMainMountPointThatIsDown_IsTriedAgain_AndTheOperatorIsToldWhenItAnswers()
    {
        // 401 is final for a session, but a mount point that is down often answers like that: the try starts over.
        using var main = new NtripFakeCaster((connection, _, s) => connection <= 3 ? NtripFakeCaster.SendAsync(s, Denied) : Accept(s));
        using var fallback = new NtripFakeCaster((_, _, s) => Accept(s));
        var (svc, notices) = Client();
        svc.ProbeBackoff = [0];

        await svc.ConnectAsync(Config(main.Port, fallback.Port, autoSwitch: true));

        Assert.That(WaitFor(() => svc.MainIsBack), Is.True);
        Assert.That(main.Accepted, Is.EqualTo(4), "the session, then three background tries, the last one answered");
        Assert.That(notices.Select(n => n.Kind), Is.EqualTo(new[] { NtripNoticeKind.SwitchedToFallback, NtripNoticeKind.MainBack }));
        svc.Dispose();
    }

    [Test]
    public async Task AManualSwitchWhileTheMainServiceIsStillConnecting_StartsNoBackgroundTry()
    {
        using var main = new NtripFakeCaster(async (_, _, _) => await Task.Delay(3000));   // never answers
        using var fallback = new NtripFakeCaster((_, _, s) => Accept(s));
        var (svc, notices) = Client();
        await svc.ConnectAsync(Config(main.Port, fallback.Port, autoSwitch: false));
        Assert.That(WaitFor(() => main.Accepted == 1), Is.True);

        await svc.SwitchToAsync(NtripService.Fallback);
        Thread.Sleep(500);

        Assert.That(main.Accepted, Is.EqualTo(1), "a main service that was only slow is not watched");
        Assert.That(notices.Select(n => n.Kind), Is.EqualTo(new[] { NtripNoticeKind.SwitchedToFallback }));
        svc.Dispose();
    }

    [Test]
    public async Task TheFallbacksLogin_IsMaskedLikeTheMainOnes()
    {
        using var main = new NtripFakeCaster((_, _, s) => NtripFakeCaster.SendAsync(s, Busy));
        using var fallback = new NtripFakeCaster((_, _, s) => NtripFakeCaster.SendAsync(s, "ERROR - bad login, your password FallbackSecret1\r\n\r\n"));
        var (svc, _) = Client();
        NtripConfiguration config = Config(main.Port, fallback.Port, autoSwitch: false);
        config.Fallback!.Password = "FallbackSecret1";

        await svc.ConnectAsync(config);
        await svc.SwitchToAsync(NtripService.Fallback);   // by hand: an automatic switch waits for a fallback that works

        Assert.That(WaitFor(() => svc.Using == NtripService.Fallback && svc.Status.State == NtripState.GaveUp), Is.True);
        Assert.That(svc.Status.FirstLine, Does.Contain("***").And.Not.Contain("FallbackSecret1"));
        Assert.That(NtripSupportReport.Build(svc, null), Does.Not.Contain("FallbackSecret1").And.Contain("the fallback service"));
        svc.Dispose();
    }

    [Test]
    public async Task WhenBothServicesFail_TheClientStaysOnTheMainService()
    {
        // A network dropout fails both: nothing is switched to until the fallback has delivered corrections.
        using var main = new NtripFakeCaster((_, _, s) => NtripFakeCaster.SendAsync(s, Busy));
        using var fallback = new NtripFakeCaster((_, _, s) => NtripFakeCaster.SendAsync(s, Busy));
        var (svc, notices) = Client();

        await svc.ConnectAsync(Config(main.Port, fallback.Port, autoSwitch: true, after: 1));

        Assert.That(WaitFor(() => fallback.Accepted >= 1), Is.True, "the fallback was tried");
        Assert.That(WaitFor(() => svc.Using == NtripService.Fallback, 1500), Is.False);
        Assert.That(notices.Where(n => n.Kind == NtripNoticeKind.SwitchedToFallback), Is.Empty);
        svc.Dispose();
    }

    [Test]
    public async Task AMainServiceThatAcceptsButSendsNothing_IsNotReportedAsBack()
    {
        // The caster is up and the base behind it is down: it accepts and sends nothing.
        using var main = new NtripFakeCaster((connection, _, s) => connection <= 2
            ? NtripFakeCaster.SendAsync(s, Busy)
            : NtripFakeCaster.SendAsync(s, "ICY 200 OK\r\n\r\n").ContinueWith(_ => Task.Delay(5000)).Unwrap());
        using var fallback = new NtripFakeCaster((_, _, s) => Accept(s));
        var (svc, _) = Client();
        svc.ProbeBackoff = [1];

        await svc.ConnectAsync(Config(main.Port, fallback.Port, autoSwitch: true, after: 1));

        Assert.That(WaitFor(() => svc.Using == NtripService.Fallback), Is.True);
        Assert.That(WaitFor(() => main.Accepted >= 3), Is.True, "the probe was accepted");
        Assert.That(WaitFor(() => svc.MainIsBack, 1500), Is.False);
        svc.Dispose();
    }
}
