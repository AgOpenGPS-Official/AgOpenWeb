// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Timing;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Sprayer;
using NSubstitute;

namespace AgOpenWeb.Services.Tests.Sprayer;

/// <summary>The sprayer readout: PGN 160 from the machine module, checked against the
/// nozzles of the open sections.</summary>
[TestFixture]
[NonParallelizable] // sets the global clock
public class SprayerServiceTests
{
    private IUdpCommunicationService _udp = null!;
    private IAudioService _audio = null!;
    private ConfigurationStore _config = null!;
    private SprayerService _service = null!;
    private TestClock _clock = null!;

    // An 8 m boom: sections of 2, 1, 2, 1 and 2 m, nozzles every 0.5 m = 4, 2, 4, 2, 4 nozzles.
    private static readonly double[] WidthsCm = { 200, 100, 200, 100, 200 };

    [SetUp]
    public void SetUp()
    {
        _clock = new TestClock(new DateTime(2026, 10, 21, 9, 0, 0));
        Clock.Set(_clock);
        _udp = Substitute.For<IUdpCommunicationService>();
        _audio = Substitute.For<IAudioService>();
        _config = new ConfigurationStore();
        for (int i = 0; i < WidthsCm.Length; i++) _config.Tool.SetSectionWidth(i, WidthsCm[i]);
        _config.Machine.SprayerNozzleSpacing = 0.5;
        _config.Machine.SprayerNozzleFlow = 1.6;
        _config.Machine.SprayerReferencePressure = 3.0;
        _config.Machine.SprayerMinPressure = 1.0;
        _config.Machine.SprayerMaxPressure = 6.0;
        _config.Machine.SprayerFlowTolerance = 20;
        _service = new SprayerService(_udp, _config, _audio);
    }

    [TearDown]
    public void TearDown() => Clock.Reset();

    // PGN 160 as Pulverizador_Nano builds it (CRC = sum of source..last payload byte).
    private static byte[] Frame(double bar, double lpm, double litres = 0, bool master = true, int sections = 0)
    {
        ushort p = (ushort)Math.Round(bar * 100), c = (ushort)Math.Round(lpm * 100);
        uint t = (uint)Math.Round(litres * 10);
        var f = new byte[]
        {
            0x80, 0x81, 123, 0xA0, 10,
            (byte)p, (byte)(p >> 8), (byte)c, (byte)(c >> 8),
            (byte)t, (byte)(t >> 8), (byte)(t >> 16), (byte)(t >> 24),
            (byte)(master ? 1 : 0), (byte)sections, 0,
        };
        int sum = 0;
        for (int i = 2; i < f.Length - 1; i++) sum += f[i];
        f[^1] = (byte)sum;
        return f;
    }

    private void Receive(byte[] frame) =>
        _udp.DataReceived += Raise.EventWith(new UdpDataReceivedEventArgs { Data = frame, PGN = frame[3] });

    // One message a second, as the module sends them.
    private void Seconds(int n, double bar, double lpm, int sections)
    {
        for (int i = 0; i < n; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            Receive(Frame(bar, lpm, sections: sections));
        }
    }

    [Test]
    public void BeforeAnyMessage_NoStatus()
    {
        Assert.That(_service.Status, Is.Null);
    }

    [Test]
    public void Message_IsDecoded()
    {
        Receive(Frame(3.25, 14.5, litres: 123.4, master: true, sections: 0b10101));

        var s = _service.Status!.Value;
        Assert.That(s.PressureBar, Is.EqualTo(3.25).Within(1e-9));
        Assert.That(s.FlowLpm, Is.EqualTo(14.5).Within(1e-9));
        Assert.That(s.TotalLitres, Is.EqualTo(123.4).Within(1e-9));
        Assert.That(s.MasterOn, Is.True);
        Assert.That(s.Sections, Is.EqualTo(0b10101));
    }

    [Test]
    public void OpenSections_GiveTheirNozzlesAndWidth()
    {
        Receive(Frame(3.0, 0, sections: 0b11111));
        Assert.That(_service.Status!.Value.OpenNozzles, Is.EqualTo(16));
        Assert.That(_service.Status!.Value.OpenWidthM, Is.EqualTo(8.0).Within(1e-9));

        Receive(Frame(3.0, 0, sections: 0b01010)); // sections 2 and 4: 2 + 2 nozzles
        Assert.That(_service.Status!.Value.OpenNozzles, Is.EqualTo(4));
        Assert.That(_service.Status!.Value.OpenWidthM, Is.EqualTo(2.0).Within(1e-9));
    }

    [Test]
    public void ExpectedFlow_FollowsTheOpenNozzlesAndThePressure()
    {
        Receive(Frame(3.0, 0, sections: 0b00001)); // 4 nozzles at the chart pressure
        Assert.That(_service.Status!.Value.ExpectedFlowLpm, Is.EqualTo(6.4).Within(1e-9));

        Receive(Frame(12.0, 0, sections: 0b11111)); // 16 nozzles, 4× the pressure = 2× the flow
        Assert.That(_service.Status!.Value.ExpectedFlowLpm, Is.EqualTo(16 * 1.6 * 2).Within(1e-9));
    }

