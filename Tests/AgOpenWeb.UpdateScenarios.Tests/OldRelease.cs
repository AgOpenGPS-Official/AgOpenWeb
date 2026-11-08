// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;

namespace AgOpenWeb.UpdateScenarios.Tests;

/// <summary>
/// An older AgOpenWeb release as the farmer installed it: the Linux x64 daemon bundle of a GitHub
/// release (agopenweb-linux-x64.tar.gz), fetched once into a cache folder and unpacked. Releases
/// before v26.7.1 published no Linux bundle; such a tag is reported, not fetched. A release can
/// also be given as a folder with a published app (AGOPENWEB_UPDATE_FROM=tag=/path/to/app).
/// </summary>
public static class OldRelease
{
    public const string Repo = "AgOpenGPS-Official/AgOpenWeb";
    public const string Asset = "agopenweb-linux-x64.tar.gz";

    /// <summary>The releases to update from, oldest first: AGOPENWEB_UPDATE_FROM (comma list of
    /// tags, or tag=path), or the four latest tagged releases known to have a Linux bundle.</summary>
    public static List<(string Tag, string? Path)> Wanted()
    {
        var list = new List<(string, string?)>();
        string env = Environment.GetEnvironmentVariable("AGOPENWEB_UPDATE_FROM") ?? "v26.7.1,v26.7.2,v26.7.3,v26.7.4";
        foreach (var raw in env.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = raw.IndexOf('=');
            list.Add(eq > 0 ? (raw[..eq], raw[(eq + 1)..]) : (raw, null));
        }
        return list;
    }

    public static string CacheDir =>
        Environment.GetEnvironmentVariable("AGOPENWEB_UPDATE_CACHE")
        ?? Path.Combine(Path.GetTempPath(), "agopenweb-releases");

    /// <summary>The folder with the release's AgOpenWeb.Desktop, or null with the reason.
    /// <c>HarnessFault</c> is set when the reason is the harness's to answer for (a bundle whose
    /// SHA-256 is not the one GitHub publishes for it, or no digest to check against), not the
    /// release's (a tag without a Linux bundle).</summary>
    public static async Task<(string? AppDir, string Note, bool HarnessFault)> GetAsync(string tag, string? givenPath)
    {
        if (givenPath != null)
        {
            string exe = Path.Combine(givenPath, "AgOpenWeb.Desktop");
            return File.Exists(exe) ? (givenPath, "from " + givenPath, false) : (null, "no AgOpenWeb.Desktop in " + givenPath, false);
        }
        string dir = Path.Combine(CacheDir, tag);
        string unpacked = Path.Combine(dir, "agopenweb-linux-x64");
        string appDir = Path.Combine(unpacked, "app");
        // The digest the bundle was checked against when it was downloaded, kept beside it: the
        // archive itself is removed once unpacked.
        string digestFile = Path.Combine(dir, Asset + ".sha256");
        var (published, digestNote) = await PublishedDigestAsync(tag);

        if (File.Exists(Path.Combine(appDir, "AgOpenWeb.Desktop")) && File.Exists(digestFile))
        {
            string kept = File.ReadAllText(digestFile).Trim();
            if (published == null)
                return (appDir, Kit.F($"cached bundle, checked on download (sha256 {Short(kept)}); not checked again: {digestNote}"), false);
            if (string.Equals(kept, published, StringComparison.OrdinalIgnoreCase))
                return (appDir, Kit.F($"cached bundle, sha256 {Short(kept)} as published"), false);
            // The release's asset changed since it was cached: fetch it again.
            Kit.TryDeleteAlways(unpacked);
            File.Delete(digestFile);
        }
        else if (Directory.Exists(unpacked))
        {
            // Unpacked without a checked digest (an older cache, or a run cut short): not trusted.
            Kit.TryDeleteAlways(unpacked);
        }

        Directory.CreateDirectory(dir);
        string tgz = Path.Combine(dir, Asset);
        string url = $"https://github.com/{Repo}/releases/download/{tag}/{Asset}";
        try
        {
            using var http = NewHttp();
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return (null, $"the release {tag} has no {Asset} (Linux bundles are published from v26.7.1 on)", false);
            resp.EnsureSuccessStatusCode();
            await using (var f = File.Create(tgz))
                await resp.Content.CopyToAsync(f);
        }
        catch (Exception ex)
        {
            TryDeleteFile(tgz);
            return (null, $"could not download {url}: {ex.GetType().Name}: {ex.Message}", false);
        }

        // The SHA-256 before anything is unpacked: a bundle that is not the published one is never run.
        if (published == null)
        {
            TryDeleteFile(tgz);
            return (null, Kit.F($"{Asset} of {tag} could not be checked, so it is not run: {digestNote}"), true);
        }
        string actual;
        await using (var f = File.OpenRead(tgz))
            actual = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(f));
        if (!string.Equals(actual, published, StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteFile(tgz);
            Kit.TryDeleteAlways(unpacked);
            return (null, Kit.F($"{Asset} of {tag} has sha256 {actual}, the release publishes {published}: removed, not run"), true);
        }

        var tar = Process.Start(new ProcessStartInfo("tar", $"xzf \"{tgz}\" -C \"{dir}\"") { RedirectStandardError = true, UseShellExecute = false });
        if (tar == null) { TryDeleteFile(tgz); return (null, "tar not available", false); }
        string err = await tar.StandardError.ReadToEndAsync();
        await tar.WaitForExitAsync();
        TryDeleteFile(tgz);
        if (tar.ExitCode != 0) { Kit.TryDeleteAlways(unpacked); return (null, "tar failed: " + err.Trim(), false); }
        if (!File.Exists(Path.Combine(appDir, "AgOpenWeb.Desktop"))) { Kit.TryDeleteAlways(unpacked); return (null, "the bundle has no app/AgOpenWeb.Desktop", false); }
        File.WriteAllText(digestFile, published + "\n");
        return (appDir, Kit.F($"downloaded {Asset} of {tag}, sha256 {Short(published)} as published"), false);
    }

