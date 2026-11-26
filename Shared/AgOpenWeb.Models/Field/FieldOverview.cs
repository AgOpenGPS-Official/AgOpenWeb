// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

﻿
namespace AgOpenWeb.Models
{
    public class FieldOverview
    {
        public FieldOverview(string creator, string offsets, string convergence, Wgs84 start)
        {
            Creator = creator;
            Offsets = offsets;
            Convergence = convergence;
            Start = start;
        }

        public string Creator { get; }
        public string Offsets { get; }
        public string Convergence { get; }

        public Wgs84 Start { get; }
    }
}
