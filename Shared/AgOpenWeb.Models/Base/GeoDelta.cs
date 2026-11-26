// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

﻿using System;

namespace AgOpenWeb.Models
{
    public struct GeoDelta
    {
        public GeoDelta(GeoCoord fromCoord, GeoCoord toCoord)
        {
            NorthingDelta = toCoord.Northing - fromCoord.Northing;
            EastingDelta = toCoord.Easting - fromCoord.Easting;
        }

        public GeoDelta(double northingDelta, double eastingDelta)
        {
            NorthingDelta = northingDelta;
            EastingDelta = eastingDelta;
        }

        public double NorthingDelta { get; }
        public double EastingDelta { get; }

        public double LengthSquared => NorthingDelta * NorthingDelta + EastingDelta * EastingDelta;
        public double Length => Math.Sqrt(LengthSquared);

        public static GeoDelta operator *(double factor, GeoDelta delta)
        {
            return new GeoDelta(factor * delta.NorthingDelta, factor * delta.EastingDelta);
        }
    }
}
