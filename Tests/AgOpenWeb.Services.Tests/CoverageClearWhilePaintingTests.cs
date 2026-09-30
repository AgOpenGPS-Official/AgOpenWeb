using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Coverage;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// Deleting the applied area while sections paint (AgOpenGPS #1205/#1206): the section
/// service only calls StartMapping on a section's off→on edge, so ClearAll must keep the
/// mapping zones mapping, or every later point is dropped until the section cycles.
/// </summary>
[TestFixture]
public class CoverageClearWhilePaintingTests
{
    private CoverageMapService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _service = new CoverageMapService(ConfigurationStore.Instance);
        _service.SetFieldBoundsFromPosition(1000.0, 2000.0);
    }

    private void Drive(int zone, double fromN, double toN)
    {
        for (double n = fromN; n <= toN; n += 1)
            _service.AddCoveragePoint(zone, new Vec2(999, n), new Vec2(1001, n));
    }

    [Test]
    public void ClearAll_WhilePainting_KeepsPainting()
    {
        _service.StartMapping(0, new Vec2(999, 2000), new Vec2(1001, 2000));
        Drive(0, 2001, 2010);
        Assume.That(_service.TotalWorkedArea, Is.GreaterThan(0));

        _service.ClearAll();
        Assert.That(_service.TotalWorkedArea, Is.EqualTo(0), "the painted area is gone");
        Assert.That(_service.IsZoneMapping(0), Is.True, "the section is still on");
        Assert.That(_service.IsPointCovered(1000, 2005), Is.False);

        Drive(0, 2020, 2030);
        Assert.That(_service.TotalWorkedArea, Is.EqualTo(20).Within(0.5),
            "painting resumes with the next point (10 m × 2 m; the first point only re-seeds)");
        Assert.That(_service.IsPointCovered(1000, 2025), Is.True);
        Assert.That(_service.IsPointCovered(1000, 2015), Is.False, "no quad bridges the gap");
    }

    [Test]
    public void ClearAll_LeavesStoppedZonesStopped()
    {
        _service.StartMapping(0, new Vec2(999, 2000), new Vec2(1001, 2000));
        Drive(0, 2001, 2005);
        _service.StopMapping(0);

        _service.ClearAll();
        Drive(0, 2020, 2030);
        Assert.That(_service.TotalWorkedArea, Is.EqualTo(0));
        Assert.That(_service.IsZoneMapping(0), Is.False);
    }
}
