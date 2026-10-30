using System;
using UnityEngine;

namespace DragonBoatVR
{
    /// <summary>
    /// 桨运动的原始编码器/协议仿真层。
    ///
    /// 固定步中的顺序是：驱动脚本产生刚体位姿 → 六个位置/角度编码器量化为原始计数，
    /// 握把力作为独立模拟量样本进入同一采集帧 → 组包并计算 CRC32 →
    /// 解析、校验序号和 CRC → 按船体局部坐标还原桨位姿 →
    /// 才交给 PaddleHydrodynamics。这样桌面仿真和未来真实传感器共用同一个下游接口。
    ///
    /// countsPerMeter / countsPerDegree / protocol 参数都是未实测的样机值，
    /// 仅用于验证数据流；拿到硬件后替换本组件或配置，不改水动力和视觉逻辑。
    /// </summary>
    public class PaddleEncoderPipeline : MonoBehaviour
    {
        public const uint Magic = 0x44424550u; // 'DBEP' - Dragon Boat Encoder Packet
        public const uint ProtocolVersion = 1u;
        public const int ChannelCount = 6; // position XYZ, local Euler XYZ
        public const int PacketSize = 52;

        [Header("关联对象")]
        public PaddleDriver driver;
        public BoatBuoyancy boat;

        [Header("样机编码器参数（未实测，可替换）")]
        [Tooltip("位置通道每米计数，桌面仿真用 0.1 mm 分辨率")]
        public float countsPerMeter = 10000f;
        [Tooltip("姿态通道每度计数，桌面仿真用 0.01 度分辨率")]
        public float countsPerDegree = 100f;
        public bool measured;
        public string provenance = "ASSUMED_SOFTWARE_ENCODER_FIXTURE: replace with measured sensor resolution and firmware packet";

        public PaddleEncoderTelemetry Telemetry { get; private set; } = new PaddleEncoderTelemetry();
        public uint AcceptedSequence => _acceptedSequence;
        public int LastPacketLength => _lastPacket == null ? 0 : _lastPacket.Length;
        public byte[] CopyLastPacket() => _lastPacket == null ? null : (byte[])_lastPacket.Clone();

        uint _packetSequence;
        uint _acceptedSequence;
        bool _hasAccepted;
        byte[] _lastPacket;
        Vector3 _lastWorldPosition;
        Quaternion _lastWorldRotation;
        double _lastDecodedTime = double.NaN;

        void Awake()
        {
            if (driver == null) driver = GetComponent<PaddleDriver>();
            if (boat == null) boat = FindObjectOfType<BoatBuoyancy>(true);
            ResetPipeline();
        }

        public void Configure(PaddleDriver source, BoatBuoyancy targetBoat)
        {
            driver = source != null ? source : driver;
            boat = targetBoat != null ? targetBoat : boat;
            ResetPipeline();
        }

        public void ResetPipeline()
        {
            _packetSequence = 0;
            _acceptedSequence = 0;
            _hasAccepted = false;
            _lastPacket = null;
            _lastDecodedTime = double.NaN;
            _lastWorldPosition = Vector3.zero;
            _lastWorldRotation = Quaternion.identity;
            Telemetry = new PaddleEncoderTelemetry
            {
                provenance = provenance,
                measured = measured,
                status = "RESET"
            };
        }

