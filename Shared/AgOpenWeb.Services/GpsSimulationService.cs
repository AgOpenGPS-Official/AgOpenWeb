// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.GPS;

namespace AgOpenWeb.Services
{
    /// <summary>
    /// Core GPS simulation service.
    /// Simulates GPS position updates based on steering input and vehicle movement.
    /// </summary>
    public class GpsSimulationService : IGpsSimulationService
    {
        private Wgs84 _currentPosition;
        private double _headingRadians;
        private double _steerAngle;
        private double _steerAngleAverage;
        private double _stepDistance;
        // Monotonic timestamp of the previous Tick (0 = none yet). Used to scale the
        // per-tick advance by REAL elapsed time so motion is independent of tick
        // cadence — see the dt-scaling note in Tick.
        private long _lastTickTimestamp;
        private bool _isAcceleratingForward;
        private bool _isAcceleratingBackward;
        private Wgs84 _initialPosition;

        // Constants from original code
        private const double DegreesToRadians = 0.0165329252; // π/180
        private const double TwoPI = Math.PI * 2.0;
        private const double SimulatedHdop = 0.7;
        private const int SimulatedSatellites = 12;

        public event EventHandler<GpsSimulationEventArgs>? GpsDataUpdated;

        public Wgs84 CurrentPosition => _currentPosition;
        public double HeadingRadians => _headingRadians;

        public double StepDistance
        {
            get => _stepDistance;
            set => _stepDistance = value;
        }

        public double SteerAngle
        {
            get => _steerAngle;
            set => _steerAngle = value;
        }

        public double SteerAngleAverage => _steerAngleAverage;

        public bool IsAcceleratingForward
        {
            get => _isAcceleratingForward;
            set => _isAcceleratingForward = value;
        }

        public bool IsAcceleratingBackward
        {
            get => _isAcceleratingBackward;
            set => _isAcceleratingBackward = value;
        }

        public GpsSimulationService()
        {
            _currentPosition = new Wgs84();
            _initialPosition = new Wgs84();
            _headingRadians = 0;
            _steerAngle = 0;
            _steerAngleAverage = 0;
            _stepDistance = 0;
            _lastTickTimestamp = 0;
            _isAcceleratingForward = false;
            _isAcceleratingBackward = false;
        }

        public void Initialize(Wgs84 startPosition)
        {
            _currentPosition = startPosition;
            _initialPosition = startPosition;
            _headingRadians = 0;
            _steerAngle = 0;
            _steerAngleAverage = 0;
            _stepDistance = 0;
            _lastTickTimestamp = 0;
            _isAcceleratingForward = false;
            _isAcceleratingBackward = false;
        }

        public void Tick(double steerAngleDegrees)
        {
            _steerAngle = steerAngleDegrees;

            // Smooth the steer angle (original algorithm from CSim.DoSimTick)
            SmoothSteerAngle();

            // Time-scale the per-tick advance. The original CSim moved a fixed
            // _stepDistance every tick, which assumes a perfectly regular ~30 Hz
            // tick. That holds under Avalonia's DispatcherTimer but NOT under a
            // jittery System.Threading.Timer (the headless host), where uneven tick
            // spacing makes the real ground speed wander even though the reported
            // speed is constant — the dead-reckoner then over/undershoots and the
            // web map re-anchors with a visible snap. Scaling the step by
            // (realDt / nominalDt) makes motion track wall-clock time, so the
            // cadence no longer matters. At a steady 30 Hz scale ≈ 1.0, so the
            // windowed/mobile behaviour is unchanged. Clamped to absorb the first
            // tick after start and any long pause without a position jump.
            double scale = 1.0;
            long nowTs = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_lastTickTimestamp != 0)
            {
                const double NominalDtSeconds = 1.0 / 30.0; // _simulatorTimer interval (33 ms)
                double dtSeconds = (nowTs - _lastTickTimestamp) / (double)System.Diagnostics.Stopwatch.Frequency;
                scale = dtSeconds / NominalDtSeconds;
                if (scale < 0.25) scale = 0.25;
                if (scale > 4.0) scale = 4.0;
            }
            _lastTickTimestamp = nowTs;
            double effectiveStep = _stepDistance * scale;

            // Calculate heading change based on steering angle
            // Using simplified bicycle model: heading_change = step_distance * tan(steer_angle) / 2
            double headingChange = effectiveStep * Math.Tan(_steerAngleAverage * DegreesToRadians) / 2.0;
            _headingRadians += headingChange;

            // Normalize heading to [0, 2π)
            while (_headingRadians >= TwoPI)
                _headingRadians -= TwoPI;
            while (_headingRadians < 0)
                _headingRadians += TwoPI;

