// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

// Linked into every scenario harness (<Compile Include="..\ScenarioShared\PortGuard.cs" />).
// Global namespace on purpose: an NUnit SetUpFixture outside any namespace runs once for the
// whole test assembly, before the first rig binds anything.

using System;
using System.Net;
using System.Net.Sockets;
using NUnit.Framework;

/// <summary>
/// Stops a harness run at the start when another process on this machine holds the module ports.
/// The app binds UDP 9999 with ReuseAddress, so a second AgOpenWeb (an app left running, a second
/// harness) binds it too without an error and the two split the packets between them: a run beside
/// an app on the developer's Mac showed valves shut for up to 192 s that the same job on GitHub
/// didn't (2026-11-10). Findings from such a run are noise; this turns it into one clear failure.
/// A bind without ReuseAddress fails whenever any other socket holds the port, on Linux and macOS.
/// </summary>
[SetUpFixture]
public sealed class PortGuard
{
    /// <summary>The app's receive port and the modules' listen port.</summary>
    public static readonly int[] Ports = { 9999, 8888 };

    [OneTimeSetUp]
    public void PortsAreFree()
    {
        foreach (int port in Ports)
        {
            if (HeldByAnother(port))
                Assert.Fail($"UDP {port} is held by another process on this machine (an AgOpenWeb app, "
                    + "a simulator or another harness run). The run would share its packets and report "
                    + "noise; stop it and run again, or run on the harness host (Tools/harness-runner).");
        }
    }

    public static bool HeldByAnother(int port)
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            probe.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
            probe.Bind(new IPEndPoint(IPAddress.Any, port));
            return false;
        }
        catch (SocketException e) when (e.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
        {
            return true;
        }
    }
}
