using System;
using UnityEngine;

namespace DragonBoatVR
{
    /// <summary>
    /// 可替换的软件控制器通讯层。
    ///
    /// 不宣称匹配未知硬件厂商协议。本层用可读的本地 v1 二进制样机协议，明确经过
    /// 打包、校验、控制器解析、执行、反馈打包和反馈校验，再进入统一仿真帧。
    /// </summary>
    public class SoftwareDeviceProtocolTwin : MonoBehaviour
    {
        public const uint CableCommandMagic = 0x31434344u; // 'DCC1'
        public const uint CableFeedbackMagic = 0x31464344u; // 'DCF1'
        public const uint PlatformCommandMagic = 0x31504344u; // 'DCP1'
        public const uint PlatformFeedbackMagic = 0x31464350u; // 'PCF1'
        public const uint Version = 1;
        public const int CableCommandBytes = 68;
        public const int PlatformCommandBytes = 80;

        public bool measured;
        public string provenance = "ASSUMED_SOFTWARE_CONTROLLER_PROTOCOL_V1: packet fields and CRC are replaceable, not measured hardware protocol";
        public DeviceProtocolTelemetry Telemetry { get; private set; } = new DeviceProtocolTelemetry();
        public CableDeviceFeedback CableFeedback { get; private set; } = InvalidCable("NOT_DISPATCHED");
        public PlatformDeviceFeedback PlatformFeedback { get; private set; } = InvalidPlatform("NOT_DISPATCHED");
        uint _lastCableSequence, _lastPlatformSequence;
        bool _hasCableSequence, _hasPlatformSequence;

        public void BeginFrame()
        {
            Telemetry = new DeviceProtocolTelemetry { provenance = provenance, measured = measured };
            CableFeedback = InvalidCable("NOT_DISPATCHED");
            PlatformFeedback = InvalidPlatform("NOT_DISPATCHED");
        }

        /// <summary>Move the replay protection window with a restored/reset simulation timeline.</summary>
        public void SynchronizeSequenceWindows(uint cableSequence, uint platformSequence)
        {
            _lastCableSequence = cableSequence;
            _lastPlatformSequence = platformSequence;
            _hasCableSequence = true;
            _hasPlatformSequence = true;
        }

        public bool DispatchCable(CableDriveTwin device, ForceFeedbackRequest request, float dt)
            => DispatchCableCore(device, request, dt, false, default, default, default);

        public bool DispatchCableAtPose(CableDriveTwin device, ForceFeedbackRequest request, float dt,
            Matrix4x4 boatLocalToWorld, Vector3 bladeWorldPosition, Quaternion bladeWorldRotation)
            => DispatchCableCore(device, request, dt, true, boatLocalToWorld, bladeWorldPosition, bladeWorldRotation);

        bool DispatchCableCore(CableDriveTwin device, ForceFeedbackRequest request, float dt, bool useObservation,
            Matrix4x4 boatLocalToWorld, Vector3 bladeWorldPosition, Quaternion bladeWorldRotation)
        {
            Telemetry.cableStatus = "PACKING_COMMAND";
            if (device == null) return RejectCable("NO_CABLE_CONTROLLER");
            byte[] command = PackCableCommand(request);
            Telemetry.cableCommandPacked = true;
            Telemetry.cableCommandBytes = command.Length;
            if (!TryParseCableCommand(command, out ForceFeedbackRequest parsed, out uint sequence, out bool crcValid))
            {
                Telemetry.cableCommandCrcValid = crcValid;
                return RejectCable(device, request, dt, crcValid ? "CABLE_COMMAND_PARSE_FAILED" : "CABLE_COMMAND_CRC_FAILED");
            }
            Telemetry.cableCommandCrcValid = true;
            if (_hasCableSequence && unchecked((int)(sequence - _lastCableSequence)) <= 0)
                return RejectCable(device, request, dt, "CABLE_COMMAND_SEQUENCE_REJECTED");
            _lastCableSequence = sequence; _hasCableSequence = true;
            Telemetry.sequence = Math.Max(Telemetry.sequence, sequence);
            Telemetry.cableCommandParsed = true;
            Telemetry.cableCommandValid = parsed.valid;

            if (useObservation) device.ReceiveAtPose(parsed, dt, boatLocalToWorld, bladeWorldPosition, bladeWorldRotation);
            else device.Receive(parsed, dt);
            Telemetry.cableExecuted = true;
            byte[] feedback = PackCableFeedback(device.Feedback);
            Telemetry.cableFeedbackPacked = feedback != null;
            Telemetry.cableFeedbackBytes = feedback != null ? feedback.Length : 0;
            return ConsumeCableFeedback(feedback, sequence, device.Feedback?.status);
        }

