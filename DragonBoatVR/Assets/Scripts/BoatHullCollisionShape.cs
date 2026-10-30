using System;
using UnityEngine;

namespace DragonBoatVR
{
    /// <summary>Generated convex parts follow the visible wood and leave the
    /// cabin open. PhysX contact is independent of the equivalent mass model.</summary>
    public sealed class BoatHullCollisionShape : MonoBehaviour
    {
        // Bound generated asset/component allocation. Per-part polygon limits
        // and measured simulation cost are checked independently.
        public const int MaximumParts = 1024;
        public const int OrnamentMaximumParts = 3072;
        public static int MaximumPartsFor(BoatModelChoice choice)
        {
            if (choice == null || choice.modelId == BoatModelChoice.Majesty) return MaximumParts;
            if (choice.modelId == BoatModelChoice.Ornament) return OrnamentMaximumParts;
            throw new InvalidOperationException("No collision allocation budget exists for this boat identity.");
        }
        public Vector3 ReferenceInertiaTensor { get; private set; }
        public Quaternion ReferenceInertiaRotation { get; private set; }
        public int SourceVertices { get; private set; }
        public int SourceTriangles { get; private set; }
        public int CollisionVertices { get; private set; }
        public int CollisionTriangles { get; private set; }
        public MeshCollider[] Colliders { get; private set; }
        public BoatCollisionMeshSource Source { get; private set; }
        public bool Ready => Colliders != null && Colliders.Length > 1;
        public Bounds WorldBounds
        {
            get
            {
                Bounds bounds = Colliders[0].bounds;
                for (int i = 1; i < Colliders.Length; i++) bounds.Encapsulate(Colliders[i].bounds);
                return bounds;
            }
        }

        public static BoatHullCollisionShape Install(BoatBuoyancy boat)
        {
            var existing = boat.GetComponent<BoatHullCollisionShape>();
            if (existing != null && existing.Ready) return existing;
            Transform model = boat.transform.Find("RealDragonBoat");
            if (model == null || !model.gameObject.activeInHierarchy) return null;
            var shape = existing != null ? existing : boat.gameObject.AddComponent<BoatHullCollisionShape>();
            shape.Build(boat, model);
            return shape;
        }

