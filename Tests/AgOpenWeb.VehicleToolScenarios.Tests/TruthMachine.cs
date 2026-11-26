// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.VehicleToolScenarios.Tests;

/// <summary>
/// The guidance hardware on the machine, as the Boards simulator offers it (GPS: UM982 dual,
/// ZED-X20P, ZED-F9P, two F9Ps; IMU: TM171, BNO085, BNO085 RVC, none; steering motor: Keya,
/// Cytron, IBT-2, Danfoss valve) and what the AiO v4.5 firmware sends AgOpenWeb: a dual
/// receiver gives $PAOGI (heading and roll from the two antennas), a single one $PANDA with
/// the IMU's heading and roll, or no heading (65535) and no roll without an IMU.
/// Plus how it is really mounted (a receiver heading error, a roll sensor the wrong way round
/// or not level).
/// </summary>
public sealed record SystemSpec(
    string Gps = "um982",           // um982 / x20p / f9p / dualf9p
    string Imu = "tm171",           // tm171 / bno / rvc / none
    string Motor = "keya",          // keya / cytron / ibt2 / danfoss
    double HeadingErrorDeg = 0,     // the receiver's heading minus the true heading
    bool RollInverted = false,      // the roll sensor reads the wrong way round
    double RollBiasDeg = 0,         // what it reads on level ground
    double ImuHeadingOffsetDeg = 37) // an IMU's yaw is not true north; AgOpenWeb learns the offset
{
    public bool Dual => Gps is "um982" or "dualf9p";
    public bool HasImu => Imu != "none";
    public bool HasRoll => Dual || HasImu;

    public string Label => Gps.ToUpperInvariant() + (Dual ? " dual" : "") + " + " + (HasImu ? Imu.ToUpperInvariant() : "no IMU") + " + " + Motor;

    /// <summary>Steering speed at the wheels (deg/s) and lag (s) of the motor (assumed, typical).</summary>
    public (double Rate, double Lag) Steering => Motor switch
    {
        "keya" => (45, 0.10),
        "cytron" => (30, 0.15),
        "ibt2" => (30, 0.15),
        "danfoss" => (25, 0.25),
        _ => (35, 0.12),
    };
}

/// <summary>
/// The machine as it really is on the field, in the field's own plane (E, N metres): a
/// bicycle model at the pivot axle steered by a hydraulic motor that follows the commanded
/// angle (rate limit, lag, end stops), the implement behind or in front of it as the real
/// linkage carries it (rigid, or a trailed tool following its hitch), and the antenna on the
/// cab, which moves sideways when the field's slope tilts the tractor.
/// </summary>
public sealed class TruthMachine
{
    public readonly VehicleSpec V;
    public readonly ToolSpec T;
    /// <summary>Cross slope: the field falls to the east by this many degrees.</summary>
    public readonly double SlopeDeg;

    public double E, N;          // pivot axle centre
    public double Heading;       // radians, clockwise from north
    public double Steer;         // actual wheel angle, degrees (+ right)
    public double SpeedKmh = 9;

    // Trailed tool / tank wheel centres.
    public double ToolAxE, ToolAxN, TankAxE, TankAxN;

    public readonly SystemSpec Sys;
    private readonly double _steerRate, _steerLag;

    public TruthMachine(VehicleSpec v, ToolSpec t, double slopeDeg, SystemSpec? sys = null)
    {
        V = v;
        T = t;
        SlopeDeg = slopeDeg;
        Sys = sys ?? new SystemSpec();
        (_steerRate, _steerLag) = Sys.Steering;
    }

    /// <summary>Roll the tractor stands at: + = tilted right (the right wheels lower).</summary>
    public double RollDeg => SlopeDeg * Math.Sin(Math.PI / 2 - Heading);

    public (double E, double N) Dir => (Math.Sin(Heading), Math.Cos(Heading));
    public (double E, double N) Right => (Math.Cos(Heading), -Math.Sin(Heading));

