// AgValoniaGPS
// Copyright (C) 2024-2025 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Linq;
using AgValoniaGPS.Models.Configuration;
using AgValoniaGPS.Models.State;
using AgValoniaGPS.Services.Interfaces;

namespace AgValoniaGPS.Services.AutoSteer;

/// <summary>
/// Statistical WAS zero analyzer ported from upstream CSmartWAS.cs.
/// Accumulates steer-angle samples while autosteer is engaged and
/// driving conditions are stable, then recommends a WAS offset
/// based on the median of the distribution. Confidence scoring
/// combines normal-fit, magnitude penalty, and sample-size factor.
/// </summary>
public sealed class SmartWasCalibrationService : ISmartWasCalibrationService
{
    // Buffer sizing
    private const int MAX_SAMPLES = 2000;
    private const int MIN_SAMPLES = 200;

    // Gating thresholds — converted from upstream's km/h and mm to AgValonia's m/s and m.
    private const double MIN_SPEED_MPS = 2.0 / 3.6;   // 2 km/h ≈ 0.5556 m/s
    private const double MAX_DIST_OFF_M = 0.5;        // 500 mm
    private const double MAX_ANGLE_DEG = 15.0;

    // Confidence scoring weights (sum to 1.0)
    private const double NORMAL_FIT_1SIGMA_WEIGHT = 0.3;
    private const double NORMAL_FIT_2SIGMA_WEIGHT = 0.3;
    private const double MAGNITUDE_WEIGHT = 0.2;
    private const double SIZE_WEIGHT = 0.2;

    private const double EXPECTED_PCT_1SIGMA = 0.68;
    private const double EXPECTED_PCT_2SIGMA = 0.95;
    private const double MAX_OFFSET_DEG = 10.0;

    // Validity gates
    private const double MIN_VALID_CONFIDENCE = 40.0;

    private readonly IAutoSteerService _autoSteerService;
    private readonly ApplicationState _appState;

    private readonly List<double> _history = new(MAX_SAMPLES + 1);
    private readonly object _dataLock = new();

    private double _meanAngle;
    private double _medianAngle;
    private double _stdDeviation;
    private double _recommendedOffset;
    private double _confidenceLevel;
    private bool _hasValidCalibration;
    private bool _isCollecting;

    public SmartWasCalibrationService(IAutoSteerService autoSteerService, ApplicationState appState)
    {
        _autoSteerService = autoSteerService;
        _appState = appState;
    }

    public bool IsCollecting => _isCollecting;

    public event EventHandler<SmartWasSnapshot>? SnapshotChanged;

    public void Start()
    {
        lock (_dataLock)
        {
            _isCollecting = true;
        }
    }

    public void Stop()
    {
        lock (_dataLock)
        {
            _isCollecting = false;
        }
    }

    public void Reset()
    {
        SmartWasSnapshot snap;
        lock (_dataLock)
        {
            _history.Clear();
            _meanAngle = 0;
            _medianAngle = 0;
            _stdDeviation = 0;
            _recommendedOffset = 0;
            _confidenceLevel = 0;
            _hasValidCalibration = false;
            snap = BuildSnapshotLocked();
        }
        SnapshotChanged?.Invoke(this, snap);
    }

    public void AddSample(double steerAngleDegrees)
    {
        // Outside-lock fast checks. ReadonlyState reads from singletons —
        // these can race but each is a single-field read; worst case the
        // sample is rejected when it shouldn't have been (or vice-versa)
        // for one frame, which is harmless in a 200-sample average.
        if (!_isCollecting) return;
        if (!_autoSteerService.IsEngaged) return;
        if (_appState.Vehicle.Speed < MIN_SPEED_MPS) return;
        if (Math.Abs(_appState.Guidance.CrossTrackError) > MAX_DIST_OFF_M) return;
        if (Math.Abs(steerAngleDegrees) > MAX_ANGLE_DEG) return;

        // WAS inversion: flip the sample sign so the recommended offset
        // direction matches the module's expected counts polarity.
        if (ConfigurationStore.Instance.AutoSteer.InvertWas)
            steerAngleDegrees = -steerAngleDegrees;

        SmartWasSnapshot snap;
        lock (_dataLock)
        {
            _history.Add(steerAngleDegrees);
            if (_history.Count > MAX_SAMPLES)
                _history.RemoveAt(0);

            if (_history.Count >= MIN_SAMPLES)
                AnalyzeDataLocked();

            snap = BuildSnapshotLocked();
        }
        SnapshotChanged?.Invoke(this, snap);
    }

