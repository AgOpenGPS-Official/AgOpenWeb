// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.State;
using AgOpenWeb.RemoteServer;
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

namespace AgOpenWeb.SteerWizardScenarios.Tests;

/// <summary>
/// The whole AgOpenWeb backend in this process, wired as on the tractor (the Desktop head's
/// services, the embedded web server, <see cref="RemoteServerWiring"/>), with two things
/// in place of the outside world:
/// <list type="bullet">
/// <item>the steer module and the steering it drives (<see cref="EmulatedSteerModule"/>, over
/// <see cref="ModuleWire"/> in place of the UDP service);</item>
/// <item>the tractor moving on the ground: a bicycle model driven by the REAL wheel angle at the
/// operator's speed, reported as RTK fixes at 10 Hz from the antenna's position.</item>
/// </list>
/// The harness sends the web client's own "id|arg" commands and reads the Wizard frame the
/// browser would get (<see cref="Frame"/>), so it sees exactly what the operator sees.
/// </summary>
public sealed class SteerWizardRig : IAsyncDisposable
{
    public string DataRoot { get; }
    public IServiceProvider Services { get; }
    public MainViewModel Vm { get; }
    public RemoteServerHost Server { get; }
    public ConfigurationStore Store { get; }
    public IAutoSteerService AutoSteer { get; }
    public IConfigurationService Config { get; }
    public EmulatedSteerModule Module { get; }
    public ModuleWire Wire { get; }
    public ApplicationState State { get; }

    private readonly ConcurrentQueue<RigEvent> _events = new();
    private readonly HostLoopDispatcher _hostLoop;
    private readonly CancellationTokenSource _cts = new();
    private Task? _gpsTask;

    // ------------------------------------------------------------ the tractor on the ground
    /// <summary>Real wheelbase / pivot of the emulated tractor (Case Puma 180 CVX class).</summary>
    public double TruthWheelbase { get; set; } = 2.88;
    public double TruthAntennaAheadOfRearAxle { get; set; } = 0.6;
    public double TruthTrackWidth { get; set; } = 1.9;
    /// <summary>Operator's ground speed, km/h (+ forwards).</summary>
    public double SpeedKmh { get; set; }
    /// <summary>GGA fix quality the receiver reports (4 RTK fixed, 5 float, 1 GPS, 0 none).</summary>
    public int FixQuality { get; set; } = 4;
    /// <summary>The receiver is sending at all.</summary>
    public bool GpsOn { get; set; } = true;
    private double _rearE, _rearN, _headingRad;
    private readonly object _poseLock = new();
    private static readonly (double Lat, double Lon) Origin = (41.512, -5.493); // made-up place

    private SteerWizardRig(string dataRoot, IServiceProvider sp, MainViewModel vm, RemoteServerHost server,
        HostLoopDispatcher hostLoop, EmulatedSteerModule module, ModuleWire wire)
    {
        DataRoot = dataRoot;
        Services = sp;
        Vm = vm;
        Server = server;
        _hostLoop = hostLoop;
        Module = module;
        Wire = wire;
        Store = sp.GetRequiredService<ConfigurationStore>();
        AutoSteer = sp.GetRequiredService<IAutoSteerService>();
        Config = sp.GetRequiredService<IConfigurationService>();
        State = sp.GetRequiredService<ApplicationState>();
    }

