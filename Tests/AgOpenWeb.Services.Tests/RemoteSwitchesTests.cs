// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.AutoSteer;
using AgOpenWeb.Services.Interfaces;
using NSubstitute;
using static AgOpenWeb.Services.ModuleCommunicationService;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// PGN 234 remote section switches, as AgOpenGPS DoRemoteSwitches → HandleButtonHardware:
/// the main byte works on a change; an On bit turns its section On; an Off bit holds it Off;
/// an Off bit released with the master on Auto puts an Off section back on Auto. A rate
/// controller sends these (Rate Control's SyncAOG*).
/// </summary>
[TestFixture]
public class RemoteSwitchDecisionTests
{
    private ConfigurationStore _store = null!;
    private ModuleCommunicationService _mc = null!;
    private List<SectionControlState> _sections = null!;

    [SetUp]
    public void SetUp()
    {
        _store = new ConfigurationStore();
        _store.NumSections = 4;
        _mc = new ModuleCommunicationService(_store);
        _sections = Enumerable.Range(0, 64).Select(i => new SectionControlState { Index = i }).ToList();
    }

    private static byte[] Switches(byte main = 0, byte onLo = 0, byte offLo = 0, byte onHi = 0, byte offHi = 0)
    {
        var ss = new byte[9];
        ss[SW_MAIN] = main;
        ss[SW_ON_GR0] = onLo;
        ss[SW_OFF_GR0] = offLo;
        ss[SW_ON_GR1] = onHi;
        ss[SW_OFF_GR1] = offHi;
        return ss;
    }

    private RemoteSwitchDecision Check(byte[] ss, bool masterAuto)
    {
        var d = _mc.CheckRemoteSwitches(ss, masterAuto, _sections);
        for (int i = 0; i < d.Sections.Length; i++) _sections[i].ButtonState = d.Sections[i];
        return d;
    }

    [Test]
    public void AutoOn_SetsMasterAndEverySectionAuto_OnTheChangeOnly()
    {
        var d = Check(Switches(main: 1), masterAuto: false);

        Assert.That(d.Master, Is.EqualTo(RemoteMasterCommand.AutoOn));
        Assert.That(d.Sections, Has.Length.EqualTo(4));
        Assert.That(d.Sections, Is.All.EqualTo(SectionButtonState.Auto));

        Assert.That(Check(Switches(main: 1), masterAuto: true).Master, Is.EqualTo(RemoteMasterCommand.None),
            "the same main byte again does nothing");
    }

    [Test]
    public void AutoOff_SetsMasterOffAndEverySectionOff()
    {
        _sections[0].ButtonState = SectionButtonState.Auto;

        var d = Check(Switches(main: 2), masterAuto: true);

        Assert.That(d.Master, Is.EqualTo(RemoteMasterCommand.AutoOff));
        Assert.That(d.Sections, Is.All.EqualTo(SectionButtonState.Off));
    }

    [Test]
    public void OnBit_TurnsTheSectionOn()
    {
        var d = Check(Switches(onLo: 0b0101), masterAuto: false);

        Assert.That(d.Sections, Is.EqualTo(new[] {
            SectionButtonState.On, SectionButtonState.Off, SectionButtonState.On, SectionButtonState.Off }));
    }

    [Test]
    public void OffBit_HoldsTheSectionOff()
    {
        foreach (var s in _sections) s.ButtonState = SectionButtonState.Auto;

        var d = Check(Switches(offLo: 0b0010), masterAuto: true);

        Assert.That(d.Sections[1], Is.EqualTo(SectionButtonState.Off));
        Assert.That(d.Sections[0], Is.EqualTo(SectionButtonState.Auto));
    }

    [Test]
    public void OffReleased_WithMasterAuto_BackToAuto()
    {
        Check(Switches(offLo: 0b0010), masterAuto: true);

        var d = Check(Switches(offLo: 0), masterAuto: true);

        Assert.That(d.Sections[1], Is.EqualTo(SectionButtonState.Auto));
    }

    [Test]
    public void OffReleased_WithMasterOff_StaysOff()
    {
        Check(Switches(offLo: 0b0010), masterAuto: false);

        var d = Check(Switches(offLo: 0), masterAuto: false);

        Assert.That(d.Sections[1], Is.EqualTo(SectionButtonState.Off));
    }

    [Test]
    public void BitsPastTheLastSection_AreIgnored()
    {
        var d = Check(Switches(onLo: 0xFF, onHi: 0xFF), masterAuto: false);

        Assert.That(d.Sections, Has.Length.EqualTo(4));
        Assert.That(d.Sections, Is.All.EqualTo(SectionButtonState.On));
    }

    [Test]
    public void Group1_IsSections9To16()
    {
        _store.NumSections = 12;

        var d = Check(Switches(onHi: 0b0001, offHi: 0b0010), masterAuto: false);

        Assert.That(d.Sections[8], Is.EqualTo(SectionButtonState.On));
        Assert.That(d.Sections[9], Is.EqualTo(SectionButtonState.Off));
    }

    [Test]
    public void SwitchHardware_ChangesNothing()
    {
        // Main bit 2 = hagre's level-driven switch box, not handled.
        var d = Check(Switches(main: 0b101, onLo: 0xFF), masterAuto: false);

        Assert.That(d.Master, Is.EqualTo(RemoteMasterCommand.None));
        Assert.That(d.Sections, Is.All.EqualTo(SectionButtonState.Off));
    }

    [Test]
    public void RateController_MasterOnWithAutoSectionOn_FollowsCoverage()
    {
        // Rate Control: Auto Section on, sections 1, 2 and 4 switched on, 3 off. It sends
        // auto on once, and the off switches in every packet.
        Check(Switches(main: 1, offLo: 0b0100), masterAuto: false);
        var d = Check(Switches(offLo: 0b0100), masterAuto: true);

        Assert.That(d.Sections, Is.EqualTo(new[] {
            SectionButtonState.Auto, SectionButtonState.Auto, SectionButtonState.Off, SectionButtonState.Auto }));
    }

