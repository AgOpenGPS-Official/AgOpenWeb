// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

namespace AgOpenWeb.VirtualModules;

/// <summary>
/// Which local addresses a virtual module's listening socket binds to.
/// </summary>
/// <remarks>
/// This is the one place the simulator and the integration tests genuinely
/// disagreed while they each kept a private copy of these modules, so it is now
/// an explicit choice rather than a hardcoded difference:
///
/// <list type="bullet">
/// <item><see cref="LoopbackOnly"/> was the test copy's behaviour, with the
/// comment "so Windows Defender Firewall does not prompt during test runs".
/// A binding socket on <c>0.0.0.0</c> triggers that prompt; loopback does
/// not, and all virtual-module traffic in a test run is 127.0.0.1 anyway.</item>
/// <item><see cref="AllInterfaces"/> was the simulator copy's behaviour. The
/// simulator has to be reachable from a host on another machine (a headless
/// SBC, say), which loopback cannot do.</item>
/// </list>
///
/// Both are correct for their caller, so neither is the default everywhere:
/// the <c>UdpTargets</c> constructors default to <see cref="AllInterfaces"/>
/// (the simulator shape), the single-host convenience constructors pin
/// <see cref="LoopbackOnly"/> (the test shape), and <c>VirtualModuleHub</c>
/// defaults to <see cref="LoopbackOnly"/> so a test that constructs a hub
/// cannot accidentally raise a firewall prompt.
/// </remarks>
public enum ModuleBindMode
{
    /// <summary>Bind 127.0.0.1 only. No firewall prompt; unreachable off-box.</summary>
    LoopbackOnly,

    /// <summary>Bind 0.0.0.0 with broadcast enabled. Reachable from other machines.</summary>
    AllInterfaces
}