    public static async Task<SteerWizardRig> StartAsync(string dataRoot, Action<EmulatedSteerModule>? truth = null)
    {
        Directory.CreateDirectory(dataRoot);
        Environment.SetEnvironmentVariable(AppDataRoot.EnvVar, dataRoot);
        ConfigurationStore.SetInstance(new ConfigurationStore());

        var events = new ConcurrentQueue<RigEvent>();
        var hostLoop = new HostLoopDispatcher();
        var dispatcher = new CapturingDispatcher(hostLoop, ex => events.Enqueue(new RigEvent(RigEventKind.Exception, ex.ToString())));

        var module = new EmulatedSteerModule();
        truth?.Invoke(module);
        var wire = new ModuleWire(module);

        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.AddProvider(new CapturingLoggerProvider(e => events.Enqueue(e)));
            b.SetMinimumLevel(LogLevel.Debug);
        });
        AddBackendServices(services, wire);
        services.AddSingleton(hostLoop);
        services.AddSingleton<IUiDispatcher>(dispatcher);
        services.AddSingleton<IUiTimerFactory>(dispatcher);
        var sp = services.BuildServiceProvider();

        sp.GetRequiredService<ISettingsService>().Load();
        var configService = sp.GetRequiredService<IConfigurationService>();
        configService.LoadAppSettings();
        sp.GetRequiredService<IPersistentStateService>().Load();
        WebBackend.WireCrossServices(sp);
        MainViewModel vm = null!;
        await hostLoop.InvokeAsync(() => vm = sp.GetRequiredService<MainViewModel>());

        var server = new RemoteServerHost();
        await server.StartAsync(
            sp.GetRequiredService<ApplicationState>(),
            sp.GetRequiredService<ICoverageMapService>(),
            sp.GetRequiredService<ISectionControlService>(),
            sp.GetRequiredService<IToolPositionService>(),
            sp.GetRequiredService<ConfigurationStore>(),
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

        var rig = new SteerWizardRig(dataRoot, sp, vm, server, hostLoop, module, wire);
        while (events.TryDequeue(out var e)) rig._events.Enqueue(e);
        dispatcher.OnException = ex => rig._events.Enqueue(new RigEvent(RigEventKind.Exception, ex.ToString()));
        CapturingLoggerProvider.Sink = e => rig._events.Enqueue(e);
        vm.FailureReported += msg => rig._events.Enqueue(new RigEvent(RigEventKind.Failure, msg));
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.StatusMessage))
                rig._events.Enqueue(new RigEvent(RigEventKind.Status, vm.StatusMessage ?? ""));
        };
        await hostLoop.InvokeAsync(() => vm.IsSimulatorEnabled = false);

        module.Start();
        rig._gpsTask = Task.Run(() => rig.GpsLoopAsync(rig._cts.Token));
        // Let the module be heard, the settings go out once and the first fixes make a plane.
        await Task.Delay(1200);
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

    /// <summary>The Desktop head's registrations with <see cref="ModuleWire"/> for the network.</summary>
    private static void AddBackendServices(IServiceCollection services, ModuleWire wire)
    {
        services.AddSingleton<ApplicationState>();
        services.AddSingleton(_ => ConfigurationStore.Instance);
        services.AddSingleton(_ => PersistentAppState.Instance);
        services.AddTransient<MainViewModel>();
        services.AddTransient<ConfigurationViewModel>();
        // Loopback only: with the machine's real interfaces the UDP service broadcasts steer and
        // machine settings to the LAN, where a real module saves them to its EEPROM.
        services.AddSingleton<ILocalNetworkInfoProvider, NoNetworkInterfaces>();
        services.AddSingleton<AgOpenWeb.Services.Network.INetworkWatcher, AgOpenWeb.Services.Network.NetworkWatcher>();
        services.AddSingleton<IUdpCommunicationService>(wire);
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

    // ------------------------------------------------------------ commands, as the browser

    /// <summary>One web-client command, as the browser holding control sends it, then wait for
    /// the host loop to run it.</summary>
    public async Task SendAsync(string cmd, string arg = "")
    {
        var handler = Server.CommandHandler ?? throw new InvalidOperationException("command handler not wired");
        handler(cmd, arg);
        await DrainAsync();
        await Task.Delay(60);
        await DrainAsync();
    }

    /// <summary>The browser that held control dropped off (Wi-Fi gone, tab closed, tablet
    /// asleep): what <see cref="ControlAuthority.Drop"/> raises, on the socket's thread.</summary>
    public async Task DropControlAsync(string reason = "client disconnected")
    {
        await Task.Run(() =>
        {
            Server.FailsafeHandler?.Invoke(reason);
            Server.AuthorityChangedHandler?.Invoke(false, "");
        });
        await Task.Delay(100);
        await DrainAsync();
    }

    /// <summary>A browser takes the free seat again (what a reload does after a drop).</summary>
    public async Task TakeControlAsync()
    {
        await Task.Run(() => Server.AuthorityChangedHandler?.Invoke(true, "Tablet"));
        await DrainAsync();
    }

    /// <summary>The Wizard frame the browser gets (null when no wizard is projected).</summary>
    public async Task<WizardDto?> FrameAsync()
    {
        WizardDto? dto = null;
        await _hostLoop.InvokeAsync(() => dto = Server.WizardProvider?.Invoke());
        return dto;
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

    public List<RigEvent> TakeEvents()
    {
        var list = new List<RigEvent>();
        while (_events.TryDequeue(out var e)) list.Add(e);
        return list;
    }

    public void Note(string text) => _events.Enqueue(new RigEvent(RigEventKind.Note, text));

    // ------------------------------------------------------------ the tractor moving

    /// <summary>Heading of the tractor, degrees from north.</summary>
    public double HeadingDeg { get { lock (_poseLock) return _headingRad * 180 / Math.PI; } }
    /// <summary>Rear-axle position, metres from the made-up origin.</summary>
    public (double E, double N) RearAxle { get { lock (_poseLock) return (_rearE, _rearN); } }
    /// <summary>Total heading change since <see cref="ResetTurnCount"/>, degrees.</summary>
    public double TurnedDeg { get { lock (_poseLock) return _turnedRad * 180 / Math.PI; } }
    private double _turnedRad;
    public void ResetTurnCount() { lock (_poseLock) _turnedRad = 0; }

    private async Task GpsLoopAsync(CancellationToken ct)
    {
        const double dt = 0.1;
        var gps = Services.GetRequiredService<IGpsService>();
        while (!ct.IsCancellationRequested)
        {
            double e, n, h, v;
            lock (_poseLock)
            {
                v = SpeedKmh / 3.6;
                double wheel = Module.WheelAngleDeg * Math.PI / 180;
                double yawRate = v * Math.Tan(wheel) / TruthWheelbase;
                _headingRad += yawRate * dt;
                _turnedRad += yawRate * dt;
                _rearE += v * Math.Sin(_headingRad) * dt;
                _rearN += v * Math.Cos(_headingRad) * dt;
                e = _rearE + TruthAntennaAheadOfRearAxle * Math.Sin(_headingRad);
                n = _rearN + TruthAntennaAheadOfRearAxle * Math.Cos(_headingRad);
                h = (_headingRad * 180 / Math.PI) % 360;
                if (h < 0) h += 360;
            }
            if (GpsOn)
            {
                var (lat, lon) = Offset(Origin, e, n);
                gps.UpdateGpsData(new GpsData
                {
                    CurrentPosition = new Position { Latitude = lat, Longitude = lon, Altitude = 300, Heading = h, Speed = Math.Abs(v) },
                    FixQuality = FixQuality,
                    SatellitesInUse = 20,
                    Hdop = 0.7,
                    DifferentialAge = 1.0,
                    HasDualHeading = true,
                    ImuHeading = h,
                    ImuValid = true,
                    SentenceType = GpsSentenceType.Simulator,
                    Timestamp = AgOpenWeb.Models.Timing.Clock.Current.Now,
                });
            }
            try { await Task.Delay(100, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private static (double Lat, double Lon) Offset((double Lat, double Lon) a, double eastM, double northM)
    {
        const double R = 6371000.0;
        return (a.Lat + northM / R * 180 / Math.PI,
                a.Lon + eastM / (R * Math.Cos(a.Lat * Math.PI / 180)) * 180 / Math.PI);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { if (_gpsTask != null) await _gpsTask; } catch { }
        Module.Dispose();
        try { await Server.StopAsync(); } catch { }
        if (Services is IDisposable d) { try { d.Dispose(); } catch { } }
        Wire.Dispose();
        _hostLoop.Dispose();
    }

    private sealed class NoImagery : IBoundaryImageryCapture
    {
        public Task<bool> TryCaptureAsync(double a, double b, double c, double d, string outPath) => Task.FromResult(false);
    }
}

public enum RigEventKind { Exception, Error, Warning, Failure, Status, Hang, Note }

public sealed record RigEvent(RigEventKind Kind, string Text)
{
    public override string ToString() => $"{Kind}: {Text}";
}

/// <summary>The host loop, reporting an exception from any work item before the loop swallows it.</summary>
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
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Warning) return;
            var text = category + ": " + formatter(state, exception) + (exception is null ? "" : " | " + exception.GetType().Name + ": " + exception.Message);
            Sink?.Invoke(new RigEvent(logLevel >= LogLevel.Error ? RigEventKind.Error : RigEventKind.Warning, text));
        }
    }
}

/// <summary>A machine with no network but the loopback: the UDP service's discovery sends go
/// to 127.0.0.1:8888 only (it always adds that one), and the module scan and subnet commands,
/// sent once per interface, go nowhere. Nothing reaches a real module on the LAN.</summary>
internal sealed class NoNetworkInterfaces : ILocalNetworkInfoProvider
{
    public IReadOnlyList<LocalNetworkAddress> GetIPv4Addresses() => Array.Empty<LocalNetworkAddress>();
}
