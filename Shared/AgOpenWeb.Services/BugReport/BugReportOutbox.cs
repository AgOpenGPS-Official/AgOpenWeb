// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgOpenWeb.Services.Storage;

namespace AgOpenWeb.Services.BugReport;

/// <summary>What the reporter typed, sent beside the dump (the dump has its own copy). The
/// email never goes in the metadata: it is written into the dump as contact.txt, which only
/// the maintainers can download.</summary>
public sealed record BugReportMeta(string Title, string Description, string GitHub, string Language, string Email = "");

public enum BugReportOutcome { Sent, Retry, Rejected }

/// <summary>One send attempt. <see cref="Issue"/> is null when the service stored the dump but
/// opens the issue later (GitHub was down).</summary>
public sealed record BugReportSendResult(BugReportOutcome Outcome, int? Issue = null, string? Url = null, string Message = "");

/// <summary>
/// Sends bug report dumps to the AgOpenWeb bug report service (Tools/bugreport-worker) and
/// keeps the ones that couldn't go yet: a tractor is often out of signal. Each queued report
/// is a sidecar <c>outbox/&lt;zip name&gt;.json</c> beside the dumps; the zip itself stays where
/// the reporter's copy is. Retries back off (1, 5, 30 min, then hourly) and stop after 14
/// days. Plans/BUG_REPORT_SUBMISSION_PLAN.md.
/// </summary>
public sealed class BugReportOutbox
{
    public static readonly Uri DefaultEndpoint = new("https://agopenweb-bugreports.agopenweb.workers.dev/v1/reports");

