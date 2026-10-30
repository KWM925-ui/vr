using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DragonBoatVR.EditorTools
{
    /// <summary>Hash primitive checks only. Session tests own footer parsing, durable
    /// publication and refusal of a truncated recording in the real replay path.</summary>
    public static class RecordingIntegritySelfTest
    {
        const string Header = "{\"schemaVersion\":5,\"recordType\":\"header\",\"text\":\"视觉\\n\\\"桨\\\"\\\\路径\"}";
        const string Frame1 = "{\"schemaVersion\":5,\"recordType\":\"frame\",\"timeSeconds\":0.01}";
        const string Frame2 = "{\"schemaVersion\":5,\"recordType\":\"frame\",\"timeSeconds\":0.02}";
        // Independently generated using Python hashlib.sha256 over strict UTF8 of the
        // three exact strings joined with LF and followed by a final LF; no BOM/footer.
        const string KnownChineseDigest = "d268fe150740e2c5962de08c0e4c0472a59b47454baabd607f8743b2915456bb";

        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
        static bool Throws<T>(Action action) where T : Exception
        { try { action(); return false; } catch (T) { return true; } }

        static string Hash(params string[] lines)
        {
            using (var digest = new RecordingContentHash())
            {
                foreach (string line in lines) digest.Append(line);
                return digest.FinishHex();
            }
        }

        public static bool CheckAll()
        {
            Require(Hash() == "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", "Empty content SHA256 differs.");
            Require(Hash("abc") == "edeaaff3f1774ad2888673770c6d64097e391bc362d7d6fb34982ddf0efd18cb", "Canonical LF differs.");
            string full = Hash(Header, Frame1, Frame2);
            Require(full == KnownChineseDigest, "Exact Unicode and JSON escape bytes differ from the independent hash vector.");
            byte[] canonical = new UTF8Encoding(false, true).GetBytes(Header + "\n" + Frame1 + "\n" + Frame2 + "\n");
            using (var sha = SHA256.Create())
                Require(BitConverter.ToString(sha.ComputeHash(canonical)).Replace("-", "").ToLowerInvariant() == full,
                    "Incremental hashing differs from one-shot canonical bytes.");
            Require(Hash(Header, Frame2, Frame1) != full, "Frame order failed to affect digest.");
            Require(Hash(Header, Frame1) != full, "Removing the tail frame failed to affect digest.");
            Require(Hash(Header, Frame1, Frame2.Replace("0.02", "0.03")) != full, "A modified frame failed to affect digest.");
            Require(Hash(Header + " ", Frame1, Frame2) != full, "Exact whitespace was normalized away.");
            Require(Hash("abc\r") != Hash("abc"), "Exact CR text was silently normalized.");
            Require(Hash("", "abc") != Hash("abc"), "An appended empty line lost its LF.");

            var digest = new RecordingContentHash();
            try
            {
                digest.Append(Header);
                Require(Throws<ArgumentNullException>(() => digest.Append(null)), "A null line was accepted.");
                Require(Throws<EncoderFallbackException>(() => digest.Append("\ud800")), "Invalid UTF16 was silently replaced.");
                digest.Append(Frame1); digest.Append(Frame2);
                Require(digest.FinishHex() == full && digest.FinishHex() == full, "Finish was unstable or rejected input changed digest.");
                Require(Throws<InvalidOperationException>(() => digest.Append(Frame1)), "A finalized digest accepted another line.");
            }
            finally { digest.Dispose(); }
            digest.Dispose();
            Require(Throws<ObjectDisposedException>(() => digest.Append(Frame1)) && Throws<ObjectDisposedException>(() => digest.FinishHex()),
                "Disposed digest accepted another operation.");
            CheckCompletionJson();
            CheckExactTimeParsing();
            UnityEngine.Debug.Log("[CHAIN] recording content hash UTF8/LF/escape/order/tail/mutation/finalize/dispose/footer-json=True; primitive only");
            return true;
        }

        static void CheckExactTimeParsing()
        {
            string frame = "{\"schemaVersion\":5,\"recordType\":\"frame\",\"timeSeconds\":2.9999999329447748}";
            string footer = "{\"schemaVersion\":5,\"recordType\":\"complete\",\"lastFrameTimeSeconds\":2.9999999329447746}";
            double time = AcquisitionJsonEvidence.ReadRecordingTimeSeconds(frame);
            Require(BitConverter.DoubleToInt64Bits(time) == 4613937818090078208L
                && time == AcquisitionJsonEvidence.ReadRecordingTimeSeconds(footer, true),
                "Frame/footer numeric representations failed the same exact .NET conversion.");
            Require(time != AcquisitionJsonEvidence.ReadRecordingTimeSeconds(footer.Replace("7746", "7750"), true),
                "Adjacent representable completion time was accepted.");
            Require(Throws<System.IO.InvalidDataException>(() => AcquisitionJsonEvidence.ReadRecordingTimeSeconds(frame, true))
                && Throws<System.IO.InvalidDataException>(() => AcquisitionJsonEvidence.ReadRecordingTimeSeconds(footer)),
                "Recording time parser accepted the wrong record type.");
        }

        static void CheckCompletionJson()
        {
            var completion = new RecordingCompletion { frameCount = 2, lastFrameTimeSeconds = 0.02,
                configurationSignature = "视觉\n\"桨\"\\路径\t\u0001", contentSha256 = KnownChineseDigest };
            string json = completion.ToJson();
            Require(json == "{\"schemaVersion\":5,\"recordType\":\"complete\",\"frameCount\":2,\"lastFrameTimeSeconds\":0.02,"
                + "\"configurationSignature\":\"视觉\\n\\\"桨\\\"\\\\路径\\t\\u0001\",\"contentSha256\":\"" + KnownChineseDigest + "\"}",
                "Completion JSON escaping or field values differ.");
            var oldCulture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                Require(completion.ToJson() == json, "Completion numbers depend on the caller culture.");
            }
            finally { CultureInfo.CurrentCulture = oldCulture; }
            completion.frameCount = 0;
            Require(Throws<InvalidOperationException>(() => completion.ToJson()), "Empty completion was accepted.");
            completion.frameCount = 2; completion.lastFrameTimeSeconds = double.NaN;
            Require(Throws<InvalidOperationException>(() => completion.ToJson()), "Nonfinite completion time was accepted.");
            completion.lastFrameTimeSeconds = 0.02; completion.contentSha256 = KnownChineseDigest.ToUpperInvariant();
            Require(Throws<InvalidOperationException>(() => completion.ToJson()), "Noncanonical SHA256 was accepted.");
            completion.contentSha256 = KnownChineseDigest; completion.configurationSignature = " ";
            Require(Throws<InvalidOperationException>(() => completion.ToJson()), "Missing configuration identity was accepted.");
            completion.configurationSignature = "\ud800";
            Require(Throws<EncoderFallbackException>(() => completion.ToJson()), "Invalid UTF16 identity was replaced.");
            completion.configurationSignature = "valid"; completion.schemaVersion = 4;
            Require(Throws<InvalidOperationException>(() => completion.ToJson()), "Legacy schema was emitted as a completion.");
        }
    }
}
