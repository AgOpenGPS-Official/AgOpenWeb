# SimpleWebServer Swap — replace Kestrel with a hand-rolled TcpListener host

**Status:** Planned. Prereq spike PASSED (`Spikes/iOSAotSpike/FINDINGS.md`).
**Decision:** One hand-rolled `SimpleWebServer` (raw `TcpListener`) on **every**
platform; **delete the ASP.NET Core dependency entirely.** Driven by the hard iOS
blocker (`NETSDK1082` — no `Microsoft.AspNetCore.App` runtime pack for `ios-arm64`),
but applied everywhere because the serving load is trivial and one engine beats two.

---

## 1. Why this is small and safe

Kestrel/ASP.NET is **not** spread across the codebase. Verified:

- Only `.cs` file using ASP.NET host types: **`Shared/AgOpenWeb.RemoteServer/RemoteServerHost.cs`**.
- Only `.csproj` with the FrameworkReference: **`Shared/AgOpenWeb.RemoteServer/AgOpenWeb.RemoteServer.csproj`**.

Everything else in `RemoteServer` is already pure BCL / Services-only:
`WebSocketHub` (raw `System.Net.WebSockets`), `WireCodec` (`BinaryWriter`, no JSON
reflection), `SceneProjector`, `CoverageProjector`, `MapBroadcaster`,
`ControlAuthority`, `Contracts`. The entire `App.RemoteWiring` command surface uses
only the `RemoteServerHost` public API + DTOs — **no ASP.NET types.**

So the swap = **rewrite one class + delete one FrameworkReference.** The hub/codec
never needed splitting because they never depended on ASP.NET.

## 2. Target architecture

Two types inside `AgOpenWeb.RemoteServer` (project becomes ASP.NET-free):

- **`SimpleWebServer`** — the reusable HTTP/1.1 + WebSocket engine over `TcpListener`.
  Pure transport: accept loop, request parsing, static-route dispatch, WS upgrade,
  connection lifetime. Knows nothing about Scene/Tick/projectors.
- **`RemoteServerHost`** — unchanged role and **unchanged public API**. Keeps the
  orchestration (construct projectors + broadcaster, wire providers/authority,
  register routes on the `SimpleWebServer`). Internally delegates all HTTP to
  `SimpleWebServer` instead of a Kestrel `WebApplication`.

Callers (`App.axaml.cs` windowed, `BackendHost`/`HeadlessHost` headless, all-in-one
mobile heads later) keep calling `new RemoteServerHost()` → `StartAsync(...)` →
`WireRemoteServer(...)` → `StopAsync()`. **Zero caller churn.**

```
RemoteServer (no FrameworkReference)
├── SimpleWebServer        ← NEW: TcpListener HTTP/1.1 + WS engine
├── RemoteServerHost       ← rewritten internals, SAME public API
├── WebSocketHub           ← unchanged (already raw WebSocket)
├── WireCodec / Contracts  ← unchanged
├── SceneProjector / CoverageProjector / MapBroadcaster / ControlAuthority ← unchanged
```

## 3. Public API parity contract (must not change)

`RemoteServerHost` keeps every member callers depend on:

- `int Port { get; }`, `int ClientCount { get; }`
- `static Task<byte[]?> FetchSatTileAsync(string quadkey)`  *(used by `BoundaryImageryCapture`)*
- Providers: `WizardProvider`, `RecordedPathProvider`, `BoundaryProvider`,
  `HeadlandSegsProvider`, `TramLinesProvider`
- Command surface: `CommandHandler`, `IsRestrictedCommand`
- Authority hooks: `AuthorityChangedHandler`, `FailsafeHandler`
- `Task StartAsync(ApplicationState, ICoverageMapService, ISectionControlService,
  IToolPositionService, ConfigurationStore, IJobService, IConfigurationService,
  IAutoSteerService, ISmartWasCalibrationService, IUdpCommunicationService,
  INtripProfileService, IFieldService, ISettingsService, IVehicleProfileService,
  IPersistentStateService, int port = 5174)`
