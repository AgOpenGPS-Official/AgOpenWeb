// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using AgOpenWeb.Models;
using AgOpenWeb.Models.State;
using AgOpenWeb.RemoteWiring;
using AgOpenWeb.Services;
using AgOpenWeb.Services.AutoSteer;
using AgOpenWeb.Services.Coverage;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.Gps;
using AgOpenWeb.Services.Headland;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Pipeline;
using AgOpenWeb.Services.Section;
using AgOpenWeb.Services.Threading;
using AgOpenWeb.Services.Tool;
using AgOpenWeb.Services.Track;
using AgOpenWeb.Services.Tram;
using AgOpenWeb.Services.YouTurn;
using AgOpenWeb.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgOpenWeb.WebClientScenarios.Tests;

/// <summary>
/// The whole AgOpenWeb backend in this process: every service real (the Desktop head's
/// registrations, the real UDP service on port 9999), a throwaway data folder, the embedded web
/// server on a free port and the command handler wired exactly as on the tractor. The browsers
/// are real (<see cref="Browsers"/>) and talk to that server over real sockets, so the control
/// authority, the observer gate and the failsafe are the shipped ones.
///
/// The tractor is AgOpenWeb's own GPS simulator (the operator's demo mode), which AutoSteer
/// steers. What the machine gets is read where a module reads it: <see cref="ModuleListener"/>
/// on 127.0.0.1:8888, where AgOpenWeb always sends its PGNs as well.
/// </summary>
public sealed class WebClientRig : IAsyncDisposable
{
    public string DataRoot { get; }
    public string ImportDir => Path.Combine(DataRoot, "AgOpenWeb", "Import");
    public IServiceProvider Services { get; }
    public MainViewModel Vm { get; }
    public AgOpenWeb.RemoteServer.RemoteServerHost Server { get; }
    public ApplicationState State { get; }
    public IAutoSteerService AutoSteer { get; }
    public ISectionControlService Sections { get; }
    public AgOpenWeb.Models.Configuration.ConfigurationStore Store { get; }
    public ModuleListener Module { get; }

    /// <summary>The page every screen opens (directly, or through its own <see cref="LinkRelay"/>).</summary>
    public int Port => Server.Port;

    private readonly ConcurrentQueue<RigEvent> _events = new();
    private readonly HostLoopDispatcher _hostLoop;

    /// <summary>Control authority changes as the host saw them (held, name, why it ended), with the time.</summary>
    public ConcurrentQueue<(long Ms, bool Held, string Reason)> ControlLog { get; } = new();
    private string? _lostReason;

    private WebClientRig(string dataRoot, IServiceProvider sp, MainViewModel vm,
        AgOpenWeb.RemoteServer.RemoteServerHost server, HostLoopDispatcher hostLoop, ModuleListener module)
    {
        DataRoot = dataRoot;
        Services = sp;
        Vm = vm;
        Server = server;
        _hostLoop = hostLoop;
        Module = module;
        State = sp.GetRequiredService<ApplicationState>();
        AutoSteer = sp.GetRequiredService<IAutoSteerService>();
        Sections = sp.GetRequiredService<ISectionControlService>();
        Store = sp.GetRequiredService<AgOpenWeb.Models.Configuration.ConfigurationStore>();
    }

