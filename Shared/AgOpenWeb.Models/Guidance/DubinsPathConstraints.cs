// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

﻿using AgOpenWeb.Models;

namespace AgOpenWeb.Models
{
    public class DubinsPathConstraints
    {

        public DubinsPathConstraints(GeoCoordDir startConstraint, GeoCoordDir goalConstraint, double radiusConstraint)
        {
            StartConstraint = startConstraint;
            GoalConstraint = goalConstraint;
            RadiusConstraint = radiusConstraint;
        }

        public GeoCoordDir StartConstraint { get; }
        public GeoCoordDir GoalConstraint { get; }
        public double RadiusConstraint { get; }
    }
}
