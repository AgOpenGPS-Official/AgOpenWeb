// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

﻿using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace AgOpenWeb.Models
{
    public class WorkedArea
    {
        private readonly List<QuadStrip> _unsavedWork = new List<QuadStrip>();

        public WorkedArea()
        {
            Area = new GeoArea();
        }

        public ReadOnlyCollection<QuadStrip> UnsavedWork => _unsavedWork.AsReadOnly();

        public GeoArea Area { get; private set; }

        public void AddStrip(QuadStrip strip)
        {
            Area += strip.Area;
            _unsavedWork.Add(strip);
        }

        public void ResetUnsavedWork()
        {
            _unsavedWork.Clear();
        }
    }
}
