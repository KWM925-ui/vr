using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace DragonBoatVR
{
    // Installed only by the runtime acceptance observer on the actual listener.
    // The mixer callback only copies to bounded memory; saving happens after stop.
    public sealed class RuntimeAudioCapture : MonoBehaviour
    {
        readonly object _gate = new object();
        float[] _samples;
        int _count, _channels, _rate;
        bool _recording, _formatChanged, _capacityReached;
        public int SampleFrames => _channels > 0 ? _count / _channels : 0;
        public int Channels => _channels;
        public int SampleRate => _rate;
        public float Peak { get; private set; }
        public int ClippedSamples { get; private set; }
        public double FirstBufferDspTime { get; private set; } = double.NaN;
        public double FirstBufferWallTime { get; private set; } = double.NaN;

        public void Begin(int maximumSeconds = 24)
        {
            if (maximumSeconds < 2 || maximumSeconds > 90) throw new ArgumentOutOfRangeException(nameof(maximumSeconds));
            lock (_gate)
            {
                _rate = AudioSettings.outputSampleRate;
                _samples = new float[_rate * 2 * maximumSeconds];
                _count = _channels = 0; _formatChanged = _capacityReached = false; _recording = true;
                FirstBufferDspTime = FirstBufferWallTime = double.NaN;
            }
        }
        void OnAudioFilterRead(float[] data, int channels)
        {
            lock (_gate)
            {
                if (!_recording || _samples == null) return;
                if (_channels == 0)
                {
                    _channels = channels;
                    FirstBufferDspTime = AudioSettings.dspTime;
                    FirstBufferWallTime = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
                }
                if (channels != _channels) { _formatChanged = true; _recording = false; return; }
                int count = Math.Min(data.Length, _samples.Length - _count);
                count -= count % channels;
                Array.Copy(data, 0, _samples, _count, count); _count += count;
                if (_count == _samples.Length) { _capacityReached = true; _recording = false; }
            }
        }
        public void Save(string path)
        {
            lock (_gate) _recording = false;
            if (_formatChanged || _capacityReached || _channels <= 0 || SampleFrames < _rate * 2)
                throw new IOException("Actual listener capture is missing, short, exceeded its bounded duration or changed channel format.");
            var pcm = new byte[_count * 2];
            Peak = 0f; ClippedSamples = 0;
            for (int i = 0; i < _count; i++)
            {
                float value = _samples[i];
                if (float.IsNaN(value) || float.IsInfinity(value)) throw new IOException("Nonfinite mixer output.");
                Peak = Math.Max(Peak, Math.Abs(value));
                if (Math.Abs(value) > 1f) ClippedSamples++;
                short sample = (short)Math.Round(Math.Max(-1f, Math.Min(1f, value)) * 32767f);
                pcm[2 * i] = (byte)(sample & 255); pcm[2 * i + 1] = (byte)((sample >> 8) & 255);
            }
            using (var writer = new BinaryWriter(File.Open(path, FileMode.CreateNew), Encoding.ASCII))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + pcm.Length);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1);
                writer.Write((short)_channels); writer.Write(_rate); writer.Write(_rate * _channels * 2);
                writer.Write((short)(_channels * 2)); writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(pcm.Length); writer.Write(pcm);
            }
            _samples = null;
        }
        void OnDisable() { lock (_gate) _recording = false; }
    }
}
