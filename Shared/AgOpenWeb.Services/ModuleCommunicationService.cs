// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Models.Communication;
using AgOpenWeb.Models.Configuration;

namespace AgOpenWeb.Services
{
    /// <summary>
    /// Core module communication service.
    /// Handles work switch and steer switch logic, raising events when UI actions are needed.
    /// Replaces FormGPS.PerformClick() calls with clean event-based architecture.
    /// Reads switch configuration from the injected ConfigurationStore's Tool config.
    /// </summary>
    public class ModuleCommunicationService : IModuleCommunicationService
    {
        private readonly ConfigurationStore _configStore;

        public ModuleCommunicationService(ConfigurationStore configStore)
        {
            _configStore = configStore;
        }

        // Access config from ConfigurationStore
        private ToolConfig Tool => _configStore.Tool;
        private MachineConfig Machine => _configStore.Machine;

        // Section control data
        public byte[] SectionControlBytes { get; } = new byte[9];
        public byte[] PreviousSectionControlBytes { get; } = new byte[9];

        // Section control indices (constants matching original)
        public const int SW_HEADER = 0;
        public const int SW_MAIN = 1;
        public const int SW_AUTO_GR0 = 2;
        public const int SW_AUTO_GR1 = 3;
        public const int SW_NUM_SECTIONS = 4;
        public const int SW_ON_GR0 = 5;
        public const int SW_OFF_GR0 = 6;
        public const int SW_ON_GR1 = 7;
        public const int SW_OFF_GR1 = 8;

        // Steering data
        public int PwmDisplay { get; set; }
        public double ActualSteerAngleDegrees { get; set; }
        public int ActualSteerAngleChart { get; set; }
        public int SensorData { get; set; } = -1;

        // Safety
        public bool IsOutOfBounds { get; set; } = true;

        // Work switch configuration - read from ConfigurationStore
        public bool IsWorkSwitchActiveLow => Tool.IsWorkSwitchActiveLow;
        public bool IsRemoteWorkSystemOn { get; set; } // Runtime state, not persisted
        public bool IsWorkSwitchEnabled => Tool.IsWorkSwitchEnabled;
        public bool IsWorkSwitchManualSections => Tool.IsWorkSwitchManualSections;
        public bool IsSteerWorkSwitchManualSections => Tool.IsSteerSwitchManualSections;
        public bool IsSteerWorkSwitchEnabled => Tool.IsSteerSwitchEnabled;

        // Machine config accessors for hydraulic control
        public bool HydraulicLiftEnabled => Machine.HydraulicLiftEnabled;
        public int RaiseTime => Machine.RaiseTime;
        public int LowerTime => Machine.LowerTime;
        public double LookAhead => Machine.LookAhead;
        public bool InvertRelay => Machine.InvertRelay;

        // User values (custom data sent to machine module)
        public int User1Value => Machine.User1Value;
        public int User2Value => Machine.User2Value;
        public int User3Value => Machine.User3Value;
        public int User4Value => Machine.User4Value;


        // Switch states (runtime, from hardware)
        public bool WorkSwitchHigh { get; set; }
        public bool SteerSwitchHigh { get; set; }

        // Previous switch states (for change detection)
        private bool _oldWorkSwitchHigh;
        private bool _oldSteerSwitchHigh;
        private bool _oldSteerSwitchRemote;

        // Events raised when UI actions are needed
        public event EventHandler<AutoSteerToggleEventArgs>? AutoSteerToggleRequested;
        public event EventHandler<SectionMasterToggleEventArgs>? SectionMasterToggleRequested;

        /// <summary>
        /// Check work and steer switch states, raising events when UI actions are needed.
        /// This replaces the direct PerformClick() calls in original CModuleComm.
        /// </summary>
        /// <param name="currentState">Current UI state (button states, AutoSteer status)</param>
        public void CheckSwitches(ModuleSwitchState currentState)
        {
            // AutoSteerAuto button enable logic (Ray Bear inspired code)
            if (currentState.IsAutoSteerAuto && SteerSwitchHigh != _oldSteerSwitchRemote)
            {
                _oldSteerSwitchRemote = SteerSwitchHigh;

                // Steer switch is active low
                if (SteerSwitchHigh == currentState.IsAutoSteerOn)
                {
                    // Request AutoSteer toggle. The steer switch → sections check below must
                    // see the state after the toggle, or engaging turns sections off.
                    var args = new AutoSteerToggleEventArgs();
                    AutoSteerToggleRequested?.Invoke(this, args);
                    if (args.IsAutoSteerOnAfter is bool on)
                        currentState.IsAutoSteerOn = on;
                }
            }

            // Remote work system logic
            if (IsRemoteWorkSystemOn)
            {
                CheckWorkSwitch(currentState);
                CheckSteerWorkSwitch(currentState);
            }
        }