            // Calculate speed (km/h) from step distance (the speed SETTING, not the
            // time-scaled step — reported speed stays steady for the dead-reckoner).
            // Original: Math.Abs(Math.Round(4 * stepDistance * 10, 2))
            double speedKmh = Math.Round(4.0 * _stepDistance * 10.0, 2);

            // Calculate next position using WGS84 bearing/distance
            _currentPosition = _currentPosition.CalculateNewPostionFromBearingDistance(_headingRadians, effectiveStep);

            // Simulate altitude
            double altitude = SimulateAltitude(_currentPosition);

            // Handle acceleration
            UpdateAcceleration();

            // Build simulated GPS data package
            // NOTE: LocalPosition conversion is NOT done here - WinForms wrapper handles it
            // because LocalPlane initialization is a UI concern
            var data = new SimulatedGpsData
            {
                Position = _currentPosition,
                LocalPosition = new GeoCoord(),  // Will be filled by WinForms wrapper
                HeadingRadians = _headingRadians,
                HeadingDegrees = ToDegrees(_headingRadians),
                SpeedKmh = speedKmh,
                SteerAngleDegrees = _steerAngleAverage,
                Hdop = SimulatedHdop,
                Altitude = altitude,
                SatellitesTracked = SimulatedSatellites,
                StepDistance = _stepDistance
            };

            // Raise event
            GpsDataUpdated?.Invoke(this, new GpsSimulationEventArgs(data));
        }

        public void Reset()
        {
            _currentPosition = _initialPosition;
            _headingRadians = 0;
            _steerAngle = 0;
            _steerAngleAverage = 0;
            _stepDistance = 0;
            _lastTickTimestamp = 0;
            _isAcceleratingForward = false;
            _isAcceleratingBackward = false;
        }

        public void SetHeading(double headingRadians)
        {
            _headingRadians = headingRadians;

            // Normalize heading to [0, 2π)
            while (_headingRadians >= TwoPI)
                _headingRadians -= TwoPI;
            while (_headingRadians < 0)
                _headingRadians += TwoPI;
        }

        /// <summary>
        /// Smooth steer angle using original CSim algorithm
        /// </summary>
        private void SmoothSteerAngle()
        {
            double diff = Math.Abs(_steerAngle - _steerAngleAverage);

            if (diff > 11)
            {
                if (_steerAngleAverage >= _steerAngle)
                {
                    _steerAngleAverage -= 6.0;
                }
                else
                {
                    _steerAngleAverage += 6.0;
                }
            }
            else if (diff > 5)
            {
                if (_steerAngleAverage >= _steerAngle)
                {
                    _steerAngleAverage -= 2.0;
                }
                else
                {
                    _steerAngleAverage += 2.0;
                }
            }
            else if (diff > 1)
            {
                if (_steerAngleAverage >= _steerAngle)
                {
                    _steerAngleAverage -= 0.5;
                }
                else
                {
                    _steerAngleAverage += 0.5;
                }
            }
            else
            {
                _steerAngleAverage = _steerAngle;
            }
        }

        /// <summary>
        /// Update step distance based on acceleration state
        /// Speed range: -10 to +25 kph
        /// stepDistance = speedKph / 40, so:
        ///   +25 kph = 0.625 stepDistance
        ///   -10 kph = -0.25 stepDistance
        /// </summary>
        private void UpdateAcceleration()
        {
            const double MaxForwardStep = 0.625;   // 25 kph
            const double MaxReverseStep = -0.25;   // -10 kph
            const double AccelStep = 0.03;         // Faster acceleration
            const double DecelStep = 0.02;         // Slightly slower deceleration

            if (_isAcceleratingForward)
            {
                _isAcceleratingBackward = false;
                _stepDistance += AccelStep;
                if (_stepDistance > MaxForwardStep)
                {
                    _stepDistance = MaxForwardStep;
                    _isAcceleratingForward = false;
                }
            }

            if (_isAcceleratingBackward)
            {
                _isAcceleratingForward = false;
                _stepDistance -= DecelStep;
                if (_stepDistance < MaxReverseStep)
                {
                    _stepDistance = MaxReverseStep;
                    _isAcceleratingBackward = false;
                }
            }
        }

        /// <summary>
        /// Simulate altitude based on latitude/longitude.
        /// Original algorithm from CSim.SimulateAltitude.
        /// </summary>
        private double SimulateAltitude(Wgs84 position)
        {
            double temp = Math.Abs(position.Latitude * 100);
            temp -= (int)temp;
            temp *= 100;
            double altitude = temp + 200;

            temp = Math.Abs(position.Longitude * 100);
            temp -= (int)temp;
            temp *= 100;
            altitude += temp;

            return altitude;
        }

        /// <summary>
        /// Convert radians to degrees
        /// </summary>
        private double ToDegrees(double radians)
        {
            return radians * (180.0 / Math.PI);
        }
    }
}
