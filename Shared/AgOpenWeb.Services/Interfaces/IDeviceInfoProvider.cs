// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.Services.Interfaces;

/// <summary>
/// What a head can say about its device that plain .NET can't: the tablet's make and model, its
/// web view, power saving, how each network is carried (Wi-Fi, Ethernet, a USB adapter) and the
/// USB devices plugged in. The bug report dump prints it in <c>device_info.txt</c>
/// (<see cref="AgOpenWeb.Services.DeviceReport"/>). The desktop needs none: its lines are read
/// in shared code.
/// </summary>
public interface IDeviceInfoProvider
{
    /// <summary>Lines of text, one fact a line. Called on a background thread.</summary>
    string Describe();
}
