// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

namespace AgOpenWeb.Services.Network;

/// <summary>
/// Finds the copies of a module's datagrams that arrive on a second network link. A tablet
/// joined to the modules' router by cable and by Wi-Fi gets every broadcast once per link, and
/// the Wi-Fi copy comes late (the router holds broadcasts for the Wi-Fi's beacon and sends a
/// few at each): taken as news, a late position read as the tractor going back (a false reverse
/// that paused the steering) and a late PGN 253 as the module's steer switch going off (a
/// tester's Puma, 20 Nov 2026, the copies about 0.3 s late). Measured there two days later with
/// more broadcasts on the network: 8 datagrams every second beacon (205 ms), each 1.5 to 1.7 s
/// late, and only half of what the module sent.
///
/// <para>Each datagram is taken from the link it arrives on first; the same bytes from the same
/// sender arriving on another link within <see cref="WindowMs"/> are its copy. Each datagram
/// taken cancels one copy at most, so a module that sends the same bytes again (PGN 253 with
/// nothing changed, ten times a second) is not taken for its own copy. No link is preferred:
/// when the first link goes quiet, the other's datagrams have no original to match and are taken
/// from the first one, with no gap.</para>
///
/// <para>One instance per listening socket. Not thread-safe: a socket's receive callbacks are
/// serial. Nothing is allocated per datagram once a sender is known.</para>
/// </summary>
internal sealed class LinkCopyFilter
{
    /// <summary>
    /// How late a copy may come. A router that sends its held broadcasts a few per beacon keeps
    /// the rest waiting: 1.49 to 1.73 s measured (8 every second beacon, about 60 waiting), which
    /// 2 s covered by a quarter of a second. Linux keeps up to 128 waiting (AP_MAX_BC_BUFFER):
    /// 3.3 s at that pace.
    /// </summary>
    public const long WindowMs = 5000;

    /// <summary>
    /// Datagrams kept per sender: <see cref="WindowMs"/> of a steer module answering 100 PGN 254
    /// a second and sending its position (about 170 a second), twice over.
    /// </summary>
    public const int Capacity = 2048;

    /// <summary>A copy after this long without one starts a new stream of copies (logged again).</summary>
    public const long QuietMs = 30_000;

    /// <summary>Senders kept; past this the list starts again (strangers broadcasting to the port).</summary>
    public const int MaxSenders = 64;

    private struct Entry
    {
        public ulong Hash;
        public int Length;
        public int Link;
        public long TimeMs;
        public bool Matched;
    }

    private sealed class Sender
    {
        public readonly Entry[] Ring = new Entry[Capacity];
        public int Start;
        public int Count;
        public bool HadCopy;
        public long LastCopyMs;
        public long Copies;
    }

    private readonly Dictionary<uint, Sender> _senders = new();

    /// <summary>
    /// Whether <paramref name="data"/>, from <paramref name="sender"/> on <paramref name="link"/>,
    /// is the copy of a datagram already taken from another link. A datagram that is not a copy is
    /// taken: remembered for <see cref="WindowMs"/> to cancel its own copy.
    /// </summary>
    /// <param name="sender">The sender's IPv4 address.</param>
    /// <param name="link">The local network interface it arrived on (its index).</param>
    /// <param name="nowMs">A monotonic time in milliseconds.</param>
    /// <param name="firstLink">For a copy: the link the datagram was taken from.</param>
    /// <param name="copiesStart">For a copy: the first from this sender in <see cref="QuietMs"/>.</param>
    public bool IsCopy(uint sender, int link, ReadOnlySpan<byte> data, long nowMs,
        out int firstLink, out bool copiesStart)
    {
        firstLink = 0;
        copiesStart = false;

        if (!_senders.TryGetValue(sender, out var s))
        {
            if (_senders.Count >= MaxSenders) _senders.Clear();
            s = new Sender();
            _senders[sender] = s;
        }

        while (s.Count > 0 && nowMs - s.Ring[s.Start].TimeMs > WindowMs)
        {
            s.Start = (s.Start + 1) % Capacity;
            s.Count--;
        }

        ulong hash = Hash(data);
        for (int i = 0; i < s.Count; i++)
        {
            ref var e = ref s.Ring[(s.Start + i) % Capacity];
            if (e.Matched || e.Link == link || e.Hash != hash || e.Length != data.Length) continue;

            e.Matched = true;
            firstLink = e.Link;
            copiesStart = !s.HadCopy || nowMs - s.LastCopyMs > QuietMs;
            s.HadCopy = true;
            s.LastCopyMs = nowMs;
            s.Copies++;
            return true;
        }

        if (s.Count == Capacity)
        {
            s.Start = (s.Start + 1) % Capacity;
            s.Count--;
        }
        s.Ring[(s.Start + s.Count) % Capacity] = new Entry
        {
            Hash = hash, Length = data.Length, Link = link, TimeMs = nowMs,
        };
        s.Count++;
        return false;
    }

    /// <summary>Copies dropped from <paramref name="sender"/> since this filter started.</summary>
    public long CopiesFrom(uint sender) => _senders.TryGetValue(sender, out var s) ? s.Copies : 0;

    /// <summary>FNV-1a, 64 bits.</summary>
    private static ulong Hash(ReadOnlySpan<byte> data)
    {
        ulong h = 14695981039346656037UL;
        foreach (byte b in data)
        {
            h ^= b;
            h *= 1099511628211UL;
        }
        return h;
    }
}