        /// <summary>
        /// Check work switch state and request section master button toggles
        /// </summary>
        private void CheckWorkSwitch(ModuleSwitchState currentState)
        {
            if (!IsWorkSwitchEnabled || _oldWorkSwitchHigh == WorkSwitchHigh)
                return;

            _oldWorkSwitchHigh = WorkSwitchHigh;

            if (WorkSwitchHigh != IsWorkSwitchActiveLow)
            {
                // Work switch is active - turn sections on
                if (IsWorkSwitchManualSections)
                {
                    if (currentState.ManualButtonState != ButtonStates.On)
                    {
                        SectionMasterToggleRequested?.Invoke(this, new SectionMasterToggleEventArgs
                        {
                            Button = SectionMasterToggleEventArgs.SectionMasterButton.Manual
                        });
                    }
                }
                else
                {
                    if (currentState.AutoButtonState != ButtonStates.Auto)
                    {
                        SectionMasterToggleRequested?.Invoke(this, new SectionMasterToggleEventArgs
                        {
                            Button = SectionMasterToggleEventArgs.SectionMasterButton.Auto
                        });
                    }
                }
            }
            else
            {
                // Work switch is inactive - turn sections off
                // Check both buttons and turn off whichever is on
                if (currentState.AutoButtonState != ButtonStates.Off)
                {
                    SectionMasterToggleRequested?.Invoke(this, new SectionMasterToggleEventArgs
                    {
                        Button = SectionMasterToggleEventArgs.SectionMasterButton.Auto
                    });
                }
                if (currentState.ManualButtonState != ButtonStates.Off)
                {
                    SectionMasterToggleRequested?.Invoke(this, new SectionMasterToggleEventArgs
                    {
                        Button = SectionMasterToggleEventArgs.SectionMasterButton.Manual
                    });
                }
            }
        }

        /// <summary>
        /// Check steer work switch state and request section master button toggles
        /// </summary>
        private void CheckSteerWorkSwitch(ModuleSwitchState currentState)
        {
            if (!IsSteerWorkSwitchEnabled || _oldSteerSwitchHigh == SteerSwitchHigh)
                return;

            _oldSteerSwitchHigh = SteerSwitchHigh;

            if ((currentState.IsAutoSteerOn && currentState.IsAutoSteerAuto)
                || (!currentState.IsAutoSteerAuto && !SteerSwitchHigh))
            {
                // Steer work switch is active - turn sections on
                if (IsSteerWorkSwitchManualSections)
                {
                    if (currentState.ManualButtonState != ButtonStates.On)
                    {
                        SectionMasterToggleRequested?.Invoke(this, new SectionMasterToggleEventArgs
                        {
                            Button = SectionMasterToggleEventArgs.SectionMasterButton.Manual
                        });
                    }
                }
                else
                {
                    if (currentState.AutoButtonState != ButtonStates.Auto)
                    {
                        SectionMasterToggleRequested?.Invoke(this, new SectionMasterToggleEventArgs
                        {
                            Button = SectionMasterToggleEventArgs.SectionMasterButton.Auto
                        });
                    }
                }
            }
            else
            {
                // Steer work switch is inactive - turn sections off
                // Check both buttons and turn off whichever is on
                if (currentState.AutoButtonState != ButtonStates.Off)
                {
                    SectionMasterToggleRequested?.Invoke(this, new SectionMasterToggleEventArgs
                    {
                        Button = SectionMasterToggleEventArgs.SectionMasterButton.Auto
                    });
                }
                if (currentState.ManualButtonState != ButtonStates.Off)
                {
                    SectionMasterToggleRequested?.Invoke(this, new SectionMasterToggleEventArgs
                    {
                        Button = SectionMasterToggleEventArgs.SectionMasterButton.Manual
                    });
                }
            }
        }

