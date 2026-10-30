using System;
using System.Collections.Generic;
using UnityEngine;

namespace DragonBoatVR
{
    /// <summary>Camera containment in the displayed hull's sampled gunwale outline.
    /// Clearance is a desktop viewing allowance, not a measured human body model.</summary>
    public sealed class BoatCabinBoundary
    {
        readonly Vector2[] _outline;
        readonly float _clearance;
        public bool Ready => _outline != null && _outline.Length >= 4;
        public float MinimumZ { get; }
        public float MaximumZ { get; }
        public IReadOnlyList<Vector2> Outline => _outline;

        public BoatCabinBoundary(BoatHullGeometry geometry, Vector3 home, float clearance)
        {
            _clearance = Mathf.Max(0.01f, clearance);
            if (geometry == null || !geometry.Ready || geometry.Port.Length != geometry.Starboard.Length) return;
            var port = geometry.Port; var starboard = geometry.Starboard;
            int nearest = 0;
            for (int i = 1; i < port.Length; i++)
                if (Mathf.Abs(port[i].z - home.z) < Mathf.Abs(port[nearest].z - home.z)) nearest = i;
            bool Open(int i) => starboard[i].x - port[i].x > 2f * _clearance
                && Mathf.Max(port[i].y, starboard[i].y) + _clearance < home.y;
            if (!Open(nearest)) return;
            int first = nearest, last = nearest;
            while (first > 0 && Open(first - 1)) first--;
            while (last + 1 < port.Length && Open(last + 1)) last++;
            if (last <= first) return;
            MinimumZ = port[first].z; MaximumZ = port[last].z;
            _outline = new Vector2[2 * (last - first + 1)];
            int index = 0;
            for (int i = first; i <= last; i++) _outline[index++] = new Vector2(port[i].x, port[i].z);
            for (int i = last; i >= first; i--) _outline[index++] = new Vector2(starboard[i].x, starboard[i].z);
        }

        public bool Contains(Vector2 point)
        {
            if (!Ready || !SimulationValue.Finite(point.x) || !SimulationValue.Finite(point.y)) return false;
            bool inside = false;
            for (int i = 0, previous = _outline.Length - 1; i < _outline.Length; previous = i++)
            {
                Vector2 a = _outline[previous], b = _outline[i], edge = b - a;
                if ((a.y > point.y) != (b.y > point.y)
                    && point.x < a.x + (point.y - a.y) * edge.x / edge.y) inside = !inside;
                float along = edge.sqrMagnitude > 0f ? Mathf.Clamp01(Vector2.Dot(point - a, edge) / edge.sqrMagnitude) : 0f;
                if ((point - (a + edge * along)).sqrMagnitude < _clearance * _clearance) return false;
            }
            return inside;
        }

        Vector2 SlideAlongOutline(Vector2 position, Vector2 displacement)
        {
            float nearestDistance = float.PositiveInfinity;
            Vector2 inwardNormal = Vector2.zero;
            for (int i = 0, previous = _outline.Length - 1; i < _outline.Length; previous = i++)
            {
                Vector2 a = _outline[previous], edge = _outline[i] - a;
                float along = edge.sqrMagnitude > 0f
                    ? Mathf.Clamp01(Vector2.Dot(position - a, edge) / edge.sqrMagnitude) : 0f;
                Vector2 offset = position - (a + edge * along);
                if (offset.sqrMagnitude >= nearestDistance) continue;
                nearestDistance = offset.sqrMagnitude;
                inwardNormal = offset.normalized;
            }
            // Remove only the motion into the nearest rail. Its actual tangent
            // can point slightly inward as the hull narrows toward either end.
            return displacement - inwardNormal * Mathf.Min(0f, Vector2.Dot(displacement, inwardNormal));
        }

        /// <summary>Small steps prevent crossing a narrow end or obstruction;
        /// a blocked move can slide along the actual gunwale tangent.</summary>
        public Vector2 Move(Vector2 start, Vector2 displacement, Func<Vector2, bool> clearHeadroom)
        {
            if (!Contains(start) || !SimulationValue.Finite(displacement.x) || !SimulationValue.Finite(displacement.y)) return start;
            int steps = Mathf.Clamp(Mathf.CeilToInt(displacement.magnitude / 0.04f), 1, 128);
            // Limit any one call to a bounded traversed distance. Ordinary input
            // is at most 0.1 s; this also rejects a large frame-time teleport.
            displacement = Vector2.ClampMagnitude(displacement, 128f * 0.04f);
            Vector2 step = displacement / steps, position = start;
            bool Allowed(Vector2 candidate) => Contains(candidate) && (clearHeadroom == null || clearHeadroom(candidate));
            for (int i = 0; i < steps; i++)
            {
                Vector2 candidate = position + step;
                if (Allowed(candidate)) { position = candidate; continue; }
                if (!Contains(candidate))
                {
                    // At a bend, the next step may contact the neighbouring
                    // edge before it is the closest edge to the current point.
                    bool slid = false;
                    for (int contact = 0; contact < 2; contact++)
                    {
                        Vector2 slide = SlideAlongOutline(contact == 0 ? candidate : position, step);
                        if (slide.sqrMagnitude <= 1e-10f || !Allowed(position + slide)) continue;
                        position += slide;
                        slid = true;
                        break;
                    }
                    if (slid) continue;
                }
                candidate = position + new Vector2(step.x, 0f);
                if (Allowed(candidate)) position = candidate;
                candidate = position + new Vector2(0f, step.y);
                if (Allowed(candidate)) position = candidate;
            }
            return position;
        }
    }
}