    private static readonly TimeSpan[] Backoff =
        { TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1) };
    internal static readonly TimeSpan GiveUpAfter = TimeSpan.FromDays(14);
    // When the network comes back a report goes at once rather than at its backoff time,
    // unless it was tried this recently (a link that keeps flapping doesn't resend MBs).
    internal static readonly TimeSpan MinRetrySpacing = TimeSpan.FromMinutes(1);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _dir;
    private readonly Uri _endpoint;
    private readonly HttpClient _http;
    private readonly Func<DateTime> _utcNow;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public BugReportOutbox(string reportsDirectory, HttpMessageHandler? handler = null,
        Uri? endpoint = null, Func<DateTime>? utcNow = null)
    {
        _dir = reportsDirectory;
        _endpoint = endpoint ?? DefaultEndpoint;
        _http = handler != null ? new HttpClient(handler) : new HttpClient();
        _http.Timeout = TimeSpan.FromMinutes(2); // a few MB over a weak mobile link
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    private string OutboxDir => Path.Combine(_dir, "outbox");

    internal sealed class Entry
    {
        public string Zip { get; set; } = "";
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public string GitHub { get; set; } = "";
        public string Language { get; set; } = "";
        public int Attempts { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime NextAttemptUtc { get; set; }
        public DateTime LastAttemptUtc { get; set; }
    }

    /// <summary>Reports still waiting to go.</summary>
    public int Pending => Directory.Exists(OutboxDir) ? Directory.GetFiles(OutboxDir, "*.json").Length : 0;

    /// <summary>The dump entry that carries the reporter's email.</summary>
    public const string ContactEntry = "contact.txt";

    /// <summary>Queue a dump (a zip in the reports directory) to send now.</summary>
    public void Enqueue(string zipName, BugReportMeta meta)
    {
        if (zipName != Path.GetFileName(zipName) || !File.Exists(Path.Combine(_dir, zipName)))
            throw new FileNotFoundException("No such bug report", zipName);
        WriteContact(Path.Combine(_dir, zipName), meta.Email.Trim());
        Directory.CreateDirectory(OutboxDir);
        var now = _utcNow();
        Save(new Entry
        {
            Zip = zipName, Title = meta.Title, Description = meta.Description,
            GitHub = meta.GitHub, Language = meta.Language,
            CreatedUtc = now, NextAttemptUtc = now,
        });
    }

    /// <summary>Send every queued report that is due. Returns each one's result (zip name →
    /// result); reports not yet due are left out. One pass at a time. With
    /// <paramref name="networkChanged"/>, a report waiting out its backoff goes now too, unless
    /// it was tried in the last <see cref="MinRetrySpacing"/>.</summary>
    public async Task<IReadOnlyList<(string Zip, BugReportSendResult Result)>> SendDueAsync(
        CancellationToken ct = default, bool networkChanged = false)
    {
        var results = new List<(string, BugReportSendResult)>();
        if (!Directory.Exists(OutboxDir)) return results;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var path in Directory.GetFiles(OutboxDir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
            {
                Entry? e;
                try { e = JsonSerializer.Deserialize<Entry>(File.ReadAllText(path), Json); }
                catch (Exception) { e = null; }
                var zip = Path.Combine(_dir, e?.Zip ?? "");
                if (e == null || e.Zip.Length == 0 || !File.Exists(zip))
                {
                    Remove(path); // the reporter deleted the zip, or the sidecar is unreadable
                    continue;
                }
                var now = _utcNow();
                bool due = e.NextAttemptUtc <= now
                    || (networkChanged && now - e.LastAttemptUtc >= MinRetrySpacing);
                if (!due) continue;
                e.LastAttemptUtc = now;

                var r = await PostAsync(zip, e, ct).ConfigureAwait(false);
                if (r.Outcome == BugReportOutcome.Retry)
                {
                    e.Attempts++;
                    if (now - e.CreatedUtc >= GiveUpAfter)
                    {
                        Remove(path);
                        r = new BugReportSendResult(BugReportOutcome.Rejected, Message: "Gave up after 14 days: " + r.Message);
                    }
                    else
                    {
                        e.NextAttemptUtc = now + Backoff[Math.Min(e.Attempts, Backoff.Length) - 1];
                        Save(e);
                    }
                }
                else Remove(path);
                results.Add((e.Zip, r));
            }
        }
        finally { _gate.Release(); }
        return results;
    }

    // contact.txt in the dump holds the email as of this send; an empty one removes it.
    private static void WriteContact(string zipPath, string email)
    {
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Update);
        zip.GetEntry(ContactEntry)?.Delete();
        if (email.Length == 0) return;
        using var w = new StreamWriter(zip.CreateEntry(ContactEntry).Open());
        w.Write("Email: " + email + "\n");
    }

    // The sidecar, and the backup and scratch copies AtomicJsonFile keeps beside it.
    private static void Remove(string path)
    {
        foreach (var p in new[] { path, path + AtomicJsonFile.BackupSuffix, path + AtomicJsonFile.TempSuffix })
            if (File.Exists(p)) File.Delete(p);
    }

    private void Save(Entry e) =>
        AtomicJsonFile.WriteJson(Path.Combine(OutboxDir, e.Zip + ".json"), e, Json);

    private async Task<BugReportSendResult> PostAsync(string zipPath, Entry e, CancellationToken ct)
    {
        try
        {
            // Quoted part names and a bare boundary: .NET's defaults (name=meta,
            // boundary="…") are valid but the Workers runtime's form parser refuses them.
            var boundary = Guid.NewGuid().ToString("N");
            using var form = new MultipartFormDataContent(boundary);
            form.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=" + boundary);
            var meta = JsonSerializer.Serialize(new
            {
                title = e.Title, description = e.Description, github = e.GitHub,
                language = e.Language, platform = Platform(), version = Version(),
            });
            var metaPart = new StringContent(meta, System.Text.Encoding.UTF8, "application/json");
            metaPart.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = "\"meta\"" };
            form.Add(metaPart);
            await using var file = File.OpenRead(zipPath);
            var dump = new StreamContent(file);
            dump.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            dump.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
                { Name = "\"dump\"", FileName = "\"" + e.Zip + "\"" };
            form.Add(dump);

            using var res = await _http.PostAsync(_endpoint, form, ct).ConfigureAwait(false);
            var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Classify(res.StatusCode, body);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return new BugReportSendResult(BugReportOutcome.Retry, Message: ex.Message);
        }
    }

    internal static BugReportSendResult Classify(HttpStatusCode status, string body)
    {
        JsonElement j = default;
        try { j = JsonDocument.Parse(body).RootElement; } catch (JsonException) { }
        string Str(string name) => j.ValueKind == JsonValueKind.Object && j.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        int code = (int)status;
        if (code == 201 && j.ValueKind == JsonValueKind.Object && j.TryGetProperty("issue", out var n) && n.TryGetInt32(out var issue))
            return new BugReportSendResult(BugReportOutcome.Sent, issue, Str("url"));
        if (code is 201 or 202) return new BugReportSendResult(BugReportOutcome.Sent);
        var msg = Str("error");
        if (msg.Length == 0) msg = "HTTP " + code.ToString(CultureInfo.InvariantCulture);
        // 429 (busy, daily cap) and server trouble go again later; anything else the
        // service turned away will be turned away again.
        return code == 429 || code >= 500
            ? new BugReportSendResult(BugReportOutcome.Retry, Message: msg)
            : new BugReportSendResult(BugReportOutcome.Rejected, Message: msg);
    }

    private static string Platform() =>
        OperatingSystem.IsAndroid() ? "Android"
        : OperatingSystem.IsIOS() ? "iOS"
        : OperatingSystem.IsWindows() ? "Windows"
        : OperatingSystem.IsMacOS() ? "macOS"
        : OperatingSystem.IsLinux() ? "Linux" : "Other";

    private static string Version() =>
        typeof(BugReportOutbox).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "";
}
