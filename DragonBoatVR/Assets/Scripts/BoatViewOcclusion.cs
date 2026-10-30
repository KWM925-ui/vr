using System.Collections.Generic;
using UnityEngine;

namespace DragonBoatVR
{
    /// <summary>Read-only camera queries against the active river's existing
    /// static meshes. This never moves the boat or changes a collider/layer.</summary>
    public sealed class BoatViewOcclusion
    {
        public struct Pose
        {
            public float yaw, pitch, distance;
            public Quaternion Rotation(float heading) => Quaternion.Euler(pitch, heading + yaw, 0f);
            public Vector3 Eye(Vector3 focus, float heading) => focus - Rotation(heading) * Vector3.forward * distance;
        }

        const float Clearance = 0.18f;
        // A mathematically tangent ray is not a stable view: equivalent mesh
        // transforms differ by a few micrometres in world-space float math.
        // Keep a small visual reserve without changing any collider or test.
        const float VisibilityReserve = 0.02f;
        readonly Transform _target;
        readonly Camera _camera;
        readonly Vector3[] _localCorners = new Vector3[8], _corners = new Vector3[8];
        readonly Vector3[] _localWitnesses, _witnesses;
        readonly int[] _activeWitnesses;
        readonly bool[] _witnessActive;
        readonly Vector3[] _localSurface;
        readonly List<SurfaceGroup> _surfaceGroups = new List<SurfaceGroup>();
        readonly List<int> _surfaceConstraints = new List<int>();
        readonly bool[] _surfaceActive;
        readonly int[] _surfaceBlockers = new int[26];
        readonly Vector3[] _surfaceDirections = new Vector3[26];
        readonly List<Bounds> _surfaceRegions = new List<Bounds>();
        readonly List<int> _surfaceRegionSolids = new List<int>();
        readonly List<Bounds> _localSolidBounds = new List<Bounds>(35);
        Matrix4x4 _boatWorld, _boatInverse;
        Bounds _localBounds, _worldBounds;
        struct SurfaceGroup { public int start, count; public Bounds bounds; }
        static readonly IComparer<Vector3>[] SurfaceOrder = {
            Comparer<Vector3>.Create((a, b) => a.x.CompareTo(b.x)),
            Comparer<Vector3>.Create((a, b) => a.y.CompareTo(b.y)),
            Comparer<Vector3>.Create((a, b) => a.z.CompareTo(b.z)) };
        int _activeCount;
        readonly List<Collider> _solids = new List<Collider>(35);
        readonly List<Bounds> _solidBounds = new List<Bounds>(35);
        static readonly float[] OrbitFallbacks = { 15f, -15f, 30f, -30f, 60f, -60f, 90f, -90f, 180f };
        Transform _root;
        Pose _applied;
        float _lastRequestedDistance;
        bool _initialized, _bindingComplete;
        int _side = 1;
        Vector3 _focus;
        float _heading, _tanX, _tanY;
        bool _fitHull;
        public bool Adjusted { get; private set; }
        public bool Clear { get; private set; }
        public int Queries { get; private set; }
        public int WitnessCount => _localWitnesses.Length;
        public int SurfaceVertexCount => _localSurface.Length;
        public bool Available => _root != null && _root.gameObject.activeInHierarchy
            && _bindingComplete && _solids[0] != null && _witnesses.Length > 0;

