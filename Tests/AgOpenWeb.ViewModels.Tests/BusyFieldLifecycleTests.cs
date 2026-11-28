// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Guidance;
using AgOpenWeb.Models.Timing;
using AgOpenWeb.Models.Track;
using AgOpenWeb.RemoteWiring;
using AgOpenWeb.Services;
using AgOpenWeb.Services.GeoJson;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// A busy field closed, on the real services: what it leaves behind, and what of it shows in the
/// next field, held in <c>TestData/BusyFieldLifecycle.txt</c>. The refactor in
/// <c>Plans/MAINVIEWMODEL_REFACTOR_PLAN.md</c> (item 6) moves the open and close steps into a field
/// session; the same things must be left behind after it, no more and no fewer.
///
/// <see cref="FieldLifecycleCharacterizationTests"/> opens a field on stand-in services and places
/// flags. This one builds the container every head builds (<see cref="BackendServices.AddBackendServices"/>)
/// and uses the field as an operator does, through the members the web commands call: a job
/// started, NTRIP switched to the field's own profile, a headland, a tram system, the background
/// imagery, contour on, AutoSteer engaged, the sections on (one by hand), and a path and a boundary
/// being recorded while it drives, fix by fix through the real GPS cycle and control loop on a clock
/// the test moves. Then the field is closed with the close command, and every public property of
/// the view model, its state and the services that hold field state is read, as the other test
/// reads the view model (<see cref="VmStateReader"/>): nothing is listed by hand.
///
/// The six close-field bugs this plan starts from (#286, #309, #310, #625, #749, #956) each left
/// something of the field behind; with any of their fixes undone this test fails.
///
/// A change that is meant makes the file again:
/// <c>AGOPENWEB_UPDATE_FIELD_LIFECYCLE=1 dotnet test --filter BusyFieldLifecycleTests</c>.
/// </summary>
[TestFixture]
[NonParallelizable] // the backend's state lease sets the statics; Clock is process-wide
public class BusyFieldLifecycleTests
{
    private const string FieldA = "Busy A", FieldB = "Busy B";
    private const double LatA = 47.4127, LonA = 8.5511, LatB = 47.4301, LonB = 8.5702;
    // A 1x1 PNG: the imagery a capture leaves for the field.
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==";

    private string _dataRoot = null!;
    private string? _dataRootBefore;
    private TestClock _clock = null!;

    [SetUp]
    public void SetUp()
    {
        _dataRootBefore = Environment.GetEnvironmentVariable(AppDataRoot.EnvVar);
        _clock = new TestClock(new DateTime(2026, 11, 27, 9, 0, 0));
        Clock.Set(_clock);
    }

    [TearDown]
    public void TearDown()
    {
        Clock.Reset();
        Environment.SetEnvironmentVariable(AppDataRoot.EnvVar, _dataRootBefore);
        ConfigurationStore.SetInstance(new ConfigurationStore());
    }

