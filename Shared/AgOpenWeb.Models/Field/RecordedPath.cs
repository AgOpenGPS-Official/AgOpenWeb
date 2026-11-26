// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

﻿using System.Collections.Generic;

namespace AgOpenWeb.Models
{

    public class RecordedPath : GeoPathBase
    {
        public List<RecordedPoint> PointList = new List<RecordedPoint>();

        public override int Count
        {
            get { return PointList.Count; }
        }

        public override GeoCoord this[int index]
        {
            get { return PointList[index].AsGeoCoord; }
        }

        public void Clear()
        {
            PointList.Clear();
        }

        public void Add(RecordedPoint point)
        {
            PointList.Add(point);
        }
    }

}
