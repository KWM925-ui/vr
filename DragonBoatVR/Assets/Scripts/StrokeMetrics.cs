using System;
using UnityEngine;

namespace DragonBoatVR
{
    /// <summary>One observation per completed physics step. Force is the unsmoothed water-model
    /// magnitude, not a force request or a measured device response.</summary>
    public struct StrokeObservation
    {
        public double timeSeconds;
        public float fixedStepSeconds;
        public bool inputValid;
        public bool bladeInWater;
        public bool waveInducedEntry;
        public float modelForceN;
        public Vector3 boatPosition;
    }

    /// <summary>Statistics consume physics observations only; reading them from any render rate
    /// has no effect. A complete dry-to-wet-to-dry stroke contributes sum(|F_model| * physicsDt).
    /// Cadence uses successive entry times, including both the drive and recovery portions.</summary>
    public sealed class StrokeMetrics
    {
        public int StrokeCount { get; private set; }
        public int ObservedSteps { get; private set; }
        public double LastImpulse { get; private set; }
        public double AverageImpulse => StrokeCount > 0 ? _totalImpulse / StrokeCount : 0d;
        public double CurrentImpulse => _inStroke ? _currentImpulse : 0d;
        public float LastPeakForceN { get; private set; }
        public float CurrentPeakForceN => _inStroke ? _peakForce : 0f;
        public double StrokeRateSpm { get; private set; }
        public bool HasStrokeRate { get; private set; }
        public double DistanceMeters { get; private set; }
        public bool StrokeInProgress => _inStroke;

        bool _hasObservation, _hasBoatPosition, _armed, _inStroke, _hasCompletedEntry;
        double _lastTime, _entryTime, _lastCompletedEntryTime;
        double _currentImpulse, _totalImpulse;
        float _peakForce;
        Vector3 _lastBoatPosition;

        public void Reset()
        {
            StrokeCount = ObservedSteps = 0;
            LastImpulse = DistanceMeters = _totalImpulse = 0d;
            LastPeakForceN = 0f;
            _hasObservation = _hasBoatPosition = false;
            _lastTime = 0d;
            _lastBoatPosition = Vector3.zero;
            InterruptStroke();
        }

        /// <returns>True only when this is a new finite physics observation.</returns>
        public bool Observe(StrokeObservation observation)
        {
            // A second delivery of the same physics step never changes either the statistics
            // or the continuity state, even if its other fields are malformed.
            if (_hasObservation && Finite(observation.timeSeconds) && observation.timeSeconds == _lastTime)
                return false;
            if (!Finite(observation.timeSeconds) || !SimulationValue.Finite(observation.fixedStepSeconds) ||
                observation.fixedStepSeconds <= 0f || !SimulationValue.Finite(observation.boatPosition))
            {
                InterruptStroke();
                // Unknown geometry breaks the distance baseline, but is not a new session.
                // Keep completed statistics and the last known time for reversal detection.
                _hasBoatPosition = false;
                return false;
            }
            if (_hasObservation && observation.timeSeconds < _lastTime) Reset();

            if (_hasObservation)
            {
                // Missing physics observations cannot be filled with a render delta or a held force.
                double elapsed = observation.timeSeconds - _lastTime;
                if (Math.Abs(elapsed - observation.fixedStepSeconds) > Math.Max(1e-6, observation.fixedStepSeconds * 1e-4))
                    InterruptStroke();
            }
            if (_hasBoatPosition)
            {
                // Horizontal travel is independent of paddle validity and vertical wave heave.
                double dx = (double)observation.boatPosition.x - _lastBoatPosition.x;
                double dz = (double)observation.boatPosition.z - _lastBoatPosition.z;
                DistanceMeters += Math.Sqrt(dx * dx + dz * dz);
            }
            _lastTime = observation.timeSeconds;
            _lastBoatPosition = observation.boatPosition;
            _hasObservation = _hasBoatPosition = true;
            ObservedSteps++;

            if (!observation.inputValid || !SimulationValue.Finite(observation.modelForceN) || observation.modelForceN < 0f)
            {
                // An expired input is not an exit event. Discard its incomplete stroke and wait
                // for a new valid dry observation before recognising another entry.
                InterruptStroke();
                return true;
            }
            if (!observation.bladeInWater)
            {
                if (_inStroke)
                {
                    LastImpulse = _currentImpulse;
                    LastPeakForceN = _peakForce;
                    _totalImpulse += LastImpulse;
                    StrokeCount++;
                    _lastCompletedEntryTime = _entryTime;
                    _hasCompletedEntry = true;
                    _inStroke = false;
                }
                _armed = true;
                return true;
            }
            if (!_inStroke && _armed)
            {
                _armed = false;
                if (observation.waveInducedEntry)
                {
                    InterruptStroke();
                    return true;
                }
                _inStroke = true;
                _entryTime = observation.timeSeconds;
                _currentImpulse = 0d;
                _peakForce = 0f;
                if (_hasCompletedEntry)
                {
                    double period = _entryTime - _lastCompletedEntryTime;
                    HasStrokeRate = period > 0d;
                    StrokeRateSpm = HasStrokeRate ? 60d / period : 0d;
                }
            }
            if (_inStroke)
            {
                _currentImpulse += (double)observation.modelForceN * observation.fixedStepSeconds;
                _peakForce = Mathf.Max(_peakForce, observation.modelForceN);
            }
            return true;
        }

        void InterruptStroke()
        {
            _armed = _inStroke = _hasCompletedEntry = false;
            _currentImpulse = _entryTime = _lastCompletedEntryTime = 0d;
            _peakForce = 0f;
            HasStrokeRate = false;
            StrokeRateSpm = 0d;
        }

        static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
