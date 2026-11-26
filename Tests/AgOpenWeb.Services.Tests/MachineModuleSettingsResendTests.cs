// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using System.Linq.Expressions;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.AutoSteer;
using AgOpenWeb.Services.Interfaces;
using NSubstitute;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// A machine module keeps PGN 235 (section widths), 236 (relay functions) and 238 (machine
/// config) in its flash and works with what it has. One switched on after AgOpenWeb (a new
/// board has 0 sections, so a sprayer module has no width for its L/ha), or back after a power
/// cut or a Wi-Fi drop with settings changed meanwhile, gets them again when its hello reply is
/// heard. And the start-up baseline, which went out before the UDP port was open, goes out once
/// the port is. Found by the machine module scenario harness (careless K1-K3).
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class MachineModuleSettingsResendTests
{
    private IUdpCommunicationService _udp = null!;
    private AutoSteerService _service = null!;
    private long _t = 1_000_000;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        ConfigurationStore.Instance.NumSections = 8;
        for (int i = 0; i < 8; i++) ConfigurationStore.Instance.Tool.SetSectionWidth(i, 100);

        _udp = Substitute.For<IUdpCommunicationService>();
        _service = new AutoSteerService(Substitute.For<ITrackGuidanceService>(), _udp,
            Substitute.For<IGpsService>(), ConfigurationStore.Instance);
        _service.ConfigEmitDebounceMilliseconds = 20;
        _service.TickClock = () => _t;
    }

    [TearDown]
    public void TearDown() => _service.Stop();

    private static Expression<Predicate<byte[]>> Pgn(byte id) => b => b.Length > 4 && b[3] == id;

    private void MachineHello()
    {
        var hello = new byte[] { 0x80, 0x81, 0x7B, 123, 5, 0, 0, 0, 0, 0, 0 };
        hello[^1] = (byte)(hello[2..^1].Sum(x => x) & 0xFF);
        _udp.DataReceived += Raise.EventWith(new UdpDataReceivedEventArgs { Data = hello, PGN = 123 });
    }

    [Test]
    public void MachineModuleNewOrBack_GetsWidthsRelayFunctionsAndMachineConfig()
    {
        _udp.IsConnected.Returns(true);
        _service.Start();
        _udp.ClearReceivedCalls();

        MachineHello(); // the first reply since the app started: the module is new
        _udp.Received(1).SendToModules(Arg.Is(Pgn(0xEB))); // 235 section widths
        _udp.Received(1).SendToModules(Arg.Is(Pgn(0xEC))); // 236 relay functions
        _udp.Received(1).SendToModules(Arg.Is(Pgn(0xEE))); // 238 machine config
        // The rate controllers on the loopback heard the widths at start: not again.
        _udp.DidNotReceive().SendToLoopback(Arg.Is(Pgn(0xEB)));

        _udp.ClearReceivedCalls();
        _t += 1000; MachineHello(); // the next hello's reply: nothing new
        _t += 1000; MachineHello();
        _udp.DidNotReceive().SendToModules(Arg.Is(Pgn(0xEB)));
        _udp.DidNotReceive().SendToModules(Arg.Is(Pgn(0xEC)));

        // Unplugged; meanwhile section 8 becomes 50 cm and is saved (sent to nobody).
        ConfigurationStore.Instance.Tool.SetSectionWidth(7, 50);
        ConfigurationStore.Instance.OnProfileSaved();
        _udp.ClearReceivedCalls();

        _t += AutoSteerService.MachineModuleSilenceMs + 1000; MachineHello(); // back
        _udp.Received(1).SendToModules(Arg.Is<byte[]>(b => b.Length > 20 && b[3] == 0xEB && (b[19] | b[20] << 8) == 50));
        _udp.Received(1).SendToModules(Arg.Is(Pgn(0xEC)));
        _udp.Received(1).SendToModules(Arg.Is(Pgn(0xEE)));
    }

    [Test]
    public void Baseline_SentAgain_OnTheFirstControlTickWithThePortOpen()
    {
        _udp.IsConnected.Returns(false); // MainViewModel opens the port after Start()
        _service.Start();
        _udp.ClearReceivedCalls();

        _service.SendPgnsForControlTick(); // still closed: nothing yet
        _udp.DidNotReceive().SendToModules(Arg.Is(Pgn(0xEB)));

        _udp.IsConnected.Returns(true);
        _service.SendPgnsForControlTick();
        _udp.Received(1).SendToModules(Arg.Is(Pgn(0xEB)));  // 235, though the same layout as at start
        _udp.Received(1).SendToLoopback(Arg.Is(Pgn(0xEB))); // and to the rate controllers
        _udp.Received(1).SendToModules(Arg.Is(Pgn(0xEC)));
        _udp.Received(1).SendToModules(Arg.Is(Pgn(0xEE)));
        _udp.Received(1).SendToModules(Arg.Is(Pgn(0xFB)));  // 251 steer config

        _udp.ClearReceivedCalls();
        _service.SendPgnsForControlTick(); // once only
        _udp.DidNotReceive().SendToModules(Arg.Is(Pgn(0xEB)));
        _udp.DidNotReceive().SendToModules(Arg.Is(Pgn(0xFB)));
    }

    [Test]
    public void Baseline_NotSentTwice_WhenThePortWasOpenAtStart()
    {
        _udp.IsConnected.Returns(true);
        _service.Start();
        _udp.ClearReceivedCalls();

        _service.SendPgnsForControlTick();
        _udp.DidNotReceive().SendToModules(Arg.Is(Pgn(0xEB)));
        _udp.DidNotReceive().SendToModules(Arg.Is(Pgn(0xFB)));
    }
}
