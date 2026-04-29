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

namespace AgValoniaGPS.Services.Interfaces;

/// <summary>
/// Smart WAS (Wheel Angle Sensor) automatic zero calibration.
/// Accumulates steer-angle samples while autosteer is engaged and the
/// vehicle is driving cleanly, then recommends a WAS offset based on
/// statistical analysis of the distribution.
///
/// Ported from upstream CSmartWAS.cs. Complementary to the manual
/// one-shot zero in WasCalibrationStepViewModel — operators may use
/// either workflow.
/// </summary>
public interface ISmartWasCalibrationService
{
    /// <summary>Begin accumulating samples (still gated by IsEngaged + speed + XTE).</summary>
    void Start();

    /// <summary>Pause accumulation. Existing samples and analysis are preserved.</summary>
    void Stop();

    /// <summary>Clear the buffer and zero all analysis results.</summary>
    void Reset();

    /// <summary>
    /// Add a steer-angle sample (degrees). Called from
    /// AutoSteerService.ProcessSteerData on the UDP receive thread.
    /// Internally gated: rejects unless IsCollecting and the vehicle
    /// state passes speed/XTE/angle/engaged thresholds.
    /// </summary>
    void AddSample(double steerAngleDegrees);

    /// <summary>
    /// Shift the existing buffer by the given offset to prevent
    /// double-correction on a follow-up Apply. Called by the
    /// ViewModel after writing the recommended offset to the
    /// vehicle config and pushing PGN 252.
    /// </summary>
    void ApplyOffsetCorrection(double offsetDegrees);

    /// <summary>Atomic snapshot of the current analysis state for UI consumption.</summary>
    SmartWasSnapshot GetSnapshot();

    /// <summary>True if Start has been called and Stop hasn't.</summary>
    bool IsCollecting { get; }

    /// <summary>
    /// Fired on the UDP receive thread after each AnalyzeData() pass.
    /// Subscribers MUST marshal to UI thread before mutating INPC
    /// properties (Dispatcher.UIThread.Post). See PR #320 for the
    /// canonical reference on the cross-thread INPC hazard.
    /// </summary>
    event EventHandler<SmartWasSnapshot>? SnapshotChanged;
}

/// <summary>
/// Atomic results bundle. All values reflect the buffer state at the
/// moment GetSnapshot() (or SnapshotChanged) was produced.
/// </summary>
public readonly struct SmartWasSnapshot
{
    public bool IsCollecting { get; init; }
    public int SampleCount { get; init; }
    public double Mean { get; init; }
    public double Median { get; init; }
    public double StdDev { get; init; }

    /// <summary>Recommended WAS offset in degrees (sign already negated from median).</summary>
    public double RecommendedOffset { get; init; }

    /// <summary>0..100. Above 40 with |offset| &lt; 10° qualifies as a usable calibration.</summary>
    public double Confidence { get; init; }

    /// <summary>True when Confidence &gt; 40 and |RecommendedOffset| &lt; 10.</summary>
    public bool HasValidCalibration { get; init; }
}
