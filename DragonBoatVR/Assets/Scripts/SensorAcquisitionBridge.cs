using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace DragonBoatVR
{
    /// <summary>
    /// Four independent ADC observations for recording/diagnostics. Never drives paddle pose,
    /// gripForce, propulsion, or motors. A firmware-specific decoder must produce the PC contract.
    /// TTL uses local monotonic receive times; device clocks are provenance only.
    /// </summary>
    public class SensorAcquisitionBridge : MonoBehaviour
    {
        public bool loadProfileOnStart = true;
        public bool externallyStepped;
        public string profileFile = "acquisition_profile.json";
        public AcquisitionProfile profile = AcquisitionProfile.Default();
        public int acceptedPackets;
        public int rejectedPackets;
        public int rejectedChannels;
        public string lastRejection;

        readonly object _gate = new object();
        AcquisitionProfile _profile;
        string _profileSignature;
        readonly StoredChannel[] _samples = new StoredChannel[4];
        bool _sequenceSeen;
        uint _sequence;
        UdpClient _udp;
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        sealed class StoredChannel
        {
            public AcquisitionChannelInput input;
            public AcquisitionSourceKind sourceKind;
            public uint packetSequence;
            public bool deviceTimeValid;
            public ulong deviceTimeUs;
            public long receivedTicks;
        }

        public AcquisitionSnapshot Latest => Snapshot();
        public string ConfigurationSignature
        {
            get { lock (_gate) { EnsureProfile(); return _profileSignature; } }
        }
        public int LocalPort
        {
            get { lock (_gate) return _udp == null ? 0 : ((IPEndPoint)_udp.Client.LocalEndPoint).Port; }
        }
        public AcquisitionReceptionDiagnostics CaptureReceptionDiagnostics()
        {
            lock (_gate)
                return new AcquisitionReceptionDiagnostics
                {
                    profileReady = _profile != null, loopbackConfigured = _profile != null && _profile.enableLoopbackUdp,
                    listeningPort = LocalPort, sourceId = _profile?.sourceId, profileVersion = _profile?.version,
                    acceptedPackets = acceptedPackets, rejectedPackets = rejectedPackets,
                    rejectedChannels = rejectedChannels, lastRejection = lastRejection
                };
        }
        public AcquisitionProfile CurrentProfile
        {
            get { lock (_gate) { EnsureProfile(); return Clone(_profile); } }
        }

        void Start()
        {
            try
            {
                if (loadProfileOnStart)
                {
                    string path = Path.Combine(Application.streamingAssetsPath, profileFile);
                    if (File.Exists(path)) profile = JsonUtility.FromJson<AcquisitionProfile>(File.ReadAllText(path));
                }
                if (_profile == null) Configure(profile);
                if (_profile.enableLoopbackUdp) StartLoopback();
            }
            catch (Exception e) { lastRejection = "PROFILE_ERROR: " + e.Message; Debug.LogError("[ADC] " + lastRejection); }
        }

        void Update() { if (!externallyStepped) PollLoopback(); }
        void OnDestroy() { StopLoopback(); }

        public void Configure(AcquisitionProfile configuration)
        {
            ValidateProfile(configuration);
            var nextProfile = Clone(configuration);
            var publicProfile = Clone(configuration);
            // Preserve the previous exact signature representation: ToJson(CurrentProfile),
            // where CurrentProfile returns another isolated JSON round-trip clone.
            string nextSignature = JsonUtility.ToJson(Clone(nextProfile));
            StopLoopback();
            lock (_gate)
            {
                _profile = nextProfile; profile = publicProfile; _profileSignature = nextSignature;
                Array.Clear(_samples, 0, _samples.Length);
                _sequenceSeen = false;
                acceptedPackets = rejectedPackets = rejectedChannels = 0;
                lastRejection = "";
            }
        }

        // Prepare the profile and a replacement socket before changing live observations.
        // A malformed profile or an occupied port leaves the previous listener usable.
        public void ConfigureLoopback(AcquisitionProfile configuration)
        {
            ValidateProfile(configuration);
            if (!configuration.enableLoopbackUdp || configuration.loopbackPort <= 0)
                throw new ArgumentException("An explicit localhost port is required.");
            var nextProfile = Clone(configuration);
            var publicProfile = Clone(configuration);
            string nextSignature = JsonUtility.ToJson(Clone(nextProfile));
            UdpClient previous;
            lock (_gate)
            {
                previous = _udp;
                var nextSocket = previous != null && LocalPort == nextProfile.loopbackPort ? previous
                    : new UdpClient(new IPEndPoint(IPAddress.Loopback, nextProfile.loopbackPort));
                _profile = nextProfile; profile = publicProfile; _profileSignature = nextSignature; _udp = nextSocket;
                Array.Clear(_samples, 0, _samples.Length);
                _sequenceSeen = false;
                acceptedPackets = rejectedPackets = rejectedChannels = 0;
                lastRejection = "";
                if (ReferenceEquals(previous, nextSocket)) previous = null;
            }
            previous?.Close();
        }

        public void ClearObservations()
        {
            lock (_gate)
            {
                Array.Clear(_samples, 0, _samples.Length);
                _sequenceSeen = false;
            }
        }

        public static void ValidateRecordedSnapshot(AcquisitionSnapshot snapshot, AcquisitionProfile profile)
        {
            ValidateProfile(profile);
            if (snapshot == null || snapshot.format != AcquisitionPacket.Format || snapshot.sourceId != profile.sourceId
                || snapshot.profileVersion != profile.version || snapshot.replayed || snapshot.capturedMonotonicTicks < 0
                || snapshot.channels == null || snapshot.channels.Length != 4)
                throw new InvalidDataException("Missing or mismatched acquisition snapshot.");
            int rawCount = 0, forceCount = 0;
            bool samePacket = true;
            for (int i = 0; i < 4; i++)
            {
                var s = snapshot.channels[i];
                var c = Array.Find(profile.channels, p => p.channelId == "CH" + (i + 1));
                if (s == null || s.channelId != c.channelId || s.sourceId != profile.sourceId || s.mapping != c.mapping
                    || !Enum.IsDefined(typeof(AcquisitionSourceKind), s.sourceKind) || !Finite(s.raw) || !Finite(s.forceN)
                    || !Finite(s.ageSeconds) || s.valid != s.rawValid)
                    throw new InvalidDataException("Invalid acquisition channel identity or value.");
                var calibration = c.calibration;
                if (s.calibrationMeasured != (calibration != null && calibration.measured)
                    || s.calibrationSourceId != calibration?.sourceId || s.calibrationVersion != calibration?.version
                    || s.calibrationEvidence != calibration?.evidence)
                    throw new InvalidDataException("Acquisition calibration provenance differs.");
                if (!s.present) { samePacket = false; if (s.rawValid || s.calibratedValid) throw new InvalidDataException("Missing channel marked valid."); continue; }
                double age = (snapshot.capturedMonotonicTicks - s.receivedMonotonicTicks) / (double)Stopwatch.Frequency;
                if (s.receivedMonotonicTicks < 0 || Math.Abs(age - s.ageSeconds) > 1e-8)
                    throw new InvalidDataException("Acquisition local-age evidence differs.");
                bool rawValid = s.hasRaw && s.rawNumericFinite && s.sourceValid && !s.saturated && s.rawUnits == c.rawUnits
                    && (!c.rawRangeConfigured || (s.raw >= c.minimumRaw && s.raw <= c.maximumRaw))
                    && age >= 0 && age <= c.maximumAgeSeconds;
                if (s.rawValid != rawValid || s.rawRangeVerified != c.rawRangeConfigured)
                    throw new InvalidDataException("Acquisition validity differs from its recorded profile.");
                string rawStatus = !s.hasRaw ? "RAW_MISSING" : !s.rawNumericFinite ? "NONFINITE_RAW"
                    : !s.sourceValid ? "SOURCE_INVALID" : s.saturated ? "SATURATED"
                    : s.rawUnits != c.rawUnits ? "RAW_UNITS_MISMATCH"
                    : c.rawRangeConfigured && (s.raw < c.minimumRaw || s.raw > c.maximumRaw) ? "RAW_OUT_OF_RANGE"
                    : age < 0 ? "LOCAL_CLOCK_REVERSED" : age > c.maximumAgeSeconds ? "STALE" : "OK";
                if (s.rawStatus != rawStatus) throw new InvalidDataException("Acquisition raw status differs.");
                double force = 0; string status = "RAW_INVALID";
                bool forceValid = rawValid && Calibrate(profile.sourceId, c, s.raw, out force, out status);
                if (s.calibratedValid != forceValid || (forceValid && Math.Abs(s.forceN - force) > 1e-8))
                    throw new InvalidDataException("Acquisition force differs from calibration.");
                if (s.forceStatus != status) throw new InvalidDataException("Acquisition force status differs.");
                if (rawValid) rawCount++;
                if (forceValid) forceCount++;
                if (i > 0 && s.packetSequence != snapshot.channels[0].packetSequence) samePacket = false;
            }
            if (snapshot.rawValidCount != rawCount || snapshot.calibratedValidCount != forceCount
                || snapshot.valid != (rawCount == 4) || snapshot.samePacket != samePacket)
                throw new InvalidDataException("Acquisition summary differs from channel evidence.");
        }

        static T Clone<T>(T value) => JsonUtility.FromJson<T>(JsonUtility.ToJson(value));
        static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        static int ChannelIndex(string id)
        {
            for (int i = 0; i < 4; i++) if (id == "CH" + (i + 1)) return i;
            return -1;
        }
        static bool Newer(uint current, uint previous) => unchecked((int)(current - previous)) > 0;
        void EnsureProfile()
        {
            if (_profile != null) return;
            ValidateProfile(profile);
            var nextProfile = Clone(profile);
            string nextSignature = JsonUtility.ToJson(Clone(nextProfile));
            _profile = nextProfile; _profileSignature = nextSignature;
        }

        public static void ValidateProfile(AcquisitionProfile p)
        {
            if (p == null || p.schemaVersion != 1 || string.IsNullOrWhiteSpace(p.version)
                || string.IsNullOrWhiteSpace(p.sourceId) || p.channels == null || p.channels.Length != 4)
                throw new ArgumentException("Profile requires version, sourceId and exactly CH1..CH4.");
            if (p.loopbackPort < 0 || p.loopbackPort > 65535) throw new ArgumentException("Invalid loopback port.");
            var seen = new bool[4];
            foreach (var c in p.channels)
            {
                int index = c == null ? -1 : ChannelIndex(c.channelId);
                if (index < 0 || seen[index]) throw new ArgumentException("Duplicate or unknown profile channel.");
                seen[index] = true;
                if (string.IsNullOrWhiteSpace(c.mapping) || string.IsNullOrWhiteSpace(c.rawUnits)
                    || !Finite(c.maximumAgeSeconds) || c.maximumAgeSeconds <= 0)
                    throw new ArgumentException("Channel requires mapping, rawUnits and positive finite TTL.");
                if (c.rawRangeConfigured && (!Finite(c.minimumRaw) || !Finite(c.maximumRaw) || c.minimumRaw >= c.maximumRaw))
                    throw new ArgumentException("Invalid raw range.");
            }
        }

        AcquisitionChannelProfile ChannelProfile(int index)
        {
            foreach (var c in _profile.channels) if (ChannelIndex(c.channelId) == index) return c;
            throw new InvalidOperationException("Missing channel profile.");
        }

        bool Reject(string reason) { rejectedPackets++; lastRejection = reason; return false; }

        public bool AcceptJson(string json, long receivedTicks)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > 16000)
            { lock (_gate) return Reject("INVALID_JSON_LENGTH"); }
            try
            {
                AcquisitionJsonEvidence.ValidatePacketJson(json);
                return Accept(JsonUtility.FromJson<AcquisitionPacket>(json), receivedTicks);
            }
            catch (InvalidDataException) { lock (_gate) return Reject("INVALID_JSON_EVIDENCE"); }
            catch (ArgumentException) { lock (_gate) return Reject("INVALID_JSON_OR_PROFILE"); }
        }

        public bool Accept(AcquisitionPacket packet, long receivedTicks)
        {
            lock (_gate)
            {
                EnsureProfile();
                if (packet == null || packet.format != AcquisitionPacket.Format || packet.sourceId != _profile.sourceId
                    || !Enum.IsDefined(typeof(AcquisitionSourceKind), packet.sourceKind) || !packet.sequenceValid
                    || packet.channels == null || packet.channels.Length == 0 || packet.channels.Length > 4 || receivedTicks < 0)
                    return Reject("INVALID_PACKET_OR_SOURCE");
                var seen = new bool[4];
                foreach (var c in packet.channels)
                {
                    int index = c == null ? -1 : ChannelIndex(c.channelId);
                    if (index < 0 || seen[index] || !c.sampleSequenceValid || string.IsNullOrWhiteSpace(c.rawUnits))
                        return Reject("INVALID_CHANNEL_SET");
                    seen[index] = true;
                }
                if (_sequenceSeen && !Newer(packet.sequence, _sequence)) return Reject("PACKET_DUPLICATE_OR_OLD");
                _sequence = packet.sequence; _sequenceSeen = true;
                int accepted = 0;
                foreach (var c in packet.channels)
                {
                    int index = ChannelIndex(c.channelId);
                    var previous = _samples[index];
                    if (previous != null && (!Newer(c.sampleSequence, previous.input.sampleSequence)
                        || receivedTicks < previous.receivedTicks))
                    { rejectedChannels++; lastRejection = "CHANNEL_DUPLICATE_OR_OLD:" + c.channelId; continue; }
                    // Preserve invalid latest observations too: a new fault must not retain an old valid force.
                    _samples[index] = new StoredChannel
                    {
                        input = new AcquisitionChannelInput { channelId = c.channelId, sampleSequenceValid = true,
                            sampleSequence = c.sampleSequence, sampleTimeValid = c.sampleTimeValid, sampleTimeUs = c.sampleTimeUs,
                            rawUnits = c.rawUnits, hasRaw = c.hasRaw, raw = c.raw, rawText = c.rawText, valid = c.valid, saturated = c.saturated },
                        sourceKind = packet.sourceKind, packetSequence = packet.sequence,
                        deviceTimeValid = packet.deviceTimeValid, deviceTimeUs = packet.deviceTimeUs, receivedTicks = receivedTicks
                    };
                    accepted++;
                }
                if (accepted == 0) return Reject("NO_NEW_CHANNELS");
                acceptedPackets++;
                return true;
            }
        }

        public AcquisitionSnapshot Snapshot() => Snapshot(Stopwatch.GetTimestamp());
        public AcquisitionSnapshot Snapshot(long nowTicks)
        {
            lock (_gate)
            {
                EnsureProfile();
                var result = new AcquisitionSnapshot { profileVersion = _profile.version, sourceId = _profile.sourceId,
                    capturedMonotonicTicks = nowTicks, channels = new AcquisitionChannelSnapshot[4], samePacket = true };
                for (int i = 0; i < 4; i++)
                {
                    var c = ChannelProfile(i); var stored = _samples[i]; var calibration = c.calibration;
                    var s = new AcquisitionChannelSnapshot { channelId = c.channelId, sourceId = _profile.sourceId,
                        mapping = c.mapping, rawUnits = c.rawUnits, rawStatus = "MISSING", forceStatus = "RAW_INVALID",
                        calibrationMeasured = calibration != null && calibration.measured,
                        calibrationSourceId = calibration?.sourceId, calibrationVersion = calibration?.version,
                        calibrationEvidence = calibration?.evidence };
                    result.channels[i] = s;
                    if (stored == null) { result.samePacket = false; continue; }
                    var input = stored.input;
                    s.present = true; s.sourceKind = stored.sourceKind; s.hasRaw = input.hasRaw;
                    s.rawNumericFinite = Finite(input.raw);
                    // Keep recordings valid JSON even for a rejected nonfinite injected sample.
                    s.raw = s.rawNumericFinite ? input.raw : 0;
                    s.rawText = input.rawText ?? (s.rawNumericFinite ? null : input.raw.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    s.rawUnits = input.rawUnits; s.sourceValid = input.valid; s.saturated = input.saturated;
                    s.packetSequence = stored.packetSequence; s.sampleSequence = input.sampleSequence;
                    s.deviceTimeValid = stored.deviceTimeValid; s.deviceTimeUs = stored.deviceTimeUs;
                    s.sampleTimeValid = input.sampleTimeValid; s.sampleTimeUs = input.sampleTimeUs;
                    s.receivedMonotonicTicks = stored.receivedTicks;
                    s.ageSeconds = (nowTicks - stored.receivedTicks) / (double)Stopwatch.Frequency;
                    s.rawRangeVerified = c.rawRangeConfigured;
                    s.rawStatus = !input.hasRaw ? "RAW_MISSING" : !Finite(input.raw) ? "NONFINITE_RAW"
                        : !input.valid ? "SOURCE_INVALID" : input.saturated ? "SATURATED"
                        : input.rawUnits != c.rawUnits ? "RAW_UNITS_MISMATCH"
                        : c.rawRangeConfigured && (input.raw < c.minimumRaw || input.raw > c.maximumRaw) ? "RAW_OUT_OF_RANGE"
                        : nowTicks < stored.receivedTicks ? "LOCAL_CLOCK_REVERSED"
                        : s.ageSeconds > c.maximumAgeSeconds ? "STALE" : "OK";
                    s.rawValid = s.rawStatus == "OK";
                    s.valid = s.rawValid;
                    if (s.rawValid)
                    {
                        result.rawValidCount++;
                        s.calibratedValid = Calibrate(_profile.sourceId, c, input.raw, out s.forceN, out s.forceStatus);
                        if (s.calibratedValid) result.calibratedValidCount++;
                    }
                    if (i > 0 && s.packetSequence != result.channels[0].packetSequence) result.samePacket = false;
                }
                result.valid = result.rawValidCount == 4;
                result.status = result.valid ? "RAW_VALID_PC_OBSERVATIONS" : result.rawValidCount > 0 ? "PARTIAL_RAW_OBSERVATIONS" : "NO_VALID_RAW_OBSERVATIONS";
                return result;
            }
        }

        static bool Calibrate(string sourceId, AcquisitionChannelProfile channel, double raw, out double force, out string status)
        {
            force = 0;
            var c = channel.calibration;
            if (c == null || !c.measured || c.mode == AcquisitionCalibrationMode.None) { status = "UNCALIBRATED"; return false; }
            if (c.sourceId != sourceId || string.IsNullOrWhiteSpace(c.version) || string.IsNullOrWhiteSpace(c.evidence))
            { status = "CALIBRATION_EVIDENCE_MISSING"; return false; }
            if (!channel.rawRangeConfigured) { status = "RAW_RANGE_UNCONFIRMED"; return false; }
            if (!c.forceRangeConfigured || !Finite(c.minimumForceN) || !Finite(c.maximumForceN) || c.minimumForceN >= c.maximumForceN)
            { status = "FORCE_RANGE_UNCONFIRMED"; return false; }
            double value;
            if (c.mode == AcquisitionCalibrationMode.ZeroScale)
            {
                if (!Finite(c.rawZero) || !Finite(c.scaleNPerRaw) || c.scaleNPerRaw == 0)
                { status = "INVALID_ZERO_SCALE"; return false; }
                value = (raw - c.rawZero) * c.scaleNPerRaw;
            }
            else if (c.mode == AcquisitionCalibrationMode.LinearFit)
            {
                if (!TryLinearFit(c.points, out double slope, out double intercept, out double min, out double max))
                { status = "INVALID_CALIBRATION_POINTS"; return false; }
                if (raw < min || raw > max) { status = "CALIBRATION_EXTRAPOLATION"; return false; }
                value = slope * raw + intercept;
            }
            else { status = "UNKNOWN_CALIBRATION_MODE"; return false; }
            if (!Finite(value)) { status = "NONFINITE_CALIBRATED_VALUE"; return false; }
            if (value < c.minimumForceN || value > c.maximumForceN) { status = "FORCE_OUT_OF_RANGE"; return false; }
            force = value; status = "CALIBRATED_N"; return true;
        }

        public static bool TryLinearFit(AcquisitionCalibrationPoint[] points, out double slope, out double intercept,
            out double minimumRaw, out double maximumRaw)
        {
            slope = intercept = 0; minimumRaw = double.PositiveInfinity; maximumRaw = double.NegativeInfinity;
            if (points == null || points.Length < 2 || points.Length > 4096) return false;
            double meanX = 0, meanY = 0;
            for (int i = 0; i < points.Length; i++)
            {
                var p = points[i]; if (p == null || !Finite(p.raw) || !Finite(p.forceN)) return false;
                meanX += (p.raw - meanX) / (i + 1); meanY += (p.forceN - meanY) / (i + 1);
                minimumRaw = Math.Min(minimumRaw, p.raw); maximumRaw = Math.Max(maximumRaw, p.raw);
            }
            double xx = 0, xy = 0;
            foreach (var p in points) { double dx = p.raw - meanX; xx += dx * dx; xy += dx * (p.forceN - meanY); }
            if (!Finite(xx) || xx <= 0 || !Finite(xy)) return false;
            slope = xy / xx; intercept = meanY - slope * meanX;
            return Finite(slope) && slope != 0 && Finite(intercept);
        }

        // Explicit opt-in only; binds 127.0.0.1, never a network-facing firmware listener.
        public bool StartLoopback()
        {
            lock (_gate)
            {
                EnsureProfile();
                if (!_profile.enableLoopbackUdp) return false;
                if (_udp != null) return true;
                _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, _profile.loopbackPort));
                return true;
            }
        }
        public void StopLoopback()
        {
            UdpClient old;
            lock (_gate) { old = _udp; _udp = null; }
            old?.Close();
        }
        public bool StartListening() => StartLoopback();
        public void StopListening() => StopLoopback();
        public int PollLoopback()
        {
            var socket = _udp; if (socket == null) return 0;
            int count = 0;
            try
            {
                while (count < 32 && socket.Available > 0)
                {
                    var peer = new IPEndPoint(IPAddress.Any, 0);
                    byte[] bytes = socket.Receive(ref peer); count++;
                    long received = Stopwatch.GetTimestamp();
                    if (!IPAddress.IsLoopback(peer.Address) || bytes.Length > 16000)
                    { lock (_gate) Reject("INVALID_LOOPBACK_DATAGRAM"); continue; }
                    try { AcceptJson(Utf8.GetString(bytes), received); }
                    catch (DecoderFallbackException) { lock (_gate) Reject("INVALID_UTF8"); }
                }
            }
            catch (SocketException e) { lock (_gate) Reject("LOOPBACK_SOCKET:" + e.SocketErrorCode); }
            catch (ObjectDisposedException) { }
            return count;
        }
    }
}