        /// <summary>把一个上游桨样本完整走一遍原始编码器包和解码器。</summary>
        public PaddleSample Process(PaddleSample source, double simulationTimeSeconds, float dt)
        {
            if (!IsConfigurationValid() || !source.valid || !SimulationValue.Finite(source.bladePosition) ||
                !SimulationValue.Finite(source.bladeRotation) || !SimulationValue.Finite(source.gripForceN)
                || !SimulationValue.Finite(source.bladeVelocity) || !SimulationValue.Finite(source.bladeAngularVelocity))
            {
                SetInvalid("INVALID_SOURCE_OR_ENCODER_CONFIGURATION", source);
                var invalid = InvalidSample(source, simulationTimeSeconds);
                invalid.source = source.source;
                invalid.originalSource = source.originalSource;
                invalid.deviceTimeUs = source.deviceTimeUs;
                return invalid;
            }

            Quaternion boatRotation = boat != null ? boat.transform.rotation : Quaternion.identity;
            Vector3 localPosition = boat != null ? boat.transform.InverseTransformPoint(source.bladePosition) : source.bladePosition;
            Quaternion localRotation = Quaternion.Inverse(boatRotation) * source.bladeRotation;
            Vector3 localEuler = WrapEuler(localRotation.eulerAngles);
            uint sequence = ++_packetSequence;
            ulong timestampUs = source.deviceTimeUs != 0 ? source.deviceTimeUs
                : (ulong)Math.Max(0d, Math.Round((source.timeSeconds > 0d ? source.timeSeconds : simulationTimeSeconds) * 1000000d));

            byte[] packet = BuildPacket(sequence, timestampUs, localPosition, localEuler, source.gripForceN);
            _lastPacket = packet;
            Telemetry = new PaddleEncoderTelemetry
            {
                provenance = provenance,
                measured = measured,
                sequence = sequence,
                sourceSequence = source.sequence,
                deviceTimeUs = timestampUs,
                packetBuilt = true,
                packetBytes = packet.Length,
                status = "PACKET_BUILT"
            };

            PaddleSample decoded = DecodePacket(packet, simulationTimeSeconds, dt, source.source, source.originalSource,
                source.bladeVelocity, source.bladeAngularVelocity, source.sequence, true);
            Telemetry.sourceSequence = source.sequence;
            decoded.source = source.source;
            decoded.originalSource = source.originalSource;
            decoded.sequence = source.sequence;
            decoded.deviceTimeUs = source.deviceTimeUs != 0 ? source.deviceTimeUs : Telemetry.deviceTimeUs;
            if (decoded.valid)
            {
                decoded.gripForceN = source.gripForceN;
                decoded.bladeVelocity = source.bladeVelocity;
                decoded.bladeAngularVelocity = source.bladeAngularVelocity;
                decoded.hasAngularVelocity = source.hasAngularVelocity;
                Telemetry.gripForceN = source.gripForceN;
                Telemetry.gripForceSource = "UPSTREAM_" + source.source.ToString().ToUpperInvariant();
            }
            return decoded;
        }

