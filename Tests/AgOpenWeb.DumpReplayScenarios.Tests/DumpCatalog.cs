// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgOpenWeb.DumpReplayScenarios.Tests;

/// <summary>One dump the harness replays: the report it came with and where the zip is.</summary>
public sealed class DumpEntry
{
    /// <summary>Short id, used in the report and for <c>AGOPENWEB_SCENARIO_ONLY</c> (the issue number,
    /// with a letter when the report has more than one dump).</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("issue")] public int Issue { get; set; }
    [JsonPropertyName("reporter")] public string Reporter { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    /// <summary>What the reporter saw, in a sentence (the issue's own words where it can be).</summary>
    [JsonPropertyName("reported")] public string Reported { get; set; } = "";
    /// <summary>The PR(s) that closed it, for the report.</summary>
    [JsonPropertyName("fixedBy")] public string FixedBy { get; set; } = "";
    /// <summary>Where the zip is: a GitHub attachment of the issue, or the dumps Worker
    /// (maintainers only, behind Cloudflare Access).</summary>
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    /// <summary>SHA-256 of the zip, so a cached or local copy is known to be the same dump. Empty:
    /// the hash is recorded on the first download (beside the cached zip) and checked after.</summary>
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";

    /// <summary>A zip in <c>AGOPENWEB_DUMP_DIR</c> that the manifest does not name: where it came
    /// from is not known, so it is treated as private.</summary>
    [JsonIgnore] public bool LocalOnly { get; set; }

    /// <summary>Kept by the bug report service (maintainers only), not attached to a public issue.</summary>
    [JsonIgnore] public bool FromService => Url.Contains("agopenweb-dumps", StringComparison.OrdinalIgnoreCase);

    /// <summary>Private: not attached to a public issue. Replayed only in a local run, and its
    /// report carries only finding codes and numbers (see the README, "Privacy").</summary>
    [JsonIgnore] public bool IsPrivate => FromService || LocalOnly;
}

/// <summary>
/// The dumps to replay: <c>TestData/dumps.json</c> (bug reports whose dump is attached to the
/// issue or kept by the bug report service), plus any zip in <c>AGOPENWEB_DUMP_DIR</c>. The zips
/// are not in the repository (a dump holds the reporter's field, which is their farm): they are
/// downloaded once into a cache folder.
///
/// Privacy: the repository is public, and so are an Actions run's cache (a fork's pull request
/// can restore it), summary and artifacts. So a dump that is not attached to a public issue (the
/// bug report service's, or an unnamed local zip) is replayed only when the run is local and
/// says so (<c>AGOPENWEB_REPLAY_PRIVATE=1</c>, never set in the workflow and ignored on GitHub
/// Actions), it is never downloaded into the public cache folder, and its report is cut down to
/// codes and numbers (<see cref="Journal"/>).
/// </summary>
public static class DumpCatalog
{
    /// <summary>The cache of public issue attachments (the workflow's dump cache holds this folder only).</summary>
    public static readonly string CacheDir = Environment.GetEnvironmentVariable("AGOPENWEB_DUMP_CACHE")
        ?? Path.Combine(Path.GetTempPath(), "agopenweb-public-dump-cache");
    /// <summary>Where a local private run keeps the bug report service's dumps: never the public cache.</summary>
    public static readonly string PrivateCacheDir = Environment.GetEnvironmentVariable("AGOPENWEB_PRIVATE_DUMP_CACHE")
        ?? Path.Combine(Path.GetTempPath(), "agopenweb-private-dump-cache");
    public static readonly string? LocalDir = Environment.GetEnvironmentVariable("AGOPENWEB_DUMP_DIR");

    /// <summary>Private dumps are replayed: only with <c>AGOPENWEB_REPLAY_PRIVATE=1</c>, and never on
    /// GitHub Actions (whatever the variable says), where the report is public.</summary>
    public static readonly bool PrivateAllowed = Environment.GetEnvironmentVariable("AGOPENWEB_REPLAY_PRIVATE") == "1"
        && !string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);