    /// <summary>The digests already read in this run, by tag: the API is asked once per tag.</summary>
    private static readonly Dictionary<string, string> Digests = new(StringComparer.Ordinal);

    /// <summary>The SHA-256 GitHub publishes for the bundle (the asset's <c>digest</c>,
    /// "sha256:…", in the release's API entry), lower-case hex, or null with the reason. With
    /// GITHUB_TOKEN set the request is authenticated (a higher rate limit on CI); without it, it
    /// works too, as on a developer's computer.</summary>
    private static async Task<(string? Sha256, string Note)> PublishedDigestAsync(string tag)
    {
        lock (Digests) { if (Digests.TryGetValue(tag, out var known)) return (known, "already read"); }
        string api = $"https://api.github.com/repos/{Repo}/releases/tags/{tag}";
        try
        {
            using var http = NewHttp();
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            string? token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            if (!string.IsNullOrEmpty(token))
                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var resp = await http.GetAsync(api);
            if (!resp.IsSuccessStatusCode)
                return (null, Kit.F($"{api} answered {(int)resp.StatusCode} {resp.ReasonPhrase}"));
            using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("assets", out var assets)) return (null, "the release has no assets list");
            foreach (var a in assets.EnumerateArray())
            {
                if (a.GetProperty("name").GetString() != Asset) continue;
                string? digest = a.TryGetProperty("digest", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.String ? d.GetString() : null;
                if (digest == null || !digest.StartsWith("sha256:", StringComparison.Ordinal))
                    return (null, Kit.F($"the release lists {Asset} without a sha256 digest ({digest ?? "none"})"));
                string hex = digest[7..].ToLowerInvariant();
                lock (Digests) Digests[tag] = hex;
                return (hex, "published");
            }
            return (null, Kit.F($"the release {tag} lists no {Asset}"));
        }
        catch (Exception ex)
        {
            return (null, Kit.F($"could not read {api}: {ex.GetType().Name}: {ex.Message}"));
        }
    }

