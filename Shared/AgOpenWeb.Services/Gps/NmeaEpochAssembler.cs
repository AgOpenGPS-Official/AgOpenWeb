// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;

namespace AgOpenWeb.Services.Gps;

/// <summary>The sentences a receiver spreads one fix over.</summary>
[Flags]
public enum EpochMembers : byte
{
    None = 0,
    /// <summary>GGA or GNS: position, fix quality, satellites, HDOP, correction age.</summary>
    Position = 1,
    /// <summary>VTG: speed and true track.</summary>
    Vtg = 2,
    /// <summary>HPR: dual-antenna heading, pitch (the vehicle's roll) and quality.</summary>
    Hpr = 4,
    /// <summary>HDT: true heading.</summary>
    Hdt = 8,
    /// <summary>THS: true heading with a mode indicator.</summary>
    Ths = 16,
}

/// <summary>
/// One decoded sentence of a multi-sentence receiver, as the parser hands it to
/// <see cref="NmeaEpochAssembler"/>. Only the fields its kind carries are meaningful.
/// </summary>
public struct EpochMemberData
{
    public EpochMembers Kind;
    /// <summary>The sentence's UTC as hhmmss.ss × 100, or -1 when it has none (VTG, HDT, THS).</summary>
    public int UtcCentiseconds;
    /// <summary>The talker was an IMU / heading sensor (IN, HE), not the receiver: the heading goes to the IMU slot.</summary>
    public bool FromImuTalker;
    /// <summary>GNS rather than GGA (for the family text).</summary>
    public bool IsGns;

    public double Latitude, Longitude, Altitude, Hdop, DifferentialAge;
    public int FixQuality, Satellites;

    public double SpeedMps, TrackDeg;

    public double HeadingDeg;
    public bool HeadingValid;
    public double RollDeg;
    public bool RollValid;
}

/// <summary>
/// Builds one fix per receiver epoch from the standard sentences a receiver prints per
/// epoch (GGA/GNS, VTG, HPR, HDT, THS), for receivers that have no single-sentence fix like
/// <c>$KSXT</c> or <c>$PAOGI</c>. The rule of <c>Plans/GPS_RECEIVER_SENTENCES_PLAN.md</c>:
/// one fix = one receiver epoch, and the fix holds only what the receiver measured in that
/// epoch, never a value carried over from an earlier one.
///
/// <para><b>Epoch identity.</b> Sentences that carry a UTC (GGA, GNS, HPR) belong to the
/// epoch with that UTC; a different UTC starts a new epoch. Sentences without one (VTG,
/// HDT, THS) attach to the open epoch, and a repeated kind starts a new epoch too.</para>
///
/// <para><b>Learning the burst.</b> The set of kinds the receiver prints per epoch is
/// learned from <see cref="EpochsToLearn"/> alike epochs. Until then every epoch is closed
/// by the start of the next (one epoch of lag). Once learned, the fix is emitted the moment
/// the last member of the set arrives: no timer, no lag. If the set changes (the receiver
/// was reconfigured, or a member stops coming), it is relearned the same way; an epoch the
/// next start closes with a learned member missing is emitted with that member absent
/// (heading invalid, roll 0, speed 0) and counted as incomplete.</para>
///
/// <para>Deterministic in the receiver's fields: the same sequence of sentences gives the
/// same fixes whatever datagrams they arrived in. Nothing is allocated per sentence; the
/// family text is rebuilt only when the learned set changes.</para>
/// </summary>
public sealed class NmeaEpochAssembler
{
    public const int EpochsToLearn = 3;

    private bool _open;
    private bool _emitted;
    private int _utc = -1;
    private EpochMembers _members;
    private bool _isGns;
    private EpochMemberData _position;
    private EpochMemberData _vtg;
    private EpochMemberData _receiverHeading;
    private EpochMemberData _imuHeading;
    private bool _hasReceiverHeading;
    private bool _hasImuHeading;

    private EpochMembers _learned;
    private EpochMembers _candidate;
    private int _candidateCount;

    /// <summary>Whether the receiver's per-epoch sentence set has been learned.</summary>
    public bool IsLearned => _learned != EpochMembers.None;

    /// <summary>The learned set, <see cref="EpochMembers.None"/> during warm-up.</summary>
    public EpochMembers LearnedBurst => _learned;

    /// <summary>The learned set as the receiver prints it, e.g. "GGA+VTG+HPR"; empty during warm-up.</summary>
    public string FamilyText { get; private set; } = "";

    /// <summary>Fixes emitted.</summary>
    public long EpochsEmitted { get; private set; }

    /// <summary>Epochs closed by the next epoch's start with a learned member missing (a lost datagram).</summary>
    public long IncompleteEpochs { get; private set; }

    /// <summary>Epochs that never got a position sentence: nothing to emit.</summary>
    public long DroppedEpochs { get; private set; }