    public static async Task<WebClientRig> StartAsync(string dataRoot)
    {
        Directory.CreateDirectory(dataRoot);
        Environment.SetEnvironmentVariable(AppDataRoot.EnvVar, dataRoot);
        AgOpenWeb.Models.Configuration.ConfigurationStore.SetInstance(new AgOpenWeb.Models.Configuration.ConfigurationStore());

        var events = new ConcurrentQueue<RigEvent>();
        var hostLoop = new HostLoopDispatcher();
        var dispatcher = new CapturingDispatcher(hostLoop, ex => events.Enqueue(new RigEvent(RigEventKind.Exception, ex.ToString())));

        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.AddProvider(new CapturingLoggerProvider(e => events.Enqueue(e)));
            b.SetMinimumLevel(LogLevel.Debug);
        });
        AddBackendServices(services);
        services.AddSingleton(hostLoop);
        services.AddSingleton<IUiDispatcher>(dispatcher);
        services.AddSingleton<IUiTimerFactory>(dispatcher);
        var sp = services.BuildServiceProvider();

        // Listening before the backend starts, so the first PGNs are seen too.
        var module = new ModuleListener();
        module.Start();

        // The sequence of WebBackend.StartAsync, with a free port instead of 5174 so a running
        // AgOpenWeb on the same machine is left alone.
        sp.GetRequiredService<ISettingsService>().Load();
        var configService = sp.GetRequiredService<IConfigurationService>();
        configService.LoadAppSettings();
        sp.GetRequiredService<IPersistentStateService>().Load();
        WebBackend.WireCrossServices(sp);
        MainViewModel vm = null!;
        await hostLoop.InvokeAsync(() => vm = sp.GetRequiredService<MainViewModel>());

        var server = new AgOpenWeb.RemoteServer.RemoteServerHost();
        await server.StartAsync(
            sp.GetRequiredService<ApplicationState>(),
            sp.GetRequiredService<ICoverageMapService>(),
            sp.GetRequiredService<ISectionControlService>(),
            sp.GetRequiredService<IToolPositionService>(),
            sp.GetRequiredService<AgOpenWeb.Models.Configuration.ConfigurationStore>(),
            sp.GetRequiredService<IJobService>(),
            configService,
            sp.GetRequiredService<IAutoSteerService>(),
            sp.GetRequiredService<ISmartWasCalibrationService>(),
            sp.GetRequiredService<IUdpCommunicationService>(),
            sp.GetRequiredService<INtripProfileService>(),
            sp.GetRequiredService<IFieldService>(),
            sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<IVehicleProfileService>(),
            sp.GetRequiredService<IPersistentStateService>(),
            sp.GetService<INtripClientService>(),
            port: FreePort());
        await hostLoop.InvokeAsync(() =>
            RemoteServerWiring.Wire(server, vm, sp, configService, new NoImagery()));
        // The tractor here is the internal simulator, and the harness reads what the steer module
        // is told. On the simulator that is "don't steer" unless the operator chose the bench
        // test, so the rig makes that choice: PGN 254 then says what it says on a receiver.
        await hostLoop.InvokeAsync(() => vm.IsSimulatorDrivingSteering = true);
        if (!vm.IsSimulatorDrivingSteering)
            throw new InvalidOperationException("The rig's simulator may not drive the steer module: "
                + "PGN 254 would say 'don't steer' whatever a scenario does (is the simulator off?).");

        var rig = new WebClientRig(dataRoot, sp, vm, server, hostLoop, module);
        while (events.TryDequeue(out var e)) rig._events.Enqueue(e);
        dispatcher.OnException = ex => rig._events.Enqueue(new RigEvent(RigEventKind.Exception, ex.ToString()));
        CapturingLoggerProvider.Sink = e => rig._events.Enqueue(e);
        vm.FailureReported += msg => rig._events.Enqueue(new RigEvent(RigEventKind.Failure, msg));
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.StatusMessage))
                rig._events.Enqueue(new RigEvent(RigEventKind.Status, vm.StatusMessage ?? ""));
        };

        // See the authority change as the host does, then let the wired handlers run as before.
        var changed = server.AuthorityChangedHandler;
        var failsafe = server.FailsafeHandler;
        server.FailsafeHandler = reason =>
        {
            rig._lostReason = reason;
            failsafe?.Invoke(reason);
        };
        server.AuthorityChangedHandler = (held, name) =>
        {
            rig.ControlLog.Enqueue((ModuleListener.NowMs, held, held ? name : rig._lostReason ?? "released"));
            if (!held) rig._lostReason = null;
            changed?.Invoke(held, name);
        };
        await rig.DrainAsync();
        rig.TakeEvents();
        return rig;
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>The Desktop head's registrations (Platforms/AgOpenWeb.Desktop/DependencyInjection),
    /// minus what needs that head: battery reader (null one) and the ISOBUS VT stack (absent, as
    /// on a head without it). The network interfaces are none (<see cref="NoNetworkInterfaces"/>),
    /// so the PGNs go to 127.0.0.1:8888 only and never to the LAN, where a real module would act
    /// on them.</summary>
    private static void AddBackendServices(IServiceCollection services)
    {
        services.AddSingleton<ApplicationState>();
        services.AddSingleton(_ => AgOpenWeb.Models.Configuration.ConfigurationStore.Instance);
        services.AddSingleton(_ => PersistentAppState.Instance);
        services.AddTransient<MainViewModel>();
        services.AddTransient<ConfigurationViewModel>();
        services.AddSingleton<ILocalNetworkInfoProvider, NoNetworkInterfaces>();
        services.AddSingleton<AgOpenWeb.Services.Network.INetworkWatcher, AgOpenWeb.Services.Network.NetworkWatcher>();
        services.AddSingleton<IUdpCommunicationService, UdpCommunicationService>();
        services.AddSingleton<IGpsService, GpsService>();
        services.AddSingleton<IDisplaySettingsService, DisplaySettingsService>();
        services.AddSingleton<IFieldStatisticsService, FieldStatisticsService>();
        services.AddSingleton<IGpsSimulationService, GpsSimulationService>();
        services.AddSingleton<IFieldService, FieldService>();
        services.AddSingleton<INtripClientService, NtripClientService>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IPersistentStateService, PersistentStateService>();
        services.AddSingleton<IJobService>(sp =>
        {
            var settings = sp.GetRequiredService<ISettingsService>();
            return new JobService(() => settings.Settings.FieldsDirectory);
        });
        services.AddSingleton<FieldPlaneFileService>();
        services.AddSingleton<IBoundaryRecordingService, BoundaryRecordingService>();
        services.AddSingleton<IBoundaryBuilderService, BoundaryBuilderService>();
        services.AddSingleton<IPolygonOffsetService, PolygonOffsetService>();
        services.AddSingleton<IHeadlandBuilderService, HeadlandBuilderService>();
        services.AddSingleton<ITurnAreaService, TurnAreaService>();
        services.AddSingleton<ITrackGuidanceService, TrackGuidanceService>();
        services.AddSingleton<IAutoSteerService, AutoSteerService>();
        services.AddSingleton<IIsobusTaskControllerService, AgOpenWeb.Services.Isobus.IsobusTaskControllerService>();
        services.AddSingleton<ISmartWasCalibrationService, SmartWasCalibrationService>();
        services.AddSingleton<ITrackCopierService, TrackCopierService>();
        services.AddSingleton<IChartDataService, ChartDataService>();
        services.AddSingleton<IAudioService, AgOpenWeb.Services.Audio.WebClientAudioService>();
        services.AddSingleton<IBatteryService, AgOpenWeb.Services.Battery.NullBatteryService>();
        services.AddSingleton<IModuleCommunicationService, ModuleCommunicationService>();
        services.AddSingleton<YouTurnCreationService>();
        services.AddSingleton<YouTurnGuidanceService>();
        services.AddSingleton<YouTurnPathingService>();
        services.AddSingleton<YouTurnStateMachine>();
        services.AddSingleton<IToolPositionService, ToolPositionService>();
        services.AddSingleton<IWorkedAreaService, WorkedAreaService>();
        services.AddSingleton<ICoverageMapService, CoverageMapService>();
        services.AddSingleton<ISectionControlService, SectionControlService>();
        services.AddSingleton<ITramLineOffsetService, TramLineOffsetService>();
        services.AddSingleton<ITramLineService, TramLineService>();
        services.AddSingleton<IVehicleProfileService, VehicleProfileService>();
        services.AddSingleton<IToolProfileService, ToolProfileService>();
        services.AddSingleton<INtripProfileService, NtripProfileService>();
        services.AddSingleton<IConfigurationService, ConfigurationService>();
        services.AddSingleton<IElevationLogService, ElevationLogService>();
        services.AddSingleton<AgOpenWeb.Models.Pipeline.IPipelineIntents, PipelineIntents>();
        services.AddSingleton<IGpsHeadingFusionService, GpsHeadingFusionService>();
        services.AddSingleton<IPositionEstimator, PositionEstimator>();
        services.AddSingleton<ISteerMachineLoopService>(_ => new SteerMachineLoopService(frequencyHz: 100.0));
        services.AddSingleton<IGpsPipelineService, GpsPipelineService>();
        services.AddSingleton<IMapService, NullMapService>();
    }

    // ------------------------------------------------------------------ the job

    /// <summary>The made-up field: 200 m east-west by 600 m north-south, near 41.5 N 5.5 W.</summary>
    public static readonly (double Lat, double Lon) Origin = (41.512, -5.493);
    public const double FieldWidthM = 200, FieldLengthM = 600, LineE = 40;
    public const string FieldName = "Test Field";

    public static (double Lat, double Lon) At(double eastM, double northM)
    {
        const double R = 6371000.0;
        return (Origin.Lat + northM / R * 180 / Math.PI,
                Origin.Lon + eastM / (R * Math.Cos(Origin.Lat * Math.PI / 180)) * 180 / Math.PI);
    }

    private static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    /// <summary>What an operator sets up before work, through the web client's own commands: an
    /// 8 m boom in eight 1 m sections, the field from a KML file, an AB line along it, a job.</summary>
    public async Task<string?> SetUpJobAsync()
    {
        await SendAsync("config.set", "tool.numSections:8");
        for (int i = 0; i < 8; i++) await SendAsync("config.set", F($"tool.sectionWidth:{i},100"));
        await SendAsync("profile.save");

        Directory.CreateDirectory(ImportDir);
        var ring = new[] { At(0, 0), At(FieldWidthM, 0), At(FieldWidthM, FieldLengthM), At(0, FieldLengthM), At(0, 0) };
        string coords = string.Join(" ", ring.Select(p => F($"{p.Lon:F9},{p.Lat:F9},0")));
        await File.WriteAllTextAsync(Path.Combine(ImportDir, FieldName + ".kml"),
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<kml xmlns=\"http://www.opengis.net/kml/2.2\"><Document><name>"
            + FieldName + "</name><Placemark><name>" + FieldName + "</name><Polygon><outerBoundaryIs><LinearRing><coordinates>"
            + coords + "</coordinates></LinearRing></outerBoundaryIs></Polygon></Placemark></Document></kml>\n");
        var start = At(LineE, 30);
        await SendAsync("sim.setCoords", F($"{start.Lat:F9},{start.Lon:F9}"));
        await SendAsync("import.scan");
        await SendAsync("field.fromKml", FieldName + ".kml\t" + FieldName);
        if (!await WaitUntilAsync(() => Vm.IsFieldOpen && Vm.CurrentFieldName == FieldName && !State.UI.IsBusy, 20000))
            return "the field did not open from the KML";

        var a = At(LineE, 10);
        var b = At(LineE, FieldLengthM - 10);
        await SendAsync("track.latLonAB", F($"{a.Lat:F9},{a.Lon:F9},{b.Lat:F9},{b.Lon:F9}"));
        if (!await ReadAsync(() => Vm.IsAutoSteerAvailable)) await SendAsync("track.activate", "0");
        if (!await ReadAsync(() => Vm.IsAutoSteerAvailable)) return "no active AB line";
        await SendAsync("field.startJob", FieldName + "\tSpraying\tscenario\tSpraying");
        await PlaceTractorAsync(30);
        return null;
    }

    /// <summary>Put the simulated tractor <paramref name="northM"/> into the field on the AB line
    /// (or <paramref name="eastM"/>, a pass beside it), standing still, AutoSteer off, sections
    /// off: the start of each scenario.</summary>
    public async Task PlaceTractorAsync(double northM, double eastM = LineE)
    {
        await RunAsync(() =>
        {
            if (Vm.IsAutoSteerEngaged) Vm.ToggleAutoSteerCommand?.Execute("scenario start");
            if (Vm.IsSectionMasterOn) Vm.ToggleSectionMasterCommand?.Execute(null);
            if (Vm.IsManualSectionMode) Vm.ToggleManualModeCommand?.Execute(null);
            if (Vm.AutoSteerConfigViewModel is { IsFreeDriveMode: true } asc) asc.ToggleFreeDriveCommand.Execute(null);
            if (AutoSteer.IsInFreeDriveMode) AutoSteer.DisableFreeDrive();
            Sections.SetAllSections(SectionButtonState.Off);
            Vm.SimulatorSpeedKph = 0;
        });
        var p = At(eastM, northM);
        await SendAsync("sim.setCoords", F($"{p.Lat:F9},{p.Lon:F9}"));
        await Task.Delay(400);
        await DrainAsync();
    }

    public Task SetSpeedAsync(double kmh) => RunAsync(() => Vm.SimulatorSpeedKph = kmh);

    // ------------------------------------------------------------------ host access

    /// <summary>One command straight into the host's handler (set-up only; scenarios use the
    /// browsers, so the authority gate applies).</summary>
    public async Task SendAsync(string cmd, string arg = "")
    {
        var handler = Server.CommandHandler ?? throw new InvalidOperationException("command handler not wired");
        handler(cmd, arg);
        await DrainAsync();
        await Task.Delay(60);
        await WaitUntilAsync(() => !State.UI.IsBusy, 15000);
    }

    public async Task DrainAsync(int rounds = 3)
    {
        for (int i = 0; i < rounds; i++)
        {
            var t = _hostLoop.InvokeAsync(() => { });
            if (await Task.WhenAny(t, Task.Delay(TimeSpan.FromSeconds(30))) != t)
            {
                _events.Enqueue(new RigEvent(RigEventKind.Hang, "host loop did not answer within 30 s"));
                return;
            }
        }
    }

    public async Task<T> ReadAsync<T>(Func<T> read)
    {
        T value = default!;
        await _hostLoop.InvokeAsync(() => value = read());
        return value;
    }

    public Task RunAsync(Action act) => _hostLoop.InvokeAsync(act);

    public async Task<bool> WaitUntilAsync(Func<bool> done, int timeoutMs = 10000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (await ReadAsync(done)) return true;
            await Task.Delay(20);
        }
        return false;
    }

    /// <summary>Everything the host did since the last call: exceptions, errors, toasts, status lines.</summary>
    public List<RigEvent> TakeEvents()
    {
        var list = new List<RigEvent>();
        while (_events.TryDequeue(out var e)) list.Add(e);
        return list;
    }

    /// <summary>The host's state as the machine sees it, in one line.</summary>
    public Task<HostState> HostAsync() => ReadAsync(HostNow);

    /// <summary>As <see cref="HostAsync"/>, on the host loop already.</summary>
    public HostState HostNow() => new HostState(
        Vm.IsAutoSteerEngaged, AutoSteer.IsInFreeDriveMode, AutoSteer.FreeDriveSteerAngle,
        Vm.IsSectionMasterOn, Vm.IsManualSectionMode,
        Enumerable.Range(0, Sections.NumSections).Count(i => Sections.SectionStates[i].ButtonState == SectionButtonState.On),
        Vm.IsSteerWizardOpen, Vm.StatusMessage ?? "");

    public async ValueTask DisposeAsync()
    {
        try { await ReadAsync(() => { Vm.SaveOpenFieldForExit(TimeSpan.FromSeconds(5)); return 0; }); } catch { }
        try { await Server.StopAsync(); } catch { }
        if (Services is IDisposable d) { try { d.Dispose(); } catch { } }
        Module.Dispose();
        _hostLoop.Dispose();
    }

    /// <summary>A machine with no network but the loopback: the UDP service's discovery sends go
    /// to 127.0.0.1:8888 only (it always adds that one), and the module scan and subnet commands,
    /// sent once per interface, go nowhere.</summary>
    private sealed class NoNetworkInterfaces : ILocalNetworkInfoProvider
    {
        public IReadOnlyList<LocalNetworkAddress> GetIPv4Addresses() => Array.Empty<LocalNetworkAddress>();
    }

    private sealed class NoImagery : IBoundaryImageryCapture
    {
        public Task<bool> TryCaptureAsync(double a, double b, double c, double d, string outPath) => Task.FromResult(false);
    }
}

