// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using AgOpenWeb.Models.Communication;

namespace AgOpenWeb.Services.Interfaces
{
    /// <summary>
    /// Service for handling module communication switch logic.
    /// Raises events when UI actions are needed instead of directly triggering button clicks.
    /// Configuration is read from ConfigurationStore (Tool and Machine configs).
    /// </summary>
    public interface IModuleCommunicationService
    {
        // Section control data
        byte[] SectionControlBytes { get; }
        byte[] PreviousSectionControlBytes { get; }

        // Steering data
        int PwmDisplay { get; set; }
        double ActualSteerAngleDegrees { get; set; }
        int ActualSteerAngleChart { get; set; }
        int SensorData { get; set; }

        // Safety
        bool IsOutOfBounds { get; set; }

        // Work switch configuration (read from ConfigurationStore.Tool)
        bool IsWorkSwitchActiveLow { get; }
        bool IsRemoteWorkSystemOn { get; set; } // Runtime state
        bool IsWorkSwitchEnabled { get; }
        bool IsWorkSwitchManualSections { get; }
        bool IsSteerWorkSwitchManualSections { get; }
        bool IsSteerWorkSwitchEnabled { get; }

        // Machine config (read from ConfigurationStore.Machine)
        bool HydraulicLiftEnabled { get; }
        int RaiseTime { get; }
        int LowerTime { get; }
        double LookAhead { get; }
        bool InvertRelay { get; }

        // User values (custom data sent to machine module)
        int User1Value { get; }
        int User2Value { get; }
        int User3Value { get; }
        int User4Value { get; }

        // Switch states (runtime, from hardware)
        bool WorkSwitchHigh { get; set; }
        bool SteerSwitchHigh { get; set; }

        // Events
        event EventHandler<AutoSteerToggleEventArgs>? AutoSteerToggleRequested;
        event EventHandler<SectionMasterToggleEventArgs>? SectionMasterToggleRequested;

        /// <summary>
        /// Check work and steer switch states, raising events when UI actions are needed.
        /// </summary>
        void CheckSwitches(ModuleSwitchState currentState);

        /// <summary>
        /// Remote section switches, PGN 234, as AgOpenGPS DoRemoteSwitches (button hardware):
        /// what the packet does to the master Auto button and the section buttons, given the
        /// current states and the previous packet (<see cref="PreviousSectionControlBytes"/>).
        /// Call it with a field open only, as AgOpenGPS acts with a job started only.
        /// </summary>
        /// <param name="switches">The switch bytes, indexed as AgOpenGPS mc.ss (SW_MAIN …).</param>
        /// <param name="masterAuto">The master Auto button is on.</param>
        /// <param name="sections">The section buttons now.</param>
        RemoteSwitchDecision CheckRemoteSwitches(byte[] switches, bool masterAuto,
            System.Collections.Generic.IReadOnlyList<SectionControlState> sections);
    }

    public enum RemoteMasterCommand
    {
        None,
        AutoOn,
        AutoOff,
    }

    /// <summary>
    /// The master Auto button to set, and every section button's state afterwards
    /// (one per configured section).
    /// </summary>
    public readonly record struct RemoteSwitchDecision(RemoteMasterCommand Master, SectionButtonState[] Sections);
}
