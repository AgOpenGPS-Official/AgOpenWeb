// Embeds a minimal web host inside the running app and streams the map feed over a
// raw binary WebSocket (no SignalR). The host passes in the live ApplicationState
// (the DI singleton the pipeline writes to) and the other services it needs.
//
// The transport is SimpleWebServer (a pure-BCL TcpListener host), NOT Kestrel:
// ASP.NET Core has no iOS runtime pack (publish fails with NETSDK1082), so Kestrel
// can't ship on iOS. SimpleWebServer runs anywhere .NET runs, including under iOS
// AOT. The hub, WireCodec, and projectors are transport-agnostic and unchanged —
// only this host's shell swapped from a Kestrel WebApplication to SimpleWebServer,
// and the ASP.NET DI container to direct construction.

using System.Collections.Concurrent;
using System.Reflection;
using SkiaSharp;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.RemoteServer;

public sealed class RemoteServerHost
{
    private SimpleWebServer? _server;
    private WebSocketHub? _ws;
    private MapBroadcaster? _broadcaster;

    /// <summary>The TCP port the server is bound on (set by StartAsync). For the launcher
    /// UI's "browse to …" readout.</summary>
    public int Port { get; private set; } = 5174;

    /// <summary>Number of connected browser clients — drives the launcher's live status.</summary>
    public int ClientCount => _ws?.ClientCount ?? 0;

    /// <summary>
    /// Broadcast a one-shot alert sound to every connected client. The host never
    /// plays audio itself (it may be a headless box with no speaker); the operator's
    /// device plays the .wav. No-op before the server has started or with no clients.
    /// </summary>
    public void PlaySound(AgOpenWeb.Services.Interfaces.SoundEffect effect)
        => _ = _ws?.BroadcastAsync(WireCodec.EncodeSound((byte)effect));

    // Satellite tile fetch (Phase MT — Draw boundary on map). Keyless Bing aerial
    // tiles via the Virtual Earth quadkey endpoint (same source as native's
    // BoundaryMapDialog). Proxied through the host so the browser draws them into the
    // CanvasKit map without CORS taint; cached in memory.
    private static readonly System.Net.Http.HttpClient _tileHttp =
        new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> _tileCache = new();

    public static async System.Threading.Tasks.Task<byte[]?> FetchSatTileAsync(string quadkey)
    {
        if (_tileCache.TryGetValue(quadkey, out var cached)) return cached;
        try
        {
            var bytes = await _tileHttp.GetByteArrayAsync(
                $"https://ecn.t0.tiles.virtualearth.net/tiles/a{quadkey}.jpeg?g=587").ConfigureAwait(false);
            if (_tileCache.Count > 1024) _tileCache.Clear();
            _tileCache[quadkey] = bytes;
            return bytes;
        }
        catch { return null; }
    }

    /// <summary>Host-supplied projector for the live Steer Wizard — returns a WizardDto
    /// while the remote wizard is open, else null. Read every broadcast tick. Set after
    /// <see cref="StartAsync"/> (it needs the MainWindow VM).</summary>
    public Func<WizardDto?>? WizardProvider
    {
        get => _broadcaster?.WizardProvider;
        set { _wizardProvider = value; if (_broadcaster is not null) _broadcaster.WizardProvider = value; }
    }
    private Func<WizardDto?>? _wizardProvider;

    /// <summary>Host-supplied projector for the Recorded Path panel (VM-owned UI state).
    /// Read every broadcast tick, re-sent on change. Set after <see cref="StartAsync"/>.</summary>
    public Func<RecordedPathDto?>? RecordedPathProvider
    {
        get => _broadcaster?.RecordedPathProvider;
        set { _recordedPathProvider = value; if (_broadcaster is not null) _broadcaster.RecordedPathProvider = value; }
    }
    private Func<RecordedPathDto?>? _recordedPathProvider;

    /// <summary>Host-supplied projector for the Boundary panel (menu list + drive-around
    /// recording). Read every broadcast tick, re-sent on change. Set after StartAsync.</summary>
    public Func<BoundaryDto?>? BoundaryProvider
    {
        get => _broadcaster?.BoundaryProvider;
        set { _boundaryProvider = value; if (_broadcaster is not null) _broadcaster.BoundaryProvider = value; }
    }
    private Func<BoundaryDto?>? _boundaryProvider;

