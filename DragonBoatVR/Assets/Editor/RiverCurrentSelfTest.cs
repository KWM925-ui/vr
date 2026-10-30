using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DragonBoatVR.EditorTools
{
    /// <summary>Checks environment controls and verifies that the hull follows
    /// uniform river flow in the same relative-water frame as the paddle.</summary>
    public static class RiverCurrentSelfTest
    {
        const float Dt = 0.01f;

        [MenuItem("DragonBoatVR/⑤ 河流环境与船体水流自检", priority = 4)]
        public static void Run()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var wave = new GameObject("WaveField_CurrentTest").AddComponent<WaveField>();
            Call(wave, "Awake");
            wave.intensity = 0f;
            var session = new GameObject("Session_CurrentTest").AddComponent<SimulationSession>();
            session.wave = wave;
            var environment = session.gameObject.AddComponent<RiverEnvironmentController>();
            environment.Bind(session);
            environment.SetCondition(RiverEnvironmentController.WaterCondition.Windy);
            environment.SetCurrent(0.30f, 0f);
            for (int i = 0; i < 200; i++) environment.Step(Dt);

            bool conditionApplied = Mathf.Abs(wave.intensity - 0.65f) < 1e-4f;
            bool forwardCurrent = Vector2.Distance(wave.backgroundCurrentMps, new Vector2(0f, 0.30f)) < 1e-4f;
            wave.intensity = 0f;
            wave.intensityRatePerSecond = 0f;

            var boatObject = new GameObject("Boat_CurrentTest");
            boatObject.transform.position = new Vector3(0f, -0.25f, 0f);
            var body = boatObject.AddComponent<Rigidbody>();
            body.mass = 150f;
            body.drag = 0f;
            body.angularDrag = 0f;
            body.useGravity = false;
            var boat = boatObject.AddComponent<BoatBuoyancy>();
            boat.points = new[] { new BoatBuoyancy.BuoyancyPoint { localOffset = Vector3.zero } };
            boat.centerOfMassOffset = Vector3.zero;
            boat.totalBuoyancyMultiplier = 0f;
            boat.applyWaveDrift = false;
            Call(boat, "Awake");
            Call(boat, "Start");

            bool previousAutoSimulation = Physics.autoSimulation;
            Physics.autoSimulation = false;
            try
            {
                wave.backgroundCurrentMps = Vector2.zero;
                Simulate(boat, body, 60);
                float stillWaterSpeed = body.velocity.magnitude;
                bool stillWaterStable = stillWaterSpeed < 1e-5f;

                ResetBody(body);
                wave.backgroundCurrentMps = new Vector2(0f, 0.30f);
                Simulate(boat, body, 250);
                float speedWithForwardFlow = body.velocity.z;

                ResetBody(body);
                wave.backgroundCurrentMps = new Vector2(0f, -0.30f);
                Simulate(boat, body, 250);
                float speedAgainstForwardFlow = body.velocity.z;

                ResetBody(body);
                environment.SetCurrent(0.30f, 90f);
                for (int i = 0; i < 200; i++) environment.Step(Dt);
                bool rightCurrent = Vector2.Distance(wave.backgroundCurrentMps, new Vector2(0.30f, 0f)) < 1e-4f;
                Simulate(boat, body, 150);
                bool lateralFlowMovesHull = body.velocity.x > 0.03f;

                bool pass = conditionApplied && forwardCurrent && stillWaterStable
                    && speedWithForwardFlow > 0.003f && speedAgainstForwardFlow < -0.003f
                    && rightCurrent && lateralFlowMovesHull;
                Debug.Log($"[CURRENTTEST] calmToWindy={conditionApplied} heading0IsPlusZ={forwardCurrent} heading90IsPlusX={rightCurrent}");
                Debug.Log($"[CURRENTTEST] stillWaterSpeed={stillWaterSpeed:F3}m/s currentPlusZ={speedWithForwardFlow:F4}m/s currentMinusZ={speedAgainstForwardFlow:F4}m/s hullRightCurrent={body.velocity.x:F3}m/s");
                Debug.Log(pass ? "[CURRENTTEST] RESULT=PASS 环境水流与船体相对水速一致"
                    : "[CURRENTTEST] RESULT=FAIL 水流方向或船体响应不符合预期");
            }
            finally
            {
                Physics.autoSimulation = previousAutoSimulation;
            }
        }

        static void Simulate(BoatBuoyancy boat, Rigidbody body, int steps)
        {
            for (int i = 0; i < steps; i++)
            {
                Call(boat, "FixedUpdate");
                Physics.Simulate(Dt);
            }
        }

        static void ResetBody(Rigidbody body)
        {
            body.position = new Vector3(0f, -0.25f, 0f);
            body.rotation = Quaternion.identity;
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }

        static void Call(object target, string method)
        {
            var member = target.GetType().GetMethod(method,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (member == null) throw new System.MissingMethodException(target.GetType().Name, method);
            member.Invoke(target, null);
        }
    }
}