        public BoatViewOcclusion(Transform target, Camera camera)
        {
            _target = target; _camera = camera;
            var vertices = new List<Vector3>();
            Transform model = target.Find("RealDragonBoat");
            if (model != null)
                foreach (var mesh in model.GetComponentsInChildren<MeshFilter>())
                {
                    var renderer = mesh.GetComponent<Renderer>();
                    if (mesh.sharedMesh == null || renderer == null || !renderer.enabled) continue;
                    // Cache rigid hull geometry in boat-local coordinates.
                    // Multiplying inverse/world matrices at a distant bank
                    // cancels large translations with float rounding, making
                    // this supposedly fixed mesh depend on the initial boat
                    // position. Compose the actual local hierarchy instead.
                    Matrix4x4 toBoat = Matrix4x4.identity;
                    for (Transform node = mesh.transform; node != target; node = node.parent)
                    {
                        if (node == null) throw new System.InvalidOperationException("Displayed mesh is outside its boat hierarchy.");
                        toBoat = Matrix4x4.TRS(node.localPosition, node.localRotation, node.localScale) * toBoat;
                    }
                    foreach (Vector3 vertex in mesh.sharedMesh.vertices) vertices.Add(toBoat.MultiplyPoint3x4(vertex));
                }
            var witnesses = new List<Vector3>();
            if (vertices.Count > 0)
            {
                Bounds bounds = new Bounds(vertices[0], Vector3.zero);
                foreach (Vector3 vertex in vertices) bounds.Encapsulate(vertex);
                _localBounds = bounds;
                for (int i = 0; i < 8; i++)
                    _localCorners[i] = new Vector3((i & 1) == 0 ? bounds.min.x : bounds.max.x,
                        (i & 2) == 0 ? bounds.min.y : bounds.max.y, (i & 4) == 0 ? bounds.min.z : bounds.max.z);
                // Keep every vertex of the actual model's convex hull. A fixed
                // set of sampled directions missed curved hull points at a bank.
                // Reuse the project's existing MIT geometry library once here.
                var input = new List<MIConvexHull.DefaultVertex>();
                foreach (Vector3 point in new HashSet<Vector3>(vertices))
                    input.Add(new MIConvexHull.DefaultVertex { Position = new[] { (double)point.x, point.y, point.z } });
                var hull = MIConvexHull.ConvexHull.Create(input);
                if (hull.Outcome != MIConvexHull.ConvexHullCreationResultOutcome.Success || hull.Result == null)
                    throw new System.InvalidOperationException("Cannot prepare displayed hull for camera visibility: " + hull.ErrorMessage);
                foreach (var point in hull.Result.Points)
                    witnesses.Add(new Vector3((float)point.Position[0], (float)point.Position[1], (float)point.Position[2]));
            }
            _localWitnesses = witnesses.ToArray(); _witnesses = new Vector3[_localWitnesses.Length];
            var surface = new HashSet<Vector3>(vertices);
            _localSurface = new Vector3[surface.Count]; surface.CopyTo(_localSurface);
            if (_localSurface.Length > 0) GroupSurface(0, _localSurface.Length);
            _surfaceActive = new bool[_localSurface.Length];
            _activeWitnesses = new int[_localWitnesses.Length]; _witnessActive = new bool[_localWitnesses.Length];
            int directionIndex = 0;
            for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++) for (int z = -1; z <= 1; z++)
            {
                if ((x == 0 && y == 0 && z == 0) || _localWitnesses.Length == 0) continue;
                Vector3 direction = new Vector3(x, y, z); int best = 0;
                _surfaceDirections[directionIndex++] = direction;
                for (int i = 1; i < _localWitnesses.Length; i++)
                    if (Vector3.Dot(_localWitnesses[i], direction) > Vector3.Dot(_localWitnesses[best], direction)) best = i;
                AddWitness(best);
            }
        }

        void GroupSurface(int start, int count)
        {
            Bounds bounds = new Bounds(_localSurface[start], Vector3.zero);
            for (int i = start + 1; i < start + count; i++) bounds.Encapsulate(_localSurface[i]);
            if (count <= 64)
            {
                _surfaceGroups.Add(new SurfaceGroup { start = start, count = count, bounds = bounds });
                return;
            }
            Vector3 size = bounds.size;
            int axis = size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;
            System.Array.Sort(_localSurface, start, count, SurfaceOrder[axis]);
            int half = count / 2;
            GroupSurface(start, half); GroupSurface(start + half, count - half);
        }