    /// <summary>Host-supplied projector for the Field Builder Headland-tab segment list
    /// (VM-owned, rides the Scene frame). Set after <see cref="StartAsync"/>.</summary>
    public Func<IReadOnlyList<HeadlandSegInfoDto>>? HeadlandSegsProvider
    {
        get => _broadcaster?.Projector.HeadlandSegsProvider;
        set { _headlandSegsProvider = value; if (_broadcaster is not null) _broadcaster.Projector.HeadlandSegsProvider = value; }
    }
    private Func<IReadOnlyList<HeadlandSegInfoDto>>? _headlandSegsProvider;

    /// <summary>Host-supplied projector for the generated tram lines (rides the Scene frame).
    /// Set after <see cref="StartAsync"/>.</summary>
    public Func<IReadOnlyList<IReadOnlyList<Vec2Dto>>>? TramLinesProvider
    {
        get => _broadcaster?.Projector.TramLinesProvider;
        set { _tramLinesProvider = value; if (_broadcaster is not null) _broadcaster.Projector.TramLinesProvider = value; }
    }
    private Func<IReadOnlyList<IReadOnlyList<Vec2Dto>>>? _tramLinesProvider;

    /// <summary>
    /// Host-supplied handler for client commands (command id → action). Invoked
    /// off the UI thread; the host marshals known ids to the UI thread and ignores
    /// the rest (the allowlist is the safety boundary). Safe to set before or
    /// after <see cref="StartAsync"/>.
    /// </summary>
    public Action<string, string>? CommandHandler
    {
        get => _ws?.CommandHandler;
        set { _commandHandler = value; if (_ws is not null) _ws.CommandHandler = value; }
    }
    private Action<string, string>? _commandHandler;

    /// <summary>Classifies a command id as Tier-2 (live actuation) — those are
    /// honored only while the sending client holds fresh control authority.</summary>
    public Func<string, bool>? IsRestrictedCommand
    {
        get => _ws?.IsRestrictedCommand;
        set { _isRestricted = value; if (_ws is not null) _ws.IsRestrictedCommand = value; }
    }
    private Func<string, bool>? _isRestricted;

    /// <summary>Raised (off the UI thread) when actuation authority is taken/lost —
    /// (held, holderName). The host drives the native "remote control active" banner.</summary>
    public Action<bool, string>? AuthorityChangedHandler { get; set; }

    /// <summary>Raised when authority is lost involuntarily (disconnect / deadman).
    /// The host reverts what the remote actuated (Phase 3). Arg = reason.</summary>
    public Action<string>? FailsafeHandler { get; set; }

