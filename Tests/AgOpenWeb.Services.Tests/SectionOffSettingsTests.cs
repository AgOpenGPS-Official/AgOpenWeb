using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Section;
using NSubstitute;

namespace AgOpenWeb.Services.Tests;

/// <summary>#110: "Off outside boundary" and "Turn-off delay" (AgOpenGPS
/// tool.isSectionOffWhenOut / tool.turnOffDelay) now reach section control.</summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class SectionOffSettingsTests
{
    private ApplicationState _appState = null!;
    private SectionControlService _service = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var config = ConfigurationStore.Instance;
        config.NumSections = 3;
        for (int i = 0; i < 3; i++) config.Tool.SetSectionWidth(i, 200); // 2 m each, 6 m tool
        config.Tool.Offset = 0;

        _appState = new ApplicationState();
        var poly = new BoundaryPolygon();
        poly.Points.Add(new BoundaryPoint(0, 0, 0));
        poly.Points.Add(new BoundaryPoint(200, 0, 0));
        poly.Points.Add(new BoundaryPoint(200, 200, 0));
        poly.Points.Add(new BoundaryPoint(0, 200, 0));
        poly.UpdateBounds();
        _appState.Field.CurrentBoundary = new Boundary { OuterBoundary = poly };

        _service = new SectionControlService(Substitute.For<IToolPositionService>(),
            Substitute.For<ICoverageMapService>(), _appState, ConfigurationStore.Instance);
        _service.SetAllAuto();
        _service.MasterState = SectionMasterState.Auto;
    }

    private void Run(double e, double n, int ticks)
    {
        for (int i = 0; i < ticks; i++) _service.Update(new Vec3(e, n, 0), 0, 0, 5.0);
    }

    [Test]
    public void OffOutsideBoundary_On_ASectionHalfOutIsOff()
    {
        ConfigurationStore.Instance.Tool.IsSectionOffWhenOut = true;
        Run(0.5, 100, 10); // tool centre 0.5 m inside the west edge: middle section 75% in
        Assert.That(_service.SectionStates[1].IsOn, Is.False);
        Assert.That(_service.SectionStates[2].IsOn, Is.True);
    }

    [Test]
    public void OffOutsideBoundary_Off_ASectionPartlyInStaysOn()
    {
        ConfigurationStore.Instance.Tool.IsSectionOffWhenOut = false;
        Run(0.5, 100, 10);
        Assert.That(_service.SectionStates[1].IsOn, Is.True);
    }

    private int TicksToOffAtEdge()
    {
        Run(100, 100, 10);                       // all on, mid-field
        Assert.That(_service.SectionStates[1].IsOn, Is.True);
        for (int t = 1; t <= 60; t++)
        {
            _service.Update(new Vec3(100, 199.9, 0), 0, 0, 5.0); // look-off point now past the north edge
            if (!_service.SectionStates[1].IsOn) return t;
        }
        return int.MaxValue;
    }

    [Test]
    public void TurnOffDelay_KeepsTheSectionOnThatLong()
    {
        ConfigurationStore.Instance.Tool.LookAheadOffSetting = 0;
        ConfigurationStore.Instance.Tool.TurnOffDelay = 0;
        int noDelay = TicksToOffAtEdge();

        SetUp();
        ConfigurationStore.Instance.Tool.LookAheadOffSetting = 0;
        ConfigurationStore.Instance.Tool.TurnOffDelay = 1.0; // 1 s at 10 Hz
        int withDelay = TicksToOffAtEdge();

        Assert.That(noDelay, Is.LessThanOrEqualTo(2));
        Assert.That(withDelay, Is.EqualTo(10).Within(1));
    }
}
