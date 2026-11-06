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

namespace AgOpenWeb.VehicleToolScenarios.Tests;

/// <summary>
/// The whole AgOpenWeb backend in this process: every service real (the Desktop head's
/// registrations), a throwaway data folder, the embedded web server on a free port, and the
/// command handler wired exactly as on the tractor. The harness sends the web client's own
/// "id|arg" commands to that handler, so a scenario goes through the same parsing, the same
/// MainViewModel code and the same files as a browser would. Nothing is mocked.
///
/// What the rig adds for testing: a dispatcher that records exceptions thrown by any work
/// item on the host loop (the real loop only writes them to Debug output), a logger that
/// keeps warnings and errors, the failure toasts and status messages, and a GPS feed that
/// puts the tractor anywhere (lat/lon, heading, speed) as a receiver would.
/// </summary>
public sealed class VehicleToolRig : IAsyncDisposable
{
    public string DataRoot { get; }
    public string FieldsDir => Settings.Settings.FieldsDirectory;
    public string ImportDir => Path.Combine(DataRoot, "AgOpenWeb", "Import");
    public IServiceProvider Services { get; }
    public MainViewModel Vm { get; }
    public AgOpenWeb.RemoteServer.RemoteServerHost Server { get; }
    public ISettingsService Settings { get; }
    public ITramLineService Tram { get; }
    public IGpsService Gps { get; }
    public ApplicationState State { get; }
    public IAutoSteerService AutoSteer { get; }
    public GpsPipelineService Pipeline { get; }
    public AgOpenWeb.Models.Configuration.ConfigurationStore Store { get; }
    public IConfigurationService Config { get; }

    /// <summary>Everything the operator would notice or a developer would want to see,
    /// in order. Drained by <see cref="TakeEvents"/> after each step.</summary>
    private readonly ConcurrentQueue<RigEvent> _events = new();

    private readonly CapturingDispatcher _dispatcher;
    private readonly HostLoopDispatcher _hostLoop;

    private VehicleToolRig(string dataRoot, IServiceProvider sp, MainViewModel vm,
        AgOpenWeb.RemoteServer.RemoteServerHost server, CapturingDispatcher dispatcher, HostLoopDispatcher hostLoop)
    {
        DataRoot = dataRoot;
        Services = sp;
        Vm = vm;
        Server = server;
        _dispatcher = dispatcher;
        _hostLoop = hostLoop;
        Settings = sp.GetRequiredService<ISettingsService>();
        Tram = sp.GetRequiredService<ITramLineService>();
        Gps = sp.GetRequiredService<IGpsService>();
        State = sp.GetRequiredService<ApplicationState>();
        AutoSteer = sp.GetRequiredService<IAutoSteerService>();
        Pipeline = (GpsPipelineService)sp.GetRequiredService<IGpsPipelineService>();
        Store = sp.GetRequiredService<AgOpenWeb.Models.Configuration.ConfigurationStore>();
        Config = sp.GetRequiredService<IConfigurationService>();
    }