    /// <param name="state">The live DI ApplicationState the app/pipeline updates.</param>
    /// <param name="port">Bound on 0.0.0.0 so LAN clients (tablets) can connect.</param>
    public async Task StartAsync(ApplicationState state, ICoverageMapService coverage,
        ISectionControlService sections, IToolPositionService tool,
        AgOpenWeb.Models.Configuration.ConfigurationStore config,
        IJobService jobs, IConfigurationService configService, IAutoSteerService autoSteer,
        ISmartWasCalibrationService smartWas, IUdpCommunicationService udp,
        INtripProfileService ntripProfiles, IFieldService fields, ISettingsService settings,
        IVehicleProfileService vehicleProfiles, IPersistentStateService persist, int port = 5174)
    {
        Port = port;

        // Direct construction — no ASP.NET DI container. (The old Kestrel host
        // registered these as singletons and resolved them; the ctors are unchanged,
        // the mapping is 1:1.)
        var authority = new ControlAuthority();
        var sceneProjector = new SceneProjector(state, sections, tool, config, coverage, jobs,
            configService, autoSteer, smartWas, udp, ntripProfiles, fields, settings,
            vehicleProfiles, persist);
        var coverageProjector = new CoverageProjector(coverage);
        _ws = new WebSocketHub(authority);
        _broadcaster = new MapBroadcaster(_ws, sceneProjector, coverage, coverageProjector, authority);

        // Hook the WS hub for inbound commands + apply any providers/handlers set
        // before start (identical wiring to the old host).
        _ws.CommandHandler = _commandHandler;
        _ws.IsRestrictedCommand = _isRestricted;
        _broadcaster.WizardProvider = _wizardProvider;
        _broadcaster.RecordedPathProvider = _recordedPathProvider;
        _broadcaster.BoundaryProvider = _boundaryProvider;
        _broadcaster.Projector.HeadlandSegsProvider = _headlandSegsProvider;
        _broadcaster.Projector.TramLinesProvider = _tramLinesProvider;

        // Control authority → broadcast state to clients + drive the native banner;
        // involuntary loss → failsafe.
        authority.Changed += st =>
        {
            _ = _ws.BroadcastAsync(WireCodec.EncodeControlState(st));
            AuthorityChangedHandler?.Invoke(st.Held, st.HolderName);
        };
        authority.Revoked += reason => FailsafeHandler?.Invoke(reason);

        var server = new SimpleWebServer(port);

        // The /ws receive loop's token is linked to the server's shutdown inside
        // SimpleWebServer, so StopAsync cancels connected browsers promptly.
        server.MapWebSocket("/ws", (socket, ct) => _ws.HandleAsync(socket, ct));

        // The UI bundle (HTML + JS) ships inside the app and changes with every build, and
        // the server is in-process on localhost — so there's no value in caching it, and a
        // stale copy in the WebView's HTTP cache silently runs old code after an app update.
        // Force a fresh fetch every load.
        var noStore = new[] { ("Cache-Control", "no-store") };
        server.MapGet("/", () => SimpleWebServer.Response.Text(ReadAsset("index.html"), "text/html", noStore));
        server.MapGet("/app.js", () => SimpleWebServer.Response.Text(ReadAsset("app.js"), "text/javascript", noStore));
        server.MapGet("/transport.js", () => SimpleWebServer.Response.Text(ReadAsset("transport.js"), "text/javascript", noStore));
        // PWA manifest — lets "Add to home screen" launch fullscreen (no browser chrome).
        server.MapGet("/manifest.webmanifest", () => SimpleWebServer.Response.Text(ReadAsset("manifest.webmanifest"), "application/manifest+json"));

        // CanvasKit (WASM Skia) — bundled locally for offline in-cab use. The wasm is
        // served as application/wasm so the browser can streaming-compile it.
        server.MapGet("/vendor/canvaskit.js", () => SimpleWebServer.Response.Text(ReadAsset("vendor.canvaskit.js"), "text/javascript"));
        server.MapGet("/vendor/canvaskit.wasm", () => SimpleWebServer.Response.Bytes(ReadAssetBytes("vendor.canvaskit.wasm"), "application/wasm"));

        // Right-nav toolbar icons (PNG/GIF), embedded from wwwroot/icons. Filename-only
        // (no path traversal); unknown names 404.
        server.MapGetPrefix("/icons/", file =>
        {
            if (file.Contains('/') || file.Contains('\\') || file.Contains(".."))
                return SimpleWebServer.Response.NotFound;
            var mime = file.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ? "image/gif" : "image/png";
            try { return SimpleWebServer.Response.Bytes(ReadAssetBytes("icons." + file), mime); }
            catch (FileNotFoundException) { return SimpleWebServer.Response.NotFound; }
        });

        // Alert sounds (.wav), embedded from wwwroot/sounds. The client preloads and
        // plays these on a SOUND message — host-side audio was removed (a headless
        // box has no speaker). Filename-only (no path traversal); unknown names 404.
        server.MapGetPrefix("/sounds/", file =>
        {
            if (file.Contains('/') || file.Contains('\\') || file.Contains(".."))
                return SimpleWebServer.Response.NotFound;
            try { return SimpleWebServer.Response.Bytes(ReadAssetBytes("sounds." + file), "audio/wav"); }
            catch (FileNotFoundException) { return SimpleWebServer.Response.NotFound; }
        });

        // Field background imagery. Served DOWNSCALED for the web: native composites
        // are huge (tens of MB) and an iOS WKWebView <img>/CanvasKit decode of that
        // OOMs — so big PNGs are decoded in THIS (app) process (which already decodes
        // them for the native map) and re-encoded as a capped JPEG. Small images pass
        // through untouched. The client cache-busts with ?v=<version> on field change.
        server.MapGet("/backpic.png", () =>
        {
            var im = state.Field.Imagery;
            if (im is null) return SimpleWebServer.Response.NotFound;
            var web = GetBackpicForWeb(im.Path);
            return web is { } w ? SimpleWebServer.Response.Bytes(w.Bytes, w.ContentType)
                                 : SimpleWebServer.Response.NotFound;
        });

        // Satellite tile proxy (Phase MT — Draw boundary on map). Quadkey-addressed,
        // served same-origin (no CORS taint) + hard browser cache.
        server.MapGetPrefix("/sattile/", async quadkey =>
        {
            bool valid = quadkey.Length is > 0 and <= 23;
            foreach (var c in quadkey) if (c is < '0' or > '3') { valid = false; break; }
            if (!valid) return SimpleWebServer.Response.NotFound;
            var bytes = await FetchSatTileAsync(quadkey).ConfigureAwait(false);
            if (bytes is null) return SimpleWebServer.Response.NotFound;
            return SimpleWebServer.Response.Bytes(bytes, "image/jpeg",
                new[] { ("Cache-Control", "public, max-age=604800") }); // a week
        });

        await server.StartAsync().ConfigureAwait(false);
        _broadcaster.Start();
        _server = server;
    }

