using System.Collections.Generic;
using UnityEngine;

namespace DragonBoatVR
{
    /// <summary>Interaction envelope sampled once from the displayed wooden hull.
    /// Gunwale strips approximate the mesh edge; they are not measured hardware geometry.</summary>
    public sealed class BoatHullGeometry : MonoBehaviour
    {
        public const float RailRadius = 0.02f;
        public Vector3[] Starboard { get; private set; }
        public Vector3[] Port { get; private set; }
        public float HalfBeam { get; private set; }
        public bool Ready => Starboard != null;

        public static BoatHullGeometry FindOrCreate(BoatBuoyancy boat)
        {
            if (boat == null) return null;
            var model = boat.transform.Find("RealDragonBoat");
            if (model == null || !model.gameObject.activeInHierarchy) return null;
            var geometry = boat.GetComponent<BoatHullGeometry>();
            if (geometry == null) geometry = boat.gameObject.AddComponent<BoatHullGeometry>();
            if (!geometry.Ready) geometry.Build(model);
            return geometry.Ready ? geometry : null;
        }

        void Build(Transform model)
        {
            var vertices = new List<Vector3>();
            foreach (var filter in model.GetComponentsInChildren<MeshFilter>())
            {
                var renderer = filter.GetComponent<Renderer>();
                if (renderer == null || !renderer.enabled || filter.sharedMesh == null) continue;
                foreach (var vertex in filter.sharedMesh.vertices)
                    vertices.Add(transform.InverseTransformPoint(filter.transform.TransformPoint(vertex)));
            }
            if (vertices.Count == 0) return;
            var bounds = new Bounds(vertices[0], Vector3.zero);
            foreach (var vertex in vertices) bounds.Encapsulate(vertex);
            HalfBeam = Mathf.Max(Mathf.Abs(bounds.min.x), Mathf.Abs(bounds.max.x));
            Starboard = SampleRail(vertices, bounds, 1f);
            Port = SampleRail(vertices, bounds, -1f);

        }

        static Vector3[] SampleRail(List<Vector3> vertices, Bounds bounds, float side)
        {
            const int count = 65;
            var outside = new float[count]; var top = new float[count];
            for (int i = 0; i < count; i++) { outside[i] = float.NegativeInfinity; top[i] = float.NegativeInfinity; }
            float spacing = bounds.size.z / (count - 1);
            foreach (var vertex in vertices)
            {
                int i = Mathf.Clamp(Mathf.RoundToInt((vertex.z - bounds.min.z) / spacing), 0, count - 1);
                outside[i] = Mathf.Max(outside[i], side * vertex.x);
            }
            foreach (var vertex in vertices)
            {
                int i = Mathf.Clamp(Mathf.RoundToInt((vertex.z - bounds.min.z) / spacing), 0, count - 1);
                if (side * vertex.x >= outside[i] * 0.78f) top[i] = Mathf.Max(top[i], vertex.y);
            }
            var points = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                int nearest = i;
                if (float.IsNegativeInfinity(top[i]))
                    for (int distance = 1; distance < count; distance++)
                    {
                        if (i - distance >= 0 && !float.IsNegativeInfinity(top[i - distance])) { nearest = i - distance; break; }
                        if (i + distance < count && !float.IsNegativeInfinity(top[i + distance])) { nearest = i + distance; break; }
                    }
                points[i] = new Vector3(side * (outside[nearest] - RailRadius), top[nearest] - RailRadius,
                    bounds.min.z + spacing * i);
            }
            return points;
        }

        public float RailTop(float fromZ, float toZ)
        {
            float top = float.NegativeInfinity;
            for (int i = 0; i < Starboard.Length; i++)
                if (Starboard[i].z >= fromZ - 0.13f && Starboard[i].z <= toZ + 0.13f)
                    top = Mathf.Max(top, Mathf.Max(Starboard[i].y, Port[i].y) + RailRadius);
            return top;
        }
    }
}
