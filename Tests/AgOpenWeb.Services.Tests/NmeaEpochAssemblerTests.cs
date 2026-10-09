// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.GPS;
using AgOpenWeb.Services.Gps;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// One fix per receiver epoch from GGA/GNS + VTG + HPR/HDT/THS
/// (Plans/GPS_RECEIVER_SENTENCES_PLAN.md, Phase 2): the burst is learned, the fix is
/// emitted when the burst is complete, a missing member is marked absent, and the fix
/// holds only what the receiver measured in that epoch.
/// </summary>
[TestFixture]
public class NmeaEpochAssemblerTests
{
    private NmeaEpochAssembler _epochs = null!;
    private ConfigurationStore _config = null!;
    private VehicleState _state;

    [SetUp]
    public void SetUp()
    {
        _epochs = new NmeaEpochAssembler();
        _config = new ConfigurationStore();
        _state = new VehicleState();
    }

    internal static byte[] Line(string body)
    {
        byte checksum = 0;
        foreach (char c in body) checksum ^= (byte)c;
        return Encoding.ASCII.GetBytes($"${body}*{checksum:X2}");
    }

    private static string Utc(int epoch) => (123456.00 + epoch * 0.10).ToString("000000.00", CultureInfo.InvariantCulture);

    internal static string Gga(int epoch, double latDdmm = 4807.038, int fix = 4, int sats = 12, double hdop = 0.9, double age = 1.5, string talker = "GN") =>
        string.Format(CultureInfo.InvariantCulture, "{0}GGA,{1},{2:0000.000},N,01131.000,E,{3},{4},{5},545.4,M,46.9,M,{6},0000", talker, Utc(epoch), latDdmm, fix, sats, hdop, age);
    internal static string Vtg(double track = 54.7, double kmh = 10.8) =>
        string.Format(CultureInfo.InvariantCulture, "GNVTG,{0},T,,M,{1:0.0},N,{2},K,D", track, kmh / 1.852, kmh);
    internal static string Hpr(int epoch, double heading = 320.961, double pitch = -1.2345, int qf = 4, string talker = "GN") =>
        string.Format(CultureInfo.InvariantCulture, "{0}HPR,{1},{2},{3},000.0000,{4},47,0.00,0999", talker, Utc(epoch), heading, pitch, qf);

    private NmeaParseResult Feed(string body)
    {
        NmeaParserServiceFast.TryParseIntoState(Line(body), ref _state, _config, _epochs, out var result);
        return result;
    }

    /// <summary>Three full epochs, which is what it takes to learn the burst.</summary>
    private void WarmUp(int firstEpoch = 0)
    {
        for (int e = firstEpoch; e < firstEpoch + 3; e++) { Feed(Gga(e)); Feed(Vtg()); Feed(Hpr(e)); }
    }

    [Test]
    public void Members_are_taken_and_the_first_fixes_come_one_epoch_late()
    {
        Assert.That(Feed(Gga(0)), Is.EqualTo(NmeaParseResult.EpochMember));
        Assert.That(Feed(Vtg()), Is.EqualTo(NmeaParseResult.EpochMember));
        Assert.That(Feed(Hpr(0)), Is.EqualTo(NmeaParseResult.EpochMember), "not learned yet: nothing says the burst is over");
        Assert.That(Feed(Gga(1)), Is.EqualTo(NmeaParseResult.Accepted), "the next epoch's start closes the first");
        Assert.That(_state.SentenceType, Is.EqualTo(GpsSentenceType.NmeaEpoch));
        Assert.That(_state.Latitude, Is.EqualTo(48.11730).Within(1e-5));
        Assert.That(_state.Longitude, Is.EqualTo(11.51667).Within(1e-5));
        Assert.That(_state.Speed, Is.EqualTo(3.0).Within(1e-9), "10.8 km/h");
        Assert.That(_state.Heading, Is.EqualTo(320.961));
        Assert.That(_state.HasDualHeading, Is.True);
        Assert.That(_state.Roll, Is.EqualTo(-1.2345), "the baseline pitch is the vehicle's roll");
        Assert.That(_state.FixQuality, Is.EqualTo(4));
        Assert.That(_state.Satellites, Is.EqualTo(12));
        Assert.That(_state.Hdop, Is.EqualTo(0.9));
        Assert.That(_state.DifferentialAge, Is.EqualTo(1.5), "the age $KSXT lacks");
        Assert.That(_epochs.IsLearned, Is.False);
    }

