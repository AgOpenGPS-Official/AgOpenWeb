// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Configuration;
using AgOpenWeb.RemoteWiring;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// The container the heads build: <see cref="BackendServices.AddBackendServices"/> with what a
/// head adds of its own (here stand-ins), the real graph that every other view model test
/// replaces with a builder of stand-ins. Each registration resolves, the view model is built
/// from it once, and <see cref="WebBackend.WireCrossServices"/> runs on it. Nothing is started
/// (that opens the UDP port).
/// </summary>
[TestFixture]
[NonParallelizable] // the backend's state lease sets the statics (ConfigurationStore.Instance)
public class BackendServicesTests
{
    private string _dataRoot = null!;
    private string? _dataRootBefore;

    [SetUp]
    public void SetUp()
    {
        _dataRootBefore = Environment.GetEnvironmentVariable(AppDataRoot.EnvVar);
        _dataRoot = Path.Combine(Path.GetTempPath(), "aow-backend-" + Guid.NewGuid().ToString("N"));
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

    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBackendServices();
        // What each head registers of its own.
        services.AddSingleton<ILocalNetworkInfoProvider, SystemLocalNetworkInfoProvider>();
        services.AddSingleton<AgOpenWeb.Services.Network.INetworkWatcher, AgOpenWeb.Services.Network.NetworkWatcher>();
        services.AddSingleton<IBatteryService, AgOpenWeb.Services.Battery.NullBatteryService>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Test]
    public void Every_shared_registration_resolves()
    {
        var shared = new ServiceCollection().AddBackendServices();
        using var sp = Build();
        foreach (var d in shared.Where(d => !d.ServiceType.ContainsGenericParameters))
            Assert.That(sp.GetService(d.ServiceType), Is.Not.Null, d.ServiceType.FullName);
    }

    [Test]
    public void The_view_model_is_built_once_from_the_real_services()
    {
        using var sp = Build();
        sp.GetRequiredService<ISettingsService>().Load();
        sp.GetRequiredService<IConfigurationService>().LoadAppSettings();
        sp.GetRequiredService<IPersistentStateService>().Load();
        WebBackend.WireCrossServices(sp);

        var vm = sp.GetRequiredService<MainViewModel>();
        Assert.That(sp.GetRequiredService<MainViewModel>(), Is.SameAs(vm), "one view model per backend");
        Assert.That(vm.State, Is.SameAs(sp.GetRequiredService<AgOpenWeb.Models.State.ApplicationState>()),
            "the view model shows the backend's state");
    }
}
