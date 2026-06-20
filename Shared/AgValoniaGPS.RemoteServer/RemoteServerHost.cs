// Embeds a minimal Kestrel server inside the running app and streams the map
// feed over a raw binary WebSocket (no SignalR). The host passes in the live
// ApplicationState (the DI singleton the pipeline writes to), and we bridge it
// into the server's own DI container as a singleton instance.

using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AgValoniaGPS.Models.State;
using AgValoniaGPS.Services;
using AgValoniaGPS.Services.Interfaces;

namespace AgValoniaGPS.RemoteServer;

public sealed class RemoteServerHost
{
    private WebApplication? _app;
    private WebSocketHub? _ws;
    private MapBroadcaster? _broadcaster;

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
        AgValoniaGPS.Models.Configuration.ConfigurationStore config,
        IJobService jobs, IConfigurationService configService, IAutoSteerService autoSteer,
        ISmartWasCalibrationService smartWas, IUdpCommunicationService udp,
        INtripProfileService ntripProfiles, IFieldService fields, ISettingsService settings,
        IVehicleProfileService vehicleProfiles, IPersistentStateService persist, int port = 5174)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

        // This is an EMBEDDED server, not the process owner. Suppress its default
        // ConsoleLifetime so it does NOT grab SIGTERM / Ctrl+C — the host process
        // (the Avalonia app when windowed, HeadlessHost as a daemon) owns process
        // signals and stops us explicitly via StopAsync. Without this, the embedded
        // host self-stops on SIGTERM and races the host's StopAsync (disposed CTS).
        builder.Services.AddSingleton<IHostLifetime, NoopHostLifetime>();

        builder.Services.AddSingleton(state);
        builder.Services.AddSingleton(coverage);
        builder.Services.AddSingleton(sections);
        builder.Services.AddSingleton(tool);
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(jobs);
        builder.Services.AddSingleton(configService);
        builder.Services.AddSingleton(autoSteer);
        builder.Services.AddSingleton(smartWas);
        builder.Services.AddSingleton(udp);
        builder.Services.AddSingleton(ntripProfiles);
        builder.Services.AddSingleton(fields);
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(vehicleProfiles);
        builder.Services.AddSingleton(persist);
        builder.Services.AddSingleton<ControlAuthority>();
        builder.Services.AddSingleton<SceneProjector>();
        builder.Services.AddSingleton<CoverageProjector>();
        builder.Services.AddSingleton<WebSocketHub>();
        builder.Services.AddSingleton<MapBroadcaster>();

        var app = builder.Build();

        app.UseWebSockets();
        app.Map("/ws", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var hub = context.RequestServices.GetRequiredService<WebSocketHub>();
            await hub.HandleAsync(socket, context.RequestAborted);
        });

        app.MapGet("/", () => Results.Content(ReadAsset("index.html"), "text/html"));
        app.MapGet("/app.js", () => Results.Content(ReadAsset("app.js"), "text/javascript"));
        app.MapGet("/transport.js", () => Results.Content(ReadAsset("transport.js"), "text/javascript"));
        // PWA manifest — lets "Add to home screen" launch fullscreen (no browser chrome).
        app.MapGet("/manifest.webmanifest", () => Results.Content(ReadAsset("manifest.webmanifest"), "application/manifest+json"));

        // CanvasKit (WASM Skia) — bundled locally for offline in-cab use. The
        // wasm is served as application/wasm so the browser can streaming-compile.
        app.MapGet("/vendor/canvaskit.js", () => Results.Content(ReadAsset("vendor.canvaskit.js"), "text/javascript"));
        app.MapGet("/vendor/canvaskit.wasm", () => Results.File(ReadAssetBytes("vendor.canvaskit.wasm"), "application/wasm"));

        // Right-nav toolbar icons (PNG), embedded from wwwroot/icons. Filename-only
        // (no path traversal); unknown names 404.
        app.MapGet("/icons/{file}", (string file) =>
        {
            if (file.Contains('/') || file.Contains('\\') || file.Contains(".."))
                return Results.NotFound();
            var mime = file.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ? "image/gif" : "image/png";
            try { return Results.File(ReadAssetBytes("icons." + file), mime); }
            catch (FileNotFoundException) { return Results.NotFound(); }
        });

        // Field background imagery (BackPic.png). The heavy bytes go over HTTP
        // (browser-decoded/cached); only the small world rectangle rides the WS
        // Scene. The client cache-busts with ?v=<version> on field change.
        app.MapGet("/backpic.png", () =>
        {
            var im = state.Field.Imagery;
            // Serve the raw bytes — Results.File(string) resolves relative to the
            // web root, which would 404 the absolute field-directory path.
            return im is not null && File.Exists(im.Path)
                ? Results.File(File.ReadAllBytes(im.Path), "image/png")
                : Results.NotFound();
        });

        // Satellite tile proxy (Phase MT — Draw boundary on map). Quadkey-addressed,
        // served same-origin (no CORS taint) + hard browser cache.
        app.MapGet("/sattile/{quadkey}", async (string quadkey, HttpContext ctx) =>
        {
            bool valid = quadkey.Length is > 0 and <= 23;
            foreach (var c in quadkey) if (c is < '0' or > '3') { valid = false; break; }
            if (!valid) return Results.NotFound();
            var bytes = await FetchSatTileAsync(quadkey);
            if (bytes is null) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "public, max-age=604800"; // a week
            return Results.File(bytes, "image/jpeg");
        });

        // Build the broadcaster now so it wires SeedProvider before clients connect.
        app.Services.GetRequiredService<MapBroadcaster>();

        // Hook the WS hub for inbound commands (apply any handler set pre-start).
        _ws = app.Services.GetRequiredService<WebSocketHub>();
        _ws.CommandHandler = _commandHandler;
        _ws.IsRestrictedCommand = _isRestricted;
        _broadcaster = app.Services.GetRequiredService<MapBroadcaster>();
        _broadcaster.WizardProvider = _wizardProvider;
        _broadcaster.RecordedPathProvider = _recordedPathProvider;
        _broadcaster.BoundaryProvider = _boundaryProvider;
        _broadcaster.Projector.HeadlandSegsProvider = _headlandSegsProvider;
        _broadcaster.Projector.TramLinesProvider = _tramLinesProvider;

        // Control authority → broadcast state to clients + drive the native banner;
        // involuntary loss → failsafe.
        var authority = app.Services.GetRequiredService<ControlAuthority>();
        authority.Changed += st =>
        {
            _ = _ws.BroadcastAsync(WireCodec.EncodeControlState(st));
            AuthorityChangedHandler?.Invoke(st.Held, st.HolderName);
        };
        authority.Revoked += reason => FailsafeHandler?.Invoke(reason);

        await app.StartAsync();
        app.Services.GetRequiredService<MapBroadcaster>().Start();
        _app = app;
    }

    public async Task StopAsync()
    {
        if (_app is null) return;
        await _app.Services.GetRequiredService<MapBroadcaster>().DisposeAsync();
        await _app.StopAsync();
        await _app.DisposeAsync();
        _app = null;
    }

    /// <summary>
    /// No-op <see cref="IHostLifetime"/> that replaces the embedded web host's
    /// default ConsoleLifetime, so the embedded server never installs process
    /// signal handlers. Process-signal handling belongs to the owning host.
    /// </summary>
    private sealed class NoopHostLifetime : IHostLifetime
    {
        public System.Threading.Tasks.Task WaitForStartAsync(System.Threading.CancellationToken ct)
            => System.Threading.Tasks.Task.CompletedTask;
        public System.Threading.Tasks.Task StopAsync(System.Threading.CancellationToken ct)
            => System.Threading.Tasks.Task.CompletedTask;
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