        /// <summary>
        /// 原始硬件/模拟器包入口。实际接入时，UDP/串口适配器把收到的完整包交到这里；
        /// 包序号、长度、magic、版本与 CRC 校验成功后才会生成统一 PaddleSample。
        /// </summary>
        public PaddleSample DecodePacket(byte[] packet, double simulationTimeSeconds, float dt,
            PaddleInputKind upstreamSource = PaddleInputKind.Encoder,
            PaddleInputKind originalSource = PaddleInputKind.Encoder,
            Vector3 fallbackLinearVelocity = default,
            Vector3 fallbackAngularVelocity = default,
            uint sourceSequence = 0,
            bool preserveSourceSequence = false)
        {
            if (!IsConfigurationValid())
            {
                var invalidConfiguration = new PaddleSample { source = upstreamSource, valid = false,
                    sequence = preserveSourceSequence ? sourceSequence : 0,
                    timeSeconds = simulationTimeSeconds, originalSource = originalSource };
                SetInvalid("INVALID_ENCODER_CONFIGURATION", invalidConfiguration);
                Telemetry.sourceSequence = invalidConfiguration.sequence;
                return invalidConfiguration;
            }

            if (!TryParsePacket(packet, out uint parsedSequence, out ulong parsedTimestamp,
                out int[] rawCounts, out Vector3 parsedLocalPosition, out Vector3 parsedLocalEuler, out float gripN,
                out bool crcValid))
            {
                Telemetry = new PaddleEncoderTelemetry { provenance = provenance, measured = measured,
                    packetBuilt = packet != null, packetAccepted = false, crcValid = crcValid,
                    packetBytes = packet != null ? packet.Length : 0,
                    status = crcValid ? "PACKET_REJECTED" : "CRC_REJECTED", rawCounts = rawCounts };
                Telemetry.packetAccepted = false;
                Telemetry.crcValid = crcValid;
                return InvalidSample(new PaddleSample { source = upstreamSource,
                    originalSource = originalSource, sequence = preserveSourceSequence ? sourceSequence : parsedSequence }, simulationTimeSeconds);
            }
            if (_hasAccepted && unchecked((int)(parsedSequence - _acceptedSequence)) <= 0)
            {
                Telemetry.packetAccepted = false;
                Telemetry.crcValid = true;
                Telemetry.rawCounts = rawCounts;
                Telemetry.sequence = parsedSequence;
                Telemetry.deviceTimeUs = parsedTimestamp;
                Telemetry.status = "SEQUENCE_REJECTED";
                return InvalidSample(new PaddleSample { source = upstreamSource,
                    originalSource = originalSource, sequence = preserveSourceSequence ? sourceSequence : parsedSequence }, simulationTimeSeconds);
            }

            bool hadPrior = _hasAccepted;
            _acceptedSequence = parsedSequence;
            _hasAccepted = true;
            Quaternion boatRotation = boat != null ? boat.transform.rotation : Quaternion.identity;
            Vector3 decodedWorldPosition = boat != null ? boat.transform.TransformPoint(parsedLocalPosition) : parsedLocalPosition;
            Quaternion decodedWorldRotation = boatRotation * Quaternion.Euler(parsedLocalEuler);
            // Device timestamps remain provenance only; never compare their clock to the
            // local simulation clock. The simulation timeline is provided by the caller.
            double decodedTime = simulationTimeSeconds;
            float sampleDt = !double.IsNaN(_lastDecodedTime) ? (float)(decodedTime - _lastDecodedTime) : dt;
            Vector3 velocity = sampleDt > 1e-6f && SimulationValue.Finite(sampleDt) && hadPrior
                ? (decodedWorldPosition - _lastWorldPosition) / sampleDt : fallbackLinearVelocity;
            Vector3 angularVelocity = sampleDt > 1e-6f && SimulationValue.Finite(sampleDt) && hadPrior
                ? SimulationValue.AngularVelocity(_lastWorldRotation, decodedWorldRotation, sampleDt) : fallbackAngularVelocity;
            if (!SimulationValue.Finite(velocity)) velocity = Vector3.zero;
            if (!SimulationValue.Finite(angularVelocity)) angularVelocity = Vector3.zero;

            _lastWorldPosition = decodedWorldPosition;
            _lastWorldRotation = decodedWorldRotation;
            _lastDecodedTime = decodedTime;
            Telemetry.packetAccepted = true;
            Telemetry.crcValid = true;
            Telemetry.sequence = parsedSequence;
            Telemetry.sourceSequence = preserveSourceSequence ? sourceSequence : parsedSequence;
            Telemetry.deviceTimeUs = parsedTimestamp;
            Telemetry.packetBuilt = true;
            Telemetry.packetBytes = packet != null ? packet.Length : 0;
            Telemetry.rawCounts = rawCounts;
            Telemetry.decodedLocalPosition = parsedLocalPosition;
            Telemetry.decodedLocalEulerDegrees = parsedLocalEuler;
            Telemetry.gripForceN = gripN;
            Telemetry.status = "DECODED_LOCAL_POSE";

            return new PaddleSample
            {
                sequence = preserveSourceSequence ? sourceSequence : parsedSequence,
                timeSeconds = decodedTime,
                deviceTimeUs = parsedTimestamp,
                bladePosition = decodedWorldPosition,
                bladeRotation = decodedWorldRotation,
                bladeVelocity = velocity,
                bladeAngularVelocity = angularVelocity,
                hasAngularVelocity = true,
                gripForceN = gripN,
                valid = true,
                // Keep the upstream source tag for existing physics policy (for example
                // synthetic balance torque); telemetry carries the fact that this pose
                // was accepted only after the encoder protocol stage.
                source = upstreamSource,
                originalSource = originalSource
            };
        }

        bool IsConfigurationValid()
        {
            return SimulationValue.Finite(countsPerMeter) && countsPerMeter > 0f &&
                SimulationValue.Finite(countsPerDegree) && countsPerDegree > 0f;
        }