    [Test]
    public void Zones_GroupBitsAreZones()
    {
        // Two zones: 1 = sections 1-2, 2 = sections 3-4.
        _store.Tool.IsSectionsNotZones = false;
        _store.Tool.Zones = 2;
        _store.Tool.SetZoneEndSection(1, 2);
        _store.Tool.SetZoneEndSection(2, 4);

        var d = Check(Switches(onLo: 0b01, offLo: 0b10), masterAuto: false);

        Assert.That(d.Sections, Is.EqualTo(new[] {
            SectionButtonState.On, SectionButtonState.On, SectionButtonState.Off, SectionButtonState.Off }));
    }

    [Test]
    public void Zones_OffReleased_WithMasterAuto_ZoneBackToAuto()
    {
        _store.Tool.IsSectionsNotZones = false;
        _store.Tool.Zones = 2;
        _store.Tool.SetZoneEndSection(1, 2);
        _store.Tool.SetZoneEndSection(2, 4);

        Check(Switches(offLo: 0b10), masterAuto: true);
        var d = Check(Switches(offLo: 0), masterAuto: true);

        Assert.That(d.Sections[2], Is.EqualTo(SectionButtonState.Auto));
        Assert.That(d.Sections[3], Is.EqualTo(SectionButtonState.Auto));
    }
}

/// <summary>PGN 234 from the module port reaches RemoteSwitchesReceived, the 14-byte packet only.</summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class RemoteSwitchReceiveTests
{
    private IUdpCommunicationService _udp = null!;
    private AutoSteerService _service = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _udp = Substitute.For<IUdpCommunicationService>();
        _service = new AutoSteerService(Substitute.For<ITrackGuidanceService>(), _udp,
            Substitute.For<IGpsService>(), ConfigurationStore.Instance);
        _service.Start();
    }

    [TearDown]
    public void TearDown() => _service.Stop();

    private void Receive(byte[] data) =>
        _udp.DataReceived += Raise.EventWith(_udp, new UdpDataReceivedEventArgs { Data = data, PGN = data[3] });

    [Test]
    public void SwitchBytes_AsAgOpenGpsMcSs()
    {
        byte[]? got = null;
        _service.RemoteSwitchesReceived += ss => got = ss;

        Receive(new byte[] { 0x80, 0x81, 0x7F, 234, 8, 1, 0, 0, 4, 0x05, 0x02, 0x10, 0x20, 0 });

        Assert.That(got, Is.Not.Null);
        Assert.That(got![SW_MAIN], Is.EqualTo(1));
        Assert.That(got[SW_NUM_SECTIONS], Is.EqualTo(4));
        Assert.That(got[SW_ON_GR0], Is.EqualTo(0x05));
        Assert.That(got[SW_OFF_GR0], Is.EqualTo(0x02));
        Assert.That(got[SW_ON_GR1], Is.EqualTo(0x10));
        Assert.That(got[SW_OFF_GR1], Is.EqualTo(0x20));
    }

    [Test]
    public void WrongLength_IsIgnored()
    {
        bool got = false;
        _service.RemoteSwitchesReceived += _ => got = true;

        Receive(new byte[] { 0x80, 0x81, 0x7F, 234, 8, 1, 0, 0, 4, 0, 0, 0, 0 });

        Assert.That(got, Is.False);
    }
}

/// <summary>
/// A rate controller beside the app sends PGN 234 to AgOpenGPS's loopback port 15555. The
/// app listens there for 234 only: its own PGN 214 for GPS_Out goes to the same port.
/// </summary>
[TestFixture]
[NonParallelizable] // real sockets on fixed ports
public class RemoteSwitchLoopbackPortTests
{
    private sealed class Loopback : ILocalNetworkInfoProvider
    {
        public IReadOnlyList<LocalNetworkAddress> GetIPv4Addresses() =>
            new[] { new LocalNetworkAddress(IPAddress.Loopback, 8, "lo0") };
    }

    [Test]
    public async Task Pgn234_OnPort15555_IsReceived_OtherPgnsAreNot()
    {
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Bind(new IPEndPoint(IPAddress.Any, UdpCommunicationService.ModulePort));
        }
        catch (SocketException)
        {
            Assert.Ignore("port 9999 is held by another process on this machine");
        }

        var udp = new UdpCommunicationService(new Loopback());
        var pgns = new List<byte>();
        udp.DataReceived += (_, e) => { lock (pgns) pgns.Add(e.PGN); };
        await udp.StartAsync();
        try
        {
            using var sender = new UdpClient();
            var to = new IPEndPoint(IPAddress.Loopback, UdpCommunicationService.AgOpenGpsLoopbackPort);
            var gpsData = new byte[] { 0x80, 0x81, 0x7C, 0xD6, 1, 0, 0 };
            var switches = new byte[] { 0x80, 0x81, 0x7F, 234, 8, 1, 0, 0, 0, 0, 0, 0, 0, 0 };
            for (int attempt = 0; attempt < 20; attempt++)
            {
                sender.Send(gpsData, gpsData.Length, to);
                sender.Send(switches, switches.Length, to);
                await Task.Delay(50);
                lock (pgns) if (pgns.Contains(234)) break;
            }

            lock (pgns)
            {
                Assert.That(pgns, Does.Contain((byte)234));
                Assert.That(pgns, Does.Not.Contain((byte)0xD6));
            }
        }
        finally
        {
            await udp.StopAsync();
            udp.Dispose();
        }
    }
}