        // Conservative broad phase for every segment from a vertex in a box to
        // the eye. The swept bounds and box silhouette planes enclose the whole
        // ray bundle. A rejected group cannot hide an unqueried surface vertex.
        static bool BundleMayHit(Bounds source, Vector3 eye, Bounds obstacle)
        {
            Bounds swept = source; swept.Encapsulate(eye);
            if (!swept.Intersects(obstacle)) return false;
            Vector3 center = source.center, extent = source.extents;
            Vector3 obstacleCenter = obstacle.center, obstacleExtent = obstacle.extents;
            for (int axis = 0; axis < 3; axis++)
            {
                int u = (axis + 1) % 3, v = (axis + 2) % 3;
                Vector3 edge = Vector3.zero; edge[axis] = 1f;
                for (int su = -1; su <= 1; su += 2) for (int sv = -1; sv <= 1; sv += 2)
                {
                    bool outsideU = su * (eye[u] - center[u]) > extent[u];
                    bool outsideV = sv * (eye[v] - center[v]) > extent[v];
                    if (outsideU == outsideV) continue;
                    Vector3 point = center; point[u] += su * extent[u]; point[v] += sv * extent[v];
                    Vector3 normal = Vector3.Cross(edge, eye - point);
                    if (Vector3.Dot(normal, center - point) > 0f) normal = -normal;
                    float nearest = Vector3.Dot(normal, obstacleCenter - point)
                        - Mathf.Abs(normal.x) * obstacleExtent.x - Mathf.Abs(normal.y) * obstacleExtent.y
                        - Mathf.Abs(normal.z) * obstacleExtent.z;
                    // This only enlarges the broad phase for roundoff. The final
                    // decision is still an unchanged native mesh ray query.
                    if (nearest > 0.0001f * normal.magnitude) return false;
                }
            }
            return true;
        }

        bool SurfaceClear(Vector3 eye)
        {
            bool clear = true;
            for (int d = 0; d < _surfaceBlockers.Length; d++) _surfaceBlockers[d] = -1;
            Vector3 localEye = _boatInverse.MultiplyPoint3x4(eye);
            for (int s = 0; s < _surfaceRegions.Count; s++)
            {
                // Keep the long stationary bank boxes in their own world
                // axes. Rotating a 600 m bank box into boat space expands it
                // across the empty river, causing thousands of false hits.
                if (!BundleMayHit(_worldBounds, eye, _surfaceRegions[s])) continue;
                Collider solid = _solids[_surfaceRegionSolids[s]];
                if (solid == null || !solid.enabled || !solid.gameObject.activeInHierarchy
                    || !BundleMayHit(_localBounds, localEye, _localSolidBounds[s])) continue;
                foreach (SurfaceGroup group in _surfaceGroups)
                {
                    if (!BundleMayHit(group.bounds, localEye, _localSolidBounds[s])) continue;
                    for (int i = group.start; i < group.start + group.count; i++)
                    {
                        Vector3 start = _boatWorld.MultiplyPoint3x4(_localSurface[i]);
                        Vector3 delta = eye - start; float length = delta.magnitude;
                        if (length < 0.00001f) continue;
                        Ray ray = new Ray(start, delta / length);
                        if (!_surfaceRegions[s].IntersectRay(ray, out float entry) || entry > length) continue;
                        Queries++;
                        if (!solid.Raycast(ray, out _, length)) continue;
                        clear = false;
                        // Retain extrema of the entire blocked patch, rather
                        // than chasing one arbitrarily ordered point per solve.
                        for (int d = 0; d < _surfaceBlockers.Length; d++)
                            if (_surfaceBlockers[d] < 0 || Vector3.Dot(_localSurface[i], _surfaceDirections[d])
                                > Vector3.Dot(_localSurface[_surfaceBlockers[d]], _surfaceDirections[d]))
                                _surfaceBlockers[d] = i;
                    }
                }
            }
            foreach (int index in _surfaceBlockers)
                if (index >= 0 && !_surfaceActive[index])
                { _surfaceConstraints.Add(index); _surfaceActive[index] = true; }
            return clear;
        }