        byte[] BuildPacket(uint sequence, ulong timestampUs, Vector3 localPosition, Vector3 localEuler, float gripN)
        {
            var data = new byte[PacketSize];
            Buffer.BlockCopy(BitConverter.GetBytes(Magic), 0, data, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(ProtocolVersion), 0, data, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(sequence), 0, data, 8, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(timestampUs), 0, data, 12, 8);
            int[] counts = Quantize(localPosition, localEuler);
            for (int i = 0; i < ChannelCount; i++) Buffer.BlockCopy(BitConverter.GetBytes(counts[i]), 0, data, 20 + i * 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(gripN), 0, data, 20 + ChannelCount * 4, 4);
            uint crc = Crc32(data, 0, PacketSize - 4);
            Buffer.BlockCopy(BitConverter.GetBytes(crc), 0, data, PacketSize - 4, 4);
            return data;
        }

        int[] Quantize(Vector3 localPosition, Vector3 localEuler)
        {
            return new[]
            {
                QuantizeInt(localPosition.x * countsPerMeter), QuantizeInt(localPosition.y * countsPerMeter), QuantizeInt(localPosition.z * countsPerMeter),
                QuantizeInt(localEuler.x * countsPerDegree), QuantizeInt(localEuler.y * countsPerDegree), QuantizeInt(localEuler.z * countsPerDegree)
            };
        }

        int QuantizeInt(float value)
        {
            if (!SimulationValue.Finite(value)) return 0;
            return Mathf.Clamp(Mathf.RoundToInt(value), int.MinValue + 1, int.MaxValue);
        }

        bool TryParsePacket(byte[] data, out uint sequence, out ulong timestampUs, out int[] rawCounts,
            out Vector3 localPosition, out Vector3 localEuler, out float gripN, out bool crcValid)
        {
            sequence = 0; timestampUs = 0; rawCounts = new int[ChannelCount];
            localPosition = localEuler = Vector3.zero; gripN = 0f; crcValid = false;
            if (data == null || data.Length != PacketSize || BitConverter.ToUInt32(data, 0) != Magic ||
                BitConverter.ToUInt32(data, 4) != ProtocolVersion) return false;
            uint expected = BitConverter.ToUInt32(data, PacketSize - 4);
            uint actual = Crc32(data, 0, PacketSize - 4);
            crcValid = expected == actual;
            if (!crcValid) return false;
            sequence = BitConverter.ToUInt32(data, 8);
            timestampUs = BitConverter.ToUInt64(data, 12);
            for (int i = 0; i < ChannelCount; i++) rawCounts[i] = BitConverter.ToInt32(data, 20 + i * 4);
            localPosition = new Vector3(rawCounts[0] / countsPerMeter, rawCounts[1] / countsPerMeter, rawCounts[2] / countsPerMeter);
            localEuler = new Vector3(rawCounts[3] / countsPerDegree, rawCounts[4] / countsPerDegree, rawCounts[5] / countsPerDegree);
            gripN = BitConverter.ToSingle(data, 20 + ChannelCount * 4);
            return SimulationValue.Finite(localPosition) && SimulationValue.Finite(localEuler) && SimulationValue.Finite(gripN);
        }

        void SetInvalid(string status, PaddleSample source)
        {
            Telemetry = new PaddleEncoderTelemetry
            {
                provenance = provenance,
                measured = measured,
                sequence = 0,
                sourceSequence = source.sequence,
                deviceTimeUs = source.deviceTimeUs,
                packetBuilt = false,
                packetAccepted = false,
                crcValid = false,
                status = status,
                rawCounts = new int[ChannelCount]
            };
        }

        static PaddleSample InvalidSample(PaddleSample source, double time)
        {
            source.timeSeconds = time;
            source.valid = false;
            source.source = PaddleInputKind.Encoder;
            return source;
        }

        static Vector3 WrapEuler(Vector3 euler)
        {
            return new Vector3(Wrap(euler.x), Wrap(euler.y), Wrap(euler.z));
        }

        static float Wrap(float value)
        {
            while (value > 180f) value -= 360f;
            while (value < -180f) value += 360f;
            return value;
        }

        static uint Crc32(byte[] data, int offset, int count)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = offset; i < offset + count; i++)
            {
                crc ^= data[i];
                for (int bit = 0; bit < 8; bit++) crc = (crc & 1u) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
            return ~crc;
        }
    }
}