public sealed record HostState(bool AutoSteer, bool FreeDrive, double FreeDriveAngle, bool Master, bool Manual,
    int SectionsForcedOn, bool Wizard, string Status)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"AutoSteer {(AutoSteer ? "on" : "off")}, free drive {(FreeDrive ? $"on {FreeDriveAngle:0}°" : "off")}, master {(Master ? "auto" : "off")}, manual {(Manual ? "on" : "off")}, {SectionsForcedOn} section(s) forced on{(Wizard ? ", steer wizard open" : "")}");
}

public enum RigEventKind { Exception, Error, Warning, Failure, Status, Hang, Note }

public sealed record RigEvent(RigEventKind Kind, string Text)
{
    public override string ToString() => $"{Kind}: {Text}";
}

/// <summary>The host loop with one difference: an exception thrown by a work item is reported
/// to the harness before the real loop swallows it (it only writes it to Debug output).</summary>
internal sealed class CapturingDispatcher : IUiDispatcher, IUiTimerFactory
{
    private readonly HostLoopDispatcher _inner;
    public Action<Exception> OnException { get; set; }

    public CapturingDispatcher(HostLoopDispatcher inner, Action<Exception> onException)
    {
        _inner = inner;
        OnException = onException;
    }

    public bool CheckAccess() => _inner.CheckAccess();

