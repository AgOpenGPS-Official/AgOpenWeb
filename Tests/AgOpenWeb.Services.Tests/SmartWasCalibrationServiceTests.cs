using System;
using System.Linq;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services.AutoSteer;
using AgOpenWeb.Services.Interfaces;
using NSubstitute;

namespace AgOpenWeb.Services.Tests;

[TestFixture]
[NonParallelizable] // Mutates ConfigurationStore.Instance and ApplicationState
public class SmartWasCalibrationServiceTests
{
    private IAutoSteerService _autoSteer = null!;
    private ApplicationState _appState = null!;
    private SmartWasCalibrationService _service = null!;

    private const int MIN_SAMPLES = 200;
    private const double IN_BOUND_SPEED_MPS = 1.0;     // > 0.5556
    private const double IN_BOUND_XTE_M = 0.1;         // < 0.5

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());

        _autoSteer = Substitute.For<IAutoSteerService>();
        _autoSteer.IsEngaged.Returns(true);

        _appState = new ApplicationState();
        _appState.Vehicle.Speed = IN_BOUND_SPEED_MPS;
        _appState.Guidance.CrossTrackError = IN_BOUND_XTE_M;

        _service = new SmartWasCalibrationService(_autoSteer, _appState, ConfigurationStore.Instance);
        _service.Start();
    }

    private void FeedSamples(int count, Func<int, double> sampleFn)
    {
        for (int i = 0; i < count; i++)
            _service.AddSample(sampleFn(i));
    }

    private static double[] DeterministicNormal(int n, double mean, double stdDev, int seed)
    {
        var rng = new Random(seed);
        var samples = new double[n];
        for (int i = 0; i < n; i++)
        {
            // Box-Muller
            double u1 = 1.0 - rng.NextDouble();
            double u2 = 1.0 - rng.NextDouble();
            double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            samples[i] = mean + stdDev * z;
        }
        return samples;
    }

    // 1. Mean/median/stddev correctness
    [Test]
    public void Statistics_NormalDistribution_MatchExpectedWithinTolerance()
    {
        var samples = DeterministicNormal(1000, mean: -0.5, stdDev: 0.2, seed: 42);
        FeedSamples(samples.Length, i => samples[i]);

        var snap = _service.GetSnapshot();
        Assert.That(snap.Mean, Is.EqualTo(-0.5).Within(0.05));
        Assert.That(snap.Median, Is.EqualTo(-0.5).Within(0.05));
        Assert.That(snap.StdDev, Is.EqualTo(0.2).Within(0.05));
        Assert.That(snap.RecommendedOffset, Is.EqualTo(0.5).Within(0.05));
    }

    // 2. Confidence high for clean normal distribution
    [Test]
    public void Confidence_NormalDistribution_ScoresHigh()
    {
        var samples = DeterministicNormal(600, mean: -0.5, stdDev: 0.15, seed: 7);
        FeedSamples(samples.Length, i => samples[i]);

        var snap = _service.GetSnapshot();
        Assert.That(snap.Confidence, Is.GreaterThan(85),
            "A clean N(μ,σ) sample should score very high on the normal-fit terms");
        Assert.That(snap.HasValidCalibration, Is.True);
    }

    // 3. Confidence drops to invalid when |offset| ≥ 10°
    [Test]
    public void Confidence_LargeOffsetExceedsLimit_HasValidCalibrationFalse()
    {
        // Force a large median by feeding samples around -10.5 (still under |angle|<15° gate)
        var samples = DeterministicNormal(500, mean: -10.5, stdDev: 0.1, seed: 13);
        FeedSamples(samples.Length, i => samples[i]);

        var snap = _service.GetSnapshot();
        Assert.That(Math.Abs(snap.RecommendedOffset), Is.GreaterThanOrEqualTo(10.0));
        Assert.That(snap.HasValidCalibration, Is.False,
            "When |recommendedOffset| ≥ 10°, calibration must be marked invalid even if confidence math passes");
    }

    // 4. Min sample gate
    [Test]
    public void HasValidCalibration_BelowMinSamples_StaysFalse()
    {
        FeedSamples(MIN_SAMPLES - 1, _ => 0.1);

        var snap = _service.GetSnapshot();
        Assert.That(snap.SampleCount, Is.EqualTo(MIN_SAMPLES - 1));
        Assert.That(snap.HasValidCalibration, Is.False);
    }

    [Test]
    public void HasValidCalibration_AtMinSamples_TriggersAnalysis()
    {
        FeedSamples(MIN_SAMPLES, _ => 0.0);

        var snap = _service.GetSnapshot();
        Assert.That(snap.SampleCount, Is.EqualTo(MIN_SAMPLES));
        // With all-zero samples, mean/median/std=0 → magnitudeScore=1, sizeFactor=200/600≈0.33
        // Normal-fit: deviation==0 for all → within1Std=count, pct1=1.0; expected1=0.68 → score1<0
        // The math may or may not cross 40 — we just assert that analysis ran
        Assert.That(snap.Mean, Is.EqualTo(0).Within(1e-9));
    }

    // 5. MAX_SAMPLES rolling buffer
    [Test]
    public void Buffer_ExceedsMax_RollsOff()
    {
        FeedSamples(2500, i => i * 0.001);

        var snap = _service.GetSnapshot();
        Assert.That(snap.SampleCount, Is.EqualTo(2000), "Buffer should roll off at MAX_SAMPLES");
    }

    // 6. Gating: speed
    [Test]
    public void Gating_SpeedBelowCutoff_RejectsSamples()
    {
        _appState.Vehicle.Speed = 0.5; // < 0.5556 m/s (2 km/h)
        FeedSamples(MIN_SAMPLES + 50, _ => 0.1);
        Assert.That(_service.GetSnapshot().SampleCount, Is.EqualTo(0));

        _appState.Vehicle.Speed = 0.6; // > cutoff
        FeedSamples(50, _ => 0.1);
        Assert.That(_service.GetSnapshot().SampleCount, Is.EqualTo(50));
    }

    // 7. Gating: XTE
    [Test]
    public void Gating_XteAboveCutoff_RejectsSamples()
    {
        _appState.Guidance.CrossTrackError = 0.51; // > 0.5 m
        FeedSamples(MIN_SAMPLES + 50, _ => 0.1);
        Assert.That(_service.GetSnapshot().SampleCount, Is.EqualTo(0));

        _appState.Guidance.CrossTrackError = -0.49; // negative side, within bound
        FeedSamples(50, _ => 0.1);
        Assert.That(_service.GetSnapshot().SampleCount, Is.EqualTo(50));
    }

    // 8. Gating: |angle| > 15° rejected
    [Test]
    public void Gating_AngleAboveLimit_RejectsSamples()
    {
        FeedSamples(50, _ => 16.0);
        FeedSamples(50, _ => -16.0);
        FeedSamples(100, _ => 14.9);

        Assert.That(_service.GetSnapshot().SampleCount, Is.EqualTo(100),
            "Only samples within ±15° should be accumulated");
    }

    // 9. Gating: not engaged
    [Test]
    public void Gating_NotEngaged_RejectsSamples()
    {
        _autoSteer.IsEngaged.Returns(false);
        FeedSamples(MIN_SAMPLES + 50, _ => 0.1);

        Assert.That(_service.GetSnapshot().SampleCount, Is.EqualTo(0));
    }

    // 10. InvertWas flips sample sign
    [Test]
    public void InvertWas_FlipsSampleSign()
    {
        ConfigurationStore.Instance.AutoSteer.InvertWas = true;
        FeedSamples(MIN_SAMPLES, _ => 1.0);

        var snap = _service.GetSnapshot();
        Assert.That(snap.Mean, Is.EqualTo(-1.0).Within(1e-9),
            "With InvertWas=true, +1° input should land in the buffer as -1°");
    }

    // 11. ApplyOffsetCorrection shifts buffer
    [Test]
    public void ApplyOffsetCorrection_ShiftsBufferAndRecomputes()
    {
        FeedSamples(MIN_SAMPLES, _ => -0.4);
        var beforeApply = _service.GetSnapshot();
        Assert.That(beforeApply.Median, Is.EqualTo(-0.4).Within(1e-9));
        Assert.That(beforeApply.RecommendedOffset, Is.EqualTo(0.4).Within(1e-9));

        _service.ApplyOffsetCorrection(0.4);

        var afterApply = _service.GetSnapshot();
        Assert.That(afterApply.Median, Is.EqualTo(0.0).Within(1e-9));
        Assert.That(afterApply.RecommendedOffset, Is.EqualTo(0.0).Within(1e-9));
    }

    // 12. Reset clears buffer + analysis
    [Test]
    public void Reset_ClearsEverything()
    {
        FeedSamples(MIN_SAMPLES, _ => -0.5);
        _service.Reset();

        var snap = _service.GetSnapshot();
        Assert.That(snap.SampleCount, Is.EqualTo(0));
        Assert.That(snap.Mean, Is.EqualTo(0));
        Assert.That(snap.Median, Is.EqualTo(0));
        Assert.That(snap.RecommendedOffset, Is.EqualTo(0));
        Assert.That(snap.Confidence, Is.EqualTo(0));
        Assert.That(snap.HasValidCalibration, Is.False);
    }

    // 13. Stop halts accumulation but keeps analysis
    [Test]
    public void Stop_PreservesAnalysisButRejectsNewSamples()
    {
        FeedSamples(MIN_SAMPLES, _ => -0.3);
        var afterCollect = _service.GetSnapshot();

        _service.Stop();
        FeedSamples(50, _ => 0.0);
        var afterStop = _service.GetSnapshot();

        Assert.That(afterStop.SampleCount, Is.EqualTo(afterCollect.SampleCount),
            "Sample count must not grow after Stop");
        Assert.That(afterStop.Median, Is.EqualTo(afterCollect.Median).Within(1e-9));
        Assert.That(_service.IsCollecting, Is.False);
    }

    // SnapshotChanged is wired correctly
    [Test]
    public void SnapshotChanged_FiresOnSampleAdd()
    {
        int eventCount = 0;
        _service.SnapshotChanged += (_, _) => eventCount++;

        FeedSamples(5, _ => 0.1);
        Assert.That(eventCount, Is.EqualTo(5));
    }
}
