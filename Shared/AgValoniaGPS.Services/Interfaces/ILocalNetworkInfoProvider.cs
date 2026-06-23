// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System.Collections.Generic;
using System.Net;

namespace AgValoniaGPS.Services.Interfaces;

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
