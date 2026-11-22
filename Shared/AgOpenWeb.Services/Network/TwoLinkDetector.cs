// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;

namespace AgOpenWeb.Services.Network;

/// <summary>
/// Finds a module heard on two network links: a tablet cabled to the modules' router that has
/// also joined the router's Wi-Fi gets every broadcast once per link, the Wi-Fi copy late (the
/// router holds broadcasts for the Wi-Fi's beacon: 1.5 to 1.7 s late on a tester's). Taken as
/// news, a late position read as the tractor going back (a false reverse paused the steering) and
/// a late PGN 253 as the steer switch going off (a tester's Puma, 20 Nov 2026). It is an
/// installation fault: the app does not work with it. While a module is on two links nothing
/// from it is used, on either link, and the operator is told to disconnect one.
///
/// <para>Strict, because a detection stops the app: a module is on two links when it is heard on
/// both, on each at least <see cref="Repeats"/> datagrams in a row with no gap over
/// <see cref="GapMs"/>. One stray datagram, or a few, never is. It clears when one of the links
/// has been quiet for <see cref="GapMs"/>, and the module is taken again. Nothing is compared or
/// kept per datagram: one lookup, nothing allocated once a module is known.</para>
///
/// <para>One instance per listening socket; its receive callbacks are serial. The lock is for
/// <see cref="AddTo"/>, read from the host loop.</para>
/// </summary>
internal sealed class TwoLinkDetector
{
    /// <summary>Datagrams in a row on each link before a module is on two links.</summary>
    public const int Repeats = 10;

    /// <summary>A gap longer than this ends a run on a link; a link this quiet clears a detection.</summary>
    public const long GapMs = 1000;

    /// <summary>Modules kept; past this the list starts again (strangers broadcasting to the port).</summary>
    public const int MaxSenders = 64;

    public enum Change { None, Detected, Cleared }

    /// <summary>A module heard on two links since the start, for Network IO and the bug report.</summary>
    public readonly record struct ModuleOnTwoLinks(uint Sender, int LinkA, int LinkB, long NotUsed, long Times, bool Now);

    private struct Heard
    {
        public int Link;
        public long LastMs;
        public int Run;
    }

    private sealed class Sender
    {
        public Heard A, B;
        public bool OnTwo;
        public long NotUsed, Times;
    }

    private readonly Dictionary<uint, Sender> _senders = new();
    private readonly object _lock = new();

    /// <summary>
    /// Whether to use a datagram from <paramref name="sender"/> that came in on <paramref name="link"/>
    /// (the local network interface's index): false while the module is on two links.
    /// </summary>
    /// <param name="nowMs">A monotonic time in milliseconds.</param>
    /// <param name="change">The module was found on two links with this datagram, or is no longer.</param>
    public bool Take(uint sender, int link, long nowMs, out Change change)
    {
        change = Change.None;
        lock (_lock)
        {
            if (!_senders.TryGetValue(sender, out var s))
            {
                if (_senders.Count >= MaxSenders) _senders.Clear();
                s = new Sender();
                _senders[sender] = s;
            }

            // The two links last heard: a third replaces the one quiet longest.
            bool onA = s.A.Link == link || s.A.Link == 0
                || (s.B.Link != link && s.B.Link != 0 && s.A.LastMs <= s.B.LastMs);
            ref Heard h = ref onA ? ref s.A : ref s.B;
            if (h.Link != link)
            {
                h.Link = link;
                h.Run = 0;
            }
            h.Run = h.Run > 0 && nowMs - h.LastMs <= GapMs ? h.Run + 1 : 1;
            h.LastMs = nowMs;

            bool onTwo = OnTwo(s, nowMs);
            if (onTwo != s.OnTwo)
            {
                s.OnTwo = onTwo;
                change = onTwo ? Change.Detected : Change.Cleared;
                if (onTwo) s.Times++;
            }
            if (!onTwo) return true;
            s.NotUsed++;
            return false;
        }
    }

    private static bool OnTwo(Sender s, long nowMs) => s.B.Link != 0 && Carries(s.A, nowMs) && Carries(s.B, nowMs);

    private static bool Carries(in Heard h, long nowMs) => h.Run >= Repeats && nowMs - h.LastMs <= GapMs;

    /// <summary>The two links a module was last heard on (0 = none).</summary>
    public (int A, int B) Links(uint sender)
    {
        lock (_lock)
            return _senders.TryGetValue(sender, out var s) ? (s.A.Link, s.B.Link) : (0, 0);
    }

    /// <summary>Adds each module found on two links since the start. Allocates nothing when there is none.</summary>
    public void AddTo(List<ModuleOnTwoLinks> list, long nowMs)
    {
        lock (_lock)
            foreach (var (ip, s) in _senders)
                if (s.Times > 0)
                    list.Add(new ModuleOnTwoLinks(ip, s.A.Link, s.B.Link, s.NotUsed, s.Times, OnTwo(s, nowMs)));
    }

    /// <summary>Whether any module was found on two links since the start.</summary>
    public bool Any()
    {
        lock (_lock)
            foreach (var s in _senders.Values)
                if (s.Times > 0) return true;
        return false;
    }
}