    public static List<DumpEntry> Manifest()
    {
        var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestData", "dumps.json");
        var doc = JsonSerializer.Deserialize<ManifestFile>(File.ReadAllText(path)) ?? new ManifestFile();
        var list = doc.Dumps;
        // A zip in AGOPENWEB_DUMP_DIR that the manifest does not name is replayed too: a new
        // dump, tried before it is added (its id is the file name).
        if (LocalDir != null && Directory.Exists(LocalDir))
            foreach (var zip in Directory.GetFiles(LocalDir, "*.zip").OrderBy(p => p, StringComparer.Ordinal))
            {
                var id = Path.GetFileNameWithoutExtension(zip);
                if (!list.Any(e => e.Id == id))
                    list.Add(new DumpEntry { Id = id, Title = "local dump " + Path.GetFileName(zip), Url = "", LocalOnly = true });
            }
        var only = Environment.GetEnvironmentVariable("AGOPENWEB_SCENARIO_ONLY");
        if (!string.IsNullOrWhiteSpace(only))
        {
            var want = only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            list = list.Where(e => want.Contains(e.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        }
        return list;
    }

    /// <summary>The zip of a dump: from <c>AGOPENWEB_DUMP_DIR</c>, else the cache, else downloaded
    /// into the cache. Null (and why) when it can't be had, or is private and the run is not.</summary>
    public static async Task<(string? Zip, string Note)> FetchAsync(DumpEntry e)
    {
        if (e.IsPrivate && !PrivateAllowed)
            return (null, (e.FromService ? "the bug report service's dump (maintainers only)" : "a local zip the manifest does not name")
                + ": private, replayed only in a local run with AGOPENWEB_REPLAY_PRIVATE=1");
        if (LocalDir != null)
        {
            var local = Path.Combine(LocalDir, e.Id + ".zip");
            if (File.Exists(local)) return Check(e, local, "local copy");
        }
        var dir = e.IsPrivate ? PrivateCacheDir : CacheDir;
        Directory.CreateDirectory(dir);
        var cached = Path.Combine(dir, e.Id + ".zip");
        if (File.Exists(cached))
        {
            var r = Check(e, cached, "cached");
            if (r.Zip != null) return r;
            // The recorded hash (if any) stays: the download below is held to it.
            File.Delete(cached);
        }
        if (string.IsNullOrEmpty(e.Url)) return (null, "no URL and no local copy");
        if (!Uri.TryCreate(e.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps) return (null, "the URL is not https");
        // Redirects are followed by hand: https only, a few hops, and the service token (a local
        // private run only) goes to the bug report service's own host, never on to another one.
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AgOpenWeb-dump-replay-harness");
        string? accessId = null, accessSecret = null;
        if (e.FromService)
        {
            // The bug report service keeps dumps for maintainers only, behind Cloudflare Access:
            // a service token in the maintainer's own environment lets a local run in.
            accessId = Environment.GetEnvironmentVariable("AGOPENWEB_DUMPS_ACCESS_ID");
            accessSecret = Environment.GetEnvironmentVariable("AGOPENWEB_DUMPS_ACCESS_SECRET");
            if (string.IsNullOrEmpty(accessId) || string.IsNullOrEmpty(accessSecret))
                return (null, "the bug report service's dump: set AGOPENWEB_DUMPS_ACCESS_ID/SECRET, or put " + e.Id + ".zip in AGOPENWEB_DUMP_DIR");
        }
        var tmp = cached + ".part";
        try
        {
            for (int hop = 0; ; hop++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (accessId != null && hop == 0)
                {
                    req.Headers.Add("CF-Access-Client-Id", accessId);
                    req.Headers.Add("CF-Access-Client-Secret", accessSecret);
                }
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                int code = (int)resp.StatusCode;
                if (code is >= 300 and < 400)
                {
                    var next = resp.Headers.Location is { } loc ? (loc.IsAbsoluteUri ? loc : new Uri(url, loc)) : null;
                    if (next == null || next.Scheme != Uri.UriSchemeHttps) return (null, "download failed: a redirect to a non-https place");
                    if (hop >= 4) return (null, "download failed: too many redirects");
                    url = next;
                    continue;
                }
                if (!resp.IsSuccessStatusCode) return (null, "download failed: HTTP " + code.ToString(System.Globalization.CultureInfo.InvariantCulture));
                await using (var fs = File.Create(tmp)) await resp.Content.CopyToAsync(fs);
                break;
            }
            // A zip starts with "PK": anything else (a sign-in page, an error page) is not cached.
            if (!LooksLikeZip(tmp)) { File.Delete(tmp); return (null, "download failed: what came back is not a zip"); }
            File.Move(tmp, cached, true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            try { File.Delete(tmp); } catch (IOException) { }
            return (null, "download failed: " + ex.Message);
        }
        // No hash in the manifest: the first download's is recorded, and later copies are held to it.
        if (string.IsNullOrEmpty(e.Sha256) && !File.Exists(cached + ".sha256"))
        {
            File.WriteAllText(cached + ".sha256", Sha256(cached));
            return Check(e, cached, "downloaded (no SHA-256 in dumps.json: recorded on this first download)");
        }
        return Check(e, cached, "downloaded");
    }

    private static bool LooksLikeZip(string path)
    {
        using var fs = File.OpenRead(path);
        return fs.ReadByte() == 'P' && fs.ReadByte() == 'K';
    }

    private static (string? Zip, string Note) Check(DumpEntry e, string zip, string how)
    {
        if (!LooksLikeZip(zip)) return (null, how + " file is not a zip");
        var sha = Sha256(zip);
        var want = e.Sha256;
        string recorded = zip + ".sha256";
        if (string.IsNullOrEmpty(want) && File.Exists(recorded)) want = File.ReadAllText(recorded).Trim();
        if (!string.IsNullOrEmpty(want) && !string.Equals(sha, want, StringComparison.OrdinalIgnoreCase))
            return (null, how + " zip is not the dump " + (string.IsNullOrEmpty(e.Sha256) ? "first downloaded" : "the manifest names")
                + " (SHA-256 " + sha[..12] + ", expected " + want[..Math.Min(12, want.Length)] + ")");
        return (zip, how);
    }

    public static string Sha256(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    private sealed class ManifestFile
    {
        [JsonPropertyName("dumps")] public List<DumpEntry> Dumps { get; set; } = new();
    }
}
