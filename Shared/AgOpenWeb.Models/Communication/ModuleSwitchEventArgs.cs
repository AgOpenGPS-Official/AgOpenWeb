// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;

namespace AgOpenWeb.Models.Communication
{
    /// <summary>
    /// Event args for AutoSteer toggle requests
    /// </summary>
    public class AutoSteerToggleEventArgs : EventArgs
    {
        /// <summary>
        /// Set by a handler that ran the toggle straight away: AutoSteer's state afterwards
        /// (false if the engage was refused). The steer switch → sections check reads it,
        /// as AgOpenGPS reads the button after its synchronous PerformClick.
        /// </summary>
        public bool? IsAutoSteerOnAfter { get; set; }
    }

    /// <summary>
    /// Event args for section master button toggle requests
    /// </summary>
    public class SectionMasterToggleEventArgs : EventArgs
    {
        /// <summary>
        /// Which button to toggle: Auto or Manual
        /// </summary>
        public SectionMasterButton Button { get; set; }

        public enum SectionMasterButton
        {
            Auto,
            Manual
        }
    }
}
