// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Interfaces;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// What AgOpenWeb knew was wrong and kept to itself. From the nightly harnesses (20 Nov 2026):
/// GPS/NTRIP K7 (CORRECTIONS-UNUSED-SILENT): for 45 s the headline said "Corrections arriving"
/// while the receiver, its corrections input on another box, stayed single with no correction
/// age; ISOBUS K7 (ISOBUS-SECTION-COUNT-MISMATCH-SILENT): the tool had 20 sections and the
/// implement 12, and the map painted sections 13-20 with nothing said.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class SilentFaultsTests
{
    private MainViewModelBuilder _b = null!;
    private MainViewModel _vm = null!;
    private readonly List<string> _told = new();

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _b = new MainViewModelBuilder();
        _vm = _b.Build();
        _vm.IsSimulatorEnabled = false;   // a real receiver
        _told.Clear();
        _vm.FailureReported += _told.Add;
    }

    // ---------------------------------------------------------------- corrections

    private void Ntrip(NtripState state) =>
        _b.NtripService.Status.Returns(new NtripSessionStatus(state, "Connected to caster/VRS3", null, "", 0, 0, NtripVersion.V2));

    private void Receiver(int quality, double age) =>
        _vm.State.Vehicle.UpdateFromGps(new Position { Latitude = 43.7, Longitude = -74.0 }, quality, 12, 0.8, age);

    private void Ticks(int n) { for (int i = 0; i < n; i++) _vm.UpdateCorrectionsUnused(); }

    [Test]
    public void CorrectionsArriving_ReceiverStaysSingle_TheOperatorIsToldOnce()
    {
        // K7 as the nightly saw it: streaming, the receiver single with an empty age (read as 0).
        Ntrip(NtripState.Streaming);
        Receiver(quality: 1, age: 0);

        Ticks(MainViewModel.CorrectionsUnusedTicks - 1);
        Assert.That(_told, Is.Empty, "a receiver takes a few seconds to use new corrections");

        Ticks(1);
        Assert.That(_told, Has.Count.EqualTo(1));
        Assert.That(_told[0], Does.StartWith("Corrections are arriving, but the receiver is not using them"));

        Ticks(MainViewModel.CorrectionsUnusedTicks * 2);
        Assert.That(_told, Has.Count.EqualTo(1), "told once");
    }

    [Test]
    public void ReceiverStartsUsingThem_TheOperatorIsTold()
    {
        Ntrip(NtripState.Streaming);
        Receiver(quality: 1, age: 0);
        Ticks(MainViewModel.CorrectionsUnusedTicks);

        Receiver(quality: 5, age: 1.2);
        Ticks(1);

        Assert.That(_told, Has.Count.EqualTo(2));
        Assert.That(_told[1], Is.EqualTo("The receiver is using the corrections now"));
    }

    [TestCase(4, 1.0, TestName = "RTK fixed")]
    [TestCase(5, 1.0, TestName = "RTK float")]
    [TestCase(2, 1.0, TestName = "DGPS")]
    [TestCase(1, 2.0, TestName = "single, but with a correction age")]
    [TestCase(4, double.NaN, TestName = "RTK fixed, a sentence without the age")]
    public void ReceiverUsingThem_NothingSaid(int quality, double age)
    {
        Ntrip(NtripState.Streaming);
        Receiver(quality, age);

        Ticks(MainViewModel.CorrectionsUnusedTicks * 2);

        Assert.That(_told, Is.Empty);
    }

    [TestCase(NtripState.Connected)]
    [TestCase(NtripState.Waiting)]
    [TestCase(NtripState.Stopped)]
    public void NoCorrectionsArriving_NothingSaid(NtripState state)
    {
        // The headline already says what the connection is doing.
        Ntrip(state);
        Receiver(quality: 1, age: 0);

        Ticks(MainViewModel.CorrectionsUnusedTicks * 2);

        Assert.That(_told, Is.Empty);
    }

    [Test]
    public void Simulator_NothingSaid()
    {
        Ntrip(NtripState.Streaming);
        _vm.IsSimulatorEnabled = true;
        Receiver(quality: 1, age: 0);

        Ticks(MainViewModel.CorrectionsUnusedTicks * 2);

        Assert.That(_told, Is.Empty);
    }

    [Test]
    public void NoFixYet_NothingSaid()
    {
        Ntrip(NtripState.Streaming);
        Receiver(quality: 0, age: 0);

        Ticks(MainViewModel.CorrectionsUnusedTicks * 2);

        Assert.That(_told, Is.Empty);
    }

    // ---------------------------------------------------------------- ISOBUS section count

    private static IsobusStatus Tc(int sections, int clients = 1, bool enabled = true) =>
        new(true, enabled, sections, clients, 0);

    private void Isobus(IsobusStatus status, int ticks) { for (int i = 0; i < ticks; i++) _vm.UpdateIsobusSectionCount(status); }

    [Test]
    public void ToolHas20_ImplementHas12_TheOperatorIsToldOnce()
    {
        ConfigurationStore.Instance.NumSections = 20;

        Isobus(Tc(12), MainViewModel.SectionCountMismatchTicks - 1);
        Assert.That(_told, Is.Empty, "an implement registering settles first");

        Isobus(Tc(12), 1);
        Assert.That(_told, Has.Count.EqualTo(1));
        Assert.That(_told[0], Does.Contain("has 12 sections and the tool here 20: sections 13-20 are painted on the map, but no implement has them"));

        Isobus(Tc(12), 100);
        Assert.That(_told, Has.Count.EqualTo(1), "told once");
    }

    [Test]
    public void ImplementHasMore_TheOperatorIsTold()
    {
        ConfigurationStore.Instance.NumSections = 8;

        Isobus(Tc(12), MainViewModel.SectionCountMismatchTicks);

        Assert.That(_told, Has.Count.EqualTo(1));
        Assert.That(_told[0], Does.Contain("its sections 9-12 are never switched"));
    }

    [Test]
    public void ToolChangedToMatch_ThenWrongAgain_ToldAgain()
    {
        ConfigurationStore.Instance.NumSections = 20;
        Isobus(Tc(12), MainViewModel.SectionCountMismatchTicks);

        ConfigurationStore.Instance.NumSections = 12;
        Isobus(Tc(12), 5);
        ConfigurationStore.Instance.NumSections = 20;
        Isobus(Tc(12), MainViewModel.SectionCountMismatchTicks);

        Assert.That(_told, Has.Count.EqualTo(2));
    }

    [Test]
    public void CountsMatch_NothingSaid()
    {
        ConfigurationStore.Instance.NumSections = 24;

        Isobus(Tc(24), 100);

        Assert.That(_told, Is.Empty);
    }

    [Test]
    public void TwoImplements_NothingSaid()
    {
        // ISOBUS K8: with two implements the heartbeat carries one of them; the tool may be the other.
        ConfigurationStore.Instance.NumSections = 24;

        Isobus(Tc(12, clients: 2), 100);

        Assert.That(_told, Is.Empty);
    }

    [Test]
    public void SectionControlOff_OrNoImplement_NothingSaid()
    {
        ConfigurationStore.Instance.NumSections = 20;

        Isobus(Tc(12, enabled: false), 100);
        Isobus(Tc(0), 100);
        Isobus(default, 100);

        Assert.That(_told, Is.Empty);
    }
}
