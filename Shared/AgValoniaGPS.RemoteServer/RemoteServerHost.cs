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
using AgValoniaGPS.Services.Interfaces;

namespace AgValoniaGPS.RemoteServer;

public sealed class RemoteServerHost
{
    private WebApplication? _app;
    private WebSocketHub? _ws;

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
        IJobService jobs, IConfigurationService configService, IAutoSteerService autoSteer, int port = 5174)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

        builder.Services.AddSingleton(state);
        builder.Services.AddSingleton(coverage);
        builder.Services.AddSingleton(sections);
        builder.Services.AddSingleton(tool);
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(jobs);
        builder.Services.AddSingleton(configService);
        builder.Services.AddSingleton(autoSteer);
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

        // Build the broadcaster now so it wires SeedProvider before clients connect.
        app.Services.GetRequiredService<MapBroadcaster>();

        // Hook the WS hub for inbound commands (apply any handler set pre-start).
        _ws = app.Services.GetRequiredService<WebSocketHub>();
        _ws.CommandHandler = _commandHandler;
        _ws.IsRestrictedCommand = _isRestricted;

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