    private static HttpClient NewHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AgOpenWeb-update-scenarios");
        return http;
    }

    private static string Short(string hex) => hex.Length > 12 ? hex[..12] + "…" : hex;

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

/// <summary>
/// An older release running as the daemon does: <c>AgOpenWeb.Desktop --headless</c> as a child
/// process with AGOPENWEB_DATA at the scenario's data folder and HOME in a scratch folder, its
/// console kept, stopped with SIGTERM (what systemd sends; the exit sequence saves the field, the
/// config and the state) or killed outright (a power cut).
/// </summary>
public sealed class OldProcess : IAsyncDisposable
{
    public string Tag { get; }
    public string AppDir { get; }
    public string DataRoot { get; }
    public int Port { get; }
    public List<string> Console { get; } = new();
    public string? Version { get; private set; }
    public bool Exited => _proc?.HasExited ?? true;
    public int? ExitCode => _proc is { HasExited: true } p ? p.ExitCode : null;

    private Process? _proc;
    private readonly string _home;

    public OldProcess(string tag, string appDir, string dataRoot, int port = 5174)
    {
        Tag = tag; AppDir = appDir; DataRoot = dataRoot; Port = port;
        _home = Path.Combine(Path.GetTempPath(), "aow-update-home-" + Guid.NewGuid().ToString("N")[..8]);
    }

    /// <summary>Start and wait for "[headless] ready" on the console (or the port to answer).</summary>
    public async Task<bool> StartAsync(double timeoutSeconds = 60)
    {
        Directory.CreateDirectory(_home);
        Directory.CreateDirectory(DataRoot);
        var psi = new ProcessStartInfo(Path.Combine(AppDir, "AgOpenWeb.Desktop"), "--headless")
        {
            WorkingDirectory = AppDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["AGOPENWEB_DATA"] = DataRoot;
        psi.Environment["HOME"] = _home;
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment.Remove("NUNIT_TEST_RUNNER");   // nothing of the test host leaks in
        _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLine(string? line)
        {
            if (line == null) return;
            lock (Console) { if (Console.Count < 400) Console.Add(line); }
            if (line.Contains("[headless] ready", StringComparison.Ordinal))
            {
                int v = line.IndexOf("(v", StringComparison.Ordinal);
                if (v >= 0) { int e = line.IndexOf(')', v); if (e > v) Version = line[(v + 2)..e]; }
                ready.TrySetResult(true);
            }
        }
        _proc.OutputDataReceived += (_, e) => OnLine(e.Data);
        _proc.ErrorDataReceived += (_, e) => OnLine(e.Data == null ? null : "stderr: " + e.Data);
        _proc.Exited += (_, _) => ready.TrySetResult(false);
        _proc.Start();
        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();
        var done = await Task.WhenAny(ready.Task, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
        if (done != ready.Task) return false;
        if (!await ready.Task) return false;
        await Task.Delay(500);   // the web server binds right after the banner
        return true;
    }

    /// <summary>SIGTERM, as systemd stop: the generic host runs the exit save. Waits for the exit.</summary>
    public async Task<bool> StopAsync(double timeoutSeconds = 40)
    {
        if (_proc == null || _proc.HasExited) return true;
        try { Process.Start("kill", $"-TERM {_proc.Id}")?.WaitForExit(); } catch { }
        var exited = _proc.WaitForExitAsync();
        if (await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds))) != exited)
        {
            try { _proc.Kill(true); } catch { }
            return false;
        }
        return true;
    }

    /// <summary>SIGKILL: no exit save at all (the power went).</summary>
    public async Task KillAsync()
    {
        if (_proc == null || _proc.HasExited) return;
        try { _proc.Kill(true); } catch { }
        await _proc.WaitForExitAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_proc != null && !_proc.HasExited) await StopAsync(10);
        _proc?.Dispose();
        Kit.TryDelete(_home);
    }
}

