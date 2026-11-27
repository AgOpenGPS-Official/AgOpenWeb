// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AgOpenWeb.Services;
using AgOpenWeb.Services.AutoSteer;
using AgOpenWeb.Services.Coverage;
using AgOpenWeb.Services.Gps;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Pipeline;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.Headland;
using AgOpenWeb.Services.Track;
using AgOpenWeb.Services.YouTurn;
using AgOpenWeb.Services.Tool;
using AgOpenWeb.Services.Section;
using AgOpenWeb.Services.Tram;
using AgOpenWeb.ViewModels;
using AgOpenWeb.RemoteWiring;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Models.State;
using AgOpenWeb.Android.Services;
using AgOpenWeb.Services.Logging;
using AgOpenWeb.Services.Threading;

namespace AgOpenWeb.Android.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAgOpenWebServices(this IServiceCollection services)
    {
        // Configure structured logging
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.AddDebug();
            builder.AddProvider(new InMemoryLoggerProvider());
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        // The services every head registers the same way (BackendServices, in Shared); what
        // follows is this platform's own.
        services.AddBackendServices();

        services.AddSingleton<ILocalNetworkInfoProvider>(_ =>
            new AndroidLocalNetworkInfoProvider(
                global::Android.App.Application.Context
                ?? throw new InvalidOperationException("Android application context is unavailable.")));
        // One watcher for network changes; UDP, NTRIP and the bug report outbox subscribe (#385).
        services.AddSingleton<AgOpenWeb.Services.Network.INetworkWatcher>(sp =>
        {
            var watcher = new AgOpenWeb.Services.Network.NetworkWatcher(
                sp.GetService<Microsoft.Extensions.Logging.ILogger<AgOpenWeb.Services.Network.NetworkWatcher>>());
            AgOpenWeb.Android.Services.AndroidNetworkEvents.Register(global::Android.App.Application.Context, watcher);
            return watcher;
        });

        // ISOBUS virtual terminal for the VT card; not registered while it is switched off
        // (FeatureSwitches): no terminal, no gateway browsing, and the cards never show.
        if (AgOpenWeb.Models.FeatureSwitches.VirtualTerminal)
            services.AddSingleton<IVirtualTerminalService, AgOpenWeb.VirtualTerminal.VirtualTerminalService>();

        // The tablet, its networks and USB devices, for the bug report dump (device_info.txt).
        services.AddSingleton<IDeviceInfoProvider>(_ =>
            new AgOpenWeb.Android.Services.AndroidDeviceInfoProvider(global::Android.App.Application.Context!));

        // Battery service — subscribes to the sticky ACTION_BATTERY_CHANGED
        // broadcast via Application.Context (no polling needed).
        services.AddSingleton<IBatteryService>(sp =>
            new AgOpenWeb.Android.Services.AndroidBatteryService(
                global::Android.App.Application.Context!,
                sp.GetRequiredService<IUiDispatcher>()));

        return services;
    }

    /// <summary>
    /// Wire up services that need cross-references after the container is built.
    /// Call this after building the service provider.
    /// </summary>
    public static void WireUpServices(this IServiceProvider serviceProvider)
    {
        // Wire AutoSteerService into UdpCommunicationService for zero-copy GPS processing
        var udpService = serviceProvider.GetRequiredService<IUdpCommunicationService>() as UdpCommunicationService;
        var autoSteerService = serviceProvider.GetRequiredService<IAutoSteerService>();

        udpService?.SetAutoSteerService(autoSteerService);
    }
}
