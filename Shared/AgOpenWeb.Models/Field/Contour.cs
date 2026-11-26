// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

﻿using System.Collections.Generic;

namespace AgOpenWeb.Models
{
    public class Contour
    {
        public Contour()
        {
            Strips = new List<GeoPathWithHeading>();
            UnsavedStrips = new List<GeoPathWithHeading>();
        }

        public List<GeoPathWithHeading> Strips { get; set; }
        public List<GeoPathWithHeading> UnsavedStrips { get; set; }
    }
}