    /// <summary>Put the machine somewhere with everything straight behind it.</summary>
    public void Place(double e, double n, double headingRad)
    {
        E = e; N = n; Heading = headingRad; Steer = 0;
        var (de, dn) = Dir;
        double hx = E - de * V.HitchLength, hy = N - dn * V.HitchLength;
        if (T.Type == "tbt")
        {
            TankAxE = hx - de * T.TankHitch; TankAxN = hy - dn * T.TankHitch;
            ToolAxE = TankAxE - de * T.HitchLength; ToolAxN = TankAxN - dn * T.HitchLength;
        }
        else
        {
            ToolAxE = hx - de * T.HitchLength; ToolAxN = hy - dn * T.HitchLength;
        }
    }

    /// <summary>Advance <paramref name="dt"/> seconds with the steering asked for <paramref name="commandDeg"/>.</summary>
    public void Advance(double dt, double commandDeg)
    {
        const int sub = 10;
        double h = dt / sub;
        double v = SpeedKmh / 3.6;
        for (int i = 0; i < sub; i++)
        {
            double cmd = Math.Clamp(commandDeg, -V.MaxSteerAngle, V.MaxSteerAngle);
            double want = Steer + (cmd - Steer) * Math.Min(1, h / _steerLag);
            double maxStep = _steerRate * h;
            Steer += Math.Clamp(want - Steer, -maxStep, maxStep);
            Heading += v / V.Wheelbase * Math.Tan(Steer * Math.PI / 180) * h;
            Heading = (Heading % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI);
            var (de, dn) = Dir;
            E += de * v * h;
            N += dn * v * h;
            FollowTool();
        }
    }

    private void FollowTool()
    {
        if (T.Type is "front" or "rear") return;
        var (hx, hy) = HitchPin;
        if (T.Type == "tbt")
        {
            Follow(hx, hy, ref TankAxE, ref TankAxN, T.TankHitch);
            Follow(TankAxE, TankAxN, ref ToolAxE, ref ToolAxN, T.HitchLength);
        }
        else Follow(hx, hy, ref ToolAxE, ref ToolAxN, T.HitchLength);
    }

    /// <summary>A trailed axle at <paramref name="len"/> behind its hitch point keeps that
    /// distance and turns towards it (the tractrix of a towed wheel set).</summary>
    private static void Follow(double hx, double hy, ref double ax, ref double ay, double len)
    {
        double dx = hx - ax, dy = hy - ay;
        double d = Math.Sqrt(dx * dx + dy * dy);
        if (d < 1e-9) return;
        ax = hx - dx / d * len;
        ay = hy - dy / d * len;
    }

    public (double E, double N) HitchPin
    {
        get { var (de, dn) = Dir; return (E - de * V.HitchLength, N - dn * V.HitchLength); }
    }

    /// <summary>The antenna's position, with the sideways move a tilt gives a high antenna.</summary>
    public (double E, double N) Antenna
    {
        get
        {
            var (de, dn) = Dir;
            var (re, rn) = Right;
            double side = V.AntennaOffset + V.AntennaHeight * Math.Sin(RollDeg * Math.PI / 180);
            return (E + de * V.AntennaPivot + re * side, N + dn * V.AntennaPivot + rn * side);
        }
    }

    /// <summary>Centre of the working width, its heading, and its left and right ends.</summary>
    public (double E, double N, double Heading) ToolCentre
    {
        get
        {
            var (de, dn) = Dir;
            var (re, rn) = Right;
            switch (T.Type)
            {
                case "front":
                    return (E + de * T.HitchLength + re * T.Offset, N + dn * T.HitchLength + rn * T.Offset, Heading);
                case "rear":
                    return (E - de * T.HitchLength + re * T.Offset, N - dn * T.HitchLength + rn * T.Offset, Heading);
                default:
                {
                    double px, py;
                    if (T.Type == "tbt") { px = TankAxE; py = TankAxN; }
                    else (px, py) = HitchPin;
                    double tx = px - ToolAxE, ty = py - ToolAxN;
                    double th = Math.Atan2(tx, ty);
                    double tde = Math.Sin(th), tdn = Math.Cos(th), tre = Math.Cos(th), trn = -Math.Sin(th);
                    double back = T.ToolToPivot;
                    return (ToolAxE - tde * back + tre * T.Offset, ToolAxN - tdn * back + trn * T.Offset, (th + 2 * Math.PI) % (2 * Math.PI));
                }
            }
        }
    }
}
