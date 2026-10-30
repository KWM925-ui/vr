using System;
using UnityEngine;

namespace DragonBoatVR
{
    // Small PCM clips, made once at startup. No synthesis, allocation or file I/O
    // runs on Unity's real-time audio callback. Original licences live beside assets.
    internal static class RiverAudioClipFactory
    {
        internal const int Rate = 22050;

        internal static float[] ReadMono(AudioClip source)
        {
            if (source == null) throw new InvalidOperationException("Required licensed river audio asset is missing.");
            source.LoadAudioData();
            var input = new float[source.samples * source.channels];
            if (!source.GetData(input, 0)) throw new InvalidOperationException("Cannot decode audio asset " + source.name);
            var mono = new float[Mathf.RoundToInt(source.samples * (float)Rate / source.frequency)];
            float ratio = source.frequency / (float)Rate;
            for (int i = 0; i < mono.Length; i++)
            {
                float position = i * ratio; int a = (int)position, b = Mathf.Min(a + 1, source.samples - 1);
                for (int c = 0; c < source.channels; c++)
                    mono[i] += Mathf.Lerp(input[a * source.channels + c], input[b * source.channels + c], position - a) / source.channels;
            }
            return mono;
        }

        internal static AudioClip Loop(string name, float[] input, float gain = 1f)
        {
            int overlap = Mathf.Min(Rate / 5, input.Length / 8);
            var output = new float[input.Length - overlap];
            for (int i = 0; i < output.Length; i++)
            {
                float sample = input[i];
                if (i < overlap)
                {
                    float blend = Mathf.SmoothStep(0f, 1f, i / (float)overlap);
                    sample = Mathf.Lerp(input[output.Length + i], sample, blend);
                }
                output[i] = sample * gain;
            }
            return Pcm(name, output);
        }

        internal static AudioClip RecordedSlice(string name, float[] recording, float startSeconds, float durationSeconds, float peak = 0f)
        {
            return Pcm(name, Slice(recording, startSeconds, durationSeconds, peak));
        }

        internal static float[] Slice(float[] recording, float startSeconds, float durationSeconds, float peak = 0f)
        {
            int offset = Mathf.RoundToInt(Rate * startSeconds);
            var output = new float[Mathf.RoundToInt(Rate * durationSeconds)];
            if (offset < 0 || output.Length < 2 || offset + output.Length > recording.Length)
                throw new InvalidOperationException("Recorded water excerpt exceeds its licensed source.");
            int attack = Mathf.Min(Rate / 100, output.Length / 8);
            int release = Mathf.Min(Rate / 20, output.Length / 8);
            for (int i = 0; i < output.Length; i++)
            {
                float edge = Mathf.Min(Mathf.Clamp01(i / (float)attack), Mathf.Clamp01((output.Length - 1 - i) / (float)release));
                output[i] = recording[offset + i] * Mathf.SmoothStep(0f, 1f, edge);
            }
            if (peak > 0f) Normalize(output, peak);
            return output;
        }

        internal static AudioClip Creak(AudioClip recording, int variation)
        {
            // Contiguous excerpts of a licensed, recorded timber creak retain
            // its friction and resonances. Only soften the cut boundaries;
            // repeated impact grains do not reproduce a flexing wooden hull.
            float[] source = ReadMono(recording);
            int offset = Mathf.RoundToInt(Rate * (0.6f + variation * 1.9f));
            var output = new float[Mathf.RoundToInt(Rate * 1.2f)];
            if (variation < 0 || variation > 2 || offset + output.Length > source.Length)
                throw new InvalidOperationException("Recorded timber creak is too short for its three excerpts.");
            int fade = Mathf.RoundToInt(Rate * 0.02f);
            for (int i = 0; i < output.Length; i++)
            {
                float edge = Mathf.Clamp01(Mathf.Min(i, output.Length - 1 - i) / (float)fade);
                output[i] = source[offset + i] * Mathf.SmoothStep(0f, 1f, edge);
            }
            return Pcm("RecordedWoodCreak_" + variation, output);
        }

        static void Normalize(float[] samples, float peak)
        {
            float largest = 0f;
            foreach (float value in samples) largest = Mathf.Max(largest, Mathf.Abs(value));
            if (largest <= 1e-8f) throw new InvalidOperationException("Generated audio is silent.");
            float gain = peak / largest;
            for (int i = 0; i < samples.Length; i++) samples[i] *= gain;
        }
        static AudioClip Pcm(string name, float[] samples)
        {
            var clip = AudioClip.Create(name, samples.Length, 1, Rate, false);
            clip.SetData(samples, 0);
            return clip;
        }
    }
}
