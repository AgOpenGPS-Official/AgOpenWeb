// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Configuration;
using AgOpenWeb.RemoteWiring;
using AgOpenWeb.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// The order the backend starts the services in (the one the view model's constructor had: the
/// battery reader, AutoSteer, the GPS pipeline, the control loop, chart collection, then the UDP port with the view
/// model's own start), and that it stops them in reverse.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class BackendStartStopTests
{
    private readonly IBatteryService _battery = Substitute.For<IBatteryService>();
    private readonly IAutoSteerService _autoSteer = Substitute.For<IAutoSteerService>();
    private readonly IGpsPipelineService _pipeline = Substitute.For<IGpsPipelineService>();
    private readonly ISteerMachineLoopService _controlLoop = Substitute.For<ISteerMachineLoopService>();
    private readonly IUdpCommunicationService _udp = Substitute.For<IUdpCommunicationService>();
    private readonly IChartDataService _charts = Substitute.For<IChartDataService>();

    [SetUp] public void SetUp() => ConfigurationStore.SetInstance(new ConfigurationStore());
    [TearDown] public void TearDown() => ConfigurationStore.SetInstance(new ConfigurationStore());

    private IServiceProvider Services() => new ServiceCollection()
        .AddSingleton(_battery).AddSingleton(_autoSteer).AddSingleton(_pipeline)
        .AddSingleton(_controlLoop).AddSingleton(_udp).AddSingleton(_charts)
        .BuildServiceProvider();

    [Test]
    public void The_services_start_in_the_constructor_s_old_order_and_the_UDP_port_last()
    {
        WebBackend.StartServices(Services(), new MainViewModelBuilder().Build());
        Received.InOrder(() =>
        {
            _battery.Start();
            _autoSteer.Start();
            _pipeline.Start();
            _controlLoop.Start();
            _charts.Start();
            _udp.StartAsync();
        });
    }

    [Test]
    public void They_stop_in_reverse()
    {
        WebBackend.StopServices(Services(), new MainViewModelBuilder().Build());
        Received.InOrder(() =>
        {
            _charts.Stop();
            _controlLoop.Stop();
            _pipeline.Stop();
            _autoSteer.Stop();
            _battery.Stop();
        });
    }
}
