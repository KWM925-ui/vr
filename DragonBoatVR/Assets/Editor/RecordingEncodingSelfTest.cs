using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace DragonBoatVR.EditorTools
{
    /// <summary>Actual replay refusal of invalid encodings. Session owns format and
    /// completeness validation; this helper only creates isolated byte-level variants.</summary>
    public static class RecordingEncodingSelfTest
    {
        static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static bool CheckRefusals(SimulationSession session, string validRecordingPath)
        {
            if (session == null || session.IsReplaying || session.IsRecording || session.boat == null)
                throw new InvalidOperationException("Encoding checks require an idle session after StopReplay.");
            byte[] original = File.ReadAllBytes(validRecordingPath);
            string text = StrictUtf8.GetString(original);
            string[] lines = text.Split(new[] { '\n' }, StringSplitOptions.None);
            var header = JsonUtility.FromJson<SimulationFrame>(lines[0].TrimEnd('\r'));
            if (header == null || header.schemaVersion != 5 || header.recordType != "header")
                throw new InvalidDataException("Encoding checks require a valid schema 5 recording.");
            string frameMarker = "\"recordType\":\"frame\"";
            string footerMarker = "\"recordType\":\"complete\"";
            int frameCharacter = text.IndexOf(frameMarker, StringComparison.Ordinal);
            int footerCharacter = text.LastIndexOf(footerMarker, StringComparison.Ordinal);
            if (frameCharacter < 0 || footerCharacter <= frameCharacter)
                throw new InvalidDataException("Encoding fixture lacks data and completion records.");
            // Put an unfinished UTF8 code point inside the existing recordType string.
            // This does not depend on the real recording already containing Chinese text.
            int frameByte = StrictUtf8.GetByteCount(text.Substring(0, frameCharacter + "\"recordType\":\"".Length));
            int footerByte = StrictUtf8.GetByteCount(text.Substring(0, footerCharacter + "\"recordType\":\"".Length));
            var variants = new Dictionary<string, byte[]>();
            byte[] invalidMiddle = (byte[])original.Clone();
            invalidMiddle[frameByte] = 0xff;
            variants.Add("invalid-utf8-middle", invalidMiddle);
            variants.Add("utf16-le-bom", WithPreamble(new UnicodeEncoding(false, true, true), text));
            variants.Add("utf16-be-bom", WithPreamble(new UnicodeEncoding(true, true, true), text));
            variants.Add("utf8-bom", Join(new byte[] { 0xef, 0xbb, 0xbf }, original));
            byte[] unfinishedCharacter = { 0xe8, 0xa7 }; // U+89C6 needs the third byte 0x86.
            variants.Add("truncated-data-codepoint", Join(Prefix(original, frameByte), unfinishedCharacter));
            variants.Add("truncated-footer-codepoint", Join(Prefix(original, footerByte), unfinishedCharacter));

            var rb = session.boat.GetComponent<Rigidbody>();
            Vector3 position = rb.position, velocity = rb.velocity, angularVelocity = rb.angularVelocity;
            Quaternion rotation = rb.rotation;
            double time = session.SimulationTime;
            string latest = session.LatestRecordingPath;
            bool indexExists = File.Exists(session.RecordingIndexPath);
            byte[] index = indexExists ? File.ReadAllBytes(session.RecordingIndexPath) : null;
            bool latestExists = latest != null && File.Exists(latest);
            byte[] latestBytes = latestExists ? File.ReadAllBytes(latest) : null;
            int refusedCount = 0;
            foreach (var variant in variants)
            {
                string candidate = validRecordingPath + ".encoding-" + variant.Key + "-" + Guid.NewGuid().ToString("N");
                using (var stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    stream.Write(variant.Value, 0, variant.Value.Length);
                bool refused = false;
                try { session.BeginReplay(candidate); }
                catch (InvalidDataException) { refused = true; }
                catch (DecoderFallbackException) { refused = true; }
                bool unchanged = !session.IsReplaying && !session.IsRecording && session.SimulationTime == time
                    && Same(position, rb.position) && Same(rotation, rb.rotation)
                    && Same(velocity, rb.velocity) && Same(angularVelocity, rb.angularVelocity)
                    && session.LatestRecordingPath == latest && File.Exists(session.RecordingIndexPath) == indexExists
                    && (!indexExists || SameBytes(index, File.ReadAllBytes(session.RecordingIndexPath)))
                    && (!latestExists || File.Exists(latest) && SameBytes(latestBytes, File.ReadAllBytes(latest)))
                    && SameBytes(original, File.ReadAllBytes(validRecordingPath));
                if (!refused || !unchanged)
                    throw new InvalidOperationException("Encoding variant " + variant.Key + " refused=" + refused
                        + "; replay/boat/time/latest/index/source preserved=" + unchanged);
                refusedCount++;
            }
            Debug.Log("[CHAIN] recording encoding UTF8-invalid/UTF16LE/UTF16BE/BOM/data-codepoint/footer-codepoint refused="
                + refusedCount + "/" + variants.Count + "; replay/boat/time/latest/index/source preserved=True");
            return refusedCount == 6;
        }

        static byte[] WithPreamble(Encoding encoding, string text) => Join(encoding.GetPreamble(), encoding.GetBytes(text));
        static byte[] Prefix(byte[] input, int count)
        { var result = new byte[count]; Buffer.BlockCopy(input, 0, result, 0, count); return result; }
        static byte[] Join(byte[] first, byte[] second)
        {
            var result = new byte[checked(first.Length + second.Length)];
            Buffer.BlockCopy(first, 0, result, 0, first.Length);
            Buffer.BlockCopy(second, 0, result, first.Length, second.Length);
            return result;
        }
        static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
        static bool Same(Vector3 a, Vector3 b) => a.x == b.x && a.y == b.y && a.z == b.z;
        static bool Same(Quaternion a, Quaternion b) => a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w;
    }
}