        bool ConsumeCableFeedback(byte[] feedback, uint sequence, string status)
        {
            bool feedbackValid = TryParseCableFeedback(feedback, sequence, out CableDeviceFeedback decoded, out bool feedbackCrc);
            Telemetry.cableFeedbackCrcValid = feedbackCrc;
            Telemetry.cableFeedbackParsed = feedbackValid;
            Telemetry.cableFeedbackValid = feedbackValid && decoded.valid;
            Telemetry.cableStatus = feedbackValid ? (status ?? "DECODED_SOFTWARE_FEEDBACK")
                : feedbackCrc ? "CABLE_FEEDBACK_PARSE_FAILED" : "CABLE_FEEDBACK_CRC_FAILED";
            CableFeedback = feedbackValid ? decoded : InvalidCable(Telemetry.cableStatus, sequence);
            CableFeedback.status = Telemetry.cableStatus;
            return feedbackValid;
        }

        public bool DispatchPlatform(StewartPlatformTwin device, PlatformMotionRequest request, float dt)
        {
            Telemetry.platformStatus = "PACKING_COMMAND";
            if (device == null) return RejectPlatform("NO_PLATFORM_CONTROLLER");
            byte[] command = PackPlatformCommand(request);
            Telemetry.platformCommandPacked = true;
            Telemetry.platformCommandBytes = command.Length;
            if (!TryParsePlatformCommand(command, out PlatformMotionRequest parsed, out uint sequence, out bool crcValid))
            {
                Telemetry.platformCommandCrcValid = crcValid;
                return RejectPlatform(device, request, dt, crcValid ? "PLATFORM_COMMAND_PARSE_FAILED" : "PLATFORM_COMMAND_CRC_FAILED");
            }
            Telemetry.platformCommandCrcValid = true;
            if (_hasPlatformSequence && unchecked((int)(sequence - _lastPlatformSequence)) <= 0)
                return RejectPlatform(device, request, dt, "PLATFORM_COMMAND_SEQUENCE_REJECTED");
            _lastPlatformSequence = sequence; _hasPlatformSequence = true;
            Telemetry.sequence = Math.Max(Telemetry.sequence, sequence);
            Telemetry.platformCommandParsed = true;
            Telemetry.platformCommandValid = parsed.valid;

            device.Receive(parsed, dt);
            Telemetry.platformExecuted = true;
            byte[] feedback = PackPlatformFeedback(device.Feedback);
            Telemetry.platformFeedbackPacked = feedback != null;
            Telemetry.platformFeedbackBytes = feedback != null ? feedback.Length : 0;
            return ConsumePlatformFeedback(feedback, sequence, device.Feedback?.status);
        }