        void AddWitness(int index)
        {
            if (_witnessActive[index]) return;
            _activeWitnesses[_activeCount++] = index; _witnessActive[index] = true;
        }

        void BindSolids(RiverEnvironmentController environment)
        {
            GameObject scene = environment == null ? null :
                environment.SelectedScene == RiverEnvironmentController.SceneProfile.XiangjiangOrangeIsland
                    ? environment.xiangjiangScene : environment.fengjianScene;
            Transform root = scene != null ? scene.transform : null;
            if (root == _root && (root == null || _bindingComplete)) return;
            _root = root; _bindingComplete = root != null;
            _solids.Clear(); _solidBounds.Clear(); _surfaceRegions.Clear(); _surfaceRegionSolids.Clear();
            var solidNames = RiverEnvironmentController.SolidSceneMeshes;
            for (int i = 0; i < solidNames.Count; i++)
            {
                Transform part = root != null ? root.Find(solidNames[i]) : null;
                AddSolid(part != null ? part.GetComponent<MeshCollider>() : null);
                // The scene builder omits empty mesh groups. Only geometry
                // actually present in this profile needs a matching collider.
                if (part != null)
                {
                    var mesh = part.GetComponent<MeshFilter>();
                    var collider = _solids[i] as MeshCollider;
                    _bindingComplete &= mesh != null && collider != null && collider.enabled
                        && collider.sharedMesh == mesh.sharedMesh && !collider.convex && !collider.isTrigger;
                }
            }
            // Waterline rocks are shared by both river profiles. Query the same
            // static meshes that stop the boat; never add camera-only geometry.
            var nature = GameObject.Find("NatureProps");
            if (nature != null)
                foreach (var solid in nature.GetComponentsInChildren<MeshCollider>())
                    if (!solid.isTrigger && solid.attachedRigidbody == null) AddSolid(solid);
        }

        void AddSolid(Collider solid)
        {
            _solids.Add(solid);
            // These scene meshes are stationary. Cache bounds per profile so
            // distant rocks do not add a native mesh query for every hull ray.
            _solidBounds.Add(solid != null ? solid.bounds : new Bounds());
            if (solid == null) return;
            var mesh = (solid as MeshCollider)?.sharedMesh;
            if (mesh == null || !mesh.isReadable)
            {
                _surfaceRegions.Add(solid.bounds); _surfaceRegionSolids.Add(_solids.Count - 1);
                return;
            }
            // Combined banks span both shores. Their single AABB also encloses
            // the empty river, so use bounds of triangle-connected pieces as a
            // read-only broad phase. Every original triangle remains covered.
            Vector3[] vertices = mesh.vertices; int[] triangles = mesh.triangles;
            var parents = new int[vertices.Length];
            for (int i = 0; i < parents.Length; i++) parents[i] = i;
            for (int i = 0; i < triangles.Length; i += 3)
            {
                int root = ComponentRoot(parents, triangles[i]);
                parents[ComponentRoot(parents, triangles[i + 1])] = root;
                parents[ComponentRoot(parents, triangles[i + 2])] = root;
            }
            var pieces = new Dictionary<int, Bounds>();
            Matrix4x4 matrix = solid.transform.localToWorldMatrix;
            foreach (int index in triangles)
            {
                int root = ComponentRoot(parents, index);
                Vector3 point = matrix.MultiplyPoint3x4(vertices[index]);
                if (!pieces.TryGetValue(root, out Bounds bounds)) bounds = new Bounds(point, Vector3.zero);
                else bounds.Encapsulate(point);
                pieces[root] = bounds;
            }
            foreach (Bounds piece in pieces.Values)
            {
                Bounds padded = piece; padded.Expand(0.0002f);
                _surfaceRegions.Add(padded); _surfaceRegionSolids.Add(_solids.Count - 1);
            }
        }