        void Build(BoatBuoyancy boat, Transform model)
        {
            var body = boat.BoatRb; var box = boat.GetComponent<BoxCollider>();
            if (body == null || box == null || !box.enabled)
                throw new InvalidOperationException("Wood contact replacement requires its active equivalent-body reference collider.");
            var choice = boat.GetComponent<BoatModelChoice>();
            var meshes = Resources.LoadAll<Mesh>(choice != null ? choice.collisionResource : "Collision/DragonBoatHull");
            Array.Sort(meshes, (a, b) => string.CompareOrdinal(a.name, b.name));
            var sourceAsset = Resources.Load<TextAsset>(choice != null ? choice.collisionSourceResource : "Collision/DragonBoatHullSource");
            var source = sourceAsset != null ? JsonUtility.FromJson<BoatCollisionMeshSource>(sourceAsset.text) : null;
            if (source == null || source.schemaVersion != 2 || source.parts == null || meshes.Length != source.parts.Length
                || meshes.Length < 2 || meshes.Length > MaximumPartsFor(choice))
                throw new InvalidOperationException("Generated wood collision compound or provenance is missing.");
            int vertices = 0, triangles = 0;
            for (int i = 0; i < meshes.Length; i++)
            {
                var part = source.parts[i]; var mesh = meshes[i];
                if (part == null || part.name != mesh.name || part.vertices < 4 || part.vertices > 120
                    || part.triangles < 4 || part.triangles > 236 || mesh.vertexCount != part.vertices || mesh.triangles.Length / 3 != part.triangles)
                    throw new InvalidOperationException("Wood collision part identity or native convex budget is invalid.");
                vertices += part.vertices; triangles += part.triangles;
            }
            if (vertices != source.collisionVertices || triangles != source.collisionTriangles
                || Vector3.Distance(model.localPosition, source.modelLocalPosition) > 1e-5f
                || Vector3.Distance(model.localScale, source.modelLocalScale) > 1e-5f
                || Quaternion.Angle(model.localRotation, source.modelLocalRotation) > 1e-4f)
                throw new InvalidOperationException("Displayed model differs from the generated wood contact shape.");
            int sourceVertices = 0, sourceTriangles = 0;
            Bounds bounds = default; bool first = true;
            foreach (var filter in model.GetComponentsInChildren<MeshFilter>())
            {
                var renderer = filter.GetComponent<Renderer>();
                if (renderer == null || !renderer.enabled || filter.sharedMesh == null) continue;
                var local = transform.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                sourceVertices += filter.sharedMesh.vertexCount;
                sourceTriangles += filter.sharedMesh.triangles.Length / 3;
                foreach (var vertex in filter.sharedMesh.vertices)
                {
                    Vector3 point = local.MultiplyPoint3x4(vertex);
                    if (first) { bounds = new Bounds(point, Vector3.zero); first = false; }
                    else bounds.Encapsulate(point);
                }
            }
            if (first || sourceVertices != source.sourceVertices || sourceTriangles != source.sourceTriangles
                || Vector3.Distance(bounds.min, source.sourceMin) > 1e-4f || Vector3.Distance(bounds.max, source.sourceMax) > 1e-4f)
                throw new InvalidOperationException("Displayed geometry differs from the generated wood contact shape.");
            ReferenceInertiaTensor = body.inertiaTensor; ReferenceInertiaRotation = body.inertiaTensorRotation;
            Vector3 centre = body.centerOfMass;
            Vector3 position = body.position; Quaternion rotation = body.rotation;
            var colliders = new MeshCollider[meshes.Length];
            try
            {
                for (int i = 0; i < meshes.Length; i++)
                {
                    var collider = gameObject.AddComponent<MeshCollider>(); colliders[i] = collider;
                    collider.enabled = false; collider.convex = true;
                    collider.sharedMaterial = box.sharedMaterial; collider.contactOffset = box.contactOffset;
                    collider.cookingOptions = MeshColliderCookingOptions.CookForFasterSimulation
                        | MeshColliderCookingOptions.EnableMeshCleaning | MeshColliderCookingOptions.WeldColocatedVertices
                        | MeshColliderCookingOptions.UseFastMidphase;
                    collider.sharedMesh = meshes[i];
                }
                box.enabled = false;
                foreach (var collider in colliders) collider.enabled = true;
                Physics.SyncTransforms();
                foreach (var collider in colliders)
                    if (collider.bounds.size.sqrMagnitude < 1e-10f)
                        throw new InvalidOperationException("PhysX produced an empty wood contact part.");
                Colliders = colliders; Source = source;
                SourceVertices = sourceVertices; SourceTriangles = sourceTriangles;
                CollisionVertices = vertices; CollisionTriangles = triangles;
            }
            catch
            {
                foreach (var collider in colliders) if (collider != null)
                { collider.enabled = false; if (Application.isPlaying) Destroy(collider); else DestroyImmediate(collider); }
                box.enabled = true; throw;
            }
            finally
            {
                body.centerOfMass = centre;
                body.inertiaTensor = ReferenceInertiaTensor; body.inertiaTensorRotation = ReferenceInertiaRotation;
                // PhysX updates its mass frame as each convex part is attached.
                // Hundreds of those conversions can accumulate pose roundoff;
                // replacing contact geometry must not move or rotate the boat.
                body.position = position; body.rotation = rotation;
            }
            if (Application.isPlaying) Destroy(box); else DestroyImmediate(box);
        }

        public bool Raycast(Ray ray, out RaycastHit hit, float distance)
        {
            hit = default; bool found = false;
            if (!Ready) return false;
            foreach (var collider in Colliders)
                if (collider.enabled && collider.Raycast(ray, out RaycastHit part, distance))
                { hit = part; distance = part.distance; found = true; }
            return found;
        }
    }
}