        bool ConsumePlatformFeedback(byte[] feedback, uint sequence, string status)
        {
            bool feedbackValid = TryParsePlatformFeedback(feedback, sequence, out PlatformDeviceFeedback decoded, out bool feedbackCrc);
            Telemetry.platformFeedbackCrcValid = feedbackCrc;
            Telemetry.platformFeedbackParsed = feedbackValid;
            Telemetry.platformFeedbackValid = feedbackValid && decoded.valid;
            Telemetry.platformStatus = feedbackValid ? (status ?? "DECODED_SOFTWARE_FEEDBACK")
                : feedbackCrc ? "PLATFORM_FEEDBACK_PARSE_FAILED" : "PLATFORM_FEEDBACK_CRC_FAILED";
            PlatformFeedback = feedbackValid ? decoded : InvalidPlatform(Telemetry.platformStatus, sequence);
            PlatformFeedback.status = Telemetry.platformStatus;
            return feedbackValid;
        }

        static CableDeviceFeedback InvalidCable(string status, uint sequence = 0, double time = 0)
            => new CableDeviceFeedback { commandSequence = sequence, timeSeconds = time, simulated = true,
                valid = false, status = status, lengthsMm = Array.Empty<float>(), tensionsN = Array.Empty<float>() };
        static PlatformDeviceFeedback InvalidPlatform(string status, uint sequence = 0, double time = 0)
            => new PlatformDeviceFeedback { commandSequence = sequence, timeSeconds = time, simulated = true,
                valid = false, status = status, actuatorLengthsMm = new float[6] };
        bool RejectCable(string status)
        {
            Telemetry.cableStatus = status;
            Telemetry.cableCommandValid = Telemetry.cableFeedbackValid = false;
            CableFeedback = InvalidCable(status);
            return false;
        }
        bool RejectPlatform(string status)
        {
            Telemetry.platformStatus = status;
            Telemetry.platformCommandValid = Telemetry.platformFeedbackValid = false;
            PlatformFeedback = InvalidPlatform(status);
            return false;
        }
        bool RejectCable(CableDriveTwin device, ForceFeedbackRequest request, float dt, string status)
        {
            RejectCable(status);
            CableFeedback = InvalidCable(status, request.commandSequence,
                double.IsNaN(request.timeSeconds) || double.IsInfinity(request.timeSeconds) ? 0 : request.timeSeconds);
            device.Receive(new ForceFeedbackRequest { commandSequence = request.commandSequence,
                inputSequence = request.inputSequence, timeSeconds = request.timeSeconds, valid = false }, dt);
            return false;
        }
        bool RejectPlatform(StewartPlatformTwin device, PlatformMotionRequest request, float dt, string status)
        {
            RejectPlatform(status);
            PlatformFeedback = InvalidPlatform(status, request.commandSequence,
                double.IsNaN(request.timeSeconds) || double.IsInfinity(request.timeSeconds) ? 0 : request.timeSeconds);
            request.valid = false;
            device.Receive(request, dt);
            return false;
        }

        public static byte[] PackCableCommand(ForceFeedbackRequest value)
        {
            var data = new byte[CableCommandBytes]; int p = 0;
            Put(data, ref p, CableCommandMagic); Put(data, ref p, Version); Put(data, ref p, value.commandSequence);
            Put(data, ref p, ToMicros(value.timeSeconds)); Put(data, ref p, value.inputSequence);
            Put(data, ref p, value.forceWorldN); Put(data, ref p, value.torqueAtGripWorldNm);
            Put(data, ref p, value.gripWorldPosition); PutFlag(data, ref p, value.valid);
            Put(data, ref p, Crc32(data, 0, data.Length - 4));
            return data;
        }

