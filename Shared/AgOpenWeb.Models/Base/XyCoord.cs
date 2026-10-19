// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

﻿namespace AgOpenWeb.Models
{
    public struct XyCoord
    {
        public XyCoord(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; }
        public double Y { get; }


        public static XyCoord operator +(XyCoord coord, XyDelta delta)
        {
            return new XyCoord(coord.X + delta.DeltaX, coord.Y + delta.DeltaY);
        }

        public static XyCoord operator -(XyCoord coord, XyDelta delta)
        {
            return new XyCoord(coord.X - delta.DeltaX, coord.Y - delta.DeltaY);
        }

    }
}