    /// <summary>
    /// Take one decoded sentence. Returns true when a fix was written into
    /// <paramref name="state"/>: the epoch it completed, or the previous epoch this sentence
    /// closed. At most one fix per call.
    /// </summary>
    public bool Add(in EpochMemberData m, ref VehicleState state, ConfigurationStore configStore)
    {
        bool emitted = false;
        bool startsNew = !_open
            || (m.UtcCentiseconds >= 0 && _utc >= 0 && m.UtcCentiseconds != _utc)
            || (_members & m.Kind) != 0;

        if (_open && startsNew)
            emitted = Close(ref state, configStore);
        if (startsNew)
            Open();
        if (m.UtcCentiseconds >= 0 && _utc < 0)
            _utc = m.UtcCentiseconds;

        Store(in m);

        // Complete: every learned member is here. One fix per call, so an epoch that
        // completes in the same call that closed its predecessor waits for the next
        // sentence or the next start (only on the call that learned the set).
        if (!emitted && !_emitted && IsLearned
            && (_members & _learned) == _learned && (_members & EpochMembers.Position) != 0)
        {
            Emit(ref state, configStore);
            emitted = true;
        }
        return emitted;
    }

    private void Open()
    {
        _open = true;
        _emitted = false;
        _utc = -1;
        _members = EpochMembers.None;
        _hasReceiverHeading = false;
        _hasImuHeading = false;
    }

    private void Store(in EpochMemberData m)
    {
        _members |= m.Kind;
        switch (m.Kind)
        {
            case EpochMembers.Position:
                _position = m;
                _isGns = m.IsGns;
                break;
            case EpochMembers.Vtg:
                _vtg = m;
                break;
            default:
                if (m.FromImuTalker) { _imuHeading = m; _hasImuHeading = true; }
                else { _receiverHeading = m; _hasReceiverHeading = true; }
                break;
        }
    }

    /// <summary>The open epoch is over: emit it if it has a position and was not emitted yet; learn from it.</summary>
    private bool Close(ref VehicleState state, ConfigurationStore configStore)
    {
        _open = false;
        if ((_members & EpochMembers.Position) == 0)
        {
            DroppedEpochs++;
            return false;
        }

        bool emitted = false;
        if (!_emitted)
        {
            if (IsLearned && (_members & _learned) != _learned) IncompleteEpochs++;
            Emit(ref state, configStore);
            emitted = true;
        }
        Learn(_members);
        return emitted;
    }

    private void Learn(EpochMembers observed)
    {
        if (observed == _learned)
        {
            _candidateCount = 0;
            return;
        }
        if (observed == _candidate)
        {
            if (++_candidateCount >= EpochsToLearn)
            {
                _learned = observed;
                _candidateCount = 0;
                FamilyText = Describe(observed, _isGns);
            }
        }
        else
        {
            _candidate = observed;
            _candidateCount = 1;
        }
    }

    private void Emit(ref VehicleState state, ConfigurationStore configStore)
    {
        _emitted = true;
        EpochsEmitted++;

        state.SentenceType = GpsSentenceType.NmeaEpoch;
        state.Latitude = _position.Latitude;
        state.Longitude = _position.Longitude;
        state.Altitude = _position.Altitude;
        state.FixQuality = _position.FixQuality;
        state.Satellites = _position.Satellites;
        state.Hdop = _position.Hdop;
        state.DifferentialAge = _position.DifferentialAge;

        bool hasVtg = (_members & EpochMembers.Vtg) != 0;
        state.Speed = hasVtg ? _vtg.SpeedMps : 0;

        // Heading: the receiver's own (dual antenna) when it printed one and it is valid;
        // an IMU talker's goes to the IMU slot like $PANDA's; else VTG's track, which the
        // fusion only uses through fix-to-fix (HasDualHeading false).
        bool receiverHeading = _hasReceiverHeading && _receiverHeading.HeadingValid;
        bool imuHeading = _hasImuHeading && _imuHeading.HeadingValid;
        state.HasDualHeading = receiverHeading;
        state.ImuValid = imuHeading;
        state.ImuHeading = imuHeading ? _imuHeading.HeadingDeg : 0;
        state.Heading = receiverHeading ? _receiverHeading.HeadingDeg
            : imuHeading ? _imuHeading.HeadingDeg
            : hasVtg ? _vtg.TrackDeg
            : 0;

        // Roll only from a fixed heading solution; otherwise it is noise (as $KSXT).
        state.Roll = 0;
        if (receiverHeading && _receiverHeading.RollValid)
        {
            state.Roll = _receiverHeading.RollDeg;
            NmeaParserServiceFast.ApplyAhrsRollCalibration(ref state.Roll, configStore);
        }
        else if (imuHeading && _imuHeading.RollValid)
        {
            state.Roll = _imuHeading.RollDeg;
            NmeaParserServiceFast.ApplyAhrsRollCalibration(ref state.Roll, configStore);
        }
        state.Pitch = 0;
        state.YawRate = 0;
    }

    /// <summary>"GGA+VTG+HPR" in the fixed order position, VTG, HPR, HDT, THS.</summary>
    public static string Describe(EpochMembers set, bool gns = false)
    {
        var parts = new System.Collections.Generic.List<string>(5);
        if ((set & EpochMembers.Position) != 0) parts.Add(gns ? "GNS" : "GGA");
        if ((set & EpochMembers.Vtg) != 0) parts.Add("VTG");
        if ((set & EpochMembers.Hpr) != 0) parts.Add("HPR");
        if ((set & EpochMembers.Hdt) != 0) parts.Add("HDT");
        if ((set & EpochMembers.Ths) != 0) parts.Add("THS");
        return string.Join("+", parts);
    }
}