    [Test]
    public void After_three_alike_epochs_the_burst_is_learned_and_the_fix_comes_on_its_last_member()
    {
        WarmUp();
        Assert.That(_epochs.IsLearned, Is.False, "the third epoch is still open");
        Assert.That(Feed(Gga(3)), Is.EqualTo(NmeaParseResult.Accepted), "closes the third, which completes the learning");
        Assert.That(_epochs.IsLearned, Is.True);
        Assert.That(_epochs.FamilyText, Is.EqualTo("GGA+VTG+HPR"));

        Assert.That(Feed(Vtg()), Is.EqualTo(NmeaParseResult.EpochMember));
        Assert.That(Feed(Hpr(3, heading: 100.5)), Is.EqualTo(NmeaParseResult.Accepted), "the last learned member completes the epoch");
        Assert.That(_state.Heading, Is.EqualTo(100.5));
        Assert.That(Feed(Gga(4)), Is.EqualTo(NmeaParseResult.EpochMember), "epoch 3 was already emitted; the new one is open");
    }

    [Test]
    public void A_member_that_never_comes_is_marked_absent_and_counted()
    {
        WarmUp(); Feed(Gga(3)); Feed(Vtg()); Feed(Hpr(3));
        Assert.That(_epochs.IsLearned, Is.True);

        Feed(Gga(4)); Feed(Vtg()); // the HPR datagram was lost
        Assert.That(Feed(Gga(5)), Is.EqualTo(NmeaParseResult.Accepted), "the next start closes epoch 4 as it is");
        Assert.That(_state.HasDualHeading, Is.False, "no heading sentence this epoch");
        Assert.That(_state.Roll, Is.Zero);
        Assert.That(_state.Heading, Is.EqualTo(54.7), "VTG's track, for the log; the fusion ignores it without a dual flag");
        Assert.That(_state.Speed, Is.EqualTo(3.0).Within(1e-9));
        Assert.That(_epochs.IncompleteEpochs, Is.EqualTo(1));
    }

    [Test]
    public void Speed_is_not_carried_over_when_VTG_is_missing()
    {
        WarmUp(); Feed(Gga(3)); Feed(Vtg()); Feed(Hpr(3));
        Feed(Gga(4)); Feed(Hpr(4));
        Assert.That(Feed(Gga(5)), Is.EqualTo(NmeaParseResult.Accepted));
        Assert.That(_state.Speed, Is.Zero, "the previous epoch's 3 m/s is not this epoch's");
        Assert.That(_state.HasDualHeading, Is.True);
    }

    [Test]
    public void A_receiver_reconfigured_to_a_smaller_set_is_relearned()
    {
        WarmUp(); Feed(Gga(3)); Feed(Vtg()); Feed(Hpr(3));
        // HPR switched off: three epochs of GGA+VTG, each closed incomplete by the next.
        for (int e = 4; e < 7; e++) { Feed(Gga(e)); Feed(Vtg()); }
        Assert.That(Feed(Gga(7)), Is.EqualTo(NmeaParseResult.Accepted));
        Assert.That(_epochs.FamilyText, Is.EqualTo("GGA+VTG"));
        Assert.That(_epochs.IncompleteEpochs, Is.EqualTo(3));
        Assert.That(Feed(Vtg()), Is.EqualTo(NmeaParseResult.Accepted), "VTG now completes the epoch");
    }

