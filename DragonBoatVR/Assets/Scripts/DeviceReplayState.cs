using System;
using System.IO;
using UnityEngine;

namespace DragonBoatVR
{
    // Header-only snapshots. These are model state, never measured hardware configuration.
    [Serializable]
    public class BoatBuoyancyReplayState
    {
        public int version = 1;
        public Vector3 lastWorldVelocity;
        public bool hasLastVelocity;
        public Vector3 localLinearAcceleration;
        public Vector3 currentEulerAngles;
        public float currentSpeedKmh, draft, waterElevation;
        public int submergedCount;
        public float[] probeDepths;
        public bool[] probeWet;
    }

    [Serializable]
    public class StewartPlatformReplayState
    {
        public int version = 1;
        public Vector3 currentPosition, currentEuler;
        public Vector3 targetEuler, targetTranslationM, actualEuler, actualTranslationM;
        public Vector3 rawTargetEuler, rawTargetTranslationM;
        public float restBoatY;
        public bool hasRestY;
        public uint sequence;
        public double simulationTime;
        public float[] actuatorLengthsMm, targetActuatorLengthsMm, neutralLengthsMm;
        public int kinematicRank;
        public float pitchSyncErrorDeg, rollSyncErrorDeg, strokeUsage;
        public PlatformMotionRequest lastRequest;
        public PlatformDeviceFeedback feedback;
    }

    [Serializable]
    public class CableDriveReplayState
    {
        public int version = 1;
        public ReplayRandomState random;
        public float sendTimer;
        public uint packetSequence, standaloneSequence;
        public float[] cableLengthsMm, motorTensionsN;
        public Vector3[] directions, attachmentsWorld, anchorsWorld, arms;
        public float[] solverTensions, solverSum, solverTarget, lower, upper, solverColumns;
        public float resultantTensionN, allocationRatio, allocationErrorN;
        public float restorationRate, forceRestorationRate, directionErrorDegrees;
        public Vector3 modeledForceWorldN, modeledTorqueAtGripWorldNm;
        public CableDeviceFeedback feedback;
    }

    [Serializable]
    public class ReplayRandomState
    {
        public int version = 1;
        public int indexA, indexB;
        public int[] values;
    }

    /// <summary>Explicit state for the seeded subtractive generator used by System.Random in
    /// this Unity runtime. Restoring is constant-time and preserves the nonzero sensor-noise stream.</summary>
    internal sealed class ReplayableRandom
    {
        readonly int[] _values = new int[56];
        int _indexA, _indexB;

        public ReplayableRandom(int seed)
        {
            int subtraction = seed == int.MinValue ? int.MaxValue : Math.Abs(seed);
            int mj = 161803398 - subtraction;
            _values[55] = mj;
            int mk = 1;
            for (int i = 1; i < 55; i++)
            {
                int ii = 21 * i % 55;
                _values[ii] = mk;
                mk = mj - mk;
                if (mk < 0) mk += int.MaxValue;
                mj = _values[ii];
            }
            for (int k = 1; k < 5; k++)
                for (int i = 1; i < 56; i++)
                {
                    _values[i] -= _values[1 + (i + 30) % 55];
                    if (_values[i] < 0) _values[i] += int.MaxValue;
                }
            _indexB = 21;
        }

        public double NextDouble()
        {
            if (++_indexA >= 56) _indexA = 1;
            if (++_indexB >= 56) _indexB = 1;
            int sample = _values[_indexA] - _values[_indexB];
            if (sample == int.MaxValue) sample--;
            if (sample < 0) sample += int.MaxValue;
            _values[_indexA] = sample;
            return sample * (1d / int.MaxValue);
        }

        public ReplayRandomState CaptureState()
        {
            return new ReplayRandomState
                { indexA = _indexA, indexB = _indexB, values = (int[])_values.Clone() };
        }

        public static void ValidateState(ReplayRandomState state)
        {
            if (state == null || state.version != 1 || state.values == null || state.values.Length != 56 ||
                state.indexA < 0 || state.indexA > 55 || state.indexB < 1 || state.indexB > 55 ||
                state.values[0] != 0 || (state.indexA == 0 ? state.indexB != 21 :
                    (state.indexB - state.indexA + 55) % 55 != 21))
                throw new InvalidDataException("Invalid cable sensor random state.");
            bool nonzero = false;
            for (int i = 1; i < 56; i++)
            {
                if (state.values[i] < 0 || state.values[i] >= int.MaxValue)
                    throw new InvalidDataException("Invalid cable sensor random value.");
                nonzero |= state.values[i] != 0;
            }
            if (!nonzero) throw new InvalidDataException("Degenerate cable sensor random state.");
        }

        public void RestoreState(ReplayRandomState state)
        {
            ValidateState(state);
            Array.Copy(state.values, _values, 56);
            _indexA = state.indexA;
            _indexB = state.indexB;
        }
    }

    internal static class DeviceReplayValue
    {
        public static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        public static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidDataException(message);
        }
        public static void Array(float[] values, int count, string name)
        {
            Require(values != null && values.Length == count, "Invalid replay array: " + name);
            foreach (float value in values) Require(SimulationValue.Finite(value), "Nonfinite replay array: " + name);
        }
        public static void Array(Vector3[] values, int count, string name)
        {
            Require(values != null && values.Length == count, "Invalid replay array: " + name);
            foreach (Vector3 value in values) Require(SimulationValue.Finite(value), "Nonfinite replay array: " + name);
        }
        public static PlatformDeviceFeedback Copy(PlatformDeviceFeedback f) => f == null ? null : new PlatformDeviceFeedback
        {
            commandSequence = f.commandSequence, timeSeconds = f.timeSeconds, simulated = f.simulated,
            valid = f.valid, capabilityLimited = f.capabilityLimited, status = f.status,
            eulerDegrees = f.eulerDegrees, translationM = f.translationM,
            actuatorLengthsMm = f.actuatorLengthsMm == null ? null : (float[])f.actuatorLengthsMm.Clone()
        };
        public static CableDeviceFeedback Copy(CableDeviceFeedback f) => f == null ? null : new CableDeviceFeedback
        {
            commandSequence = f.commandSequence, timeSeconds = f.timeSeconds, simulated = f.simulated,
            valid = f.valid, capabilityLimited = f.capabilityLimited, status = f.status,
            forceWorldN = f.forceWorldN, torqueAtGripWorldNm = f.torqueAtGripWorldNm,
            forceResidualN = f.forceResidualN, torqueResidualNm = f.torqueResidualNm,
            lengthsMm = f.lengthsMm == null ? null : (float[])f.lengthsMm.Clone(),
            tensionsN = f.tensionsN == null ? null : (float[])f.tensionsN.Clone()
        };
    }
}