    public async Task StopAsync()
    {
        if (_server is null) return;
        if (_broadcaster is not null) await _broadcaster.DisposeAsync().ConfigureAwait(false);
        await _server.StopAsync().ConfigureAwait(false);
        _server = null;
    }

    // Web imagery: cap the longest side so an iOS WKWebView can decode it. Native
    // composites are often 6000+ px / tens of MB; the WebView's image-decode budget is
    // far smaller than the app's. Cache the result by path + mtime + size so a 42 MB
    // PNG is decoded at most once per change. Small images pass through as-is (PNG).
    private const int WebImageryMaxDim = 2048;
    private static readonly ConcurrentDictionary<string, (long Stamp, byte[] Bytes, string ContentType)> _backpicCache = new();

    private readonly record struct WebImage(byte[] Bytes, string ContentType);

    private static WebImage? GetBackpicForWeb(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var fi = new FileInfo(path);
            long stamp = fi.LastWriteTimeUtc.Ticks ^ fi.Length;
            if (_backpicCache.TryGetValue(path, out var c) && c.Stamp == stamp)
                return new WebImage(c.Bytes, c.ContentType);

            // Peek dimensions cheaply (no full decode) — pass through if already small.
            int w, h;
            using (var codec = SKCodec.Create(path))
            {
                if (codec is null) return new WebImage(File.ReadAllBytes(path), "image/png");
                w = codec.Info.Width; h = codec.Info.Height;
            }
            if (System.Math.Max(w, h) <= WebImageryMaxDim)
            {
                var raw = File.ReadAllBytes(path);
                _backpicCache[path] = (stamp, raw, "image/png");
                return new WebImage(raw, "image/png");
            }

            // Downscale: decode (the app process already decodes this for the native map,
            // so the memory is proven) → resize → JPEG. Bound the cache.
            using var src = SKBitmap.Decode(path);
            if (src is null) return new WebImage(File.ReadAllBytes(path), "image/png");
            double scale = (double)WebImageryMaxDim / System.Math.Max(w, h);
            int nw = System.Math.Max(1, (int)System.Math.Round(w * scale));
            int nh = System.Math.Max(1, (int)System.Math.Round(h * scale));
            using var scaled = src.Resize(new SKImageInfo(nw, nh), new SKSamplingOptions(SKCubicResampler.Mitchell));
            if (scaled is null) return new WebImage(File.ReadAllBytes(path), "image/png");
            using var img = SKImage.FromBitmap(scaled);
            using var data = img.Encode(SKEncodedImageFormat.Jpeg, 85);
            var jpg = data.ToArray();
            if (_backpicCache.Count > 8) _backpicCache.Clear();
            _backpicCache[path] = (stamp, jpg, "image/jpeg");
            return new WebImage(jpg, "image/jpeg");
        }
        catch { return null; }
    }

    private static string ReadAsset(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        var res = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(".wwwroot." + name, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"Embedded client asset not found: {name}");
        using var s = asm.GetManifestResourceStream(res)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private static byte[] ReadAssetBytes(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        var res = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(".wwwroot." + name, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"Embedded client asset not found: {name}");
        using var s = asm.GetManifestResourceStream(res)!;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
