using System.IO;
using System.Linq;
using System.Reflection;
using AgValoniaGPS.Models;
using AgValoniaGPS.Models.State;
using AgValoniaGPS.Services;
using AgValoniaGPS.Services.Interfaces;
using NSubstitute;

namespace AgValoniaGPS.Services.Tests;

/// <summary>
/// Tier-2 persistent application state (appstate.json) round-trips correctly and
/// migrates once from a legacy appsettings.json. The completeness test mirrors
/// the config-side guard: every PersistentAppState property must round-trip
/// through the service, so a new state property can't silently fail to persist.
/// </summary>
[TestFixture]
[NonParallelizable] // Modifies PersistentAppState.Instance
public class PersistentStateServiceTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "agv-state-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dir);
        PersistentAppState.SetInstance(new PersistentAppState());
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private PersistentStateService NewService(PersistentAppState state) =>
        new PersistentStateService(state, _dir);

    [Test]
    public void SaveAndLoad_RoundTrip_PreservesAllValues()
    {
        var save = new PersistentAppState
        {
            WindowWidth = 1600, WindowHeight = 900, WindowX = 200, WindowY = 150,
            WindowMaximized = true,
            SimulatorPanelX = 500, SimulatorPanelY = 300, SimulatorPanelVisible = true,
            CameraZoom = 75.5, CameraPitch = -45, CameraMode = CameraMode.NorthUp,
            IsDayMode = false, Is2DMode = true, IsNorthUp = true,
            SimulatorLatitude = 48.8566, SimulatorLongitude = 2.3522,
            SimulatorSpeed = 12.0, SimulatorSteerAngle = -5.0,
            LastOpenedField = "North40",
            BoundaryDrawRightSide = false, BoundaryDrawAtPivot = true, BoundaryOffset = 250,
        };
        NewService(save).Save();

        var loaded = new PersistentAppState();
        var ok = NewService(loaded).Load();

        Assert.That(ok, Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(loaded.WindowWidth, Is.EqualTo(1600));
            Assert.That(loaded.WindowHeight, Is.EqualTo(900));
            Assert.That(loaded.WindowX, Is.EqualTo(200));
            Assert.That(loaded.WindowY, Is.EqualTo(150));
            Assert.That(loaded.WindowMaximized, Is.True);
            Assert.That(loaded.SimulatorPanelX, Is.EqualTo(500));
            Assert.That(loaded.SimulatorPanelY, Is.EqualTo(300));
            Assert.That(loaded.SimulatorPanelVisible, Is.True);
            Assert.That(loaded.CameraZoom, Is.EqualTo(75.5));
            Assert.That(loaded.CameraPitch, Is.EqualTo(-45));
            Assert.That(loaded.CameraMode, Is.EqualTo(CameraMode.NorthUp));
            Assert.That(loaded.IsDayMode, Is.False);
            Assert.That(loaded.Is2DMode, Is.True);
            Assert.That(loaded.IsNorthUp, Is.True);
            Assert.That(loaded.SimulatorLatitude, Is.EqualTo(48.8566));
            Assert.That(loaded.SimulatorLongitude, Is.EqualTo(2.3522));
            Assert.That(loaded.SimulatorSpeed, Is.EqualTo(12.0));
            Assert.That(loaded.SimulatorSteerAngle, Is.EqualTo(-5.0));
            Assert.That(loaded.LastOpenedField, Is.EqualTo("North40"));
            Assert.That(loaded.BoundaryDrawRightSide, Is.False);
            Assert.That(loaded.BoundaryDrawAtPivot, Is.True);
            Assert.That(loaded.BoundaryOffset, Is.EqualTo(250));
        });
    }

    [Test]
    public void Load_MissingStateFile_ReportsFirstRun()
    {
        var state = new PersistentAppState();
        var loaded = NewService(state).Load();
        Assert.That(loaded, Is.False);
        Assert.That(state.IsFirstRun, Is.True);
    }

    [Test]
    public void Load_NoStateButLegacyAppSettings_MigratesAndIsNotFirstRun()
    {
        // Write a legacy appsettings.json carrying the old state-typed fields.
        File.WriteAllText(Path.Combine(_dir, "appsettings.json"),
            "{\"windowWidth\":1440,\"windowHeight\":810,\"cameraPitch\":-50," +
            "\"simulatorLatitude\":51.5,\"simulatorLongitude\":-0.12,\"lastOpenedField\":\"Legacy\"}");

        var state = new PersistentAppState();
        var loaded = NewService(state).Load();

        Assert.That(loaded, Is.False);          // no appstate.json yet
        Assert.That(state.IsFirstRun, Is.False); // but a legacy file existed → upgrade, not first run
        Assert.Multiple(() =>
        {
            Assert.That(state.WindowWidth, Is.EqualTo(1440));
            Assert.That(state.WindowHeight, Is.EqualTo(810));
            Assert.That(state.CameraPitch, Is.EqualTo(-50));
            Assert.That(state.SimulatorLatitude, Is.EqualTo(51.5));
            Assert.That(state.SimulatorLongitude, Is.EqualTo(-0.12));
            Assert.That(state.LastOpenedField, Is.EqualTo("Legacy"));
        });
    }

    /// <summary>
    /// Every persistable PersistentAppState property must be copied by the
    /// service's load path (ApplySnapshot). IsFirstRun/LastRunDate are recomputed
    /// by Load, not copied. A new state property that isn't applied fails here.
    /// </summary>
    [Test]
    public void Completeness_EveryStateProperty_RoundTrips()
    {
        var recomputedByLoad = new System.Collections.Generic.HashSet<string>
        {
            nameof(PersistentAppState.IsFirstRun),
            nameof(PersistentAppState.LastRunDate),
        };

        // Persist a snapshot with every value mutated away from defaults.
        var save = new PersistentAppState
        {
            WindowWidth = 1, WindowHeight = 2, WindowX = 3, WindowY = 4, WindowMaximized = true,
            SimulatorPanelX = 5, SimulatorPanelY = 6, SimulatorPanelVisible = true,
            CameraZoom = 7, CameraPitch = -33, CameraMode = CameraMode.HeadingUp,
            IsDayMode = false, Is2DMode = true, IsNorthUp = true,
            SimulatorLatitude = 12.5, SimulatorLongitude = 13.5, SimulatorSpeed = 9, SimulatorSteerAngle = 8,
            LastOpenedField = "X", BoundaryDrawRightSide = false, BoundaryDrawAtPivot = true, BoundaryOffset = 42,
        };
        NewService(save).Save();

        var loaded = new PersistentAppState();
        NewService(loaded).Load();

        var defaults = new PersistentAppState();
        var notApplied = typeof(PersistentAppState)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && !recomputedByLoad.Contains(p.Name))
            .Where(p => Equals(p.GetValue(loaded), p.GetValue(defaults)))
            .Select(p => p.Name)
            .ToList();

        Assert.That(notApplied, Is.Empty,
            "PersistentAppState properties not carried through PersistentStateService " +
            $"(add them to ApplySnapshot): {string.Join(", ", notApplied)}");
    }
}
