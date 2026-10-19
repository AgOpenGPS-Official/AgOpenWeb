// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

﻿namespace AgOpenWeb.Models
{
    public struct GeoCoordDir
    {
        public GeoCoordDir(GeoCoord coord, GeoDir direction)
        {
            Coord = coord;
            Direction = direction;
        }

        public GeoCoord Coord { get; }
        public GeoDir Direction { get; }
    }
}
