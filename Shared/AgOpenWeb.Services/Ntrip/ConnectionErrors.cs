// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Net.Sockets;

namespace AgOpenWeb.Services;

/// <summary>
/// Turns a socket error from connecting into an outcome with a plain message. The text of the
/// exception is localised by the operating system, so only the error code is used.
/// </summary>
public static class ConnectionErrors
{
    public static NtripOutcome Classify(SocketError code, string host, int port)
    {
        string where = host + ":" + port;
        return code switch
        {
            SocketError.ConnectionRefused => Unreachable("Cannot reach " + where + ". Is the base station or caster switched on?", code),
            SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain =>
                Unreachable("Cannot find " + host + ". Check the address and your internet connection.", code),
            SocketError.TimedOut => Unreachable("No answer from " + where + ".", code),
            SocketError.HostUnreachable => Unreachable("Cannot reach " + where + " (no route).", code),
            SocketError.NetworkDown or SocketError.NetworkUnreachable =>
                new NtripOutcome(NtripCategory.NoNetwork, "No network connection.", code.ToString()),
            SocketError.ConnectionReset or SocketError.ConnectionAborted => Unreachable(where + " closed the connection.", code),
            _ => Unreachable("Cannot connect to " + where + " (" + code + ").", code),
        };
    }

    private static NtripOutcome Unreachable(string message, SocketError code) =>
        new(NtripCategory.CasterUnreachable, message, code.ToString());
}