        public static bool TryParseCableCommand(byte[] data, out ForceFeedbackRequest value, out uint sequence, out bool crcValid)
        {
            value = default; sequence = 0; crcValid = false;
            if (data == null || data.Length != CableCommandBytes || !CheckCrc(data, out crcValid)) return false;
            int p = 0;
            if (ReadUInt(data, ref p) != CableCommandMagic || ReadUInt(data, ref p) != Version) return false;
            sequence = ReadUInt(data, ref p); double time = ReadUInt64(data, ref p) / 1000000d;
            uint inputSequence = ReadUInt(data, ref p); Vector3 force = ReadVector(data, ref p);
            Vector3 torque = ReadVector(data, ref p); Vector3 grip = ReadVector(data, ref p);
            bool valid = ReadFlag(data, ref p, out bool validFlag); if (!validFlag) return false;
            if (!SimulationValue.Finite(force) || !SimulationValue.Finite(torque) || !SimulationValue.Finite(grip)) return false;
            value = new ForceFeedbackRequest { commandSequence = sequence, inputSequence = inputSequence,
                timeSeconds = time, forceWorldN = force, torqueAtGripWorldNm = torque, gripWorldPosition = grip, valid = valid };
            return true;
        }

        static byte[] PackCableFeedback(CableDeviceFeedback value)
        {
            if (value == null || value.lengthsMm == null || value.tensionsN == null || value.lengthsMm.Length != value.tensionsN.Length) return null;
            int count = value.lengthsMm.Length;
            var data = new byte[68 + count * 8]; int p = 0;
            Put(data, ref p, CableFeedbackMagic); Put(data, ref p, Version); Put(data, ref p, value.commandSequence);
            Put(data, ref p, ToMicros(value.timeSeconds)); PutFlag(data, ref p, value.valid);
            PutFlag(data, ref p, value.capabilityLimited); Put(data, ref p, (uint)count);
            for (int i = 0; i < count; i++) Put(data, ref p, value.lengthsMm[i]);
            for (int i = 0; i < count; i++) Put(data, ref p, value.tensionsN[i]);
            Put(data, ref p, value.forceWorldN); Put(data, ref p, value.torqueAtGripWorldNm);
            Put(data, ref p, value.forceResidualN); Put(data, ref p, value.torqueResidualNm);
            Put(data, ref p, Crc32(data, 0, data.Length - 4));
            return data;
        }

        public static byte[] PackPlatformCommand(PlatformMotionRequest value)
        {
            var data = new byte[PlatformCommandBytes]; int p = 0;
            Put(data, ref p, PlatformCommandMagic); Put(data, ref p, Version); Put(data, ref p, value.commandSequence);
            Put(data, ref p, ToMicros(value.timeSeconds)); Put(data, ref p, value.rawEulerDegrees);
            Put(data, ref p, value.rawTranslationM); Put(data, ref p, value.eulerDegrees); Put(data, ref p, value.translationM);
            PutFlag(data, ref p, value.valid); PutFlag(data, ref p, value.clamped);
            Put(data, ref p, Crc32(data, 0, data.Length - 4));
            return data;
        }

        public static bool TryParsePlatformCommand(byte[] data, out PlatformMotionRequest value, out uint sequence, out bool crcValid)
        {
            value = default; sequence = 0; crcValid = false;
            if (data == null || data.Length != PlatformCommandBytes || !CheckCrc(data, out crcValid)) return false;
            int p = 0;
            if (ReadUInt(data, ref p) != PlatformCommandMagic || ReadUInt(data, ref p) != Version) return false;
            sequence = ReadUInt(data, ref p); double time = ReadUInt64(data, ref p) / 1000000d;
            Vector3 rawEuler = ReadVector(data, ref p); Vector3 rawPosition = ReadVector(data, ref p);
            Vector3 euler = ReadVector(data, ref p); Vector3 position = ReadVector(data, ref p);
            bool valid = ReadFlag(data, ref p, out bool validFlag);
            bool clamped = ReadFlag(data, ref p, out bool clampedFlag);
            if (!validFlag || !clampedFlag) return false;
            if (!SimulationValue.Finite(rawEuler) || !SimulationValue.Finite(rawPosition) ||
                !SimulationValue.Finite(euler) || !SimulationValue.Finite(position)) return false;
            value = new PlatformMotionRequest { commandSequence = sequence, timeSeconds = time,
                rawEulerDegrees = rawEuler, rawTranslationM = rawPosition, eulerDegrees = euler,
                translationM = position, valid = valid, clamped = clamped };
            return true;
        }