    [Test]
    public async Task What_a_busy_field_leaves_behind_is_what_it_left_before_the_refactor()
    {
        var first = Read(await Backend.StartAsync());
        var noise = Differences(first, Read(await Backend.StartAsync())).ToList();

        // Field A, used as an operator does, then closed.
        var used = await Backend.StartAsync();
        var before = Read(used);
        var busy = await UseFieldA(used);
        Assert.That(busy, Is.Empty, "a feature of the busy field was not in use when it was closed: " + string.Join("; ", busy));
        await used.CloseField();
        var leftBehind = Differences(before, Read(used)).Except(noise).ToList();

        // Field B opened after A, against B opened on a fresh backend. Each backend is read before
        // the next is built: building one sets the process-wide statics (ConfigurationStore.Instance).
        await used.Vm.OpenFieldAsync(used.FieldDir(FieldB), FieldB);
        var usedB = Read(used);
        var fresh = await Backend.StartAsync();
        await fresh.Vm.OpenFieldAsync(fresh.FieldDir(FieldB), FieldB);
        var freshB = Read(fresh);
        var fresh2 = await Backend.StartAsync();
        await fresh2.Vm.OpenFieldAsync(fresh2.FieldDir(FieldB), FieldB);
        var openNoise = Differences(freshB, Read(fresh2)).ToList();
        var showingThrough = Differences(freshB, usedB).Except(noise).Except(openNoise).ToList();

        var now = new List<string>
        {
            "# What a busy field leaves behind on close, and what of it shows when the next field is opened.",
            "# Made by BusyFieldLifecycleTests (AGOPENWEB_UPDATE_FIELD_LIFECYCLE=1); do not edit by hand.",
        };
        now.AddRange(leftBehind.OrderBy(s => s, StringComparer.Ordinal).Select(s => "left behind: " + s));
        now.AddRange(showingThrough.OrderBy(s => s, StringComparer.Ordinal).Select(s => "showing through: " + s));

        string file = RecordFile();
        if (Environment.GetEnvironmentVariable("AGOPENWEB_UPDATE_FIELD_LIFECYCLE") == "1")
        {
            File.WriteAllLines(file, now);
            Assert.Pass($"wrote {now.Count - 2} lines to {file}");
        }

        var expected = File.ReadAllLines(file).Where(l => !l.StartsWith('#')).ToList();
        var actual = now.Where(l => !l.StartsWith('#')).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(actual.Except(expected), Is.Empty, "now left behind or showing through, and not before");
            Assert.That(expected.Except(actual), Is.Empty, "left behind or showing through before, and not now");
        });
    }

    /// <summary>
    /// Field A used as on a working day. Returns what was not in use when it should have been
    /// (empty when the field is as busy as the test means it to be).
    /// </summary>
    private async Task<List<string>> UseFieldA(Backend b)
    {
        var vm = b.Vm;
        await vm.OpenFieldStartingNewJobAsync(b.FieldDir(FieldA), FieldA, "Spraying", "", "Busy"); // field.startJob

        vm.ActivateTrackAt(0);                                                            // track.select
        vm.RemoteCreateHeadlandWholeBoundary(12);                                         // headland.wholeBoundary
        vm.RemoteAddTramSystem();                                                         // tram.add
        string png = Path.Combine(b.DataRoot, "capture.png");
        File.WriteAllBytes(png, Convert.FromBase64String(Png));
        vm.ApplyCapturedBackground(vm.ActiveField, png, LatA + 0.001, LonA - 0.001, LatA - 0.001, LonA + 0.001,
            0, 1, 0, 1);                                                                  // boundary.captureImagery
        vm.PlaceFlagAtWorldPosition(10, 10, FlagColor.Red);                               // flag.place
        vm.ToggleContourModeCommand.Execute(null);                                        // contour.toggle

        // Drive north up the AB line: the sections in Auto, a path recorded and stopped (it is the
        // field's recorded path now), then section 1 on by hand (Auto -> On), AutoSteer on, and a
        // second path and a boundary being recorded while driving on into the headland.
        b.Drive(LatA, LonA, -60, -80, -40);
        vm.ToggleSectionMasterCommand.Execute(null);                                      // section.master
        vm.StartRecordedPathCommand.Execute(null);                                        // recpath.start
        b.Drive(LatA, LonA, -60, -40, 40);
        vm.StopRecordedPathCommand.Execute(null);                                         // recpath.stop
        vm.ToggleSectionCommand.Execute(0);                                               // section.toggle
        vm.ToggleAutoSteerCommand.Execute(null);                                          // autosteer.toggle
        vm.StartRecordedPathCommand.Execute(null);                                        // recpath.start
        vm.DriveAroundFieldCommand.Execute(null);                                         // boundary.driveAround
        if (!vm.IsBoundaryRecording) vm.ToggleRecordingCommand.Execute(null);             // boundary.toggleRecording
        b.Drive(LatA, LonA, -60, 40, 95);

        var notInUse = new List<string>();
        void Need(bool inUse, string what) { if (!inUse) notInUse.Add(what); }
        Need(vm.IsFieldOpen, "the field is not open");
        Need(b.Get<IJobService>().ActiveJob != null, "no job");
        Need(b.Ntrip.ConnectedTo == "A-RTK@field-a.example:2101", "NTRIP is not on the field's profile (" + b.Ntrip.ConnectedTo + ")");
        Need(vm.HasHeadland, "no headland");
        Need(b.Get<ITramLineService>().HasTramLines, "no tram lines");
        Need(vm.State.Field.Imagery != null, "no imagery");
        Need(vm.IsContourModeOn, "contour off");
        Need(b.Get<ICoverageMapService>().TotalWorkedArea > 0, "no coverage painted");
        Need(b.Get<ISectionControlService>().SectionStates[0].ButtonState.ToString() == "On"
            && b.Get<ISectionControlService>().IsAnySectionOn, "no section on by hand");
        Need(vm.IsAutoSteerEngaged, "AutoSteer not engaged");
        Need(vm.State.RecordedPath.RecordedPoints.Count > 0, "no recorded path");
        Need(vm.IsRecordingPath, "no path being recorded");
        Need(b.Get<IBoundaryRecordingService>().PointCount > 0, "no boundary being recorded");
        return notInUse;
    }

    private static Dictionary<string, string> Read(Backend b)
    {
        var all = VmStateReader.Read(b.Vm);
        // What the services hold of a field, read as the view model is.
        foreach (var (name, service) in new (string, object)[]
        {
            ("coverage", b.Get<ICoverageMapService>()),
            ("sections", b.Get<ISectionControlService>()),
            ("boundaryRecording", b.Get<IBoundaryRecordingService>()),
            ("tram", b.Get<ITramLineService>()),
            ("job", b.Get<IJobService>()),
            ("fields", b.Get<IFieldService>()),
        })
            VmStateReader.ReadService(service, name, all);
        all["ntrip.connectedTo"] = b.Ntrip.ConnectedTo ?? "none";
        return all;
    }

    private static IEnumerable<string> Differences(Dictionary<string, string> a, Dictionary<string, string> b) =>
        VmStateReader.Differences(a, b);

    private static string RecordFile([CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "TestData", "BusyFieldLifecycle.txt");

    /// <summary>
    /// A backend as the heads build it, in a data folder of its own, with its two fields and its
    /// NTRIP profiles: the view model's thread inline, its timers firing only when told, the GPS
    /// cycle run inline on each fix and the control loop ticked by the test (nothing opens a port
    /// or starts a thread). NTRIP is a stand-in that remembers which caster it was asked for.
    /// </summary>
    private sealed class Backend
    {
        public MainViewModel Vm = null!;
        public string DataRoot = null!;
        public FakeNtrip Ntrip = null!;
        private ServiceProvider _sp = null!;
        private IGpsService _gps = null!;
        private ISteerMachineLoopService _loop = null!;

        public T Get<T>() where T : notnull => _sp.GetRequiredService<T>();

        public string FieldDir(string name) => Path.Combine(Get<ISettingsService>().Settings.FieldsDirectory, name);

        public static async Task<Backend> StartAsync()
        {
            var b = new Backend { DataRoot = Path.Combine(Path.GetTempPath(), "aow-busy-" + Guid.NewGuid().ToString("N")) };
            Directory.CreateDirectory(b.DataRoot);
            Environment.SetEnvironmentVariable(AppDataRoot.EnvVar, b.DataRoot);
            b.Ntrip = new FakeNtrip();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddBackendServices();
            services.AddSingleton<ILocalNetworkInfoProvider, SystemLocalNetworkInfoProvider>();
            services.AddSingleton<AgOpenWeb.Services.Network.INetworkWatcher, AgOpenWeb.Services.Network.NetworkWatcher>();
            services.AddSingleton<IBatteryService, AgOpenWeb.Services.Battery.NullBatteryService>();
            var dispatcher = new AgOpenWeb.Services.Threading.InlineUiDispatcher();
            services.AddSingleton<IUiDispatcher>(dispatcher);
            services.AddSingleton<IUiTimerFactory>(new RecordingTimerFactory(new List<AgOpenWeb.Services.Threading.ManualUiTimer>()));
            services.AddSingleton<INtripClientService>(b.Ntrip.Client);
            b._sp = services.BuildServiceProvider();

            b.Get<ISettingsService>().Load();
            b.Get<IConfigurationService>().LoadAppSettings();
            b.Get<IPersistentStateService>().Load();
            var store = b.Get<ConfigurationStore>();
            store.Connections.NtripEnabled = true;
            store.Connections.NtripProfilePerField = true;
            WebBackend.WireCrossServices(b._sp);
            b.WriteFields();
            await b.WriteNtripProfilesAsync();

            if (b.Get<IGpsPipelineService>() is GpsPipelineService pipeline) pipeline.SynchronousMode = true;
            b._gps = b.Get<IGpsService>();
            b._loop = b.Get<ISteerMachineLoopService>();
            b.Get<IBatteryService>().Start();
            b.Get<IAutoSteerService>().Start();
            b.Get<IGpsPipelineService>().Start();
            b.Vm = b.Get<MainViewModel>();
            b.Vm.Start(Task.CompletedTask);
            return b;
        }

        public async Task CloseField()
        {
            if (Vm.CloseFieldCommand is CommunityToolkit.Mvvm.Input.IAsyncRelayCommand close) await close.ExecuteAsync(null); // field.close
            else Vm.CloseFieldCommand.Execute(null);
        }

        /// <summary>Fixes at 10 Hz northward at 10 km/h along easting <paramref name="e"/>, the control
        /// loop ticked ten times between them, as on the tractor.</summary>
        public void Drive(double lat0, double lon0, double e, double fromN, double toN)
        {
            const double speed = 10 / 3.6, fixSeconds = 0.1;
            var plane = new LocalPlane(new Wgs84(lat0, lon0), new SharedFieldProperties());
            for (double n = fromN; n < toN; n += speed * fixSeconds)
            {
                var ll = plane.ConvertGeoCoordToWgs84(new GeoCoord(n, e));
                _gps.UpdateGpsData(new GpsData
                {
                    CurrentPosition = new Position { Latitude = ll.Latitude, Longitude = ll.Longitude, Heading = 0, Speed = speed },
                    FixQuality = 4, SatellitesInUse = 14, Hdop = 0.7, IsValid = true,
                });
                for (int t = 0; t < 10; t++)
                {
                    _clock().AdvanceMs(10);
                    _loop.Tick(Clock.Current.GetTimestamp());
                }
            }
        }

        private static Func<TestClock> _clock = () => (TestClock)Clock.Current;

        private void WriteFields()
        {
            string dirA = FieldDir(FieldA), dirB = FieldDir(FieldB);
            Directory.CreateDirectory(dirA);
            Directory.CreateDirectory(dirB);
            GeoJsonFieldService.Save(new Field
            {
                Name = FieldA, DirectoryPath = dirA, Origin = new Position { Latitude = LatA, Longitude = LonA },
                Boundary = new Boundary { OuterBoundary = Square(-100, -100, 200) },
            }, new[] { Track.FromABLine("AB A", new Vec3(-60, -100, 0), new Vec3(-60, 100, 0)) });
            GeoJsonFieldService.Save(new Field
            {
                Name = FieldB, DirectoryPath = dirB, Origin = new Position { Latitude = LatB, Longitude = LonB },
                Boundary = new Boundary { OuterBoundary = Square(-50, -80, 100) },
            }, new[] { Track.FromABLine("AB B", new Vec3(0, -80, 0), new Vec3(0, 80, 0)) });
        }

        private async Task WriteNtripProfilesAsync()
        {
            var profiles = Get<INtripProfileService>();
            var main = profiles.CreateNewProfile("Base");
            main.CasterHost = "base.example"; main.MountPoint = "BASE"; main.AutoConnectOnFieldLoad = true;
            await profiles.SaveProfileAsync(main);
            await profiles.SetDefaultProfileAsync(main.Id);
            var own = profiles.CreateNewProfile("Field A RTK");
            own.CasterHost = "field-a.example"; own.MountPoint = "A-RTK"; own.AutoConnectOnFieldLoad = true;
            own.AssociatedFields.Add(FieldA);
            await profiles.SaveProfileAsync(own);
        }

        private static BoundaryPolygon Square(double e0, double n0, double side)
        {
            var p = new BoundaryPolygon();
            foreach (var (e, n) in new[] { (e0, n0), (e0 + side, n0), (e0 + side, n0 + side), (e0, n0 + side) })
                p.Points.Add(new BoundaryPoint(e, n, 0));
            p.UpdateBounds();
            return p;
        }
    }

    /// <summary>An NTRIP client that connects to nothing and remembers which caster it was asked for.</summary>
    private sealed class FakeNtrip
    {
        public INtripClientService Client { get; } = Substitute.For<INtripClientService>();
        public string? ConnectedTo { get; private set; }

        public FakeNtrip()
        {
            Client.ConnectAsync(Arg.Any<NtripConfiguration>()).Returns(ci =>
            {
                var c = ci.Arg<NtripConfiguration>();
                ConnectedTo = c.MountPoint + "@" + c.CasterAddress + ":" + c.CasterPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
                Client.IsConnected.Returns(true);
                Client.IsActive.Returns(true);
                return Task.CompletedTask;
            });
            Client.DisconnectAsync().Returns(_ =>
            {
                ConnectedTo = null;
                Client.IsConnected.Returns(false);
                Client.IsActive.Returns(false);
                return Task.CompletedTask;
            });
        }
    }
}
