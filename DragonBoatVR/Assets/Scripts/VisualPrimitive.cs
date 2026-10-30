using UnityEngine;

namespace DragonBoatVR
{
    /// <summary>Built-in mesh primitives for display, with no participation in physics.</summary>
    internal static class VisualPrimitive
    {
        public static GameObject Create(PrimitiveType type)
        {
            var visual = GameObject.CreatePrimitive(type);
            var collider = visual.GetComponent<Collider>();
            if (collider != null)
            {
                // Destroy is deferred until after Update. Disable immediately:
                // a FixedUpdate/PhysX step can run before that destruction.
                collider.enabled = false;
                if (Application.isPlaying) Object.Destroy(collider);
                else Object.DestroyImmediate(collider);
            }
            return visual;
        }
    }
}
