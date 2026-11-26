// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Net;

namespace AgOpenWeb.Services.Interfaces;

/// <summary>
/// One IPv4 address assigned to a local network link.
/// </summary>
public sealed record LocalNetworkAddress(
    IPAddress Address,
    int PrefixLength,
    string? InterfaceName = null);

/// <summary>
/// Platform abstraction for local IPv4 interface discovery.
/// Android does not reliably populate System.Net.NetworkInformation on all
/// devices, so its application target supplies a ConnectivityManager-backed
/// implementation.
/// </summary>
public interface ILocalNetworkInfoProvider
{
    IReadOnlyList<LocalNetworkAddress> GetIPv4Addresses();
}