    public void ApplyOffsetCorrection(double offsetDegrees)
    {
        SmartWasSnapshot snap;
        lock (_dataLock)
        {
            if (_history.Count == 0)
            {
                snap = BuildSnapshotLocked();
            }
            else
            {
                for (int i = 0; i < _history.Count; i++)
                    _history[i] += offsetDegrees;

                AnalyzeDataLocked();
                snap = BuildSnapshotLocked();
            }
        }
        SnapshotChanged?.Invoke(this, snap);
    }

    public SmartWasSnapshot GetSnapshot()
    {
        lock (_dataLock)
        {
            return BuildSnapshotLocked();
        }
    }

    private SmartWasSnapshot BuildSnapshotLocked()
    {
        return new SmartWasSnapshot
        {
            IsCollecting = _isCollecting,
            SampleCount = _history.Count,
            Mean = _meanAngle,
            Median = _medianAngle,
            StdDev = _stdDeviation,
            RecommendedOffset = _recommendedOffset,
            Confidence = _confidenceLevel,
            HasValidCalibration = _hasValidCalibration,
        };
    }

    private void AnalyzeDataLocked()
    {
        if (_history.Count < MIN_SAMPLES)
        {
            _hasValidCalibration = false;
            return;
        }

        CalculateStatisticsLocked();

        // Recommended offset is the negative of the median: if the operator's
        // wheels-straight zero-point lies at -2.3°, applying +2.3° corrects it.
        _recommendedOffset = -_medianAngle;

        _confidenceLevel = CalculateConfidenceLocked();

        _hasValidCalibration =
            _confidenceLevel > MIN_VALID_CONFIDENCE &&
            Math.Abs(_recommendedOffset) < MAX_OFFSET_DEG;
    }

    private void CalculateStatisticsLocked()
    {
        int count = _history.Count;

        // Mean
        double sum = 0;
        for (int i = 0; i < count; i++) sum += _history[i];
        _meanAngle = sum / count;

        // Median (sort a copy; List<double>.Sort would mutate the buffer in place)
        var sorted = new List<double>(_history);
        sorted.Sort();
        _medianAngle = (count % 2 == 0)
            ? (sorted[count / 2 - 1] + sorted[count / 2]) * 0.5
            : sorted[count / 2];

        // Sample standard deviation
        if (count > 1)
        {
            double sumSquares = 0;
            for (int i = 0; i < count; i++)
            {
                double d = _history[i] - _meanAngle;
                sumSquares += d * d;
            }
            _stdDeviation = Math.Sqrt(sumSquares / (count - 1));
        }
        else
        {
            _stdDeviation = 0;
        }
    }

    private double CalculateConfidenceLocked()
    {
        int count = _history.Count;
        if (count < MIN_SAMPLES) return 0;

        int within1Std = 0;
        int within2Std = 0;
        for (int i = 0; i < count; i++)
        {
            double deviation = Math.Abs(_history[i] - _medianAngle);
            if (deviation <= _stdDeviation) within1Std++;
            if (deviation <= 2 * _stdDeviation) within2Std++;
        }

        double pct1 = (double)within1Std / count;
        double pct2 = (double)within2Std / count;

        double score1 = 1 - Math.Abs(pct1 - EXPECTED_PCT_1SIGMA) / EXPECTED_PCT_1SIGMA;
        double score2 = 1 - Math.Abs(pct2 - EXPECTED_PCT_2SIGMA) / EXPECTED_PCT_2SIGMA;
        double magnitudeScore = Math.Max(0, 1 - Math.Abs(_recommendedOffset) / MAX_OFFSET_DEG);
        double sizeFactor = Math.Min(1.0, (double)count / (MIN_SAMPLES * 3));

        double confidence =
            (score1 * NORMAL_FIT_1SIGMA_WEIGHT +
             score2 * NORMAL_FIT_2SIGMA_WEIGHT +
             magnitudeScore * MAGNITUDE_WEIGHT +
             sizeFactor * SIZE_WEIGHT) * 100.0;

        return Math.Max(0, Math.Min(100, confidence));
    }
}