        static int ComponentRoot(int[] parents, int index)
        {
            while (parents[index] != index)
            { parents[index] = parents[parents[index]]; index = parents[index]; }
            return index;
        }

        bool SegmentClear(Vector3 start, Vector3 end)
        {
            Vector3 delta = end - start;
            float length = delta.magnitude;
            if (length < 0.00001f) return true;
            Ray ray = new Ray(start, delta / length);
            for (int i = 0; i < _solids.Count; i++)
            {
                Collider solid = _solids[i];
                if (solid == null || !_solidBounds[i].IntersectRay(ray, out float entry) || entry > length) continue;
                if (solid == null || !solid.enabled || !solid.gameObject.activeInHierarchy) continue;
                Queries++;
                if (solid.Raycast(ray, out RaycastHit hit, length)) return false;
            }
            return true;
        }

        bool PoseClear(Pose pose, bool exhaustive = false)
        {
            Quaternion rotation = pose.Rotation(_heading);
            Vector3 eye = pose.Eye(_focus, _heading);
            if (!SegmentClear(_focus, eye)) return false;
            // This is a conservative test of the entire displayed hull's ray
            // bundle, not a reduced vertex sample. Keep native ray checks for
            // every potentially intersecting bundle and for the lens footprint.
            if (HullBundleMayHit(eye))
            {
                int count = exhaustive ? _witnesses.Length : _activeCount;
                bool witnessesClear = true;
                for (int i = 0; i < count; i++)
                {
                    int index = exhaustive ? i : _activeWitnesses[i];
                    if (!SegmentClear(_witnesses[index], eye))
                    {
                        if (!exhaustive) return false;
                        // A complex new hull can expose several missed boundary
                        // points at once. Learn the whole blocked set before the
                        // next search, rather than using one attempt per vertex.
                        AddWitness(index);
                        witnessesClear = false;
                    }
                }
                if (!witnessesClear) return false;
                foreach (int index in _surfaceConstraints)
                    if (!SegmentClear(_boatWorld.MultiplyPoint3x4(_localSurface[index]), eye)) return false;
                // Convex-hull vertices suffice for framing, but small rocks can
                // occlude a face's interior while all its boundary rays are clear.
                if (exhaustive && !SurfaceClear(eye)) return false;
            }
            // Cover the near-plane footprint and a small lens allowance, so a
            // clear central ray cannot leave the image's edge inside a wall.
            float near = _camera.nearClipPlane;
            Vector3 right = rotation * Vector3.right * Mathf.Max(Clearance, near * _tanX);
            Vector3 up = rotation * Vector3.up * Mathf.Max(Clearance, near * _tanY);
            Vector3 nearCenter = eye + rotation * Vector3.forward * near;
            return SegmentClear(_focus, nearCenter + right + up) && SegmentClear(_focus, nearCenter + right - up)
                && SegmentClear(_focus, nearCenter - right + up) && SegmentClear(_focus, nearCenter - right - up);
        }

        bool HullBundleMayHit(Vector3 eye)
        {
            for (int s = 0; s < _surfaceRegions.Count; s++)
            {
                if (!BundleMayHit(_worldBounds, eye, _surfaceRegions[s])) continue;
                Collider solid = _solids[_surfaceRegionSolids[s]];
                if (solid != null && solid.enabled && solid.gameObject.activeInHierarchy) return true;
            }
            return false;
        }

        float FitDistance(Quaternion rotation)
        {
            Quaternion inverse = Quaternion.Inverse(rotation);
            float minimum = 4f;
            foreach (Vector3 corner in _corners)
            {
                Vector3 p = inverse * (corner - _focus);
                minimum = Mathf.Max(Mathf.Max(minimum, _camera.nearClipPlane - p.z),
                    Mathf.Max(Mathf.Abs(p.x) / _tanX - p.z, Mathf.Abs(p.y) / _tanY - p.z));
            }
            return minimum + 0.05f;
        }

