// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

﻿using System.Collections.Generic;

namespace AgOpenWeb.Models
{
    public class HeadPath
    {
        public HeadPath(string name, double moveDistance, int mode, int aPoint)
        {
            Path = new GeoPathWithHeading();
            Name = name;
            MoveDistance = moveDistance;
            Mode = mode;
            APoint = aPoint;
        }

        public GeoPathWithHeading Path { get; }
        public string Name { get; }
        public double MoveDistance { get; }
        public int Mode { get; }
        public int APoint { get; }
    }

}
