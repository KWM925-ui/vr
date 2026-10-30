using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace DragonBoatVR
{
    /// <summary>Opt-in diagnostic: actual end-of-frame screen images, never an
    /// animation reconstructed from desired poses. Ordinary experience has no capture.</summary>
    public sealed class RuntimeMotionCapture : MonoBehaviour
    {
        [Serializable] public sealed class Frame
        {
            public int index, unityFrame, width, height;
            public string file, sha256, stage;
            public double wallTime, captureSubmittedWallTime, readbackCompletedWallTime, captureCompletedWallTime, dspTime, simulationTime;
            public uint feedbackSequence;
            public float phase;
            public bool inputValid;
            public Vector3 bladeLocal, upperGripLocal, boatPosition, boatVelocity, boatAngularVelocity;
            public Quaternion paddleRotationLocal, boatRotation;
        }
        [Serializable] public sealed class LightingObservation
        {
            public string skybox, paddleShader;
            public int ambientMode;
            public float ambientIntensity;
            public bool updateRequested, asynchronousReadbackSupported;
            public Vector3[] worldDirections;
            public Color[] ambientRadiance, paddleRadiance;
        }
        [Serializable] public sealed class CaptureReport
        {
            public int schemaVersion = 2, requestedFps = 20, writtenFrames;
            public string captureMode = "async_gpu_readback";
            public string graphicsDeviceType;
            public bool readbackRowsFlippedForEncoding;
            public string scope = "Actual end-of-frame ScreenCapture, asynchronous GPU readback and background JPEG encoding. Image clocks and poses belong to submission, not completion. No manual camera/physics rendering.";
            public double firstAudioBufferDspTime, firstAudioBufferWallTime;
            public int audioSampleRate, audioSampleFrames;
            public long maximumQueuedBytes;
            public LightingObservation lightingAtFirstFrame, lightingAtLastFrame;
            public float boatMass, gripToBladeLength;
            public Vector3 boatCentreOfMass, boatInertiaTensor, seatLocalEye, paddleStationLocal, bladeHalfSize;
            public List<Frame> frames = new List<Frame>();
        }
        sealed class Pending { public Frame frame; public byte[] rgba; }
        sealed class Readback
        {
            public Frame frame;
            public RenderTexture texture;
            public AsyncGPUReadbackRequest request;
            public int byteCount;
        }
        const long MaximumQueuedBytes = 8 * 1024 * 1024;
        const int MaximumFrames = 1800;
        readonly object _gate = new object();
        readonly Queue<Pending> _queue = new Queue<Pending>();
        readonly Queue<Readback> _readbacks = new Queue<Readback>();
        readonly AutoResetEvent _wake = new AutoResetEvent(false);
        readonly CaptureReport _report = new CaptureReport();
        SimulationSession _session;
        RuntimeAudioCapture _audio;
        Func<string> _stage;
        string _directory, _error;
        Thread _thread;
        Coroutine _routine;
        long _queuedBytes;
        bool _capturing, _closing, _completed;
        static double WallTime => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        public static bool Requested => Array.IndexOf(Environment.GetCommandLineArgs(), "-captureMotion") >= 0;

        public void Begin(string directory, SimulationSession session, RuntimeAudioCapture audio, Func<string> stage)
        {
            if (Application.isEditor) throw new InvalidOperationException("Continuous screen acceptance requires a real Player window.");
            if (_thread != null) throw new InvalidOperationException("Motion capture already started.");
            if (!SystemInfo.supportsAsyncGPUReadback)
                throw new NotSupportedException("Motion evidence requires asynchronous GPU readback support.");
            _directory = Path.GetFullPath(directory); _session = session; _audio = audio; _stage = stage;
            _report.graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString();
            _report.readbackRowsFlippedForEncoding = SystemInfo.graphicsUVStartsAtTop;
            var driver = session.drivers[0]; var body = session.boat.BoatRb;
            var seat = FindObjectOfType<BoatSeatCamera>(); var hydro = driver.GetComponent<PaddleHydrodynamics>();
            _report.boatMass = body.mass; _report.boatCentreOfMass = body.centerOfMass; _report.boatInertiaTensor = body.inertiaTensor;
            _report.gripToBladeLength = driver.shaftToBlade; _report.paddleStationLocal = driver.pivotLocalPos;
            if (seat != null) _report.seatLocalEye = seat.LocalEyePosition;
            if (hydro != null) _report.bladeHalfSize = new Vector3(hydro.BladeWidth, hydro.bladeLength, hydro.bladeThickness) * 0.5f;
            _thread = new Thread(WriteImages) { IsBackground = true, Name = "DragonBoat motion evidence" };
            _thread.Start(); _capturing = true; _routine = StartCoroutine(CaptureFrames());
        }

        IEnumerator CaptureFrames()
        {
            var endOfFrame = new WaitForEndOfFrame();
            double next = WallTime;
            while (_capturing)
            {
                yield return endOfFrame;
                try
                {
                    CollectReadbacks(false);
                    if (!_capturing || WallTime < next) continue;
                    next = WallTime + 1.0 / _report.requestedFps;
                    CaptureFrame();
                }
                catch (Exception error) { lock (_gate) _error = error.ToString(); _capturing = false; }
            }
        }

        void CaptureFrame()
        {
            if (_report.frames.Count >= MaximumFrames) throw new IOException("Motion evidence frame bound reached.");
            var driver = _session.drivers[0]; var body = _session.boat.BoatRb; var boat = _session.boat.transform;
            if (_report.frames.Count == 0) _report.lightingAtFirstFrame = ObserveLighting(driver);
            var frame = new Frame
            {
                index = _report.frames.Count, unityFrame = Time.frameCount, stage = _stage(),
                wallTime = WallTime, dspTime = AudioSettings.dspTime, simulationTime = _session.SimulationTime,
                feedbackSequence = _session.closedLoop.ObservationSequence,
                phase = driver.Phase01, inputValid = driver.Sample.valid,
                bladeLocal = boat.InverseTransformPoint(driver.transform.position),
                paddleRotationLocal = Quaternion.Inverse(boat.rotation) * driver.transform.rotation,
                boatPosition = boat.position, boatRotation = boat.rotation,
                boatVelocity = body.velocity, boatAngularVelocity = body.angularVelocity
            };
            frame.upperGripLocal = frame.bladeLocal + frame.paddleRotationLocal * Vector3.up * driver.shaftToBlade;
            frame.file = "motion_" + frame.index.ToString("D5") + ".jpg";
            frame.width = Screen.width; frame.height = Screen.height;
            int byteCount = checked(frame.width * frame.height * 4);
            lock (_gate)
            {
                if (_error != null) throw new IOException(_error);
                // Reserve raw bytes before submitting a readback. GPU requests and
                // the background encoder share the original bounded queue budget.
                if (_queuedBytes + byteCount > MaximumQueuedBytes) throw new IOException("Motion evidence queue overflow.");
                _queuedBytes += byteCount;
                _report.maximumQueuedBytes = Math.Max(_report.maximumQueuedBytes, _queuedBytes);
            }
            var texture = RenderTexture.GetTemporary(frame.width, frame.height, 0, RenderTextureFormat.ARGB32);
            try
            {
                // Unity 2022.3 ScreenCapture.CaptureScreenshotIntoRenderTexture
                // explicitly supports AsyncGPUReadback to avoid main-thread stalls.
                ScreenCapture.CaptureScreenshotIntoRenderTexture(texture);
                var request = AsyncGPUReadback.Request(texture, 0, TextureFormat.RGBA32);
                frame.captureSubmittedWallTime = WallTime;
                _readbacks.Enqueue(new Readback { frame = frame, texture = texture, request = request, byteCount = byteCount });
                _report.frames.Add(frame);
            }
            catch
            {
                RenderTexture.ReleaseTemporary(texture);
                lock (_gate) _queuedBytes -= byteCount;
                throw;
            }
        }

        void CollectReadbacks(bool wait)
        {
            while (_readbacks.Count > 0)
            {
                var item = _readbacks.Peek();
                if (!wait && !item.request.done) break;
                _readbacks.Dequeue();
                try
                {
                    if (wait) item.request.WaitForCompletion(); // Only during final drain, after capture stops.
                    if (item.request.hasError) throw new IOException("Motion evidence GPU readback failed.");
                    byte[] rgba = item.request.GetData<byte>().ToArray();
                    if (rgba.Length != item.byteCount) throw new IOException("Motion evidence pixel count differs.");
                    item.frame.readbackCompletedWallTime = WallTime;
                    lock (_gate)
                    {
                        if (_error != null) throw new IOException(_error);
                        _queue.Enqueue(new Pending { frame = item.frame, rgba = rgba });
                    }
                    _wake.Set();
                }
                catch
                {
                    lock (_gate) _queuedBytes -= item.byteCount;
                    throw;
                }
                finally { RenderTexture.ReleaseTemporary(item.texture); }
            }
        }

        static LightingObservation ObserveLighting(PaddleDriver driver)
        {
            var shaft = driver.transform.Find("Shaft");
            var renderer = shaft != null ? shaft.GetComponent<Renderer>() : driver.GetComponentInChildren<Renderer>();
            var sample = new LightingObservation
            {
                skybox = RenderSettings.skybox != null ? RenderSettings.skybox.name : null,
                paddleShader = renderer != null && renderer.sharedMaterial != null ? renderer.sharedMaterial.shader.name : null,
                ambientMode = (int)RenderSettings.ambientMode, ambientIntensity = RenderSettings.ambientIntensity,
                updateRequested = EnvironmentProbeBootstrap.UpdateRequested,
                asynchronousReadbackSupported = SystemInfo.supportsAsyncGPUReadback,
                worldDirections = new[] { Vector3.up, Vector3.down, Vector3.right, Vector3.left, Vector3.forward, Vector3.back },
                ambientRadiance = new Color[6], paddleRadiance = new Color[6]
            };
            RenderSettings.ambientProbe.Evaluate(sample.worldDirections, sample.ambientRadiance);
            LightProbes.GetInterpolatedProbe(driver.transform.position, renderer, out var localProbe);
            localProbe.Evaluate(sample.worldDirections, sample.paddleRadiance);
            return sample;
        }

        void WriteImages()
        {
            try
            {
                byte[] row = Array.Empty<byte>();
                using (var hash = SHA256.Create())
                while (true)
                {
                    Pending item;
                    lock (_gate)
                    {
                        item = _queue.Count > 0 ? _queue.Dequeue() : null;
                        if (item == null && _closing) return;
                    }
                    if (item == null) { _wake.WaitOne(); continue; }
                    // Texture encoders use Unity's bottom-left pixel order. D3D
                    // render-target readback starts at the top, unlike ReadPixels.
                    // Normalize rows here so the PlayerLoop never copies an image.
                    if (_report.readbackRowsFlippedForEncoding)
                    {
                        int stride = checked(item.frame.width * 4);
                        if (row.Length != stride) row = new byte[stride];
                        for (int y = 0; y < item.frame.height / 2; y++)
                        {
                            int a = y * stride, b = (item.frame.height - 1 - y) * stride;
                            Buffer.BlockCopy(item.rgba, a, row, 0, stride);
                            Buffer.BlockCopy(item.rgba, b, item.rgba, a, stride);
                            Buffer.BlockCopy(row, 0, item.rgba, b, stride);
                        }
                    }
                    // EncodeArrayToJPG is explicitly thread-safe in Unity 2022.3;
                    // Texture2D.EncodeToJPG is no longer called in the PlayerLoop.
                    byte[] jpeg = ImageConversion.EncodeArrayToJPG(item.rgba, GraphicsFormat.R8G8B8A8_UNorm,
                        (uint)item.frame.width, (uint)item.frame.height, 0, 78);
                    item.frame.captureCompletedWallTime = WallTime;
                    using (var output = new FileStream(Path.Combine(_directory, item.frame.file), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                        output.Write(jpeg, 0, jpeg.Length);
                    item.frame.sha256 = BitConverter.ToString(hash.ComputeHash(jpeg)).Replace("-", "").ToLowerInvariant();
                    lock (_gate) { _queuedBytes -= item.rgba.Length; _report.writtenFrames++; }
                }
            }
            catch (Exception error) { lock (_gate) _error = error.ToString(); }
        }

        public void StopCapture()
        {
            _capturing = false;
            if (_routine != null) { StopCoroutine(_routine); _routine = null; }
            // Process only this recorder's requests, in submitted frame order.
            // Keep each temporary texture alive until its readback is finished.
            while (_readbacks.Count > 0)
            {
                try { CollectReadbacks(true); }
                catch (Exception error) { lock (_gate) _error = error.ToString(); }
            }
            lock (_gate) _closing = true;
            _wake.Set();
        }

        public void Complete()
        {
            if (_completed) return;
            StopCapture();
            if (_thread == null || !_thread.Join(3000)) throw new IOException("Motion image writer did not finish.");
            if (_error != null) throw new IOException("Motion evidence failed: " + _error);
            if (_report.frames.Count < 100 || _report.writtenFrames != _report.frames.Count)
                throw new IOException("Continuous motion evidence is absent, short or incomplete.");
            _report.firstAudioBufferDspTime = _audio.FirstBufferDspTime;
            _report.firstAudioBufferWallTime = _audio.FirstBufferWallTime;
            _report.audioSampleRate = _audio.SampleRate; _report.audioSampleFrames = _audio.SampleFrames;
            _report.lightingAtLastFrame = ObserveLighting(_session.drivers[0]);
            if (double.IsNaN(_report.firstAudioBufferDspTime)) throw new IOException("Motion/audio timeline has no actual audio buffer.");
            File.WriteAllText(Path.Combine(_directory, "motion_capture.json"), JsonUtility.ToJson(_report, true) + "\n", new UTF8Encoding(false));
            _completed = true;
        }

        void OnDestroy()
        {
            StopCapture();
            if (_thread == null || _thread.Join(3000)) _wake.Dispose();
        }
    }
}
