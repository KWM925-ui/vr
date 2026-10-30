using System;
using UnityEngine;

namespace DragonBoatVR
{
    public enum AcquisitionSourceKind { Unverified, Fixture, CsvImport, DeviceAdapter }
    public enum AcquisitionCalibrationMode { None, ZeroScale, LinearFit }

    // Current PC receiver diagnostics only; deliberately excluded from recorded frames.
    public struct AcquisitionReceptionDiagnostics
    {
        public bool profileReady, loopbackConfigured;
        public int listeningPort, acceptedPackets, rejectedPackets, rejectedChannels;
        public string sourceId, profileVersion, lastRejection;

        public bool Matches(AcquisitionReceptionDiagnostics other) =>
            profileReady == other.profileReady && loopbackConfigured == other.loopbackConfigured
            && listeningPort == other.listeningPort && acceptedPackets == other.acceptedPackets
            && rejectedPackets == other.rejectedPackets && rejectedChannels == other.rejectedChannels
            && sourceId == other.sourceId && profileVersion == other.profileVersion && lastRejection == other.lastRejection;
    }

    // PC-side normalized observations. This is not the PCB firmware/USB protocol.
    [Serializable]
    public class AcquisitionPacket
    {
        public const string Format = "dragonboat.pc-adc.v1";
        public string format;
        public string sourceId;
        public AcquisitionSourceKind sourceKind;
        public bool sequenceValid;
        public uint sequence;
        public bool deviceTimeValid;
        public ulong deviceTimeUs;
        public AcquisitionChannelInput[] channels;
    }

    [Serializable]
    public class AcquisitionChannelInput
    {
        public string channelId;
        public bool sampleSequenceValid;
        public uint sampleSequence;
        public bool sampleTimeValid;
        public ulong sampleTimeUs;
        public string rawUnits;
        public bool hasRaw;
        public double raw;
        public string rawText;
        public bool valid;
        public bool saturated;
    }

    [Serializable]
    public class AcquisitionCalibrationPoint
    {
        public double raw;
        public double forceN;
    }

    [Serializable]
    public class AcquisitionCalibration
    {
        public AcquisitionCalibrationMode mode;
        public bool measured;
        public string sourceId;
        public string version;
        public string evidence;
        public double rawZero;
        public double scaleNPerRaw;
        public AcquisitionCalibrationPoint[] points;
        public bool forceRangeConfigured;
        public double minimumForceN;
        public double maximumForceN;
    }

    [Serializable]
    public class AcquisitionChannelProfile
    {
        public string channelId;
        // An explicit project attachment/sensor ID may replace Unassigned after wiring is known.
        public string mapping = "Unassigned";
        public string rawUnits = "raw";
        public double maximumAgeSeconds = 0.1;
        // No default ADC encoding/range is inferred from the schematic.
        public bool rawRangeConfigured;
        public double minimumRaw;
        public double maximumRaw;
        public AcquisitionCalibration calibration = new AcquisitionCalibration();
    }

    [Serializable]
    public class AcquisitionProfile
    {
        public int schemaVersion = 1;
        public string version = "pcb-four-channel-unassigned-v1";
        public string sourceId = "unassigned-pcb";
        public bool enableLoopbackUdp;
        public int loopbackPort = 9874;
        public AcquisitionChannelProfile[] channels;

        public static AcquisitionProfile Default()
        {
            var result = new AcquisitionProfile { channels = new AcquisitionChannelProfile[4] };
            for (int i = 0; i < 4; i++) result.channels[i] = new AcquisitionChannelProfile { channelId = "CH" + (i + 1) };
            return result;
        }
    }

    [Serializable]
    public class AcquisitionChannelSnapshot
    {
        public string channelId;
        public string sourceId;
        public AcquisitionSourceKind sourceKind;
        public string mapping;
        public string rawUnits;
        public bool present;
        public bool hasRaw;
        public double raw;
        public string rawText;
        public bool rawNumericFinite;
        public bool sourceValid;
        public bool saturated;
        public uint packetSequence;
        public uint sampleSequence;
        public bool deviceTimeValid;
        public ulong deviceTimeUs;
        public bool sampleTimeValid;
        public ulong sampleTimeUs;
        public long receivedMonotonicTicks;
        public double ageSeconds;
        public bool rawRangeVerified;
        public bool valid;
        public bool rawValid;
        public string rawStatus;
        public bool calibrationMeasured;
        public string calibrationSourceId;
        public string calibrationVersion;
        public string calibrationEvidence;
        public bool calibratedValid;
        public double forceN;
        public string forceStatus;
    }

    [Serializable]
    public class AcquisitionSnapshot
    {
        public string format = AcquisitionPacket.Format;
        public string profileVersion;
        public string sourceId;
        public long capturedMonotonicTicks;
        public bool replayed;
        // valid means four fresh valid RAW observations, not measured force, pose, or motor feedback.
        public bool valid;
        public bool samePacket;
        public int rawValidCount;
        public int calibratedValidCount;
        public string status;
        public AcquisitionChannelSnapshot[] channels;

        // Replay reproduces the recorded local-age decision; it never compares historical host clocks.
        public static AcquisitionSnapshot ForReplay(AcquisitionSnapshot recorded, bool expired = false)
        {
            if (recorded == null) return new AcquisitionSnapshot { replayed = true, valid = false,
                status = "REPLAY_ACQUISITION_MISSING", channels = new AcquisitionChannelSnapshot[0] };
            var copy = JsonUtility.FromJson<AcquisitionSnapshot>(JsonUtility.ToJson(recorded));
            copy.replayed = true;
            if (!expired) return copy;
            copy.valid = false; copy.rawValidCount = copy.calibratedValidCount = 0;
            copy.status = "REPLAY_ACQUISITION_EXPIRED";
            if (copy.channels != null) foreach (var channel in copy.channels)
            {
                if (channel == null) continue;
                channel.valid = channel.rawValid = channel.calibratedValid = false;
                channel.rawStatus = "REPLAY_EXPIRED"; channel.forceStatus = "REPLAY_EXPIRED";
            }
            return copy;
        }
    }
}