    public static async Task<VehicleToolRig> StartAsync(string dataRoot)
    {
        Directory.CreateDirectory(dataRoot);
        Environment.SetEnvironmentVariable(AppDataRoot.EnvVar, dataRoot);
        // Each rig starts from the settings in its own data folder, as an app start does: a
        // fresh store, not the one the previous scenario left behind.
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

        // The sequence of WebBackend.StartAsync, with a free port instead of 5174 so a running
        // AgOpenWeb on the same machine is left alone.
        sp.GetRequiredService<ISettingsService>().Load();
        var configService = sp.GetRequiredService<IConfigurationService>();
        configService.LoadAppSettings();
        sp.GetRequiredService<IPersistentStateService>().Load();
        WebBackend.WireCrossServices(sp);
        var vm = await OnLoop(hostLoop, () => sp.GetRequiredService<MainViewModel>());

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

        var rig = new VehicleToolRig(dataRoot, sp, vm, server, dispatcher, hostLoop);
        while (events.TryDequeue(out var e)) rig._events.Enqueue(e);
        dispatcher.OnException = ex => rig._events.Enqueue(new RigEvent(RigEventKind.Exception, ex.ToString()));
        CapturingLoggerProvider.Sink = e => rig._events.Enqueue(e);
        vm.FailureReported += msg => rig._events.Enqueue(new RigEvent(RigEventKind.Failure, msg));
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.StatusMessage))
                rig._events.Enqueue(new RigEvent(RigEventKind.Status, vm.StatusMessage ?? ""));
            // The VM belongs to the host loop (Docs/ARCHITECTURE.md, threading model): a
            // change raised from any other thread races with commands and the GPS cycle.
            if (!hostLoop.CheckAccess() && e.PropertyName is { } prop && rig._offLoopSeen.Add(prop))
                rig._events.Enqueue(new RigEvent(RigEventKind.OffLoop,
                    prop + " changed on thread " + Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture)
                    + " (not the host loop) at " + CallerSummary()));
        };

        // The built-in GPS simulator would fight the harness's own fixes: switch it off,
        // as a tractor with a real receiver has it.
        await hostLoop.InvokeAsync(() => vm.IsSimulatorEnabled = false);
        // Each fix runs its GPS cycle inline (the test mode of the pipeline), so the steer
        // angle for a fix is known before the simulated tractor moves on.
        rig.Pipeline.SynchronousMode = true;
        rig.Pipeline.CycleCompleted += r => { Volatile.Write(ref rig._lastCycle, r); Interlocked.Increment(ref rig._cycleCount); };
        await rig.DrainAsync();
        rig.TakeEvents();
        return rig;
    }

    private readonly HashSet<string> _offLoopSeen = new();

    /// <summary>The first few AgOpenWeb frames of the current stack (who changed the VM).</summary>
    private static string CallerSummary()
    {
        var st = new System.Diagnostics.StackTrace(2, false);
        var names = st.GetFrames()
            .Select(f => f.GetMethod())
            .Where(m => m?.DeclaringType?.FullName is { } n && n.StartsWith("AgOpenWeb.", StringComparison.Ordinal)
                        && !n.StartsWith("AgOpenWeb.VehicleToolScenarios", StringComparison.Ordinal)
                        && !n.Contains("ObservableObject", StringComparison.Ordinal))
            .Select(m => m!.DeclaringType!.Name.Split('+')[0].Split('<')[0] + "." + m.Name)
            .Distinct()
            .Take(3);
        return string.Join(" < ", names);
    }

    /// <summary>Wait until <paramref name="done"/> holds on the host loop (field opens, imports
    /// and closes finish on their own after the command returns).</summary>
    public async Task<bool> WaitUntilAsync(Func<bool> done, int timeoutMs = 10000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            await DrainAsync(1);
            if (await ReadAsync(done)) return true;
            await Task.Delay(20);
        }
        return false;
    }

    /// <summary>Let async work started by the last command finish: wait while the busy
    /// overlay is up, then a short quiet period.</summary>
    public async Task SettleAsync()
    {
        await Task.Delay(80);
        await WaitUntilAsync(() => !State.UI.IsBusy, 15000);
        await DrainAsync();
    }

    private static async Task<T> OnLoop<T>(HostLoopDispatcher loop, Func<T> f)
    {
        T result = default!;
        await loop.InvokeAsync(() => result = f());
        return result;
    }

    /// <summary>The Desktop head's registrations (Platforms/AgOpenWeb.Desktop/DependencyInjection),
    /// minus what needs that head: battery reader (null one) and the ISOBUS VT stack (absent, as
    /// on a head without it).</summary>
    private static void AddBackendServices(IServiceCollection services)
    {
        services.AddSingleton<ApplicationState>();
        services.AddSingleton(_ => AgOpenWeb.Models.Configuration.ConfigurationStore.Instance);
        services.AddSingleton(_ => PersistentAppState.Instance);
        services.AddTransient<MainViewModel>();
        services.AddTransient<ConfigurationViewModel>();
        services.AddSingleton<ILocalNetworkInfoProvider, SystemLocalNetworkInfoProvider>();
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

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    // ------------------------------------------------------------------ commands

    /// <summary>Send one web-client command, exactly as a browser holding control does, and
    /// wait until the host loop has run it (and whatever it posted behind it).</summary>
    public async Task SendAsync(string cmd, string arg = "")
    {
        var handler = Server.CommandHandler ?? throw new InvalidOperationException("command handler not wired");
        handler(cmd, arg);
        await DrainAsync();
        await SettleAsync();
    }

    /// <summary>Wait for the host loop to run everything queued so far, twice (a command
    /// often posts follow-up work), with a timeout that reports a hung loop.</summary>
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

    /// <summary>Read VM state on the host loop (where the VM lives).</summary>
    public async Task<T> ReadAsync<T>(Func<T> read)
    {
        T value = default!;
        await _hostLoop.InvokeAsync(() => value = read());
        return value;
    }

    public async Task RunAsync(Action act) => await _hostLoop.InvokeAsync(act);

    public List<RigEvent> TakeEvents()
    {
        var list = new List<RigEvent>();
        while (_events.TryDequeue(out var e)) list.Add(e);
        return list;
    }

    /// <summary>Answer the host's pending confirmation (yes/no), as the browser holding control does.</summary>
    public async Task<string?> AnswerPromptAsync(bool yes)
    {
        var (pending, seq, title) = await ReadAsync(() => (Vm.IsPromptPending, Vm.PromptSeq,
            (Vm.ConfirmationDialogTitle ?? Vm.ErrorDialogTitle ?? "") + ": " + (Vm.ConfirmationDialogMessage ?? Vm.ErrorDialogMessage ?? "")));
        if (!pending) return null;
        await SendAsync("prompt.answer", string.Create(CultureInfo.InvariantCulture, $"{seq},{(yes ? 1 : 0)},0"));
        return title;
    }

    // ------------------------------------------------------------------ GPS

    /// <summary>The cycle results of the GPS pipeline, newest last (the pipeline runs each fix
    /// inline on the sender's thread, so a fix's result is here when <see cref="SendSentence"/>
    /// returns).</summary>
    public GpsCycleResult? LastCycle => Volatile.Read(ref _lastCycle);
    private GpsCycleResult? _lastCycle;
    public int CycleCount => Volatile.Read(ref _cycleCount);
    private int _cycleCount;

    /// <summary>One NMEA sentence as the GPS module sends it to port 9999: it goes through the
    /// real parser (roll invert and zero, dual heading flag) and the whole GPS cycle.</summary>
    public void SendSentence(string sentence)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(sentence);
        AutoSteer.ProcessGpsDatagram(bytes, AgOpenWeb.Models.GPS.GpsSource.ModulePort);
    }

    /// <summary>A $PAOGI (dual antenna) or $PANDA (single antenna + IMU) sentence for a fix.</summary>
    public static string Sentence(bool dual, bool imu, double lat, double lon, double headingDeg, double rollDeg,
        double speedKmh, int fix = 4, double altitude = 300)
    {
        var inv = CultureInfo.InvariantCulture;
        string Ll(double v, bool isLat)
        {
            double a = Math.Abs(v);
            int deg = (int)a;
            double min = (a - deg) * 60.0;
            return isLat ? deg.ToString("00", inv) + min.ToString("00.0000000", inv)
                         : deg.ToString("000", inv) + min.ToString("00.0000000", inv);
        }
        double knots = speedKmh / 1.852;
        double h = ((headingDeg % 360) + 360) % 360;
        string body = dual
            ? string.Join(",", "PAOGI", "120000.00", Ll(lat, true), lat >= 0 ? "N" : "S", Ll(lon, false), lon >= 0 ? "E" : "W",
                fix.ToString(inv), "20", "0.7", altitude.ToString("F2", inv), "1.0", knots.ToString("F3", inv),
                h.ToString("F3", inv), rollDeg.ToString("F3", inv), "0.00", "0.00")
            : string.Join(",", "PANDA", "120000.00", Ll(lat, true), lat >= 0 ? "N" : "S", Ll(lon, false), lon >= 0 ? "E" : "W",
                fix.ToString(inv), "20", "0.7", altitude.ToString("F2", inv), "1.0", knots.ToString("F3", inv),
                imu ? ((int)Math.Round(h * 10)).ToString(inv) : "65535", imu ? ((int)Math.Round(rollDeg * 10)).ToString(inv) : "0", "0", "0");
        byte cs = 0;
        foreach (char c in body) cs ^= (byte)c;
        return "$" + body + "*" + cs.ToString("X2", inv) + "\r\n";
    }

    /// <summary>Wait until the VM has applied the last cycle (it posts each result to the host loop).</summary>
    public async Task WaitForVmAsync() => await DrainAsync(1);

    // ------------------------------------------------------------------ helpers

    /// <summary>Field-local E/N of a lat/lon in the open field's plane (what the web client's
    /// s2w gives for a tap on the map).</summary>
    public async Task<(double E, double N)> ToFieldAsync(double lat, double lon)
    {
        var plane = await ReadAsync(() => State.Field.LocalPlane);
        if (plane == null)
        {
            var (olat, olon) = await ReadAsync(() => (State.Field.OriginLatitude, State.Field.OriginLongitude));
            plane = new LocalPlane(new Wgs84(olat, olon), new SharedFieldProperties());
        }
        var g = plane.ConvertWgs84ToGeoCoord(new Wgs84(lat, lon));
        return (g.Easting, g.Northing);
    }

    public async Task<(double Lat, double Lon)> ToLatLonAsync(double e, double n)
    {
        var (olat, olon) = await ReadAsync(() => (State.Field.OriginLatitude, State.Field.OriginLongitude));
        var plane = await ReadAsync(() => State.Field.LocalPlane) ?? new LocalPlane(new Wgs84(olat, olon), new SharedFieldProperties());
        var w = plane.ConvertGeoCoordToWgs84(new GeoCoord(n, e));
        return (w.Latitude, w.Longitude);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await ReadAsync(() => { Vm.SaveOpenFieldForExit(TimeSpan.FromSeconds(5)); return 0; });
        }
        catch { /* the run is over; a save fault was already logged */ }
        try { await Server.StopAsync(); } catch { }
        if (Services is IDisposable d) { try { d.Dispose(); } catch { } }
        _hostLoop.Dispose();
    }

    /// <summary>Imagery capture needs the internet; the harness has none (the host then
    /// shows "No aerial imagery", as on a tractor without signal).</summary>
    private sealed class NoImagery : IBoundaryImageryCapture
    {
        public Task<bool> TryCaptureAsync(double a, double b, double c, double d, string outPath) => Task.FromResult(false);
    }
}

public enum RigEventKind { Exception, Error, Warning, Failure, Status, Hang, Note, OffLoop }

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
    private static readonly object Sync = new();

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
            if (exception != null && logLevel >= LogLevel.Error)
            {
                // The whole stack, for confirming a finding (the report keeps one line).
                var dump = Environment.GetEnvironmentVariable("AGOPENWEB_SCENARIO_ERRORS");
                if (!string.IsNullOrEmpty(dump))
                    lock (Sync) File.AppendAllText(dump, DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + " " + category + "\n" + exception + "\n\n");
            }
            Sink?.Invoke(new RigEvent(logLevel >= LogLevel.Error ? RigEventKind.Error : RigEventKind.Warning, text));
        }
    }
}