        bool HullFits(Pose pose)
        {
            if (_witnesses.Length == 0) return false;
            Quaternion inverse = Quaternion.Inverse(pose.Rotation(_heading));
            foreach (Vector3 point in _witnesses)
            {
                Vector3 p = inverse * (point - _focus);
                float depth = p.z + pose.distance;
                if (depth < _camera.nearClipPlane || Mathf.Abs(p.x) > depth * _tanX
                    || Mathf.Abs(p.y) > depth * _tanY) return false;
            }
            return true;
        }

        float DistanceLimit(Quaternion rotation)
        {
            Ray ray = new Ray(_focus, -(rotation * Vector3.forward));
            float limit = 60f;
            for (int i = 0; i < _solids.Count; i++)
            {
                Collider solid = _solids[i];
                if (solid == null || !_solidBounds[i].IntersectRay(ray, out float entry) || entry > limit + Clearance) continue;
                if (solid == null || !solid.enabled || !solid.gameObject.activeInHierarchy) continue;
                Queries++;
                if (solid.Raycast(ray, out RaycastHit hit, limit + Clearance)) limit = Mathf.Min(limit, hit.distance - Clearance);
            }
            return limit;
        }

        bool TryDirection(Pose requested, float yaw, float pitch, out Pose candidate)
        {
            candidate = new Pose { yaw = yaw, pitch = pitch, distance = requested.distance };
            Quaternion rotation = candidate.Rotation(_heading);
            float minimum = _fitHull ? FitDistance(rotation) : Mathf.Min(4f, requested.distance);
            float maximum = DistanceLimit(rotation);
            if (minimum > maximum) return false;
            candidate.distance = Mathf.Clamp(requested.distance, minimum, maximum);
            if (PoseClear(candidate)) return true;
            float blocked = candidate.distance;
            candidate.distance = minimum;
            if (!PoseClear(candidate)) return false;
            // Refine only between an observed clear endpoint and an observed
            // blocked endpoint. Each retained result is queried again in PhysX.
            for (int i = 0; i < 7; i++)
            {
                Pose trial = candidate; trial.distance = (candidate.distance + blocked) * 0.5f;
                if (PoseClear(trial)) candidate = trial; else blocked = trial.distance;
            }
            Pose reserved = candidate;
            reserved.distance = Mathf.Max(minimum, candidate.distance - VisibilityReserve);
            if (PoseClear(reserved)) candidate = reserved;
            return true;
        }

        bool Elevate(Pose requested, float yaw, out Pose candidate)
        {
            if (TryDirection(requested, yaw, requested.pitch, out candidate)) return true;
            float lower = requested.pitch;
            for (float pitch = Mathf.Min(80f, lower + 8f); ; pitch = Mathf.Min(80f, pitch + 8f))
            {
                if (TryDirection(requested, yaw, pitch, out candidate))
                {
                    float upper = pitch;
                    for (int i = 0; i < 8; i++)
                    {
                        float middle = (lower + upper) * 0.5f;
                        if (TryDirection(requested, yaw, middle, out Pose refined)) { upper = middle; candidate = refined; }
                        else lower = middle;
                    }
                    float reserve = Mathf.Atan2(VisibilityReserve, Mathf.Max(4f, candidate.distance)) * Mathf.Rad2Deg;
                    if (TryDirection(requested, yaw, Mathf.Min(80f, upper + reserve), out Pose guarded)) candidate = guarded;
                    return true;
                }
                if (pitch >= 80f) return false;
                lower = pitch;
            }
        }

        bool FixedDistanceClear(Pose requested, float pitch, out Pose candidate)
        {
            candidate = requested; candidate.pitch = pitch;
            Quaternion rotation = candidate.Rotation(_heading);
            return (!_fitHull || HullFits(candidate)) && candidate.distance <= DistanceLimit(rotation)
                && PoseClear(candidate);
        }

