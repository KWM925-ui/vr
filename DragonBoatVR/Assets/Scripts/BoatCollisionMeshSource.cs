using System;
using UnityEngine;

namespace DragonBoatVR
{
    [Serializable]
    public sealed class BoatCollisionMeshSource
    {
        [Serializable] public sealed class Part
        {
            public string name;
            public int vertices, triangles;
        }
        public int schemaVersion = 2;
        public string provenance = "UNMEASURED_COMPOUND_COLLISION_APPROXIMATION_OF_DISPLAYED_WOOD";
        public string collisionSourceKind = "DISPLAYED_MESH_ALL";
        public string decompositionEngine = "CoACD 1.0.14";
        public string decompositionGrouping = "COMBINED";
        public string sourceSetSha256;
        public string nativeLibrarySha256, convexHullCommit, sourceObjSha256, sourceImportSha256;
        public float decompositionThresholdM;
        public int preprocessResolution, randomSeed;
        public int maximumVerticesPerPart;
        public bool mergeParts;
        public float sharedFaceOverlapM;
        public int sourceVertices, sourceTriangles, collisionVertices, collisionTriangles;
        public Vector3 sourceMin, sourceMax;
        public Vector3 modelLocalPosition, modelLocalScale;
        public Quaternion modelLocalRotation;
        public Part[] parts;
    }
}
