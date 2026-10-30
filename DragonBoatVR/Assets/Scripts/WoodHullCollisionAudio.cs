using UnityEngine;

namespace DragonBoatVR
{
    public sealed class WoodHullCollisionAudio : MonoBehaviour
    {
        [System.NonSerialized] public RiverAudioController owner;
        void OnCollisionEnter(Collision collision)
        {
            if (owner == null || collision.contactCount == 0) return;
            var contact = collision.GetContact(0);
            float speed = Mathf.Abs(Vector3.Dot(collision.relativeVelocity, contact.normal));
            owner.EmitWoodImpact(contact.point, speed, owner.MotionSourceTime);
        }
    }
    internal static class WoodenContactGeometry
    {
        internal static bool SegmentTouchesBlade(Vector3 a, Vector3 b, Vector3 centre, Quaternion rotation,
            Vector3 halfSize, float railRadius, out Vector3 bladePoint, out Vector3 railPoint)
        {
            Quaternion inverse = Quaternion.Inverse(rotation);
            Vector3 start = inverse * (a - centre), end = inverse * (b - centre), delta = end - start;
            Vector3 extent = halfSize + Vector3.one * railRadius;
            float enter = 0f, exit = 1f;
            for (int axis = 0; axis < 3; axis++)
            {
                if (Mathf.Abs(delta[axis]) < 1e-8f)
                {
                    if (Mathf.Abs(start[axis]) > extent[axis]) { bladePoint = railPoint = Vector3.zero; return false; }
                }
                else
                {
                    float first = (-extent[axis] - start[axis]) / delta[axis];
                    float last = (extent[axis] - start[axis]) / delta[axis];
                    enter = Mathf.Max(enter, Mathf.Min(first, last)); exit = Mathf.Min(exit, Mathf.Max(first, last));
                    if (enter > exit) { bladePoint = railPoint = Vector3.zero; return false; }
                }
            }
            Vector3 local = start + delta * ((enter + exit) * 0.5f);
            railPoint = centre + rotation * local;
            bladePoint = centre + rotation * new Vector3(Mathf.Clamp(local.x, -halfSize.x, halfSize.x),
                Mathf.Clamp(local.y, -halfSize.y, halfSize.y), Mathf.Clamp(local.z, -halfSize.z, halfSize.z));
            return true;
        }

        // Closest points on finite segments, including parallel and degenerate cases.
        internal static float SegmentDistance(Vector3 p0, Vector3 p1, Vector3 q0, Vector3 q1,
            out Vector3 onP, out Vector3 onQ)
        {
            Vector3 d1 = p1 - p0, d2 = q1 - q0, r = p0 - q0;
            float a = d1.sqrMagnitude, e = d2.sqrMagnitude, f = Vector3.Dot(d2, r), s, t;
            if (a <= 1e-10f && e <= 1e-10f) { onP = p0; onQ = q0; return Vector3.Distance(onP, onQ); }
            if (a <= 1e-10f) { s = 0f; t = Mathf.Clamp01(f / e); }
            else
            {
                float c = Vector3.Dot(d1, r);
                if (e <= 1e-10f) { t = 0f; s = Mathf.Clamp01(-c / a); }
                else
                {
                    float b = Vector3.Dot(d1, d2), denominator = a * e - b * b;
                    s = denominator > 1e-10f ? Mathf.Clamp01((b * f - c * e) / denominator) : 0f;
                    t = (b * s + f) / e;
                    if (t < 0f) { t = 0f; s = Mathf.Clamp01(-c / a); }
                    else if (t > 1f) { t = 1f; s = Mathf.Clamp01((b - c) / a); }
                }
            }
            onP = p0 + d1 * s; onQ = q0 + d2 * t;
            return Vector3.Distance(onP, onQ);
        }
    }
}