    [Test]
    public void A_member_that_starts_coming_is_learned_too()
    {
        for (int e = 0; e < 3; e++) { Feed(Gga(e)); Feed(Vtg()); }
        Feed(Gga(3));
        Assert.That(_epochs.FamilyText, Is.EqualTo("GGA+VTG"));
        Assert.That(Feed(Vtg()), Is.EqualTo(NmeaParseResult.Accepted));
        // HPR switched on: it arrives after the epoch was emitted and is learned from there.
        Assert.That(Feed(Hpr(3)), Is.EqualTo(NmeaParseResult.EpochMember), "late for this epoch");
        for (int e = 4; e < 7; e++) { Feed(Gga(e)); Feed(Vtg()); Feed(Hpr(e)); }
        Feed(Gga(7));
        Assert.That(_epochs.FamilyText, Is.EqualTo("GGA+VTG+HPR"));
        Feed(Vtg());
        Assert.That(Feed(Hpr(7, heading: 12.5)), Is.EqualTo(NmeaParseResult.Accepted));
        Assert.That(_state.HasDualHeading, Is.True);
        Assert.That(_state.Heading, Is.EqualTo(12.5));
    }

    [Test]
    public void HPR_roll_needs_a_fixed_heading_and_heading_needs_fixed_or_float()
    {
        Feed(Gga(0)); Feed(Hpr(0, qf: 5)); Feed(Gga(1));
        Assert.That(_state.HasDualHeading, Is.True, "float heading is a heading");
        Assert.That(_state.Roll, Is.Zero, "float roll is noise");
        Feed(Hpr(1, qf: 0)); Feed(Gga(2));
        Assert.That(_state.HasDualHeading, Is.False, "QF 0 is no heading");
    }

    [Test]
    public void Roll_goes_through_the_AHRS_calibration()
    {
        _config.Ahrs.IsRollInvert = true;
        _config.Ahrs.RollZero = 0.5;
        Feed(Gga(0)); Feed(Hpr(0, pitch: 2.0)); Feed(Gga(1));
        Assert.That(_state.Roll, Is.EqualTo(-2.5).Within(1e-9));
    }

    [Test]
    public void An_IMU_talker_heading_goes_to_the_IMU_slot()
    {
        Feed(Gga(0)); Feed(Hpr(0, heading: 200.0, talker: "IN")); Feed(Gga(1));
        Assert.That(_state.ImuValid, Is.True);
        Assert.That(_state.ImuHeading, Is.EqualTo(200.0));
        Assert.That(_state.HasDualHeading, Is.False);
        Assert.That(_state.Heading, Is.EqualTo(200.0), "seeded from the IMU, as $PANDA");
    }

    [Test]
    public void HDT_and_THS_are_headings_and_THS_V_is_not()
    {
        Feed(Gga(0)); Feed("GPHDT,275.5,T"); Feed(Gga(1));
        Assert.That(_state.HasDualHeading, Is.True);
        Assert.That(_state.Heading, Is.EqualTo(275.5));
        Feed("GPTHS,10.0,V"); Feed(Gga(2));
        Assert.That(_state.HasDualHeading, Is.False);
        Feed("GPTHS,11.0,A"); Feed(Gga(3));
        Assert.That(_state.HasDualHeading, Is.True);
        Assert.That(_state.Heading, Is.EqualTo(11.0));
    }

    [TestCase("RR", 4)]
    [TestCase("FA", 5)]
    [TestCase("DD", 2)]
    [TestCase("AN", 1)]
    [TestCase("NN", 0)]
    public void GNS_mode_letters_give_the_fix_on_the_GGA_scale(string mode, int expected)
    {
        Feed($"GNGNS,{Utc(0)},4807.038,N,01131.000,E,{mode},12,0.9,545.4,46.9,1.5,0000,V");
        Feed($"GNGNS,{Utc(1)},4807.038,N,01131.000,E,{mode},12,0.9,545.4,46.9,1.5,0000,V");
        Assert.That(_state.FixQuality, Is.EqualTo(expected));
        Assert.That(_state.DifferentialAge, Is.EqualTo(1.5));
    }