- `Task StopAsync()`

## 4. Replace the ASP.NET DI container with direct construction

Today `StartAsync` registers the 15 injected services as singletons, then resolves
the projectors/hub/broadcaster from `app.Services`. With Kestrel gone there is no
container — **construct them directly** (ctors are unchanged, the mapping is 1:1):

```
var authority   = new ControlAuthority();
var sceneProj   = new SceneProjector(state, sections, tool, config, coverage, jobs,
                      configService, autoSteer, smartWas, udp, ntripProfiles,
                      fields, settings, vehicleProfiles, persist);
var covProj     = new CoverageProjector(coverage);
var hub         = new WebSocketHub(authority);
var broadcaster = new MapBroadcaster(hub, sceneProj, coverage, covProj, authority);
```

Then wire exactly as today (providers, `hub.CommandHandler`/`IsRestrictedCommand`,
`authority.Changed`/`Revoked` → broadcast + handlers), `broadcaster.Start()`, and
start the `SimpleWebServer`. **Delete** the ASP.NET-only ceremony:
`NoopHostLifetime`, `HostOptions.ShutdownTimeout`, `IHostApplicationLifetime`
linking — `StopAsync` cancels our own CTS and stops the listener.

## 5. Route port table (Kestrel `MapGet` → `SimpleWebServer` handler)

All are "produce bytes + content-type". `SimpleWebServer` exposes a tiny registration
API (`MapGet(path, handler)`, `MapGetPrefix("/icons/", handler)`, `MapWebSocket("/ws", hub.HandleAsync)`).

| Route | Source | Content-Type | Notes |
|---|---|---|---|
| `GET /` | embedded `index.html` | `text/html; charset=utf-8` | |
| `GET /app.js` | embedded | `text/javascript` | |
| `GET /transport.js` | embedded | `text/javascript` | |
| `GET /manifest.webmanifest` | embedded | `application/manifest+json` | PWA install |
| `GET /vendor/canvaskit.js` | embedded | `text/javascript` | |
| `GET /vendor/canvaskit.wasm` | embedded | **`application/wasm`** | required for streaming-compile |
| `GET /icons/{file}` | embedded `icons.*` | `image/png` / `image/gif` | **path-traversal guard**; 404 unknown |
| `GET /backpic.png` | field imagery from disk | `image/png` | 404 when no imagery |
| `GET /sattile/{quadkey}` | `FetchSatTileAsync` (Bing proxy) | `image/jpeg` | validate quadkey (`0–3`, len ≤ 23); `Cache-Control: public, max-age=604800` |
| `GET /ws` | `WebSocketHub.HandleAsync` | — | RFC-6455 upgrade → `WebSocket.CreateFromStream(stream, IsServer:true)` |

The spike already proved `/` + `/ws`; the rest are the same handler shape with
different bytes/MIME.

## 6. `SimpleWebServer` engine spec (HTTP/1.1 subset)

Productionize beyond the spike's proof-of-concept:

- **Bind** `0.0.0.0:port` (LAN + loopback). `TcpClient.NoDelay = true`.
- **Accept loop**: one `Task` per connection (spike pattern); bounded by a max-
  concurrent-connections semaphore.
- **Request parse**: read request line + headers to `\r\n\r\n`; **cap header bytes**
  (16 KB) and reject oversize with `431`. Lowercase header lookup.
- **Keep-alive**: honor HTTP/1.1 persistent connections — after a GET response, loop
  and read the next request on the same socket unless `Connection: close`. (Spike
  used close-per-request; fine for localhost, wasteful for a 9-asset LAN page load.)
  Always frame responses with `Content-Length`.
- **WebSocket upgrade**: `SHA1(key + magic)` → base64 `Sec-WebSocket-Accept`, write
  `101`, hand the raw `NetworkStream` to `WebSocket.CreateFromStream`, then call the
  registered `hub.HandleAsync(socket, ct)` — **verbatim reuse**, the hub already owns
  the socket lifetime, seed, drain, and close.