        static byte[] PackPlatformFeedback(PlatformDeviceFeedback value)
        {
            if (value == null || value.actuatorLengthsMm == null || value.actuatorLengthsMm.Length != 6) return null;
            var data = new byte[84]; int p = 0;
            Put(data, ref p, PlatformFeedbackMagic); Put(data, ref p, Version); Put(data, ref p, value.commandSequence);
            Put(data, ref p, ToMicros(value.timeSeconds)); PutFlag(data, ref p, value.valid);
            PutFlag(data, ref p, value.capabilityLimited); Put(data, ref p, value.eulerDegrees);
            Put(data, ref p, value.translationM); Put(data, ref p, 6u);
            for (int i = 0; i < 6; i++) Put(data, ref p, value.actuatorLengthsMm[i]);
            Put(data, ref p, Crc32(data, 0, data.Length - 4));
            return data;
        }

        static bool TryParseFeedback(byte[] data, uint expectedMagic, uint expectedSequence,
            out bool deviceValid, out bool crcValid)
        {
            deviceValid = false; crcValid = false;
            if (expectedMagic == CableFeedbackMagic)
            {
                bool accepted = TryParseCableFeedback(data, expectedSequence, out CableDeviceFeedback decoded, out crcValid);
                deviceValid = accepted && decoded.valid;
                return accepted;
            }
            if (expectedMagic == PlatformFeedbackMagic)
            {
                bool accepted = TryParsePlatformFeedback(data, expectedSequence, out PlatformDeviceFeedback decoded, out crcValid);
                deviceValid = accepted && decoded.valid;
                return accepted;
            }
            return false;
        }

        public static bool TryParseCableFeedback(byte[] data, uint expectedSequence,
            out CableDeviceFeedback value, out bool crcValid)
        {
            value = null; crcValid = false;
            if (data == null || data.Length < 68 || data.Length > 68 + 64 * 8 || !CheckCrc(data, out crcValid)) return false;
            int p = 0;
            if (!ReadFeedbackHeader(data, ref p, CableFeedbackMagic, expectedSequence,
                out double time, out bool valid, out bool limited)) return false;
            uint count = ReadUInt(data, ref p);
            if (count > 64 || data.Length != 68 + count * 8) return false;
            var lengths = new float[count];
            var tensions = new float[count];
            for (int i = 0; i < count; i++)
            {
                lengths[i] = ReadFloat(data, ref p);
                if (!SimulationValue.Finite(lengths[i])) return false;
            }
            for (int i = 0; i < count; i++)
            {
                tensions[i] = ReadFloat(data, ref p);
                if (!SimulationValue.Finite(tensions[i])) return false;
            }
            Vector3 force = ReadVector(data, ref p), torque = ReadVector(data, ref p);
            float forceResidual = ReadFloat(data, ref p), torqueResidual = ReadFloat(data, ref p);
            if (!SimulationValue.Finite(force) || !SimulationValue.Finite(torque)
                || !SimulationValue.Finite(forceResidual) || !SimulationValue.Finite(torqueResidual)
                || p != data.Length - 4) return false;
            value = new CableDeviceFeedback { commandSequence = expectedSequence, timeSeconds = time,
                simulated = true, valid = valid, capabilityLimited = limited, status = "DECODED_SOFTWARE_FEEDBACK",
                lengthsMm = lengths, tensionsN = tensions, forceWorldN = force, torqueAtGripWorldNm = torque,
                forceResidualN = forceResidual, torqueResidualNm = torqueResidual };
            return true;
        }

