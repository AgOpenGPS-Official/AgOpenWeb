// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System.Linq;
using System.Reflection;
using AgValoniaGPS.Models.State;
using AgValoniaGPS.Services.Interfaces;

namespace AgValoniaGPS.Services.Tests.Pipeline;

/// <summary>
/// End-of-Phase-F locks for the <c>ConnectionState</c> one-way contract.
/// Communication services raise events and expose polling queries; the
/// ViewModel's handlers marshal to the UI thread before writing. No
/// service should take a <see cref="ConnectionState"/> parameter — that
/// would tempt cross-thread mutation.
///
/// Mirrors the reflection-based guards for YouTurn (Phase C C9),
/// Guidance (Phase D D10), and LocalPlane (Phase E E3).
/// </summary>
[TestFixture]
public class ConnectionStateCycleTests
{
    [Test]
    public void IGpsPipelineService_has_no_ConnectionState_parameter()
    {
        AssertNoConnectionStateParameter(typeof(IGpsPipelineService));
    }

    [Test]
    public void INtripClientService_has_no_ConnectionState_parameter()
    {
        AssertNoConnectionStateParameter(typeof(INtripClientService));
    }

    [Test]
    public void IUdpCommunicationService_has_no_ConnectionState_parameter()
    {
        AssertNoConnectionStateParameter(typeof(IUdpCommunicationService));
    }

    private static void AssertNoConnectionStateParameter(System.Type serviceInterface)
    {
        var offenders = serviceInterface.GetMethods()
            .Where(m => m.GetParameters().Any(p =>
                p.ParameterType == typeof(ConnectionState)
                || p.ParameterType == typeof(ConnectionState).MakeByRefType()))
            .Select(m => m.Name)
            .ToList();

        Assert.That(offenders, Is.Empty,
            $"{serviceInterface.Name} must not accept a ConnectionState parameter. "
            + "The one-way contract is: services raise events / expose polling, the "
            + "ViewModel marshals to the UI thread and writes State.Connections. "
            + "Offending method(s): " + string.Join(", ", offenders));
    }
}