    [Test]
    public void A_different_UTC_or_a_repeated_kind_starts_a_new_epoch()
    {
        Feed(Gga(0)); Feed(Vtg());
        Assert.That(Feed(Hpr(1)), Is.EqualTo(NmeaParseResult.Accepted), "HPR of epoch 1 closes epoch 0");
        Assert.That(_state.HasDualHeading, Is.False, "epoch 0 had no HPR");
        Feed(Gga(1));
        Assert.That(Feed(Vtg()), Is.EqualTo(NmeaParseResult.EpochMember));
        Assert.That(Feed(Vtg(track: 99)), Is.EqualTo(NmeaParseResult.Accepted), "a second VTG means the epoch is over");
        Assert.That(_state.Heading, Is.EqualTo(320.961), "epoch 1 had HPR");
    }

    [Test]
    public void Members_printed_before_the_position_sentence_belong_to_its_epoch()
    {
        Feed(Vtg()); Feed("GPHDT,1.0,T");
        Assert.That(Feed(Gga(0)), Is.EqualTo(NmeaParseResult.EpochMember), "the epoch had no UTC yet; GGA gives it one");
        Assert.That(Feed(Gga(1)), Is.EqualTo(NmeaParseResult.Accepted));
        Assert.That(_state.Heading, Is.EqualTo(1.0));
        Assert.That(_state.Speed, Is.EqualTo(3.0).Within(1e-9));
        Assert.That(_epochs.DroppedEpochs, Is.Zero);
    }

    [Test]
    public void An_epoch_without_a_position_is_dropped_not_emitted()
    {
        Feed(Vtg()); Feed("GPHDT,1.0,T");
        Assert.That(Feed(Vtg()), Is.EqualTo(NmeaParseResult.EpochMember), "a second VTG ends the first epoch, which had no position");
        Assert.That(_epochs.DroppedEpochs, Is.EqualTo(1));
        Assert.That(_epochs.EpochsEmitted, Is.Zero);
    }

    [Test]
    public void Without_an_assembler_these_sentences_are_unknown()
    {
        var state = new VehicleState();
        NmeaParserServiceFast.TryParseIntoState(Line(Gga(0)), ref state, _config, out var result);
        Assert.That(result, Is.EqualTo(NmeaParseResult.UnknownSentence));
        Assert.That(NmeaParserServiceFast.ParseIntoState(Line(Gga(0)), ref state, _config), Is.False);
    }

    [Test]
    public void Describe_lists_the_members_in_the_receivers_order()
    {
        Assert.That(NmeaEpochAssembler.Describe(EpochMembers.Position | EpochMembers.Hdt | EpochMembers.Vtg), Is.EqualTo("GGA+VTG+HDT"));
        Assert.That(NmeaEpochAssembler.Describe(EpochMembers.Position, gns: true), Is.EqualTo("GNS"));
    }

    [Test]
    public void The_learned_epoch_path_allocates_nothing()
    {
        WarmUp(); Feed(Gga(3)); Feed(Vtg()); Feed(Hpr(3));
        var gga = Line(Gga(4)); var vtg = Line(Vtg()); var hpr = Line(Hpr(4));
        for (int i = 0; i < 100; i++) { Parse(gga); Parse(vtg); Parse(hpr); }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) { Parse(gga); Parse(vtg); Parse(hpr); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.That(allocated, Is.EqualTo(0), $"{allocated} bytes allocated over 1000 epochs");

        void Parse(byte[] line) => NmeaParserServiceFast.TryParseIntoState(line, ref _state, _config, _epochs, out _);
    }
}
