// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.Services.DataQuality;

/// <summary>What a sensor found. The numbers go to the page as they are (the status frame's
/// data quality list), so a new one is added at the end and given its text in
/// <c>js/network.js</c> (<c>dataStreamText</c>).</summary>
public enum DataFinding : byte
{
    /// <summary>Nothing to judge: silent, too little yet, or not looked at (the simulator is the source).</summary>
    NoData = 0,
    /// <summary>Arriving at its own rate.</summary>
    Even = 1,
    /// <summary>Some never arrived.</summary>
    Gaps = 2,
    /// <summary>Held back and delivered several at a time.</summary>
    Bursts = 3,
    /// <summary>Every position newer than the one before.</summary>
    InOrder = 4,
    /// <summary>Positions that are not newer than the one before: the same one again, or an older one.</summary>
    OutOfOrder = 5,
    /// <summary>The sender sends as often as the app is set up for.</summary>
    AtRate = 6,
    /// <summary>The sender sends less often than the app is set up for.</summary>
    Slow = 7,
    /// <summary>Every sentence arrived whole.</summary>
    Whole = 8,
    /// <summary>Sentences that arrived damaged (a failed checksum, fields that do not read).</summary>
    Damaged = 9,
}

/// <summary>One sensor's judgement, as the operator's screens are given it.</summary>
/// <param name="Name">What the sensor watches, as the operator is told of it ("GPS positions").</param>
/// <param name="Faulty">The data is not fit to use, and the operator was told.</param>
/// <param name="RateHz">Arrivals a second, where the sensor counts them; else 0.</param>
/// <param name="NominalHz">The rate the sender sends at, where the sensor knows it; else 0.</param>
/// <param name="Amount">How much is wrong, in the sensor's own measure: the per cent missing
/// (<see cref="DataFinding.Gaps"/>), the number in the last minute (<see cref="DataFinding.OutOfOrder"/>,
/// <see cref="DataFinding.Damaged"/>).</param>
public readonly record struct DataReading(
    string Name, DataFinding Finding, bool Faulty, double RateHz, double NominalHz, double Amount);

/// <summary>
/// One thing that is watched in the data the app is given: how a stream arrives, whether the
/// positions come in order, and whatever is added later. A sensor measures and says what it
/// found. It does not decide what happens then, and it never repairs, drops or smooths the
/// data: <see cref="DataQualityMonitor"/> tells the operator, logs it and puts it in the bug
/// report, the same for every sensor.
/// <para>
/// A sensor is fed from wherever its data passes (a receive thread, nothing allocated there)
/// and read about once a second from the host loop. The texts describe the last
/// <see cref="Read"/>, and are asked for only when the operator is told and for a bug report.
/// </para>
/// </summary>
public interface IDataSensor
{
    /// <summary>What it watches, as the operator is told of it.</summary>
    string Name { get; }

    /// <summary>What the operator can do about a fault, one sentence without a full stop.</summary>
    string Advice { get; }

    /// <summary>What is said when a fault is over ("arriving evenly again").</summary>
    string Recovered { get; }

    /// <summary>Judge the data now.</summary>
    DataReading Read(long now);

    /// <summary>What is wrong, in words, for the operator ("arriving with gaps, about 19% missing").</summary>
    string Describe();

    /// <summary>The figures of the last reading, for the log and the bug report.</summary>
    string Figures();

    /// <summary>How often it went faulty since the app started.</summary>
    int Faults { get; }

    /// <summary>Forget what was measured (the source closed).</summary>
    void Reset();

    /// <summary>A sensor that sees the cause of what this one sees, or null: while that one is
    /// faulty this one is not reported, so the operator gets one message per cause (a damaged
    /// sentence is also a position that did not arrive).</summary>
    IDataSensor? ExplainedBy => null;
}
