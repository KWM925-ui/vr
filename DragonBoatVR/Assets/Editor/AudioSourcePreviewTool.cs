using System;
using System.IO;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;

namespace DragonBoatVR.EditorTools
{
    /// <summary>Decode a local licensed recording with Unity's existing decoder
    /// for offline waveform inspection. Does not change scene audio or import assets.</summary>
    public static class AudioSourcePreviewTool
    {
        public static void Run()
        {
            string source = Argument("-audioSourcePath"), output = Argument("-audioOutputPath");
            using (var request = UnityWebRequestMultimedia.GetAudioClip(new Uri(Path.GetFullPath(source)).AbsoluteUri, AudioType.MPEG))
            {
                var handler = (DownloadHandlerAudioClip)request.downloadHandler;
                handler.compressed = false; handler.streamAudio = false;
                var operation = request.SendWebRequest();
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (!operation.isDone && DateTime.UtcNow < deadline) Thread.Sleep(10);
                if (!operation.isDone || request.result != UnityWebRequest.Result.Success)
                    throw new IOException("Local audio decoding failed: " + request.error);
                var clip = DownloadHandlerAudioClip.GetContent(request);
                if (clip == null) throw new IOException("Decoded audio clip missing.");
                try
                {
                    var samples = new float[clip.samples * clip.channels];
                    if (!clip.GetData(samples, 0)) throw new IOException("Decoded PCM is unavailable.");
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
                    int bytes = checked(samples.Length * 2);
                    using (var writer = new BinaryWriter(File.Create(output)))
                    {
                        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + bytes);
                        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
                        writer.Write((short)1); writer.Write((short)clip.channels); writer.Write(clip.frequency);
                        writer.Write(clip.frequency * clip.channels * 2); writer.Write((short)(clip.channels * 2)); writer.Write((short)16);
                        writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(bytes);
                        foreach (float sample in samples) writer.Write((short)Mathf.RoundToInt(Mathf.Clamp(sample, -1f, 1f) * 32767f));
                    }
                    Debug.Log($"[AUDIO_PREVIEW] samples={clip.samples} channels={clip.channels} frequency={clip.frequency} output={output}");
                    Debug.Log("[AUDIO_PREVIEW] RESULT=PASS decoded only; no subjective listening acceptance");
                }
                finally { UnityEngine.Object.DestroyImmediate(clip); }
            }
        }

        static string Argument(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, name);
            if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("Missing " + name);
            return args[index + 1];
        }
    }
}
