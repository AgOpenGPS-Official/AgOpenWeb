using AgValoniaGPS.Models;
using AgValoniaGPS.Models.State;

namespace AgValoniaGPS.Services.Tests;

/// <summary>
/// Tests that config (AppSettings) and persistent-state (PersistentAppState)
/// validation catches and clamps corrupt values. Window geometry and simulator
/// position moved to PersistentAppState; AppSettings keeps GPS-rate / NTRIP-port
/// range checks.
/// </summary>
[TestFixture]
public class SettingsValidationTests
{
    [Test]
    public void ValidateAndFix_ValidSettings_NoFixes()
    {
        var settings = new AppSettings();
        var fixes = settings.ValidateAndFix();
        Assert.That(fixes, Is.Empty);
    }

    [Test]
    public void ValidateAndFix_NegativeGpsRate_ResetsToDefault()
    {
        var settings = new AppSettings { GpsUpdateRate = -5 };
        settings.ValidateAndFix();
        Assert.That(settings.GpsUpdateRate, Is.EqualTo(10));
    }

    [Test]
    public void ValidateAndFix_InvalidPort_ResetsToDefault()
    {
        var settings = new AppSettings { NtripCasterPort = 999999 };
        settings.ValidateAndFix();
        Assert.That(settings.NtripCasterPort, Is.EqualTo(2101));
    }

    // ── Persistent state: simulator position is clamped in the setters ──

    [Test]
    public void PersistentState_ClampSimulatorLatitude()
    {
        var state = new PersistentAppState();
        state.SimulatorLatitude = 100;
        Assert.That(state.SimulatorLatitude, Is.EqualTo(90));

        state.SimulatorLatitude = -100;
        Assert.That(state.SimulatorLatitude, Is.EqualTo(-90));
    }

    [Test]
    public void PersistentState_ClampSimulatorLongitude()
    {
        var state = new PersistentAppState();
        state.SimulatorLongitude = 71280000;
        Assert.That(state.SimulatorLongitude, Is.EqualTo(180));

        state.SimulatorLongitude = -200;
        Assert.That(state.SimulatorLongitude, Is.EqualTo(-180));
    }

    [Test]
    public void PersistentState_ClampCameraPitch()
    {
        var state = new PersistentAppState();
        state.CameraPitch = 0;     // above max tilt
        Assert.That(state.CameraPitch, Is.EqualTo(-20));

        state.CameraPitch = -200;  // below straight-down
        Assert.That(state.CameraPitch, Is.EqualTo(-90));
    }
}
