using System;
using UnityEngine;
using UnityEditor.SceneManagement;

namespace DragonBoatVR.EditorTools
{
    public static class WaveHeightSamplerSelfTest
    {
        public static void Run()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var wave = new GameObject("Height equivalence fixture").AddComponent<WaveField>();
            var sampler = new WaveHeightSampler();
            int count = 0; float maximum = 0f;
            for (int spectrum = 0; spectrum < 2; spectrum++)
            {
                if (spectrum == 1)
                {
                    EditorSceneManager.OpenScene("Assets/Scenes/Demo.unity", OpenSceneMode.Single);
                    wave = UnityEngine.Object.FindObjectOfType<WaveField>(true);
                    if (wave == null || wave.waves.Length != 6) throw new InvalidOperationException("Locked six-wave scene missing");
                }
            foreach (bool horizontal in new[] { false, true })
                foreach (int iterations in new[] { 1, 3, 6 })
                    foreach (float intensity in new[] { 0f, 0.4f, 1f, 1.7f })
                        foreach (float time in new[] { 0f, 0.37f, 17f })
                        {
                            wave.horizontalDisplacement = horizontal; wave.inverseIterations = iterations;
                            wave.intensity = intensity; wave.Step(time + .01f);
                            wave.transform.position = Vector3.up * (intensity - .5f);
                            sampler.Capture(wave);
                            for (int x = -12; x <= 12; x++)
                                for (int z = -12; z <= 12; z++)
                                {
                                    float px = x * .371f, pz = z * .529f;
                                    float error = Mathf.Abs(sampler.GetHeight(px, pz) - wave.Sample(px, pz).height);
                                    if (!SimulationValue.Finite(error) || error > 1e-6f)
                                        throw new InvalidOperationException("Height differs from full sample: " + error);
                                    maximum = Mathf.Max(maximum, error); count++;
                                }
                        }
            }
            float old = sampler.GetHeight(.1f, .2f);
            wave.intensity = 0f;
            if (sampler.GetHeight(.1f, .2f) != old) throw new InvalidOperationException("Captured coefficients were mutated");
            sampler.Capture(wave);
            if (sampler.GetHeight(.1f, .2f) != wave.BaseHeight) throw new InvalidOperationException("Recapture failed");
            Debug.Log($"[WAVEHEIGHT] samples={count} maxErrorM={maximum:R}");
            Debug.Log("[WAVEHEIGHT] RESULT=PASS exact height sampling and snapshot refresh");
        }
    }
}
