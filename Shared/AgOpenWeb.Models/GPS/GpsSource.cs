// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

namespace AgOpenWeb.Models.GPS;

/// <summary>
/// Which listening port a GPS datagram came in on. The module port carries everything an
/// AiO board or a receiver with its own network port sends; 2211 and 2222 are Ace's GPS1 /
/// GPS2 ports, for a bridge that keeps two receivers apart. The line splitter keeps one
/// partial-line tail per source, and Network IO names the source.
/// </summary>
public enum GpsSource : byte
{
    /// <summary>UDP 9999, the module port.</summary>
    ModulePort = 0,
    /// <summary>UDP 2211 (Ace GPS1).</summary>
    Gps1 = 1,
    /// <summary>UDP 2222 (Ace GPS2).</summary>
    Gps2 = 2,
}

/// <summary>Why the parser did or did not take a line (System Data card counters).</summary>
public enum NmeaParseResult : byte
{
    /// <summary>A complete fix was decoded.</summary>
    Accepted = 0,
    /// <summary>Too short, no <c>$</c>, or no checksum marker where one must be.</summary>
    BadFrame = 1,
    /// <summary>The checksum did not match: a corrupted or mis-split line.</summary>
    BadChecksum = 2,
    /// <summary>A well-formed line of a kind this build does not decode (a <c>#</c> log, <c>$GNGGA</c>…).</summary>
    UnknownSentence = 3,
    /// <summary>A known sentence whose fields did not decode (too few, non-numeric).</summary>
    BadFields = 4,
    /// <summary>A member of a multi-sentence epoch, taken into the open epoch; the fix follows when the epoch closes.</summary>
    EpochMember = 5,
}
