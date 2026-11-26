// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using AgOpenWeb.Services.BugReport;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// Send Report: the outbox posts the dump to the bug report service, keeps it when there is no
/// connection or the service is busy, and drops it once sent or turned away. The reporter's
/// zip stays; the only change to it is contact.txt, the email they gave.
/// </summary>
[TestFixture, NonParallelizable]
public class BugReportOutboxTests
{
    private string _dir = null!;
    private DateTime _now;
    private FakeService _svc = null!;
    private BugReportOutbox _outbox = null!;

    private const string Zip = "bugreport_gps-out_2026-10-20_090000.zip";
    private static readonly BugReportMeta Meta = new("GPS_Out no speed", "Steps:\tone\ntwo", "crf767", "de");

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "brtest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        using (var z = ZipFile.Open(Path.Combine(_dir, Zip), ZipArchiveMode.Create))
            using (var w = new StreamWriter(z.CreateEntry("system_info.txt").Open())) w.Write("App Version: 1.0.0.0\n");
        _now = new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);
        _svc = new FakeService();
        _outbox = new BugReportOutbox(_dir, _svc, new Uri("https://reports.example/v1/reports"), () => _now);
    }

    [TearDown] public void TearDown() { _svc.Dispose(); try { Directory.Delete(_dir, true); } catch { } }

    private sealed class FakeService : HttpMessageHandler
    {
        public Queue<Func<HttpResponseMessage>> Replies { get; } = new();
        public List<(string Meta, byte[] Dump, string? DumpType)> Posts { get; } = new();
        public string LastContentType = "", LastBody = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var form = (MultipartFormDataContent)request.Content!;
            LastContentType = form.Headers.ContentType!.ToString();
            LastBody = await form.ReadAsStringAsync(ct);
            string meta = "";
            byte[] dump = Array.Empty<byte>();
            string? type = null;
            foreach (var part in form)
            {
                var name = part.Headers.ContentDisposition?.Name?.Trim('"');
                if (name == "meta") meta = await part.ReadAsStringAsync(ct);
                if (name == "dump") { dump = await part.ReadAsByteArrayAsync(ct); type = part.Headers.ContentType?.MediaType; }
            }
            Posts.Add((meta, dump, type));
            return Replies.Count > 0 ? Replies.Dequeue()() : Reply(HttpStatusCode.ServiceUnavailable, "{}");
        }
    }

    private static HttpResponseMessage Reply(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json) };

    private static Func<HttpResponseMessage> Offline => () => throw new HttpRequestException("No route to host");

    [Test]
    public async Task Sent_OpensTheIssue_AndLeavesTheReportersZip()
    {
        _svc.Replies.Enqueue(() => Reply(HttpStatusCode.Created, "{\"issue\":412,\"url\":\"https://github.com/o/r/issues/412\"}"));
        _outbox.Enqueue(Zip, Meta);

        var results = await _outbox.SendDueAsync();

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Zip, Is.EqualTo(Zip));
        Assert.That(results[0].Result, Is.EqualTo(new BugReportSendResult(BugReportOutcome.Sent, 412, "https://github.com/o/r/issues/412")));
        Assert.That(_outbox.Pending, Is.Zero);
        Assert.That(File.Exists(Path.Combine(_dir, Zip)), Is.True);

        var (meta, dump, type) = _svc.Posts.Single();
        using var j = JsonDocument.Parse(meta);
        Assert.Multiple(() =>
        {
            Assert.That(j.RootElement.GetProperty("title").GetString(), Is.EqualTo("GPS_Out no speed"));
            Assert.That(j.RootElement.GetProperty("description").GetString(), Is.EqualTo("Steps:\tone\ntwo"));
            Assert.That(j.RootElement.GetProperty("github").GetString(), Is.EqualTo("crf767"));
            Assert.That(j.RootElement.GetProperty("language").GetString(), Is.EqualTo("de"));
            Assert.That(j.RootElement.GetProperty("platform").GetString(), Is.Not.Empty);
            Assert.That(dump, Is.EqualTo(File.ReadAllBytes(Path.Combine(_dir, Zip))));
            Assert.That(type, Is.EqualTo("application/zip"));
        });
    }

    [Test]
    public async Task Email_GoesIntoTheDump_NeverIntoTheMetadata()
    {
        _svc.Replies.Enqueue(() => Reply(HttpStatusCode.Created, "{\"issue\":1,\"url\":\"u\"}"));
        _outbox.Enqueue(Zip, Meta with { GitHub = "", Email = " grower@example.com " });

        await _outbox.SendDueAsync();

        var (meta, dump, _) = _svc.Posts.Single();
        Assert.That(meta, Does.Not.Contain("grower@example.com"));
        using var z = new ZipArchive(new MemoryStream(dump));
        using var r = new StreamReader(z.GetEntry(BugReportOutbox.ContactEntry)!.Open());
        Assert.That(r.ReadToEnd(), Is.EqualTo("Email: grower@example.com\n"));
        Assert.That(z.GetEntry("system_info.txt"), Is.Not.Null);
    }

    [Test]
    public void Email_Cleared_RemovesTheContactFromTheDump()
    {
        _outbox.Enqueue(Zip, Meta with { Email = "grower@example.com" });
        _outbox.Enqueue(Zip, Meta);

        using var z = ZipFile.OpenRead(Path.Combine(_dir, Zip));
        Assert.That(z.GetEntry(BugReportOutbox.ContactEntry), Is.Null);
    }

    [Test]
    public async Task Form_IsWhatTheWorkersRuntimeParses()
    {
        // workerd turned away .NET's default form (unquoted part names, quoted boundary).
        _svc.Replies.Enqueue(() => Reply(HttpStatusCode.Created, "{\"issue\":1,\"url\":\"u\"}"));
        _outbox.Enqueue(Zip, Meta);
        await _outbox.SendDueAsync();
        Assert.Multiple(() =>
        {
            Assert.That(_svc.LastContentType, Does.Match("^multipart/form-data; boundary=[0-9a-f]{32}$"));
            Assert.That(_svc.LastBody, Does.Contain("Content-Disposition: form-data; name=\"meta\"\r\n"));
            Assert.That(_svc.LastBody, Does.Contain($"Content-Disposition: form-data; name=\"dump\"; filename=\"{Zip}\"\r\n"));
        });
    }

    [Test]
    public async Task Offline_StaysQueued_AndGoesWhenDue()
    {
        _svc.Replies.Enqueue(Offline);
        _outbox.Enqueue(Zip, Meta);

        var first = await _outbox.SendDueAsync();
        Assert.That(first.Single().Result.Outcome, Is.EqualTo(BugReportOutcome.Retry));
        Assert.That(_outbox.Pending, Is.EqualTo(1));

        _now = _now.AddSeconds(30); // 1 minute back-off not over
        Assert.That(await _outbox.SendDueAsync(), Is.Empty);
        Assert.That(_svc.Posts, Has.Count.EqualTo(1));

        _now = _now.AddSeconds(31);
        _svc.Replies.Enqueue(() => Reply(HttpStatusCode.Created, "{\"issue\":7,\"url\":\"u\"}"));
        var second = await _outbox.SendDueAsync();
        Assert.That(second.Single().Result.Issue, Is.EqualTo(7));
        Assert.That(_outbox.Pending, Is.Zero);
        Assert.That(Directory.GetFiles(Path.Combine(_dir, "outbox")), Is.Empty, "no .bak/.tmp left behind");
    }

    [Test]
    public async Task NetworkBack_SendsAtOnce_ButNotWithinAMinuteOfTheLastTry()
    {
        // #385: out of signal for a while, the report waits out a 30 min backoff; when the
        // network comes back it goes now. A link that flaps doesn't resend within a minute.
        for (int i = 0; i < 3; i++) _svc.Replies.Enqueue(Offline);
        _outbox.Enqueue(Zip, Meta);
        await _outbox.SendDueAsync();                 // try 1 → wait 1 min
        _now = _now.AddMinutes(1); await _outbox.SendDueAsync(); // try 2 → wait 5 min
        _now = _now.AddMinutes(5); await _outbox.SendDueAsync(); // try 3 → wait 30 min
        Assert.That(_svc.Posts, Has.Count.EqualTo(3));

        _now = _now.AddSeconds(40);
        Assert.That(await _outbox.SendDueAsync(networkChanged: true), Is.Empty, "tried 40 s ago");

        _now = _now.AddSeconds(30);
        _svc.Replies.Enqueue(() => Reply(HttpStatusCode.Created, "{\"issue\":8,\"url\":\"u\"}"));
        Assert.That(await _outbox.SendDueAsync(), Is.Empty, "no change: still waits for its 30 min");
        Assert.That((await _outbox.SendDueAsync(networkChanged: true)).Single().Result.Issue, Is.EqualTo(8));
    }

    [Test]
    public async Task Queue_SurvivesARestart()
    {
        _svc.Replies.Enqueue(Offline);
        _outbox.Enqueue(Zip, Meta);
        await _outbox.SendDueAsync();

        // A new outbox over the same folder, as after the app restarts an hour later.
        using var svc = new FakeService();
        svc.Replies.Enqueue(() => Reply(HttpStatusCode.Created, "{\"issue\":9,\"url\":\"u\"}"));
        _now = _now.AddHours(1);
        var again = new BugReportOutbox(_dir, svc, new Uri("https://reports.example/v1/reports"), () => _now);

        Assert.That((await again.SendDueAsync()).Single().Result.Issue, Is.EqualTo(9));
        Assert.That(svc.Posts.Single().Meta, Does.Contain("GPS_Out no speed"));
    }

    [Test]
    public async Task BackOff_1_5_30_ThenHourly()
    {
        for (int i = 0; i < 5; i++) _svc.Replies.Enqueue(Offline);
        _outbox.Enqueue(Zip, Meta);
        var gaps = new List<double>();
        var last = _now;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            while ((await _outbox.SendDueAsync()).Count == 0) _now = _now.AddSeconds(15);
            gaps.Add((_now - last).TotalMinutes);
            last = _now;
        }
        Assert.That(gaps, Is.EqualTo(new[] { 0.0, 1, 5, 30, 60 }));
    }

    [Test]
    public async Task Busy_429_And_ServerErrors_Retry()
    {
        _svc.Replies.Enqueue(() => Reply(HttpStatusCode.TooManyRequests, "{\"error\":\"Too many reports; try again in a minute.\"}"));
        _outbox.Enqueue(Zip, Meta);
        var r = (await _outbox.SendDueAsync()).Single().Result;
        Assert.That(r, Is.EqualTo(new BugReportSendResult(BugReportOutcome.Retry, Message: "Too many reports; try again in a minute.")));
        Assert.That(_outbox.Pending, Is.EqualTo(1));

        _now = _now.AddMinutes(1);
        _svc.Replies.Enqueue(() => Reply(HttpStatusCode.BadGateway, "<html>"));
        Assert.That((await _outbox.SendDueAsync()).Single().Result.Outcome, Is.EqualTo(BugReportOutcome.Retry));
    }

    [Test]
    public async Task TurnedAway_IsDropped_WithTheServicesReason()
    {
        _svc.Replies.Enqueue(() => Reply(HttpStatusCode.BadRequest, "{\"error\":\"Not an AgOpenWeb bug report.\"}"));
        _outbox.Enqueue(Zip, Meta);
        var r = (await _outbox.SendDueAsync()).Single().Result;
        Assert.That(r, Is.EqualTo(new BugReportSendResult(BugReportOutcome.Rejected, Message: "Not an AgOpenWeb bug report.")));
        Assert.That(_outbox.Pending, Is.Zero);
        Assert.That(File.Exists(Path.Combine(_dir, Zip)), Is.True);
    }

    [Test]
    public async Task Accepted_WithoutIssueYet_CountsAsSent()
    {
        _svc.Replies.Enqueue(() => Reply(HttpStatusCode.Accepted, "{\"id\":\"2026/10/20/abc.zip\"}"));
        _outbox.Enqueue(Zip, Meta);
        Assert.That((await _outbox.SendDueAsync()).Single().Result, Is.EqualTo(new BugReportSendResult(BugReportOutcome.Sent)));
        Assert.That(_outbox.Pending, Is.Zero);
    }

    [Test]
    public async Task GivesUp_After14Days()
    {
        _outbox.Enqueue(Zip, Meta);
        _now = _now.AddDays(14);
        _svc.Replies.Enqueue(Offline);
        var r = (await _outbox.SendDueAsync()).Single().Result;
        Assert.That(r.Outcome, Is.EqualTo(BugReportOutcome.Rejected));
        Assert.That(r.Message, Does.StartWith("Gave up after 14 days"));
        Assert.That(_outbox.Pending, Is.Zero);
    }

    [Test]
    public async Task ZipDeletedByTheReporter_DropsTheQueuedReport()
    {
        _outbox.Enqueue(Zip, Meta);
        File.Delete(Path.Combine(_dir, Zip));
        Assert.That(await _outbox.SendDueAsync(), Is.Empty);
        Assert.That(_outbox.Pending, Is.Zero);
        Assert.That(_svc.Posts, Is.Empty);
    }

    [Test]
    public void Enqueue_OnlyTakesAZipInTheReportsFolder()
    {
        Assert.Throws<FileNotFoundException>(() => _outbox.Enqueue("../" + Zip, Meta));
        Assert.Throws<FileNotFoundException>(() => _outbox.Enqueue("missing.zip", Meta));
        Assert.That(_outbox.Pending, Is.Zero);
    }
}