    [Test]
    public void FlowAsExpected_NoAlarm()
    {
        Seconds(10, 3.0, 25.6, 0b11111);
        Assert.That(_service.Status!.Value.Alarms, Is.EqualTo(SprayerAlarms.None));
        _audio.DidNotReceive().Play(Arg.Any<SoundEffect>());
    }

    [Test]
    public void FlowLow_AlarmsAfterThreeMessages_AndSounds()
    {
        Seconds(5, 3.0, 25.6, 0b11111);                 // settled, all fine
        Seconds(2, 3.0, 12.0, 0b11111);                 // a blocked filter: half the flow
        Assert.That(_service.Status!.Value.Alarms, Is.EqualTo(SprayerAlarms.None), "not yet: 2 messages");
        Seconds(1, 3.0, 12.0, 0b11111);
        Assert.That(_service.Status!.Value.Alarms, Is.EqualTo(SprayerAlarms.FlowLow));
        _audio.Received(1).Play(SoundEffect.SprayerAlarm);
    }

    [Test]
    public void FlowHigh_Alarms()
    {
        Seconds(6, 3.0, 40.0, 0b11111); // a nozzle gone, or a burst hose
        Assert.That(_service.Status!.Value.Alarms, Is.EqualTo(SprayerAlarms.FlowHigh));
    }

    [Test]
    public void SectionChange_FlowCheckWaitsForTheValves()
    {
        Seconds(5, 3.0, 25.6, 0b11111);
        // Sections 1 and 2 close: 10 nozzles left (16 L/min), but the flow meter still says
        // 25.6 for a moment. No alarm while it catches up.
        Seconds(3, 3.0, 25.6, 0b11100);
        Assert.That(_service.Status!.Value.Alarms, Is.EqualTo(SprayerAlarms.None));
        Seconds(3, 3.0, 16.0, 0b11100);
        Assert.That(_service.Status!.Value.Alarms, Is.EqualTo(SprayerAlarms.None));
    }

    [Test]
    public void AlarmLimit_FollowsTheOpenSections()
    {
        // 16 L/min is right for 10 nozzles but far too little for 16.
        Seconds(6, 3.0, 16.0, 0b11100);
        Assert.That(_service.Status!.Value.Alarms, Is.EqualTo(SprayerAlarms.None));
        Seconds(6, 3.0, 16.0, 0b11111);
        Assert.That(_service.Status!.Value.Alarms, Is.EqualTo(SprayerAlarms.FlowLow));
    }

    [Test]
    public void PressureLow_OnlyWhileSpraying()
    {
        Seconds(5, 0.2, 0, 0);
        Assert.That(_service.Status!.Value.Alarms, Is.EqualTo(SprayerAlarms.None), "pump off, sections off");
        Seconds(5, 0.2, 0, 0b11111);
        Assert.That(_service.Status!.Value.Alarms & SprayerAlarms.PressureLow, Is.EqualTo(SprayerAlarms.PressureLow));
    }

    [Test]
    public void PressureHigh_Alarms()
    {
        Seconds(3, 7.0, 0, 0);
        Assert.That(_service.Status!.Value.Alarms, Is.EqualTo(SprayerAlarms.PressureHigh));
    }

    [Test]
    public void FlowWithSectionsOff_Alarms()
    {
        Seconds(6, 3.0, 6.4, 0); // a section switched to manual, or a valve stuck open
        Assert.That(_service.Status!.Value.Alarms, Is.EqualTo(SprayerAlarms.FlowSectionsOff));
    }

    [Test]
    public void NozzleFlowZero_NoFlowCheck()
    {
        _config.Machine.SprayerNozzleFlow = 0;
        Seconds(6, 3.0, 2.0, 0b11111);
        Assert.That(_service.Status!.Value.Alarms, Is.EqualTo(SprayerAlarms.None));
        Assert.That(double.IsNaN(_service.Status!.Value.ExpectedFlowLpm), Is.True);
    }

    [Test]
    public void AlarmSoundsAgain_EveryTenSeconds()
    {
        Seconds(3, 7.0, 0, 0);
        _audio.Received(1).Play(SoundEffect.SprayerAlarm);
        Seconds(9, 7.0, 0, 0);
        _audio.Received(1).Play(SoundEffect.SprayerAlarm);
        Seconds(1, 7.0, 0, 0);
        _audio.Received(2).Play(SoundEffect.SprayerAlarm);
    }

    [Test]
    public void Silence_ReportsNoData()
    {
        Seconds(1, 3.0, 0, 0);
        _clock.Advance(TimeSpan.FromSeconds(4));
        Assert.That(_service.Status!.Value.Alarms, Is.EqualTo(SprayerAlarms.NoData));
    }

    [Test]
    public void OtherSourceOrShortFrame_Ignored()
    {
        var f = Frame(3.0, 10);
        f[2] = 126;
        Receive(f);
        Receive(Frame(3.0, 10)[..10]);
        Assert.That(_service.Status, Is.Null);
    }
}