        bool ElevateAtDistance(Pose requested, out Pose candidate)
        {
            if (FixedDistanceClear(requested, requested.pitch, out candidate)) return true;
            float lower = requested.pitch;
            for (float pitch = Mathf.Min(80f, lower + 8f); ; pitch = Mathf.Min(80f, pitch + 8f))
            {
                if (FixedDistanceClear(requested, pitch, out candidate))
                {
                    float upper = pitch;
                    for (int i = 0; i < 8; i++)
                    {
                        float middle = (lower + upper) * 0.5f;
                        if (FixedDistanceClear(requested, middle, out Pose refined)) { upper = middle; candidate = refined; }
                        else lower = middle;
                    }
                    float reserve = Mathf.Atan2(VisibilityReserve, Mathf.Max(4f, candidate.distance)) * Mathf.Rad2Deg;
                    if (FixedDistanceClear(requested, Mathf.Min(80f, upper + reserve), out Pose guarded)) candidate = guarded;
                    return true;
                }
                if (pitch >= 80f) return false;
                lower = pitch;
            }
        }

        Pose ChooseView(Pose requested, bool requestClear, float deltaTime, bool reset)
        {
            Pose chosen = requested;
            bool solved = requestClear;
            if (!solved && _witnesses.Length > 0)
            {
                Pose search = requested;
                if (_initialized && Adjusted && !reset)
                    search.distance = Mathf.Clamp(_applied.distance + requested.distance - _lastRequestedDistance, 4f, 60f);
                solved = Elevate(search, requested.yaw, out chosen);
                if (!solved)
                    foreach (float offset in OrbitFallbacks)
                        if (Elevate(search, requested.yaw + offset * _side, out chosen))
                        {
                            float blocked = 0f, clear = offset * _side;
                            for (int i = 0; i < 8; i++)
                            {
                                float middle = (blocked + clear) * 0.5f;
                                if (TryDirection(search, requested.yaw + middle, chosen.pitch, out Pose refined))
                                { clear = middle; chosen = refined; }
                                else blocked = middle;
                            }
                            solved = true; break;
                        }
                // At the first obstruction, shortening a long orbit can cause
                // a multi-metre jump even when a small rise keeps it clear.
                // Compare actual eye travel with a solution retaining the last
                // distance before applying the usual read-only smoothing.
                if (solved && _initialized && !reset)
                {
                    Vector3 previousEye = _applied.Eye(_focus, _heading);
                    float travel = (chosen.Eye(_focus, _heading) - previousEye).sqrMagnitude;
                    if (travel > 0.0225f)
                    {
                        Pose retained = requested;
                        retained.distance = Mathf.Clamp(_applied.distance + requested.distance - _lastRequestedDistance, 4f, 60f);
                        if (ElevateAtDistance(retained, out Pose elevated)
                            && (elevated.Eye(_focus, _heading) - previousEye).sqrMagnitude < travel)
                            chosen = elevated;
                    }
                }
            }
            if (!solved)
            {
                // A narrow bridge may have no fully visible hull pose in the
                // allowed range. Preserve an existing clear view when possible;
                // report any unresolved obstruction rather than claiming a pass.
                chosen = _initialized && PoseClear(_applied) ? _applied : requested;
                if (!PoseClear(chosen)) chosen.distance = Mathf.Max(0.25f, DistanceLimit(chosen.Rotation(_heading)));
            }
            if (_initialized && !reset && (Adjusted || !requestClear))
            {
                float blend = 1f - Mathf.Exp(-Mathf.Max(0f, deltaTime) / 0.22f);
                for (int i = 0; i < 8; i++, blend *= 0.5f)
                {
                    Pose smooth = new Pose { yaw = Mathf.LerpAngle(_applied.yaw, chosen.yaw, blend),
                        pitch = Mathf.Lerp(_applied.pitch, chosen.pitch, blend), distance = Mathf.Lerp(_applied.distance, chosen.distance, blend) };
                    if (PoseClear(smooth) && (!_fitHull || HullFits(smooth))) { chosen = smooth; break; }
                }
            }
            return chosen;
        }