- **Shutdown**: `StopAsync` cancels the server CTS (linked into every `HandleAsync`
  so connected browsers don't stall the stop) and `listener.Stop()`.
- **Errors**: a faulting connection must never take down the accept loop or other
  clients (per-connection try/catch, as in the spike).

## 7. Harden checklist (LAN-exposed = untrusted input)

- [ ] Path-traversal guard on `/icons/{file}` (reject `/`, `\`, `..`) — carried over.
- [ ] Header-size + request-line caps; reject malformed with `400`.
- [ ] Max concurrent connections; idle/read timeout so a slow-loris can't pin sockets.
- [ ] Quadkey validation on `/sattile` (carried over).
- [ ] Correct `Content-Length` on every response (no chunked needed).
- [ ] `application/wasm` MIME (CanvasKit streaming-compile).
- [ ] No directory listing, no fallthrough file serving — only the explicit routes.

## 8. Implementation sequence

1. **Add `SimpleWebServer.cs`** to `RemoteServer` (productionized from the spike's
   `SpikeWebHost`: keep-alive, limits, route registry, `MapWebSocket`).
2. **Rewrite `RemoteServerHost.cs`** internals to use `SimpleWebServer` + direct
   construction (§4) + route registration (§5). Public API unchanged (§3).
3. **Delete** `<FrameworkReference Include="Microsoft.AspNetCore.App" />` from
   `AgOpenWeb.RemoteServer.csproj`. Confirm it now restores/builds with no ASP.NET.
4. **Build all platforms** — Desktop (win/mac/linux), Android, and a device build for
   iOS (`-c Release -r ios-arm64`) to confirm the project is now iOS-referenceable.
5. **Delete the spike head** once the real host is device-verified (it served its
   purpose).

## 9. Verification matrix

| Target | How | Pass criteria |
|---|---|---|
| Desktop windowed | run app, open browser on localhost + a LAN device | Scene/Tick/coverage + all panels behave identically to the Kestrel build; field switch, commands, wizard, boundary/headland/tram all work |
| Headless cab-PC (`BackendHost`/`HeadlessHost`) | run `--windowed`-off daemon, connect PWA | same parity; clean start/stop, no stalled-shutdown |
| Android all-in-one | foreground-service host + WebView | page loads, WS streams, commands round-trip |
| **iOS all-in-one** | real `RemoteServerHost` + full projectors on physical iPad, `-r ios-arm64` Release | page loads in WKWebView, WS streams, command tap reaches host (spike already proved the engine; this proves the real host) |
| Load | 9-asset page load + multiple WS clients | keep-alive reuse; no connect storms; stable under field driving |

## 10. Risks & deferrals

- **HTTP framing bugs** (keep-alive, Content-Length) — main hand-rolled risk;
  covered by the verification matrix and the header/timeout caps.
- **TLS (`https`/`wss`) is deferred, not lost.** localhost (mobile all-in-one) and
  trusted-LAN don't need it. If the **distributed/remote** host later serves over an
  untrusted network, wrap the accepted socket in `SslStream` inside `SimpleWebServer`
  — still one engine. TLS is **not** a reason to keep Kestrel.
- **Range/gzip** intentionally omitted — all assets are small and fetched whole.

## 11. Follow-on dependency (not part of this swap)

The iOS/Android **all-in-one heads** need the same `WireRemoteServer` command/projector
wiring that currently lives in `Platforms/AgOpenWeb.Desktop/App.RemoteWiring.cs`.
Hoisting that to a Shared location (so all heads reuse one wiring) is part of the
broader web-UI migration (`Plans/WEBUI_MIGRATION_PLAN.md`), tracked separately. This
swap only makes `RemoteServer` referenceable by those heads; it does not move the
wiring. Sequencing: this swap can land before or after the AgOpenWeb namespace rename
(independent).
