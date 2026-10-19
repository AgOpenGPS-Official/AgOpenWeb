// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

﻿namespace AgOpenWeb.Models
{
    public class SharedFieldProperties
    {
        public SharedFieldProperties()
        {
            DriftCompensation = new GeoDelta(0.0, 0.0);
        }

        public GeoDelta DriftCompensation { get; set; }
    }
}