        /// <summary>
        /// AgOpenGPS DoRemoteSwitches → HandleButtonHardware (MTZ8302): the main byte works on
        /// a change, an On bit turns its section On, an Off bit holds it Off, and an Off bit
        /// released with the master on Auto puts the section back on Auto. Switch hardware
        /// (main bit 2, hagre's level-driven box) is not handled: those packets change nothing.
        /// </summary>
        public RemoteSwitchDecision CheckRemoteSwitches(byte[] switches, bool masterAuto,
            System.Collections.Generic.IReadOnlyList<SectionControlState> sections)
        {
            var ss = SectionControlBytes;
            var ssP = PreviousSectionControlBytes;
            Array.Copy(switches, 1, ss, 1, 8);

            int n = Math.Min(_configStore.NumSections, sections.Count);
            var state = new SectionButtonState[n];
            for (int i = 0; i < n; i++) state[i] = sections[i].ButtonState;

            var master = RemoteMasterCommand.None;
            if ((ss[SW_MAIN] & 0x04) == 0)
            {
                if (ss[SW_MAIN] != ssP[SW_MAIN])
                {
                    // The master Auto button, with every section to its state.
                    if ((ss[SW_MAIN] & 1) == 1)
                    {
                        master = RemoteMasterCommand.AutoOn;
                        masterAuto = true;
                        Array.Fill(state, SectionButtonState.Auto);
                    }
                    if ((ss[SW_MAIN] & 2) == 2)
                    {
                        master = RemoteMasterCommand.AutoOff;
                        masterAuto = false;
                        Array.Fill(state, SectionButtonState.Off);
                    }
                    ssP[SW_MAIN] = ss[SW_MAIN];
                }

                if (Tool.IsSectionsNotZones)
                {
                    RemoteOn(state, ss[SW_ON_GR0], 0);
                    RemoteOn(state, ss[SW_ON_GR1], 8);
                    ssP[SW_ON_GR0] = ss[SW_ON_GR0];
                    ssP[SW_ON_GR1] = ss[SW_ON_GR1];

                    if (masterAuto)
                    {
                        RemoteOffReleased(state, ssP[SW_OFF_GR0], ss[SW_OFF_GR0], 0);
                        RemoteOffReleased(state, ssP[SW_OFF_GR1], ss[SW_OFF_GR1], 8);
                    }
                    ssP[SW_OFF_GR0] = ss[SW_OFF_GR0];
                    ssP[SW_OFF_GR1] = ss[SW_OFF_GR1];

                    RemoteOff(state, ss[SW_OFF_GR0], 0);
                    RemoteOff(state, ss[SW_OFF_GR1], 8);
                }
                else
                {
                    RemoteZones(state, masterAuto, ssP[SW_OFF_GR0]);
                    ssP[SW_ON_GR0] = ss[SW_ON_GR0];
                    ssP[SW_OFF_GR0] = ss[SW_OFF_GR0];
                }
            }

            return new RemoteSwitchDecision(master, state);
        }

        private static bool Bit(byte b, int i) => (b & (1 << i)) != 0;

        private static void RemoteOn(SectionButtonState[] state, byte on, int first)
        {
            for (int i = 0; i < 8 && first + i < state.Length; i++)
                if (Bit(on, i)) state[first + i] = SectionButtonState.On;
        }

        private static void RemoteOffReleased(SectionButtonState[] state, byte offBefore, byte off, int first)
        {
            for (int i = 0; i < 8 && first + i < state.Length; i++)
                if (Bit(offBefore, i) && !Bit(off, i) && state[first + i] == SectionButtonState.Off)
                    state[first + i] = SectionButtonState.Auto;
        }

        private static void RemoteOff(SectionButtonState[] state, byte off, int first)
        {
            for (int i = 0; i < 8 && first + i < state.Length; i++)
                if (Bit(off, i)) state[first + i] = SectionButtonState.Off;
        }

        /// <summary>
        /// Zones, group 0 only: zone z's state is its last section's, as in AgOpenGPS. An On bit
        /// turns a zone that isn't on Auto On; an Off bit released with the master on Auto puts
        /// an Off zone on Auto; an Off bit holds the zone Off.
        /// </summary>
        private void RemoteZones(SectionButtonState[] state, bool masterAuto, byte offBefore)
        {
            var ss = SectionControlBytes;
            var ranges = Tool.ZoneRanges;
            int zones = Math.Clamp(Tool.Zones, 0, 8);
            for (int z = 0; z < zones && z + 1 < ranges.Length; z++)
            {
                int start = ranges[z];
                int end = Math.Min(ranges[z + 1], state.Length);
                if (end <= start) continue;

                if (Bit(ss[SW_ON_GR0], z) && state[end - 1] != SectionButtonState.Auto)
                    Array.Fill(state, SectionButtonState.On, start, end - start);
                if (masterAuto && Bit(offBefore, z) && !Bit(ss[SW_OFF_GR0], z) && state[end - 1] == SectionButtonState.Off)
                    Array.Fill(state, SectionButtonState.Auto, start, end - start);
                if (Bit(ss[SW_OFF_GR0], z))
                    Array.Fill(state, SectionButtonState.Off, start, end - start);
            }
        }
    }
}