        public static bool TryParsePlatformFeedback(byte[] data, uint expectedSequence,
            out PlatformDeviceFeedback value, out bool crcValid)
        {
            value = null; crcValid = false;
            if (data == null || data.Length != 84 || !CheckCrc(data, out crcValid)) return false;
            int p = 0;
            if (!ReadFeedbackHeader(data, ref p, PlatformFeedbackMagic, expectedSequence,
                out double time, out bool valid, out bool limited)) return false;
            Vector3 euler = ReadVector(data, ref p), translation = ReadVector(data, ref p);
            uint count = ReadUInt(data, ref p);
            if (!SimulationValue.Finite(euler) || !SimulationValue.Finite(translation) || count != 6) return false;
            var lengths = new float[6];
            for (int i = 0; i < lengths.Length; i++)
            {
                lengths[i] = ReadFloat(data, ref p);
                if (!SimulationValue.Finite(lengths[i])) return false;
            }
            if (p != data.Length - 4) return false;
            value = new PlatformDeviceFeedback { commandSequence = expectedSequence, timeSeconds = time,
                simulated = true, valid = valid, capabilityLimited = limited, status = "DECODED_SOFTWARE_FEEDBACK",
                eulerDegrees = euler, translationM = translation, actuatorLengthsMm = lengths };
            return true;
        }

        static bool ReadFeedbackHeader(byte[] data, ref int p, uint magic, uint sequence,
            out double time, out bool valid, out bool limited)
        {
            time = 0; valid = limited = false;
            if (ReadUInt(data, ref p) != magic || ReadUInt(data, ref p) != Version || ReadUInt(data, ref p) != sequence)
                return false;
            time = ReadUInt64(data, ref p) / 1000000d;
            valid = ReadFlag(data, ref p, out bool validFlag);
            limited = ReadFlag(data, ref p, out bool limitedFlag);
            return validFlag && limitedFlag;
        }

        static bool CheckCrc(byte[] data, out bool valid)
        {
            valid = false;
            if (data == null || data.Length < 8) return false;
            uint expected = BitConverter.ToUInt32(data, data.Length - 4);
            valid = expected == Crc32(data, 0, data.Length - 4);
            return valid;
        }

        static ulong ToMicros(double seconds) => (ulong)Math.Max(0d, Math.Round(seconds * 1000000d));
        static void Put(byte[] d, ref int p, uint x) { Buffer.BlockCopy(BitConverter.GetBytes(x), 0, d, p, 4); p += 4; }
        static void PutFlag(byte[] d, ref int p, bool value) => Put(d, ref p, value ? 1u : 0u);
        static void Put(byte[] d, ref int p, ulong x) { Buffer.BlockCopy(BitConverter.GetBytes(x), 0, d, p, 8); p += 8; }
        static void Put(byte[] d, ref int p, float x) { Buffer.BlockCopy(BitConverter.GetBytes(x), 0, d, p, 4); p += 4; }
        static void Put(byte[] d, ref int p, Vector3 x) { Put(d, ref p, x.x); Put(d, ref p, x.y); Put(d, ref p, x.z); }
        static uint ReadUInt(byte[] d, ref int p) { uint x = BitConverter.ToUInt32(d, p); p += 4; return x; }
        static bool ReadFlag(byte[] d, ref int p, out bool valid)
        {
            uint raw = ReadUInt(d, ref p);
            valid = raw <= 1u;
            return raw == 1u;
        }
        static ulong ReadUInt64(byte[] d, ref int p) { ulong x = BitConverter.ToUInt64(d, p); p += 8; return x; }
        static float ReadFloat(byte[] d, ref int p) { float x = BitConverter.ToSingle(d, p); p += 4; return x; }
        static Vector3 ReadVector(byte[] d, ref int p) => new Vector3(ReadFloat(d, ref p), ReadFloat(d, ref p), ReadFloat(d, ref p));

        static uint Crc32(byte[] data, int offset, int count)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = offset; i < offset + count; i++)
            {
                crc ^= data[i];
                for (int b = 0; b < 8; b++) crc = (crc & 1u) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
            return ~crc;
        }
    }
}
