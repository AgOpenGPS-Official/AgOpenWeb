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
    /// <summary>SHA-256 of the zip, so a cached or local copy is known to be the same dump.</summary>
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
}

/// <summary>
/// The dumps to replay: <c>TestData/dumps.json</c> (bug reports whose dump is attached to the
/// issue or kept by the bug report service), plus any zip in <c>AGOPENWEB_DUMP_DIR</c>. The zips
/// are not in the repository (a dump holds the reporter's field, which is their farm): they are
/// downloaded once into a cache folder.
/// </summary>
public static class DumpCatalog
{
    public static readonly string CacheDir = Environment.GetEnvironmentVariable("AGOPENWEB_DUMP_CACHE")
        ?? Path.Combine(Path.GetTempPath(), "agopenweb-dump-cache");
    public static readonly string? LocalDir = Environment.GetEnvironmentVariable("AGOPENWEB_DUMP_DIR");

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
                    list.Add(new DumpEntry { Id = id, Title = "local dump " + Path.GetFileName(zip), Url = "" });
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
    /// into the cache. Null (and why) when it can't be had.</summary>
    public static async Task<(string? Zip, string Note)> FetchAsync(DumpEntry e)
    {
        if (LocalDir != null)
        {
            var local = Path.Combine(LocalDir, e.Id + ".zip");
            if (File.Exists(local)) return Check(e, local, "local copy");
        }
        Directory.CreateDirectory(CacheDir);
        var cached = Path.Combine(CacheDir, e.Id + ".zip");
        if (File.Exists(cached))
        {
            var r = Check(e, cached, "cached");
            if (r.Zip != null) return r;
            File.Delete(cached);
        }
        if (string.IsNullOrEmpty(e.Url)) return (null, "no URL and no local copy");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AgOpenWeb-dump-replay-harness");
        var req = new HttpRequestMessage(HttpMethod.Get, e.Url);
        if (e.Url.Contains("agopenweb-dumps", StringComparison.Ordinal))
        {
            // The bug report service keeps dumps for maintainers only, behind Cloudflare Access:
            // a service token lets a nightly run in.
            var id = Environment.GetEnvironmentVariable("AGOPENWEB_DUMPS_ACCESS_ID");
            var secret = Environment.GetEnvironmentVariable("AGOPENWEB_DUMPS_ACCESS_SECRET");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(secret))
                return (null, "maintainer-only dump (the bug report service): set AGOPENWEB_DUMPS_ACCESS_ID/SECRET, or put " + e.Id + ".zip in AGOPENWEB_DUMP_DIR");
            req.Headers.Add("CF-Access-Client-Id", id);
            req.Headers.Add("CF-Access-Client-Secret", secret);
        }
        try
        {
            using var resp = await http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return (null, "download failed: HTTP " + ((int)resp.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
            var tmp = cached + ".part";
            await using (var fs = File.Create(tmp)) await resp.Content.CopyToAsync(fs);
            File.Move(tmp, cached, true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return (null, "download failed: " + ex.Message);
        }
        return Check(e, cached, "downloaded");
    }

    private static (string? Zip, string Note) Check(DumpEntry e, string zip, string how)
    {
        var sha = Sha256(zip);
        if (!string.IsNullOrEmpty(e.Sha256) && !string.Equals(sha, e.Sha256, StringComparison.OrdinalIgnoreCase))
            return (null, how + " zip is not the dump the manifest names (SHA-256 " + sha[..12] + ", expected " + e.Sha256[..Math.Min(12, e.Sha256.Length)] + ")");
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
