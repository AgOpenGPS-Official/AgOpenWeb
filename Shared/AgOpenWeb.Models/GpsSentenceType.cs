// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

namespace AgOpenWeb.Models;

/// <summary>Which sentence the latest GPS fix came from (#157).</summary>
public enum GpsSentenceType : byte
{
    None = 0,
    /// <summary><c>$PANDA</c>: single antenna; the heading field is the IMU heading.</summary>
    Panda = 1,
    /// <summary><c>$PAOGI</c>: the heading field is the dual-antenna heading.</summary>
    Paogi = 2,
    /// <summary>The built-in simulator (its heading stands in for a dual heading, as
    /// AgOpenGPS's CSim sets headingTrueDual).</summary>
    Simulator = 3,
    /// <summary><c>$KSXT</c>: a Bynav/Unicore receiver's own fix; the heading field is the
    /// dual-antenna heading.</summary>
    Ksxt = 4,
    /// <summary>A fix assembled from a receiver's standard sentences (GGA/GNS + VTG + HPR/HDT/THS).</summary>
    NmeaEpoch = 5,
    /// <summary>Unicore <c>#INSPVAXA</c> / <c>#INSPVAA</c>: one fused INS fix per epoch (UM981).</summary>
    Inspvax = 6,
}
