// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;

namespace AgOpenWeb.Services.Interfaces;

/// <summary>
/// ISOBUS virtual terminals on the implement bus, one to four (as displays in one cab, each
/// implement choosing one), each shown in its own VT card. The bus is reached through a gateway
/// on the network or an slcan adapter plugged into this device. A terminal flattens its implement's
/// screens into draw lists; the card paints them and sends the operator's touches back through
/// these calls, each naming its terminal (0 first). Optional: a head registers one only when
/// it is built with the terminal (AgOpenWeb.VirtualTerminal); without one the card never shows.
/// Calls may come from any thread and return at once.
/// </summary>
public interface IVirtualTerminalService
{
    /// <summary>Every message for the cards, as JSON with <c>"type"</c> first, and the terminal it
    /// is about: a terminal's own (<c>screen</c>, <c>edit</c>, <c>workingSets</c>, <c>alarms</c>,
    /// <c>audio</c>, <c>volume</c>, <c>identify</c>), or the link to the bus (<c>link</c>, with
    /// the number of terminals, the adapter and the serial ports there are) under -1. Raised on the terminals' or the link's thread; keep
    /// handlers short.</summary>
    event Action<int, string>? Message;

    /// <summary>A picture the screens refer to by id: id, width, height, RGBA rows top first.
    /// Raised once, before the first screen that uses it. Ids are unique across the terminals.</summary>
    event Action<uint, int, int, byte[]>? Image;

    /// <summary>An entered value was refused (out of range, a character not allowed); the
    /// field stays open.</summary>
    event Action? InputRefused;

    /// <summary>Connects to the saved gateway, if there is one.</summary>
    void Start();

    /// <summary>Saves the gateway ("host" or "host:port"; "" turns the terminals off), stops using
    /// an adapter, and connects to it.</summary>
    void SetGateway(string gateway, string name = "");

    /// <summary>Saves an slcan adapter on this device (its serial port) and uses it instead of the
    /// gateway; "" goes back to the gateway.</summary>
    void SetAdapter(string port);

    /// <summary>Looks for serial ports and gateways again now, and sends the link message.</summary>
    void RefreshPorts();

    /// <summary>Saves how many terminals (1 to 4) and starts them again: the implements see
    /// their terminal go and come back, and load their pools from the store.</summary>
    void SetCount(int count);

    /// <summary>A soft key (<paramref name="button"/> false) or a button on the terminal's mask.
    /// A press is repeated as held while it is down, and must end in a release or an abort.</summary>
    void Key(int terminal, bool button, ushort objectId, VtKeyAction action);

    /// <summary>Opens an input field: an <c>edit</c> message follows.</summary>
    void Select(int terminal, ushort objectId);

    /// <summary>A number (raw, before offset and scale), a boolean (0 or 1) or a list index.</summary>
    void SetNumber(int terminal, ushort objectId, uint value);

    void SetString(int terminal, ushort objectId, string text);

    /// <summary>Abandons the open field.</summary>
    void Escape(int terminal);

    /// <summary>Shows this implement (its 64-bit NAME) on the terminal it is connected to.</summary>
    void Activate(int terminal, ulong name);

    /// <summary>Ends every key still down as aborted (control changed hands or was dropped).</summary>
    void AbortKeys();
}

public enum VtKeyAction { Press, Release, Abort }
