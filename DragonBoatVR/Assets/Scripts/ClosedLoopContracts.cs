using System;
using UnityEngine;

namespace DragonBoatVR
{
    [Serializable]
    public sealed class ClosedLoopConfiguration
    {
        public bool enabled = true;
        public bool measured;
        public string provenance = "UNMEASURED_SOFTWARE_PLANT: local UDP controller, compliant grip and actuator response; not hardware acceptance";
        public string pythonExecutable = "python.exe";
        public float feedbackTimeoutSeconds = 0.1f;
        public float motorTauSeconds = 0.035f;
        public float motorSlewNPerSecond = 2000f;
        public float platformTauSeconds = 0.06f;
        public float platformSpeedMmPerSecond = 500f;
        public float gripStiffnessNPerM = 1600f;
        public float gripDampingNsPerM = 100f;
        public float gripMassKg = 2f;
        public float angularStiffness = 30f;
        public float angularDamping = 3f;
        public float angularInertia = 0.15f;
        // The CAD desktop fixture also selects these ideal stops. Their envelope
        // is derived from the displayed hull/paddle, not measured joint limits.
        public bool hardwareStopsEnabled;
        public float hardwareStopClearanceM = 0.02f;
    }

    [Serializable]
    public sealed class ClosedLoopCommand
    {
        public string kind = "step";
        public uint commandSequence;
        public uint sourceFeedbackSequence;
        public long sourceSampleTick;
        public double simulationTime;
        public bool active, cableValid, platformValid;
        public float[] desiredGripPosition, desiredGripRotation, desiredGripVelocity;
        // Radians per second in the same boat/platform frame as grip rotation.
        public float[] desiredGripAngularVelocity;
        public bool dragFeedbackEnabled;
        public float dragDissipationW;
        public float[] sourceGripVelocity, sourceGripAngularVelocity, minTensions;
        public float shaftLength;
        public float[] tensionTargets, maxTensions, anchors, attachments, requestForce, requestTorque;
        public float[] platformTargetLengthsMm, platformBase, platformTop, platformMinMm, platformMaxMm;
        public float platformNeutralHeight;
        public float[] platformTargetPosition, platformTargetEuler;
        public float motorTauSeconds, motorSlewNPerSecond, platformTauSeconds, platformSpeedMmPerSecond;
        public float gripStiffnessNPerM, gripDampingNsPerM, gripMassKg;
        public float angularStiffness, angularDamping, angularInertia;
        public bool hardwareStopsEnabled;
        // side, hull half beam/top, blade half width/length/thickness,
        // shaft radius, clearance (all lengths in metres, in the boat frame).
        public float[] hardwareStopGeometry;
    }

    [Serializable]
    public sealed class ClosedLoopFeedback
    {
        public string kind;
        public uint commandSequence;
        public double controllerTimeSeconds;
        public double controllerWallTimeSeconds, plantStepSeconds;
        public long plantSampleTick, plantDroppedTicks;
        public bool valid, watchdog, capabilityLimited, motorSaturated;
        public uint commandSourceFeedbackSequence;
        public long commandSourceSampleTick;
        public double commandSourceAgeSeconds = -1, commandAgeSeconds = -1;
        public bool commandExecutionFresh, cableExecutionEnabled, platformExecutionEnabled;
        public string status;
        public int[] rawEncoderCounts;
        public string encoderPacketBase64;
        public float[] tensionN, cableLengthsMm, forceLocal, torqueLocal;
        public float[] platformPosition, platformEuler, platformLengthsMm;
        public float[] platformVelocity, platformAngularVelocity, platformAcceleration, platformAngularAcceleration;
        public float[] platformInducedAcceleration, platformInducedAngularAcceleration;
        public float[] gripPosition, gripVelocity, gripAngularVelocity, actualGripEffortN;
        public float[] effectiveTensionTargets;
        public bool dragFeedbackActive;
        public float dragTargetScale;
        public float platformFitResidualMm;
        public int geometryRank;
        public float countsPerMeter, countsPerDegree;
        public bool hardwareStopsEnabled, hardwareStopActive;
        public float[] hardwareStopGeometry, hardwareStopReactionForce, hardwareStopReactionTorque;
        public float hardwareStopProjectionM, hardwareStopProjectionRadians;
    }

    [Serializable]
    public sealed class ClosedLoopTelemetry
    {
        public string scope = "UNMEASURED_SOFTWARE_CLOSED_LOOP";
        public string status = "NOT_STARTED";
        public bool active, ready, feedbackFresh, encoderConsumed;
        public bool intentPaused;
        // Input freshness above belongs to the physics observation. Dispatch is
        // checked again without rewriting the already integrated paddle sample.
        public bool commandSent, commandSourceFresh;
        public bool deviceFeedbackFresh;
        public double deviceFeedbackReceivedAgeSeconds = -1, deviceFeedbackAckAgeSeconds = -1;
        public double commandSourceReceivedAgeSeconds = -1, commandSourceAckAgeSeconds = -1;
        public string commandPacketBase64;
        public string acknowledgedCommandPacketBase64, acknowledgedSourcePacketBase64;
        public int hostProcessId, controllerProcessId;
        public string sessionId;
        public uint commandSequence, feedbackSequence, acknowledgedCommandSequence;
        public long sentPackets, receivedPackets, rejectedPackets;
        public double receivedAgeSeconds = -1, commandAckAgeSeconds = -1;
        public float actualPoseErrorM, forceResidualN, torqueResidualNm, platformFitResidualMm;
        public Vector3 desiredBladePositionLocal, actualBladePositionLocal, actualGripEffortN;
        public Vector3 observationBoatPosition;
        public Quaternion observationBoatRotation;
        public string feedbackPacketBase64, encoderPacketBase64;
        public ClosedLoopFeedback feedback;
    }
}
