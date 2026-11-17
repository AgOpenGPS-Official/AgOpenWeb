// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Threading;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Tool;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services.Tool;

/// <summary>
/// Tool position state as published by <see cref="ToolPositionService"/> at the end of each
/// Update: a value, copied whole by readers (a seqlock, <see cref="ToolPositionService"/>), so
/// they see a fully-consistent view across its fields and the 100 Hz writer allocates nothing.
/// </summary>
internal readonly record struct ToolPositionSnapshot(
    Vec3 ToolPosition,
    Vec3 ToolPivotPosition,
    double ToolHeading,
    Vec3 TankPosition,
    Vec3 HitchPosition,
    bool IsToolPositionReady,
    bool HasPosition);

/// <summary>
/// Calculates tool/implement position relative to vehicle pivot point.
/// Implements Torriem's algorithm for trailing tool heading calculation.
///
/// Thread-safety (#313): the host control loop calls Update at 100 Hz on
/// its own thread; readers (GPS pipeline, UI, renderer) pull from any
/// thread. A write lock serializes Update / ResetTrailingState so the
/// internal Torriem state stays consistent across frames; readers get a
/// fully-consistent snapshot via atomic reference swap with no locking.
///
/// Tool Types:
/// - Fixed Front: Tool rigidly attached in front of pivot
/// - Fixed Rear: Tool rigidly attached behind pivot
/// - Trailing: Tool trails behind on a hitch, swings during turns
/// - TBT (Tow-Between-Tractor): Two-stage trailing with tank trailer
/// </summary>
public class ToolPositionService : IToolPositionService
{
    // Current state — only mutated under _writeLock by Update / ResetTrailingState.
    private Vec3 _toolPosition;
    private Vec3 _toolPivotPosition;
    private Vec3 _tankPosition;
    private Vec3 _hitchPosition;
    private double _toolHeading;

    // State for trailing calculations (Torriem algorithm) — internal to writers.
    private Vec3 _lastToolPivotPos;
    private Vec3 _lastTankPos;
    private Vec3 _lastHitchPos;
    private int _startCounter;
    private const int STARTUP_FRAMES = 50;
    private const double JUMP_THRESHOLD_SQ = 400.0; // 20m squared - snap on GPS jumps

    // Jackknife: AgOpenGPS (Position.designer.cs) springs a trailed tool back behind what pulls it
    // past 1.9 rad from it, and a TBT tank back behind the tractor past 2.0 rad.
    private const double TOOL_JACKKNIFE = 1.9;
    private const double TANK_JACKKNIFE = 2.0;


    private static double AngleBetween(double a, double b) => Math.Abs(Math.PI - Math.Abs(Math.Abs(a - b) - Math.PI));

    // The published state, behind a seqlock: the writer (under _writeLock) makes _version odd,
    // writes, makes it even again; a reader copies the value and retries if the version was odd
    // or moved meanwhile. Readers never lock, and the 100 Hz writer allocates nothing: a new
    // snapshot object every tick sent the control thread to the runtime for fresh allocation
    // space every ~50 ticks, where a background collection (a browser's seed of 20 MB, the
    // Working Day scenarios' CONTROL-TICK-OVER-10-MS) held it for 100 ms in short waits.
    private ToolPositionSnapshot _snapshot;
    private int _version;

