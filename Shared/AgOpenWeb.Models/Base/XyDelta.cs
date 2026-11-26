// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

﻿using System;

namespace AgOpenWeb.Models
{
    public struct XyDelta
    {
        public XyDelta(XyCoord fromCoord, XyCoord toCoord)
        {
            DeltaX = toCoord.X - fromCoord.X;
            DeltaY = toCoord.Y - fromCoord.Y;
        }

        public XyDelta(double deltaX, double deltaY)
        {
            DeltaX = deltaX;
            DeltaY = deltaY;
        }

        public double DeltaX { get; }
        public double DeltaY { get; }

    }
}
