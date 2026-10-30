using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DragonBoatVR
{
    /// <summary>Schema 5 completion evidence. The exact header and frame lines are hashed;
    /// this footer is excluded. Readers must validate every field before trusting it.</summary>
    [Serializable]
    public sealed class RecordingCompletion
    {
        public int schemaVersion = 5;
        public string recordType = "complete";
        public int frameCount;
        public double lastFrameTimeSeconds;
        public string configurationSignature;
        public string contentSha256;

        // Called by the recording worker; no Unity serialization API may run there.
        public string ToJson()
        {
            if (schemaVersion != 5 || recordType != "complete" || frameCount <= 0
                || double.IsNaN(lastFrameTimeSeconds) || double.IsInfinity(lastFrameTimeSeconds)
                || string.IsNullOrWhiteSpace(configurationSignature) || contentSha256 == null
                || contentSha256.Length != 64)
                throw new InvalidOperationException("Invalid recording completion evidence.");
            foreach (char digit in contentSha256)
                if (!((digit >= '0' && digit <= '9') || (digit >= 'a' && digit <= 'f')))
                    throw new InvalidOperationException("Recording completion requires a lowercase SHA256 digest.");
            return "{\"schemaVersion\":5,\"recordType\":\"complete\",\"frameCount\":"
                + frameCount.ToString(CultureInfo.InvariantCulture) + ",\"lastFrameTimeSeconds\":"
                + lastFrameTimeSeconds.ToString("R", CultureInfo.InvariantCulture)
                + ",\"configurationSignature\":" + Quoted(configurationSignature)
                + ",\"contentSha256\":" + Quoted(contentSha256) + "}";
        }

        static string Quoted(string value)
        {
            // Verify UTF16 before constructing the JSON so the worker cannot emit replacement
            // glyphs instead of the supplied identity. Unicode itself remains unchanged.
            new UTF8Encoding(false, true).GetByteCount(value);
            var text = new StringBuilder(value.Length + 2);
            text.Append('"');
            foreach (char character in value)
            {
                switch (character)
                {
                    case '"': text.Append("\\\""); break;
                    case '\\': text.Append("\\\\"); break;
                    case '\b': text.Append("\\b"); break;
                    case '\f': text.Append("\\f"); break;
                    case '\n': text.Append("\\n"); break;
                    case '\r': text.Append("\\r"); break;
                    case '\t': text.Append("\\t"); break;
                    default:
                        if (character < 32) text.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        else text.Append(character);
                        break;
                }
            }
            return text.Append('"').ToString();
        }
    }

    /// <summary>Incremental SHA256 over each exact line encoded as strict UTF8 followed
    /// by one LF byte. No BOM, newline conversion, JSON normalization or footer is added.
    /// The owning writer/reader serializes calls; this object does not own a file.</summary>
    public sealed class RecordingContentHash : IDisposable
    {
        readonly SHA256 _sha = SHA256.Create();
        readonly UTF8Encoding _utf8 = new UTF8Encoding(false, true);
        static readonly byte[] LineFeed = { 10 };
        string _finishedHex;
        bool _disposed;

        public void Append(string exactLine)
        {
            ThrowDisposed();
            if (_finishedHex != null) throw new InvalidOperationException("Recording content hash is already complete.");
            if (exactLine == null) throw new ArgumentNullException(nameof(exactLine));
            // Encode first. An invalid UTF16 line must not partially change the digest.
            byte[] bytes = _utf8.GetBytes(exactLine);
            if (bytes.Length != 0) _sha.TransformBlock(bytes, 0, bytes.Length, bytes, 0);
            _sha.TransformBlock(LineFeed, 0, 1, LineFeed, 0);
        }

        public string FinishHex()
        {
            ThrowDisposed();
            if (_finishedHex != null) return _finishedHex;
            _sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            byte[] digest = _sha.Hash;
            const string digits = "0123456789abcdef";
            var text = new char[digest.Length * 2];
            for (int i = 0; i < digest.Length; i++)
            {
                text[2 * i] = digits[digest[i] >> 4];
                text[2 * i + 1] = digits[digest[i] & 15];
            }
            _finishedHex = new string(text);
            return _finishedHex;
        }

        void ThrowDisposed()
        { if (_disposed) throw new ObjectDisposedException(nameof(RecordingContentHash)); }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _sha.Dispose();
        }
    }
}
