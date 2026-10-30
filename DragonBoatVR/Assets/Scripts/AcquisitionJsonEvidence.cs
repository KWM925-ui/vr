using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DragonBoatVR
{
    /// <summary>Checks JSON evidence before JsonUtility can replace missing fields with defaults.
    /// Value/profile validation remains the bridge's responsibility.</summary>
    public static class AcquisitionJsonEvidence
    {
        public static void ValidatePacketJson(string json)
        {
            Node packet = Parse(json, 16000, 4096);
            Object(packet);
            String(packet, "format"); String(packet, "sourceId"); Integer(packet, "sourceKind", 0, 3);
            Boolean(packet, "sequenceValid"); Integer(packet, "sequence", 0, uint.MaxValue);
            if (Boolean(packet, "deviceTimeValid")) Integer(packet, "deviceTimeUs", 0, ulong.MaxValue);
            Node channels = Array(packet, "channels", 1, 4);
            foreach (Node channel in channels.items)
            {
                Object(channel); String(channel, "channelId"); String(channel, "rawUnits");
                Boolean(channel, "sampleSequenceValid"); Integer(channel, "sampleSequence", 0, uint.MaxValue);
                if (Boolean(channel, "sampleTimeValid")) Integer(channel, "sampleTimeUs", 0, ulong.MaxValue);
                if (Boolean(channel, "hasRaw")) Number(channel, "raw");
                Boolean(channel, "valid"); Boolean(channel, "saturated");
                OptionalNullableString(channel, "rawText");
            }
        }

        public static void ValidateProfileJson(string json)
        {
            ValidateProfile(Parse(json, 256 * 1024, 10000));
        }

        public static void ValidateRawProfileJson(string json)
        {
            ValidateRawProfile(Parse(json, 256 * 1024, 10000));
        }

        public static void ValidateRawRecordingProfileJson(string headerJson)
        {
            Node header = Parse(headerJson, 2 * 1024 * 1024, 100000);
            Object(header);
            Integer(header, "schemaVersion", 5, 5);
            if (String(header, "recordType") != "header") Fail("Raw replay profile requires a schema 5 header.");
            ValidateRawProfile(Field(header, "acquisitionProfile"));
        }

        static void ValidateRawProfile(Node profile)
        {
            ValidateProfile(profile);
            foreach (Node channel in Array(profile, "channels", 4, 4).items)
            {
                Node calibration = Field(channel, "calibration");
                Object(calibration);
                Array(calibration, "points", 0, 0);
            }
        }

        public static void ValidateFrameJson(string json)
        {
            Node frame = Parse(json, 2 * 1024 * 1024, 100000);
            Object(frame);
            // A recorded version/type must be explicit even when it equals the class initializer.
            long schema = (long)Integer(frame, "schemaVersion", 4, 5);
            string type = String(frame, "recordType");
            if (type == "invalidated") return;
            if (type == "complete")
            {
                if (schema != 5) Fail("Completion requires recording schema 5.");
                Integer(frame, "frameCount", 1, int.MaxValue);
                Number(frame, "lastFrameTimeSeconds"); String(frame, "configurationSignature");
                string hash = String(frame, "contentSha256");
                if (hash.Length != 64) Fail("Invalid recording content hash.");
                foreach (char digit in hash)
                    if (!((digit >= '0' && digit <= '9') || (digit >= 'a' && digit <= 'f')))
                        Fail("Invalid recording content hash.");
                return;
            }
            if (type != "header" && type != "frame") Fail("Invalid frame record type.");
            if (!Boolean(frame, "hasAcquisitionSnapshot")) return;
            ValidateSnapshot(Field(frame, "acquisitionSnapshot"));
            if (type == "header") ValidateProfile(Field(frame, "acquisitionProfile"));
        }

        // Unity's native JSON numeric conversion can differ by one double bit between
        // its own frame JSON and the worker's invariant round-trip footer JSON. Use
        // the same strict .NET parser for both sides of the exact integrity comparison.
        public static double ReadRecordingTimeSeconds(string json, bool completion = false)
        {
            Node record = Parse(json, 2 * 1024 * 1024, 100000);
            Object(record);
            long schema = (long)Integer(record, "schemaVersion", 4, 5);
            string type = String(record, "recordType");
            if (completion ? schema != 5 || type != "complete" : type != "header" && type != "frame")
                Fail("Unexpected recording time record type.");
            string field = completion ? "lastFrameTimeSeconds" : "timeSeconds";
            Number(record, field);
            return double.Parse(Field(record, field).scalar, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        static void ValidateSnapshot(Node snapshot)
        {
            Object(snapshot); String(snapshot, "format"); String(snapshot, "profileVersion"); String(snapshot, "sourceId");
            Integer(snapshot, "capturedMonotonicTicks", 0, long.MaxValue);
            Boolean(snapshot, "replayed"); Boolean(snapshot, "valid"); Boolean(snapshot, "samePacket");
            Integer(snapshot, "rawValidCount", 0, 4); Integer(snapshot, "calibratedValidCount", 0, 4);
            String(snapshot, "status");
            foreach (Node channel in Array(snapshot, "channels", 4, 4).items)
            {
                Object(channel);
                String(channel, "channelId"); String(channel, "sourceId"); String(channel, "mapping"); String(channel, "rawUnits");
                Integer(channel, "sourceKind", 0, 3);
                foreach (string key in new[] { "present", "hasRaw", "rawNumericFinite", "sourceValid", "saturated",
                    "deviceTimeValid", "sampleTimeValid", "rawRangeVerified", "valid", "rawValid", "calibrationMeasured", "calibratedValid" })
                    Boolean(channel, key);
                Integer(channel, "packetSequence", 0, uint.MaxValue); Integer(channel, "sampleSequence", 0, uint.MaxValue);
                Integer(channel, "deviceTimeUs", 0, ulong.MaxValue); Integer(channel, "sampleTimeUs", 0, ulong.MaxValue);
                Integer(channel, "receivedMonotonicTicks", 0, long.MaxValue);
                Number(channel, "raw"); Number(channel, "ageSeconds"); Number(channel, "forceN");
                String(channel, "rawStatus"); String(channel, "forceStatus");
                NullableString(channel, "calibrationSourceId"); NullableString(channel, "calibrationVersion"); NullableString(channel, "calibrationEvidence");
                OptionalNullableString(channel, "rawText");
            }
        }

        static void ValidateProfile(Node profile)
        {
            Object(profile);
            Integer(profile, "schemaVersion", 1, 1); String(profile, "version"); String(profile, "sourceId");
            Boolean(profile, "enableLoopbackUdp"); Integer(profile, "loopbackPort", 0, 65535);
            foreach (Node channel in Array(profile, "channels", 4, 4).items)
            {
                Object(channel); String(channel, "channelId"); String(channel, "mapping"); String(channel, "rawUnits");
                Number(channel, "maximumAgeSeconds"); Boolean(channel, "rawRangeConfigured");
                Number(channel, "minimumRaw"); Number(channel, "maximumRaw");
                Node calibration = Field(channel, "calibration");
                if (calibration.kind == Kind.Null) continue;
                Object(calibration); Integer(calibration, "mode", 0, 2); Boolean(calibration, "measured");
                NullableString(calibration, "sourceId"); NullableString(calibration, "version"); NullableString(calibration, "evidence");
                Number(calibration, "rawZero"); Number(calibration, "scaleNPerRaw");
                Boolean(calibration, "forceRangeConfigured"); Number(calibration, "minimumForceN"); Number(calibration, "maximumForceN");
                if (Integer(calibration, "mode", 0, 2) == 2)
                    foreach (Node point in Array(calibration, "points", 2, 4096).items)
                    { Object(point); Number(point, "raw"); Number(point, "forceN"); }
            }
        }

        public static void ValidateTransition(AcquisitionSnapshot previous, AcquisitionSnapshot next)
        {
            if (next == null || next.channels == null || next.channels.Length != 4)
                Fail("Missing next acquisition transition.");
            if (previous == null) return;
            if (previous.channels == null || previous.channels.Length != 4 || next.replayed || previous.replayed
                || next.format != previous.format || next.sourceId != previous.sourceId || next.profileVersion != previous.profileVersion
                || next.capturedMonotonicTicks < previous.capturedMonotonicTicks)
                Fail("Acquisition profile or local clock changed inside recording.");
            for (int i = 0; i < 4; i++)
            {
                AcquisitionChannelSnapshot a = previous.channels[i], b = next.channels[i];
                if (a == null || b == null || a.channelId != b.channelId || a.sourceId != b.sourceId || a.mapping != b.mapping
                    || a.calibrationMeasured != b.calibrationMeasured || a.calibrationSourceId != b.calibrationSourceId
                    || a.calibrationVersion != b.calibrationVersion || a.calibrationEvidence != b.calibrationEvidence)
                    Fail("Acquisition channel identity changed inside recording.");
                if (!a.present) continue;
                if (!b.present) Fail("Stored acquisition channel disappeared without a new recording.");
                if (a.sampleSequence == b.sampleSequence)
                {
                    if (a.packetSequence != b.packetSequence || a.receivedMonotonicTicks != b.receivedMonotonicTicks
                        || a.sourceKind != b.sourceKind || a.hasRaw != b.hasRaw || a.raw != b.raw || a.rawText != b.rawText
                        || a.rawUnits != b.rawUnits || a.rawNumericFinite != b.rawNumericFinite || a.sourceValid != b.sourceValid
                        || a.saturated != b.saturated || a.deviceTimeValid != b.deviceTimeValid || a.deviceTimeUs != b.deviceTimeUs
                        || a.sampleTimeValid != b.sampleTimeValid || a.sampleTimeUs != b.sampleTimeUs)
                        Fail("Repeated acquisition sample changed its input or refreshed local TTL.");
                }
                else if (!Newer(b.sampleSequence, a.sampleSequence) || !Newer(b.packetSequence, a.packetSequence)
                    || b.receivedMonotonicTicks < a.receivedMonotonicTicks)
                    Fail("Acquisition sample/packet sequence or receive clock moved backwards.");
            }
        }
        static bool Newer(uint current, uint previous) => unchecked((int)(current - previous)) > 0;

        enum Kind { Object, Array, String, Number, Boolean, Null }
        sealed class Node
        {
            public Kind kind;
            public string scalar;
            public Dictionary<string, Node> fields;
            public List<Node> items;
        }
        static void Fail(string reason) { throw new InvalidDataException(reason); }
        static void Object(Node value) { if (value == null || value.kind != Kind.Object) Fail("JSON object required."); }
        static Node Field(Node value, string name)
        {
            Object(value);
            if (!value.fields.TryGetValue(name, out Node field)) Fail("Missing JSON evidence field: " + name);
            return field;
        }
        static string String(Node value, string name)
        {
            Node field = Field(value, name);
            if (field.kind != Kind.String || string.IsNullOrWhiteSpace(field.scalar)) Fail("Nonempty string required: " + name);
            return field.scalar;
        }
        static void NullableString(Node value, string name)
        { Node field = Field(value, name); if (field.kind != Kind.String && field.kind != Kind.Null) Fail("String/null required: " + name); }
        static void OptionalNullableString(Node value, string name)
        { if (value.fields.ContainsKey(name)) NullableString(value, name); }
        static bool Boolean(Node value, string name)
        { Node field = Field(value, name); if (field.kind != Kind.Boolean) Fail("Boolean required: " + name); return field.scalar == "true"; }
        static void Number(Node value, string name)
        { Node field = Field(value, name); if (field.kind != Kind.Number) Fail("Number required: " + name); }
        static decimal Integer(Node value, string name, decimal minimum, decimal maximum)
        {
            Node field = Field(value, name);
            if (field.kind != Kind.Number || !decimal.TryParse(field.scalar, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number)
                || number != decimal.Truncate(number) || number < minimum || number > maximum)
                Fail("Bounded integer required: " + name);
            return decimal.Parse(field.scalar, NumberStyles.Float, CultureInfo.InvariantCulture);
        }
        static Node Array(Node value, string name, int minimum, int maximum)
        {
            Node field = Field(value, name);
            if (field.kind != Kind.Array || field.items.Count < minimum || field.items.Count > maximum) Fail("Invalid JSON array: " + name);
            return field;
        }
        static Node Parse(string json, int maximumCharacters, int maximumNodes)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > maximumCharacters) Fail("JSON size limit exceeded or empty.");
            var parser = new Parser(json, maximumNodes);
            Node result = parser.Value(0); parser.Space();
            if (!parser.End) Fail("Trailing JSON content.");
            return result;
        }

        sealed class Parser
        {
            readonly string input;
            readonly int maximumNodes;
            int position, nodes;
            public Parser(string text, int limit) { input = text; maximumNodes = limit; }
            public bool End => position == input.Length;
            public void Space()
            { while (!End && (input[position] == ' ' || input[position] == '\t' || input[position] == '\r' || input[position] == '\n')) position++; }
            bool Take(char token)
            { if (!End && input[position] == token) { position++; return true; } return false; }
            void Need(char token) { if (!Take(token)) Fail("Malformed JSON punctuation."); }
            public Node Value(int depth)
            {
                if (depth > 48 || ++nodes > maximumNodes) Fail("JSON depth/node limit exceeded.");
                Space(); if (End) Fail("Truncated JSON.");
                if (Take('{'))
                {
                    var result = new Node { kind = Kind.Object, fields = new Dictionary<string, Node>(StringComparer.Ordinal) };
                    Space(); if (Take('}')) return result;
                    do
                    {
                        Space(); string key = Text(); Space(); Need(':');
                        Node child = Value(depth + 1);
                        if (result.fields.ContainsKey(key)) Fail("Duplicate JSON key: " + key);
                        result.fields.Add(key, child); Space();
                        if (Take('}')) return result;
                        Need(',');
                    } while (true);
                }
                if (Take('['))
                {
                    var result = new Node { kind = Kind.Array, items = new List<Node>() };
                    Space(); if (Take(']')) return result;
                    do { result.items.Add(Value(depth + 1)); Space(); if (Take(']')) return result; Need(','); } while (true);
                }
                if (input[position] == '"') return new Node { kind = Kind.String, scalar = Text() };
                if (input[position] == 't') { Literal("true"); return new Node { kind = Kind.Boolean, scalar = "true" }; }
                if (input[position] == 'f') { Literal("false"); return new Node { kind = Kind.Boolean, scalar = "false" }; }
                if (input[position] == 'n') { Literal("null"); return new Node { kind = Kind.Null }; }
                int start = position;
                Take('-');
                if (!Take('0')) { if (End || input[position] < '1' || input[position] > '9') Fail("Invalid JSON number."); Digits(); }
                if (Take('.')) { int fraction = position; Digits(); if (position == fraction) Fail("Invalid JSON fraction."); }
                if (Take('e') || Take('E'))
                { if (!Take('+')) Take('-'); int exponent = position; Digits(); if (position == exponent) Fail("Invalid JSON exponent."); }
                string numeric = input.Substring(start, position - start);
                if (!double.TryParse(numeric, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                    || double.IsNaN(number) || double.IsInfinity(number)) Fail("Nonfinite JSON number.");
                return new Node { kind = Kind.Number, scalar = numeric };
            }
            void Digits() { while (!End && input[position] >= '0' && input[position] <= '9') position++; }
            void Literal(string text)
            { foreach (char value in text) { if (End || input[position++] != value) Fail("Invalid JSON literal."); } }
            string Text()
            {
                Need('"'); var result = new StringBuilder(); bool closed = false;
                while (!End)
                {
                    char character = input[position++];
                    if (character == '"') { closed = true; break; }
                    if (character < 0x20) Fail("Unescaped JSON control character.");
                    if (character == '\\')
                    {
                        if (End) Fail("Truncated JSON escape.");
                        switch (input[position++])
                        {
                            case '"': character = '"'; break;
                            case '\\': character = '\\'; break;
                            case '/': character = '/'; break;
                            case 'b': character = '\b'; break;
                            case 'f': character = '\f'; break;
                            case 'n': character = '\n'; break;
                            case 'r': character = '\r'; break;
                            case 't': character = '\t'; break;
                            case 'u':
                                if (position + 4 > input.Length || !ushort.TryParse(input.Substring(position, 4),
                                    NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort code)) Fail("Invalid JSON unicode escape.");
                                character = (char)ushort.Parse(input.Substring(position, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
                                position += 4; break;
                            default: Fail("Unknown JSON escape."); break;
                        }
                    }
                    result.Append(character);
                    if (result.Length > 65536) Fail("JSON string limit exceeded.");
                }
                if (!closed) Fail("Truncated JSON string.");
                for (int i = 0; i < result.Length; i++)
                    if (char.IsHighSurrogate(result[i])) { if (++i >= result.Length || !char.IsLowSurrogate(result[i])) Fail("Invalid unicode surrogate."); }
                    else if (char.IsLowSurrogate(result[i])) Fail("Invalid unicode surrogate.");
                return result.ToString();
            }
        }
    }
}
