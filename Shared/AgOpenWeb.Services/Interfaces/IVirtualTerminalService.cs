// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;

namespace AgOpenWeb.Services.Interfaces;

/// <summary>
/// An ISOBUS virtual terminal on the implement bus, shown in the web client's VT card. The
/// terminal flattens the implement's screens into draw lists; the card paints them and sends
/// the operator's touches back through these calls. Optional: a head registers one only when
/// it is built with the terminal (AgOpenWeb.VirtualTerminal); without one the card never shows.
/// Calls may come from any thread and return at once.
/// </summary>
public interface IVirtualTerminalService
{
    /// <summary>Every message for the card, as JSON with <c>"type"</c> first: the terminal's
    /// own (<c>screen</c>, <c>edit</c>, <c>workingSets</c>, <c>alarms</c>, <c>audio</c>,
    /// <c>volume</c>) and the link to the bus (<c>link</c>). Raised on the terminal's or the
    /// link's thread; keep handlers short.</summary>
    event Action<string>? Message;

    /// <summary>A picture the screens refer to by id: id, width, height, RGBA rows top first.
    /// Raised once, before the first screen that uses it.</summary>
    event Action<uint, int, int, byte[]>? Image;

    /// <summary>An entered value was refused (out of range, a character not allowed); the
    /// field stays open.</summary>
    event Action? InputRefused;

    /// <summary>Connects to the saved gateway, if there is one.</summary>
    void Start();

    /// <summary>Saves the gateway ("host" or "host:port"; "" turns the terminal off) and
    /// connects to it.</summary>
    void SetGateway(string gateway);

    /// <summary>A soft key (<paramref name="button"/> false) or a button on the mask. A press
    /// is repeated as held while it is down, and must end in a release or an abort.</summary>
    void Key(bool button, ushort objectId, VtKeyAction action);

    /// <summary>Opens an input field: an <c>edit</c> message follows.</summary>
    void Select(ushort objectId);

    /// <summary>A number (raw, before offset and scale), a boolean (0 or 1) or a list index.</summary>
    void SetNumber(ushort objectId, uint value);

    void SetString(ushort objectId, string text);

    /// <summary>Abandons the open field.</summary>
    void Escape();

    /// <summary>Shows this implement (its 64-bit NAME).</summary>
    void Activate(ulong name);

    /// <summary>Ends every key still down as aborted (control changed hands or was dropped).</summary>
    void AbortKeys();
}

public enum VtKeyAction { Press, Release, Abort }