        public Pose Resolve(Vector3 focus, float heading, float yaw, float pitch, float distance,
            float deltaTime, bool reset, RiverEnvironmentController environment)
        {
            Queries = 0; BindSolids(environment);
            _focus = focus; _heading = heading;
            _tanY = Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            _tanX = _tanY * _camera.aspect;
            _boatWorld = _target.localToWorldMatrix; _boatInverse = _target.worldToLocalMatrix;
            for (int i = 0; i < _corners.Length; i++) _corners[i] = _boatWorld.MultiplyPoint3x4(_localCorners[i]);
            _worldBounds = new Bounds(_corners[0], Vector3.zero);
            for (int i = 1; i < _corners.Length; i++) _worldBounds.Encapsulate(_corners[i]);
            _worldBounds.Expand(0.0002f); // Conservative float-transform reserve.
            for (int i = 0; i < _witnesses.Length; i++) _witnesses[i] = _boatWorld.MultiplyPoint3x4(_localWitnesses[i]);
            _localSolidBounds.Clear();
            foreach (Bounds world in _surfaceRegions)
            {
                Vector3 x = _boatInverse.MultiplyVector(Vector3.right * world.extents.x);
                Vector3 y = _boatInverse.MultiplyVector(Vector3.up * world.extents.y);
                Vector3 z = _boatInverse.MultiplyVector(Vector3.forward * world.extents.z);
                _localSolidBounds.Add(new Bounds(_boatInverse.MultiplyPoint3x4(world.center), 2f * new Vector3(
                    Mathf.Abs(x.x) + Mathf.Abs(y.x) + Mathf.Abs(z.x),
                    Mathf.Abs(x.y) + Mathf.Abs(y.y) + Mathf.Abs(z.y),
                    Mathf.Abs(x.z) + Mathf.Abs(y.z) + Mathf.Abs(z.z))));
            }
            Pose requested = new Pose { yaw = yaw, pitch = pitch, distance = distance }, chosen = requested;
            // A bounding box proves inclusion but cannot prove that the curved
            // boat is cropped. Check its actual convex hull before allowing a
            // requested whole-boat view to lose that framing during protection.
            _fitHull = distance >= FitDistance(requested.Rotation(heading)) - 0.05f || HullFits(requested);
            bool requestClear = PoseClear(requested, true), verified = false;
            // Search with a small active set, but verify every original surface
            // vertex before applying a pose. Any missed point becomes a search
            // constraint immediately; grouped bounds eliminate impossible hits.
            for (int attempt = 0; attempt < 8; attempt++)
            {
                chosen = ChooseView(requested, requestClear, deltaTime, reset);
                bool alreadyChecked = requestClear && chosen.yaw == requested.yaw
                    && chosen.pitch == requested.pitch && chosen.distance == requested.distance;
                if ((alreadyChecked || PoseClear(chosen, true)) && (!_fitHull || HullFits(chosen))) { verified = true; break; }
            }
            Adjusted = Mathf.Abs(Mathf.DeltaAngle(chosen.yaw, yaw)) > 0.001f
                || Mathf.Abs(chosen.pitch - pitch) > 0.001f || Mathf.Abs(chosen.distance - distance) > 0.001f || !requestClear;
            if (!Adjusted) chosen = requested;
            Clear = Available && verified;
            float offsetFromRequest = Mathf.DeltaAngle(yaw, chosen.yaw);
            if (Mathf.Abs(offsetFromRequest) > 1f) _side = offsetFromRequest > 0f ? 1 : -1;
            _applied = chosen; _lastRequestedDistance = distance; _initialized = true;
            return chosen;
        }
    }
}