/// <summary>
/// A browser, as far as the older build can tell: the WebSocket at /ws, HELLO read, control
/// acquired and kept with presence beats, commands sent as "id|arg" text frames. The frames the
/// build sends are read for the few fixed-layout ones that matter here (HELLO, CONTROL_STATE,
/// APP_INFO's version); the rest are counted and dropped, since their layout moves between builds.
/// </summary>
public sealed class WebClient : ICommandSender, IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _reader, _beat;
    public string DataRoot { get; }
    public string? ClientId { get; private set; }
    public bool? Held { get; private set; }
    public string? HolderName { get; private set; }
    public string? AppVersion { get; private set; }
    public int FramesSeen;
    public int CommandsSent;
    /// <summary>Real seconds given to a command on the old build before the next one.</summary>
    public double CommandGap { get; set; } = 0.35;

    public WebClient(string dataRoot) { DataRoot = dataRoot; }

    public async Task<bool> ConnectAsync(int port, double timeoutSeconds = 20)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                await _ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), _cts.Token);
                break;
            }
            catch when (sw.Elapsed.TotalSeconds < timeoutSeconds)
            {
                await Task.Delay(300);
            }
        }
        _reader = Task.Run(ReadLoopAsync);
        // HELLO comes first; the hub then grants control to the first connection by itself. Ask
        // anyway, as the client does, and beat at 2 Hz.
        var t0 = Stopwatch.StartNew();
        while (ClientId == null && t0.Elapsed.TotalSeconds < 10) await Task.Delay(50);
        await RawSendAsync("control.acquire|Harness");
        _beat = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try { await RawSendAsync("control.presence"); } catch { }
                try { await Task.Delay(500, _cts.Token); } catch { break; }
            }
        });
        t0.Restart();
        while (Held != true && t0.Elapsed.TotalSeconds < 10) await Task.Delay(50);
        return ClientId != null && Held == true;
    }

    private async Task RawSendAsync(string msg)
    {
        if (_ws.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(msg);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, _cts.Token);
    }

    public async Task SendAsync(string cmd, string arg = "")
    {
        await RawSendAsync(arg.Length == 0 ? cmd : cmd + "|" + arg);
        Interlocked.Increment(ref CommandsSent);
        await Task.Delay(TimeSpan.FromSeconds(CommandGap));
    }

    public Task PauseAsync(double seconds) => seconds > 0 ? Task.Delay(TimeSpan.FromSeconds(seconds)) : Task.CompletedTask;

    private async Task ReadLoopAsync()
    {
        var buf = new byte[1 << 20];
        var ms = new MemoryStream();
        try
        {
            while (_ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                ms.SetLength(0);
                WebSocketReceiveResult res;
                do
                {
                    res = await _ws.ReceiveAsync(buf, _cts.Token);
                    if (res.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buf, 0, res.Count);
                } while (!res.EndOfMessage);
                Interlocked.Increment(ref FramesSeen);
                Decode(ms.GetBuffer().AsSpan(0, (int)ms.Length));
            }
        }
        catch { /* closing */ }
    }

    private void Decode(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 1) return;
        byte type = frame[0];
        int o = 1;
        string Str(ReadOnlySpan<byte> f)
        {
            int n = BitConverter.ToInt32(f.Slice(o, 4)); o += 4;
            string s = Encoding.UTF8.GetString(f.Slice(o, n)); o += n;
            return s;
        }
        try
        {
            switch (type)
            {
                case 7: ClientId = Str(frame); break;                       // HELLO: client id
                case 6:                                                      // CONTROL_STATE: held, holderId, holderName
                    bool held = frame[o++] != 0;
                    string holderId = Str(frame);
                    string holderName = Str(frame);
                    Held = held && holderId == ClientId;
                    HolderName = holderName;
                    break;
                case 14: AppVersion = Str(frame); break;                     // APP_INFO: version first
            }
        }
        catch { /* a frame of another shape: not needed */ }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { if (_ws.State == WebSocketState.Open) await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
        _ws.Dispose();
    }
}
