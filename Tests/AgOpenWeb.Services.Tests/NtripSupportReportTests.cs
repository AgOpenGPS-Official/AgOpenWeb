// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.IO.Compression;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services.Interfaces;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// The NTRIP part of the bug report: it says what the connection did and why it failed, and it
/// never holds the login, not even when a caster repeats it in its reply.
/// </summary>
[TestFixture, NonParallelizable]
public class NtripSupportReportTests
{
    private const string Password = "S3cretPassw0rd";
    private const string User = "farmer@example.com";

    /// <summary>Keeps every log line, to check none holds the login.</summary>
    private sealed class CapturingLogger : ILogger<NtripClientService>
    {
        public readonly ConcurrentQueue<string> Lines = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Enqueue(formatter(state, exception));
    }

    private static NtripConfiguration Config(int port) => new()
    {
        CasterAddress = "127.0.0.1", CasterPort = port, MountPoint = "MOUNT",
        Username = User, Password = Password, SubnetAddress = "127.0.0", UdpForwardPort = 0, GgaIntervalSeconds = 0,
    };

    private static bool WaitFor(Func<bool> condition, int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (condition()) return true; Thread.Sleep(10); }
        return condition();
    }

    private static (NtripClientService svc, CapturingLogger log) Client()
    {
        var log = new CapturingLogger();
        return (new NtripClientService(Substitute.For<IGpsService>(), log), log);
    }

    [Test]
    public void BeforeNtripIsUsed_ThereIsNoReport()
    {
        var (svc, _) = Client();

        Assert.That(NtripSupportReport.Build(svc, null), Is.Null);
        Assert.That(NtripSupportReport.Summary(svc), Is.Null);
    }

    [Test]
    public async Task AWrongLogin_IsReadableFromTheReport()
    {
        using var caster = new NtripFakeCaster((_, _, s) => NtripFakeCaster.SendAsync(s, "HTTP/1.1 401 Unauthorized\r\n\r\n"));
        var (svc, _) = Client();

        await svc.ConnectAsync(Config(caster.Port));
        Assert.That(WaitFor(() => svc.Status.State == NtripState.GaveUp), Is.True);

        string report = NtripSupportReport.Build(svc, null)!;
        Assert.That(report, Does.Contain("MOUNT@127.0.0.1:" + caster.Port));
        Assert.That(report, Does.Contain("user name yes, password yes"));
        Assert.That(report, Does.Contain("State:             GaveUp"));
        Assert.That(report, Does.Contain("Last failure:      LoginRejected: Authentication failed"));
        Assert.That(report, Does.Contain("Authentication failed"));
        Assert.That(report, Does.Contain("caster said: HTTP/1.1 401 Unauthorized"));
        Assert.That(report, Does.Contain("Connecting").And.Contain("GaveUp"), "the steps are listed");
        Assert.That(NtripSupportReport.Summary(svc), Does.StartWith("GaveUp, LoginRejected, MOUNT@127.0.0.1:"));
        svc.Dispose();
    }

    [Test]
    public async Task ARetryingSession_ListsEachFailureOnce()
    {
        using var caster = new NtripFakeCaster((_, _, s) => NtripFakeCaster.SendAsync(s, "HTTP/1.1 503 Service Unavailable\r\n\r\n"));
        var (svc, _) = Client();

        await svc.ConnectAsync(Config(caster.Port));
        Assert.That(WaitFor(() => caster.Accepted >= 3), Is.True);
        await svc.DisconnectAsync();

        string[] steps = svc.Events.Select(e => e.Message).ToArray();
        Assert.That(steps, Has.Some.Contain("Trying again in 1 s"));
        Assert.That(steps, Has.Some.Contain("Trying again in 2 s"));
        Assert.That(steps.Last(), Is.EqualTo("Disconnected"));
        Assert.That(steps.Zip(steps.Skip(1)).Any(pair => pair.First == pair.Second), Is.False, "a step that repeats the last one is not written twice");
        svc.Dispose();
    }

    [Test]
    public async Task TheLogin_IsInNoPlace_EvenWhenTheCasterRepeatsIt()
    {
        string basic = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(User + ":" + Password));
        using var caster = new NtripFakeCaster((_, _, s) => NtripFakeCaster.SendAsync(s,
            $"ERROR - bad login for {User} with {Password}, token {basic}\r\n\r\n"));
        var (svc, log) = Client();

        await svc.ConnectAsync(Config(caster.Port));
        Assert.That(WaitFor(() => svc.Status.State == NtripState.GaveUp), Is.True);

        var everywhere = new List<string>
        {
            NtripSupportReport.Build(svc, null)!, NtripSupportReport.Summary(svc)!,
            svc.Status.Message, svc.Status.FirstLine,
        };
        everywhere.AddRange(svc.Events.Select(e => e.Message + " " + e.FirstLine));
        everywhere.AddRange(log.Lines);
        Assert.That(everywhere, Has.None.Contain(Password));
        Assert.That(everywhere, Has.None.Contain(User));
        Assert.That(everywhere, Has.None.Contain(basic));
        Assert.That(svc.Status.FirstLine, Does.Contain("***"));
        svc.Dispose();
    }

    [Test]
    public async Task TheDump_CarriesTheSessionAndAnNtripLineInSystemInfo()
    {
        using var caster = new NtripFakeCaster((_, _, s) => NtripFakeCaster.SendAsync(s, "HTTP/1.1 404 Not Found\r\n\r\n"));
        var (svc, _) = Client();
        await svc.ConnectAsync(Config(caster.Port));
        Assert.That(WaitFor(() => svc.Status.State == NtripState.GaveUp), Is.True);

        string dir = Path.Combine(Path.GetTempPath(), "ntripdump_" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = Substitute.For<ISettingsService>();
            settings.Settings.Returns(new AppSettings());
            string zip = DebugDumpService.CreateDump(settings, RuntimeStateCapture.From(new ApplicationState()), new ConfigurationStore(),
                outputDirectory: dir, ntripSessionReport: NtripSupportReport.Build(svc, null), ntripSummary: NtripSupportReport.Summary(svc));

            using var archive = ZipFile.OpenRead(zip);
            string Read(string name) { using var reader = new StreamReader(archive.GetEntry(name)!.Open()); return reader.ReadToEnd(); }
            Assert.That(Read("ntrip_session.txt"), Does.Contain("Last failure:      UnknownMountpoint"));
            Assert.That(Read("system_info.txt"), Does.Contain("NTRIP: GaveUp, UnknownMountpoint, MOUNT@127.0.0.1:"));
            Assert.That(Read("ntrip_session.txt") + Read("system_info.txt"), Does.Not.Contain(Password));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* temp folder */ }
            svc.Dispose();
        }
    }

    [Test]
    public async Task ACasterThatRepeatsTheLoginInOtherForms_IsMaskedToo()
    {
        // Upper case, URL-escaped (as in a redirect), and the password with a character that gets escaped.
        using var caster = new NtripFakeCaster((_, _, s) => NtripFakeCaster.SendAsync(s,
            "ERROR - bad login FARMER@EXAMPLE.COM, farmer%40example.com, S3cret%21pw\r\n\r\n"));
        var (svc, log) = Client();
        var config = Config(caster.Port);
        config.Password = "S3cret!pw";

        await svc.ConnectAsync(config);
        Assert.That(WaitFor(() => svc.Status.State == NtripState.GaveUp), Is.True);

        string everything = NtripSupportReport.Build(svc, null) + string.Join("\n", log.Lines) + svc.Status.Message + svc.Status.FirstLine;
        Assert.That(everything.ToLowerInvariant(), Does.Not.Contain("farmer").And.Not.Contain("s3cret"));
        svc.Dispose();
    }

    [Test]
    public async Task AUserNameThatIsAlsoTheMountPoint_IsMaskedInWhatTheCasterSaid_NotInWhatTheClientWrote()
    {
        using var caster = new NtripFakeCaster((_, _, s) => NtripFakeCaster.SendAsync(s, "ERROR - AGO unknown login\r\n\r\n"));
        var (svc, _) = Client();
        var config = Config(caster.Port);
        config.Username = "AGO";
        config.MountPoint = "AGO";

        await svc.ConnectAsync(config);
        Assert.That(WaitFor(() => svc.Status.State == NtripState.GaveUp), Is.True);

        Assert.That(svc.Status.FirstLine, Is.EqualTo("ERROR - *** unknown login"));
        Assert.That(svc.Events.First().Message, Does.Contain("/AGO"), "the client's own text keeps the mount point");
        Assert.That(svc.Service!.ToString(), Does.StartWith("AGO@"));
        svc.Dispose();
    }

    [Test]
    public async Task AfterTheUserDisconnects_TheSummaryStillSaysWhatWentWrong()
    {
        using var caster = new NtripFakeCaster((_, _, s) => NtripFakeCaster.SendAsync(s, "HTTP/1.1 401 Unauthorized\r\n\r\n"));
        var (svc, _) = Client();
        await svc.ConnectAsync(Config(caster.Port));
        Assert.That(WaitFor(() => svc.Status.State == NtripState.GaveUp), Is.True);

        await svc.DisconnectAsync();

        Assert.That(NtripSupportReport.Summary(svc), Does.StartWith("Stopped by the user, MOUNT@127.0.0.1:").And.Contain("before that GaveUp, LoginRejected"));
        string report = NtripSupportReport.Build(svc, null)!;
        Assert.That(report, Does.Contain("State:             Stopped"));
        Assert.That(report, Does.Contain("Last failure:      LoginRejected"));
        svc.Dispose();
    }

    [Test]
    public void TheEventLog_KeepsTheLastFortySteps_AndSkipsARepeat()
    {
        var log = new NtripEventLog();
        NtripEvent Step(string message, int attempts = 0) =>
            new(DateTime.Now, "s", NtripState.Waiting, NtripCategory.CasterBusy, message, "", NtripVersion.V2, attempts);

        log.Add(Step("a"));
        log.Add(Step("a"));
        Assert.That(log.ToArray(), Has.Count.EqualTo(1), "the same step twice is one");
        log.Add(Step("a", attempts: 1));
        Assert.That(log.ToArray(), Has.Count.EqualTo(2), "the same text at another attempt is kept: a long outage still counts");

        for (int i = 0; i < 100; i++) log.Add(Step("step " + i));
        IReadOnlyList<NtripEvent> kept = log.ToArray();
        Assert.That(kept, Has.Count.EqualTo(NtripEventLog.MaxEvents));
        Assert.That(kept[^1].Message, Is.EqualTo("step 99"));
        Assert.That(kept[0].Message, Is.EqualTo("step 60"));
    }

    [Test]
    public void ADumpWithoutNtrip_HasNoSessionFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ntripdump_" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = Substitute.For<ISettingsService>();
            settings.Settings.Returns(new AppSettings());
            string zip = DebugDumpService.CreateDump(settings, RuntimeStateCapture.From(new ApplicationState()), new ConfigurationStore(), outputDirectory: dir);

            using var archive = ZipFile.OpenRead(zip);
            Assert.That(archive.GetEntry("ntrip_session.txt"), Is.Null);
            Assert.That(new StreamReader(archive.GetEntry("system_info.txt")!.Open()).ReadToEnd(), Does.Not.Contain("NTRIP:"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* temp folder */ }
        }
    }
}
