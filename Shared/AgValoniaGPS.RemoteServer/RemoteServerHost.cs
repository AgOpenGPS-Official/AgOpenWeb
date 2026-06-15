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
    public Action<string>? CommandHandler
    {
        get => _ws?.CommandHandler;
        set { _commandHandler = value; if (_ws is not null) _ws.CommandHandler = value; }
    }
    private Action<string>? _commandHandler;

    /// <param name="state">The live DI ApplicationState the app/pipeline updates.</param>
    /// <param name="port">Bound on 0.0.0.0 so LAN clients (tablets) can connect.</param>
    public async Task StartAsync(ApplicationState state, ICoverageMapService coverage,
        ISectionControlService sections, IToolPositionService tool, int port = 5174)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

        builder.Services.AddSingleton(state);
        builder.Services.AddSingleton(coverage);
        builder.Services.AddSingleton(sections);
        builder.Services.AddSingleton(tool);
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

        // Build the broadcaster now so it wires SeedProvider before clients connect.
        app.Services.GetRequiredService<MapBroadcaster>();

        // Hook the WS hub for inbound commands (apply any handler set pre-start).
        _ws = app.Services.GetRequiredService<WebSocketHub>();
        _ws.CommandHandler = _commandHandler;

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
}