    public void Post(Action action, UiDispatcherPriority priority = UiDispatcherPriority.Default) =>
        _inner.Post(Wrap(action), priority);

    public Task InvokeAsync(Action action, UiDispatcherPriority priority = UiDispatcherPriority.Default) =>
        _inner.InvokeAsync(Wrap(action), priority);

    public IUiTimer Create() => new TimerWrapper(_inner.Create(), this);

    private Action Wrap(Action action) => () =>
    {
        try { action(); }
        catch (Exception ex) { OnException(ex); throw; }
    };

    private sealed class TimerWrapper : IUiTimer, IDisposable
    {
        private readonly IUiTimer _inner;
        private readonly CapturingDispatcher _owner;
        private EventHandler? _tick;

        public TimerWrapper(IUiTimer inner, CapturingDispatcher owner)
        {
            _inner = inner;
            _owner = owner;
            _inner.Tick += (s, e) =>
            {
                try { _tick?.Invoke(s, e); }
                catch (Exception ex) { _owner.OnException(ex); throw; }
            };
        }

        public TimeSpan Interval { get => _inner.Interval; set => _inner.Interval = value; }
        public bool IsEnabled => _inner.IsEnabled;
        public event EventHandler? Tick { add => _tick += value; remove => _tick -= value; }
        public void Start() => _inner.Start();
        public void Stop() => _inner.Stop();
        public void Dispose() => (_inner as IDisposable)?.Dispose();
    }
}

/// <summary>Keeps warnings and errors from every service's logger.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public static Action<RigEvent>? Sink;

    public CapturingLoggerProvider(Action<RigEvent> sink) { Sink = sink; }

    public ILogger CreateLogger(string categoryName) => new L(categoryName);
    public void Dispose() { }

    private sealed class L(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Warning) return;
            var text = category + ": " + formatter(state, exception) + (exception is null ? "" : " | " + exception.GetType().Name + ": " + exception.Message);
            Sink?.Invoke(new RigEvent(logLevel >= LogLevel.Error ? RigEventKind.Error : RigEventKind.Warning, text));
        }
    }
}