    private ToolPositionSnapshot Read()
    {
        var spin = new SpinWait();
        while (true)
        {
            int v1 = Volatile.Read(ref _version);
            if ((v1 & 1) == 0)
            {
                var s = _snapshot;
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref _version) == v1) return s;
            }
            spin.SpinOnce(sleep1Threshold: -1);
        }
    }

    // Serializes the two writers (Update from control loop, ResetTrailingState
    // from UI/MainViewModel). Brief — contains only field updates and snapshot
    // construction.
    private readonly object _writeLock = new();

    private readonly ConfigurationStore _configStore;

    public ToolPositionService(ConfigurationStore configStore)
    {
        _configStore = configStore;
    }

    public Vec3 ToolPosition => Read().ToolPosition;
    public Vec3 ToolPivotPosition => Read().ToolPivotPosition;
    public double ToolHeading => Read().ToolHeading;
    public Vec3 TankPosition => Read().TankPosition;
    public Vec3 HitchPosition => Read().HitchPosition;

    /// <summary>
    /// True when tool position is based on actual movement, not startup snap.
    /// During startup, the tool heading may be unreliable (GPS heading = 0 when stationary).
    /// </summary>
    public bool IsToolPositionReady => Read().IsToolPositionReady;

    /// <summary>
    /// True once <see cref="Update"/> has run: the tool has a position. A fixed tool's position
    /// is right from the first update; <see cref="IsToolPositionReady"/> waits for a trailing
    /// tool's heading to settle and never turns true for a fixed one.
    /// </summary>
    public bool HasPosition => Read().HasPosition;

    /// <summary>Caller holds _writeLock (the one writer at a time the seqlock needs).</summary>
    private void PublishSnapshot()
    {
        Interlocked.Increment(ref _version);   // odd: being written
        _snapshot = new ToolPositionSnapshot(
            _toolPosition, _toolPivotPosition, _toolHeading,
            _tankPosition, _hitchPosition,
            _startCounter >= STARTUP_FRAMES,
            HasPosition: true);
        Interlocked.Increment(ref _version);   // even: done
    }

    public void Update(Vec3 vehiclePivot, double vehicleHeading)
    {
        lock (_writeLock)
        {
            var tool = _configStore.Tool;
            var vehicle = _configStore.Vehicle;

            // The point the tool hangs on (RigGeometry): a fixed tool's working point, or the
            // tractor's hitch pin for a trailed one.
            var mount = RigGeometry.Mount(tool);
            _hitchPosition = RigGeometry.AlongTractor(vehiclePivot, vehicleHeading, RigGeometry.AttachX(vehicle, tool));

            switch (mount)
            {
                case ToolMount.TBT: CalculateTBTToolPosition(vehicleHeading, tool); break;
                case ToolMount.Trailing: CalculateTrailingToolPosition(vehicleHeading, tool); break;
                default: CalculateFixedToolPosition(vehiclePivot, vehicleHeading, tool); break;
            }

            // No lateral offset here: the section spans carry it (SectionControlService,
            // as AgOpenGPS's section positions do), so this is the tool's point on the
            // tractor's line. Shifting it as well painted the coverage at twice the offset (#446).

            PublishSnapshot();
        }
    }

    /// <summary>
    /// Fixed tool position - tool follows vehicle heading exactly
    /// </summary>
    private void CalculateFixedToolPosition(Vec3 vehiclePivot, double vehicleHeading, ToolConfig tool)
    {
        _toolHeading = vehicleHeading;

        // For fixed tools, the tool center is at the hitch point
        // The hitch length already accounts for front vs rear
        _toolPosition = _hitchPosition;
        _toolPivotPosition = _hitchPosition;
        _tankPosition = new Vec3(0, 0, 0);
    }

    /// <summary>
    /// Trailing tool position - implements Torriem's algorithm.
    /// Tool heading calculated from movement vector.
    /// </summary>
    private void CalculateTrailingToolPosition(double vehicleHeading, ToolConfig tool)
    {
        _startCounter++;

        // Always snap until we have enough movement for a reliable heading.
        // This prevents the tool from drifting during startup when heading
        // defaults to 0 (north) because GPS hasn't computed heading from movement yet.
        if (_startCounter < STARTUP_FRAMES)
        {
            SnapToolBehindVehicle(vehicleHeading, tool);
            return;
        }

        // Detect large position jumps (GPS glitch, RTK reacquire, drift change)
        // Compare hitch-to-hitch (not hitch-to-toolPivot which includes trailing length)
        double jumpDx = _hitchPosition.Easting - _lastHitchPos.Easting;
        double jumpDy = _hitchPosition.Northing - _lastHitchPos.Northing;
        if (jumpDx * jumpDx + jumpDy * jumpDy > JUMP_THRESHOLD_SQ) // > 20m
        {
            SnapToolBehindVehicle(vehicleHeading, tool);
            _lastHitchPos = _hitchPosition;
            return;
        }
        _lastHitchPos = _hitchPosition;

        // Torriem's algorithm: calculate heading from movement
        // Tool heading = direction from current position toward hitch
        double dx = _hitchPosition.Easting - _lastToolPivotPos.Easting;
        double dy = _hitchPosition.Northing - _lastToolPivotPos.Northing;

        // Only update heading if we've moved enough
        if (Math.Abs(dx) > 0.001 || Math.Abs(dy) > 0.001)
        {
            _toolHeading = Math.Atan2(dx, dy);
            if (_toolHeading < 0) _toolHeading += 2 * Math.PI; // Normalize to [0, 2pi]
        }

        // Jackknifed: spring it back behind the hitch (AgOpenGPS, against the hitch's heading)
        if (AngleBetween(_toolHeading, vehicleHeading) > TOOL_JACKKNIFE)
        {
            SnapToolBehindVehicle(vehicleHeading, tool);
            return;
        }

        // Calculate tool pivot position - trails behind hitch
        _toolPivotPosition = new Vec3(
            _hitchPosition.Easting - Math.Sin(_toolHeading) * tool.TrailingHitchLength,
            _hitchPosition.Northing - Math.Cos(_toolHeading) * tool.TrailingHitchLength,
            _toolHeading
        );

        // Calculate tool center position (offset from pivot by TrailingToolToPivotLength)
        double pivotOffset = RigGeometry.ToolBehindPivot(tool);
        _toolPosition = new Vec3(
            _hitchPosition.Easting - Math.Sin(_toolHeading) * pivotOffset,
            _hitchPosition.Northing - Math.Cos(_toolHeading) * pivotOffset,
            _toolHeading
        );

        _tankPosition = new Vec3(0, 0, 0);

        // Save for next frame
        _lastToolPivotPos = _toolPivotPosition;
        _lastHitchPos = _hitchPosition;
    }

    /// <summary>
    /// TBT (Tow-Between-Tractor) position - two-stage trailing.
    /// Tank trailer follows vehicle, tool follows tank.
    /// </summary>
    private void CalculateTBTToolPosition(double vehicleHeading, ToolConfig tool)
    {
        _startCounter++;

        // During startup, snap everything behind vehicle
        if (_startCounter < STARTUP_FRAMES)
        {
            SnapTBTBehindVehicle(vehicleHeading, tool);
            return;
        }

        // Detect large position jumps - snap instead of trailing
        double jumpDx = _hitchPosition.Easting - _lastHitchPos.Easting;
        double jumpDy = _hitchPosition.Northing - _lastHitchPos.Northing;
        if (jumpDx * jumpDx + jumpDy * jumpDy > JUMP_THRESHOLD_SQ) // > 20m
        {
            SnapTBTBehindVehicle(vehicleHeading, tool);
            _lastHitchPos = _hitchPosition;
            return;
        }
        _lastHitchPos = _hitchPosition;

        // Stage 1: Tank follows hitch (Torriem's algorithm)
        double tankDx = _hitchPosition.Easting - _lastTankPos.Easting;
        double tankDy = _hitchPosition.Northing - _lastTankPos.Northing;

        double tankHeading = _tankPosition.Heading;
        if (Math.Abs(tankDx) > 0.001 || Math.Abs(tankDy) > 0.001)
        {
            tankHeading = Math.Atan2(tankDx, tankDy);
            if (tankHeading < 0) tankHeading += 2 * Math.PI;
        }

        // The tank jackknifed against the tractor: spring it back behind the hitch (AgOpenGPS)
        if (AngleBetween(tankHeading, vehicleHeading) > TANK_JACKKNIFE)
            tankHeading = vehicleHeading;

        // Tank position trails behind hitch
        _tankPosition = new Vec3(
            _hitchPosition.Easting - Math.Sin(tankHeading) * tool.TankTrailingHitchLength,
            _hitchPosition.Northing - Math.Cos(tankHeading) * tool.TankTrailingHitchLength,
            tankHeading
        );

        // Stage 2: Tool follows tank (Torriem's algorithm)
        double toolDx = _tankPosition.Easting - _lastToolPivotPos.Easting;
        double toolDy = _tankPosition.Northing - _lastToolPivotPos.Northing;

        if (Math.Abs(toolDx) > 0.001 || Math.Abs(toolDy) > 0.001)
        {
            _toolHeading = Math.Atan2(toolDx, toolDy);
            if (_toolHeading < 0) _toolHeading += 2 * Math.PI;
        }

        // The tool jackknifed against the tank: spring it back behind the tank (AgOpenGPS)
        if (AngleBetween(_toolHeading, tankHeading) > TOOL_JACKKNIFE)
            _toolHeading = tankHeading;

        // Tool pivot trails behind tank
        _toolPivotPosition = new Vec3(
            _tankPosition.Easting - Math.Sin(_toolHeading) * tool.TrailingHitchLength,
            _tankPosition.Northing - Math.Cos(_toolHeading) * tool.TrailingHitchLength,
            _toolHeading
        );

        // Tool center position
        double pivotOffset = RigGeometry.ToolBehindPivot(tool);
        _toolPosition = new Vec3(
            _tankPosition.Easting - Math.Sin(_toolHeading) * pivotOffset,
            _tankPosition.Northing - Math.Cos(_toolHeading) * pivotOffset,
            _toolHeading
        );

        // Save for next frame
        _lastTankPos = _tankPosition;
        _lastToolPivotPos = _toolPivotPosition;
        _lastHitchPos = _hitchPosition;
    }

    /// <summary>
    /// Snap trailing tool directly behind vehicle (for startup or jackknife recovery)
    /// </summary>
    private void SnapToolBehindVehicle(double vehicleHeading, ToolConfig tool)
    {
        _toolHeading = vehicleHeading;

        _toolPivotPosition = new Vec3(
            _hitchPosition.Easting - Math.Sin(vehicleHeading) * tool.TrailingHitchLength,
            _hitchPosition.Northing - Math.Cos(vehicleHeading) * tool.TrailingHitchLength,
            vehicleHeading
        );

        double pivotOffset = RigGeometry.ToolBehindPivot(tool);
        _toolPosition = new Vec3(
            _hitchPosition.Easting - Math.Sin(vehicleHeading) * pivotOffset,
            _hitchPosition.Northing - Math.Cos(vehicleHeading) * pivotOffset,
            vehicleHeading
        );

        _tankPosition = new Vec3(0, 0, 0);
        _lastToolPivotPos = _toolPivotPosition;
        _lastHitchPos = _hitchPosition;
    }

    /// <summary>
    /// Snap TBT (tank + tool) directly behind vehicle
    /// </summary>
    private void SnapTBTBehindVehicle(double vehicleHeading, ToolConfig tool)
    {
        _toolHeading = vehicleHeading;

        // Tank behind hitch
        _tankPosition = new Vec3(
            _hitchPosition.Easting - Math.Sin(vehicleHeading) * tool.TankTrailingHitchLength,
            _hitchPosition.Northing - Math.Cos(vehicleHeading) * tool.TankTrailingHitchLength,
            vehicleHeading
        );

        // Tool behind tank
        _toolPivotPosition = new Vec3(
            _tankPosition.Easting - Math.Sin(vehicleHeading) * tool.TrailingHitchLength,
            _tankPosition.Northing - Math.Cos(vehicleHeading) * tool.TrailingHitchLength,
            vehicleHeading
        );

        double pivotOffset = RigGeometry.ToolBehindPivot(tool);
        _toolPosition = new Vec3(
            _tankPosition.Easting - Math.Sin(vehicleHeading) * pivotOffset,
            _tankPosition.Northing - Math.Cos(vehicleHeading) * pivotOffset,
            vehicleHeading
        );

        _lastTankPos = _tankPosition;
        _lastToolPivotPos = _toolPivotPosition;
        _lastHitchPos = _hitchPosition;
    }

    public (Vec3 left, Vec3 right) GetToolEdgePositions()
    {
        var tool = _configStore.Tool;
        double halfWidth = tool.Width / 2.0;

        return GetSectionEdgePositions(-halfWidth, halfWidth);
    }

    public Vec3 GetSectionPosition(int sectionIndex, double sectionLeft, double sectionRight)
    {
        double sectionCenter = (sectionLeft + sectionRight) / 2.0;

        // Read consistent pose from snapshot.
        var snap = Read();
        double perpHeading = snap.ToolHeading + Math.PI / 2.0;

        return new Vec3(
            snap.ToolPosition.Easting + Math.Sin(perpHeading) * sectionCenter,
            snap.ToolPosition.Northing + Math.Cos(perpHeading) * sectionCenter,
            snap.ToolHeading
        );
    }

    public (Vec3 left, Vec3 right) GetSectionEdgePositions(double sectionLeft, double sectionRight)
    {
        var snap = Read();
        double perpHeading = snap.ToolHeading + Math.PI / 2.0;

        var left = new Vec3(
            snap.ToolPosition.Easting + Math.Sin(perpHeading) * sectionLeft,
            snap.ToolPosition.Northing + Math.Cos(perpHeading) * sectionLeft,
            snap.ToolHeading
        );

        var right = new Vec3(
            snap.ToolPosition.Easting + Math.Sin(perpHeading) * sectionRight,
            snap.ToolPosition.Northing + Math.Cos(perpHeading) * sectionRight,
            snap.ToolHeading
        );

        return (left, right);
    }

    public void ResetTrailingState(Vec3 vehiclePivot, double vehicleHeading)
    {
        lock (_writeLock)
        {
            _startCounter = STARTUP_FRAMES - 5; // Brief snap period (5 frames) then resume trailing

            var tool = _configStore.Tool;
            var vehicle = _configStore.Vehicle;

            _hitchPosition = RigGeometry.AlongTractor(vehiclePivot, vehicleHeading, RigGeometry.AttachX(vehicle, tool));

            if (RigGeometry.Mount(tool) == ToolMount.TBT)
            {
                SnapTBTBehindVehicle(vehicleHeading, tool);
            }
            else
            {
                SnapToolBehindVehicle(vehicleHeading, tool);
            }

            PublishSnapshot();
        }
    }
}
