// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

﻿namespace AgOpenWeb.Models
{
    public class DebugGeoPolygon : GeoPolygon
    {
        private readonly GeoPolygon _goldenPolygon;
        private DebugGeoPolygon()
        {
        }

        public DebugGeoPolygon(GeoPolygon goldenPolygon)
        {
            _goldenPolygon = goldenPolygon;
        }

        public override void Add(GeoCoord coord)
        {
            base.Add(coord);
            DebugAsserts.AreEqual(coord, _goldenPolygon[Count - 1]);

        }

        public void AreEqual()
        {
            DebugAsserts.AreEqual(Count, _goldenPolygon.Count);
        }
    }
}
