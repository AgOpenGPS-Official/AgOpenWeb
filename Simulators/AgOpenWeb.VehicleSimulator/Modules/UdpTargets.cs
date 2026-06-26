// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace AgOpenWeb.VehicleSimulator.Modules;

/// <summary>
/// The live set of UDP destinations every virtual module sends to. Populated from the
/// UI's network-interface checkboxes — each checked NIC contributes its subnet broadcast
/// (loopback contributes 127.0.0.1), all on the host's receive port (9999). The original
/// localhost-only sim was hardwired to 127.0.0.1, so it could only ever drive a host on the
/// same machine; this lets it reach a headless host on another box (e.g. an SBC).
///
/// The endpoint array is swapped atomically (volatile) so the module send threads always read
/// a consistent snapshot without locking; the UI can update the selection mid-run.
/// </summary>
public sealed class UdpTargets
{
    private volatile IPEndPoint[] _endpoints;

    public UdpTargets(params IPEndPoint[] initial) =>
        _endpoints = initial ?? Array.Empty<IPEndPoint>();

    /// <summary>Current destinations (snapshot; safe to enumerate off any thread).</summary>
    public IReadOnlyList<IPEndPoint> Endpoints => _endpoints;

    public void Set(IEnumerable<IPEndPoint> endpoints) =>
        _endpoints = endpoints?.ToArray() ?? Array.Empty<IPEndPoint>();
}
