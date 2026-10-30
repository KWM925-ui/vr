using UnityEngine;

namespace DragonBoatVR
{
    /// <summary>
    /// 用 -paddle-probe 显式启用的桨位诊断:每 0.5s 记录桨在船体局部坐标里的位置,
    /// 以 [PADDLEPROBE] 前缀写 Debug.Log。run_runtime_check 的 runtime.log 可 grep。
    /// 若局部坐标随时间漂移 → 驱动逻辑问题;若局部坐标稳定但画面异常 → 网格/朝向问题。
    /// </summary>
    public class PaddleAttachmentProbe : MonoBehaviour
    {
        public Transform boat;
        float _timer;

        void Awake()
        {
            // Debug.Log writes synchronously on the physics thread. Routine
            // experience/acceptance already records poses with the bounded
            // background writer; the old console probe is an explicit diagnostic.
            enabled = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-paddle-probe") >= 0;
        }

        void FixedUpdate()
        {
            if (boat == null) return;
            _timer += Time.fixedDeltaTime;
            if (_timer < 0.5f) return;
            _timer = 0f;
            Vector3 lp = boat.InverseTransformPoint(transform.position);
            Debug.Log($"[PADDLEPROBE] t={Time.time:F1} local=({lp.x:F3},{lp.y:F3},{lp.z:F3}) " +
                      $"world=({transform.position.x:F2},{transform.position.y:F2},{transform.position.z:F2})");
        }
    }
}
