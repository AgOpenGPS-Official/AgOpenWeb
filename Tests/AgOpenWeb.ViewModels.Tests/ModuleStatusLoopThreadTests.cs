// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Configuration;
using AgOpenWeb.RemoteWiring;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Threading;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// The view model's module status loop (the hello, the module and GPS checks, the module-lost
/// and GPS-lost alarms, every 100 ms) writes the view model's state, so it runs on the host loop.
/// It runs where the view model was started. The heads start the backend on their own start-up
/// thread (iOS from the thread pool), and built and started the view model there: the loop ran on
/// the thread pool, beside the host loop (seen on the Desktop head). The backend builds it on the
/// host loop now (<see cref="WebBackend.CreateViewModelOnHostLoopAsync"/>), as the harness rigs did.
/// </summary>
[TestFixture]
[NonParallelizable] // the backend's state lease sets the statics (ConfigurationStore.Instance)
public class ModuleStatusLoopThreadTests
{
    private string _dataRoot = null!;
    private string? _dataRootBefore;

    [SetUp]
    public void SetUp()
    {
        _dataRootBefore = Environment.GetEnvironmentVariable(AppDataRoot.EnvVar);
        _dataRoot = Path.Combine(Path.GetTempPath(), "aow-status-loop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);
        Environment.SetEnvironmentVariable(AppDataRoot.EnvVar, _dataRoot); // not the developer's own settings
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable(AppDataRoot.EnvVar, _dataRootBefore);
        try { Directory.Delete(_dataRoot, true); } catch { }
        ConfigurationStore.SetInstance(new ConfigurationStore());
    }

    [Test]
    public async Task The_module_status_loop_runs_on_the_host_loop_when_a_head_starts_the_backend_from_another_thread()
    {
        // The shared container, with a stand-in for the UDP port (no socket): open at once and
        // connected, so the loop starts and sends its hello.
        var helloOnHostLoop = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        HostLoopDispatcher? hostLoop = null;
        var udp = Substitute.For<IUdpCommunicationService>();
        udp.StartAsync().Returns(Task.CompletedTask);
        udp.IsConnected.Returns(true);
        udp.When(u => u.SendHelloPacket()).Do(_ => helloOnHostLoop.TrySetResult(hostLoop!.CheckAccess()));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBackendServices();
        services.AddSingleton<ILocalNetworkInfoProvider, SystemLocalNetworkInfoProvider>();
        services.AddSingleton<AgOpenWeb.Services.Network.INetworkWatcher, AgOpenWeb.Services.Network.NetworkWatcher>();
        services.AddSingleton<IBatteryService, AgOpenWeb.Services.Battery.NullBatteryService>();
        services.AddSingleton(udp);
        await using var sp = services.BuildServiceProvider();
        hostLoop = sp.GetRequiredService<HostLoopDispatcher>();
        sp.GetRequiredService<ISettingsService>().Load();
        sp.GetRequiredService<IConfigurationService>().LoadAppSettings();
        sp.GetRequiredService<IPersistentStateService>().Load();
        WebBackend.WireCrossServices(sp);

        // As a head does it: from a thread that is not the host loop (here the test's).
        Assume.That(hostLoop.CheckAccess(), Is.False);
        var vm = await WebBackend.CreateViewModelOnHostLoopAsync(sp);
        try
        {
            var sent = await helloOnHostLoop.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(sent, Is.True, "the module status loop ran off the host loop, writing the view model's state from there");
        }
        finally
        {
            await hostLoop.InvokeAsync(() => WebBackend.StopServices(sp, vm));
        }
    }
}
