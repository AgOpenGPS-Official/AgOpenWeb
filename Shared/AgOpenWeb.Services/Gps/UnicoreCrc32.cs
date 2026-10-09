// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;

namespace AgOpenWeb.Services.Gps;

/// <summary>
/// The 32-bit CRC that ends a Unicore / NovAtel ASCII log (<c>#INSPVAXA,…;…*e877c178</c>):
/// CRC-32 with the reflected polynomial 0xEDB88320, no initial value and no final XOR, over
/// every byte after the <c>#</c> or <c>%</c> up to the <c>*</c>. Table-driven, computed
/// once; nothing is allocated per call.
/// </summary>
public static class UnicoreCrc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int j = 0; j < 8; j++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0;
        foreach (byte b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
