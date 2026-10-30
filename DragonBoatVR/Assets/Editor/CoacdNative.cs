using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace DragonBoatVR.EditorTools
{
    // Adapted from CoACD 1.0.14's published ctypes ABI. The pinned native
    // library performs the decomposition; every returned allocation is freed.
    internal static class CoacdNative
    {
        [StructLayout(LayoutKind.Sequential)] struct NativeMesh
        {
            public IntPtr vertices;
            public ulong vertexCount;
            public IntPtr triangles;
            public ulong triangleCount;
        }
        [StructLayout(LayoutKind.Sequential)] struct NativeMeshes
        {
            public IntPtr meshes;
            public ulong count;
        }
        [DllImport("lib_coacd", CallingConvention = CallingConvention.Cdecl)]
        static extern NativeMeshes CoACD_run(ref NativeMesh mesh, double threshold,
            int maxHulls, int preprocess, int preprocessResolution, int resolution,
            int mctsNodes, int mctsIterations, int mctsDepth,
            [MarshalAs(UnmanagedType.I1)] bool pca,
            [MarshalAs(UnmanagedType.I1)] bool merge,
            [MarshalAs(UnmanagedType.I1)] bool decimate, int maxVertices,
            [MarshalAs(UnmanagedType.I1)] bool extrude, double extrudeMargin,
            int approximation, uint seed, [MarshalAs(UnmanagedType.I1)] bool realMetric);
        [DllImport("lib_coacd", CallingConvention = CallingConvention.Cdecl)]
        static extern void CoACD_freeMeshArray(NativeMeshes meshes);

        public static List<Mesh> Decompose(Mesh source, float thresholdM, int preprocessResolution, uint seed,
            int maximumVertices = 120, bool merge = true, float sharedFaceOverlapM = 0.005f)
        {
            if (maximumVertices < 4 || maximumVertices > 120 || !SimulationValue.Finite(sharedFaceOverlapM) || sharedFaceOverlapM < 0)
                throw new ArgumentOutOfRangeException("Invalid native convex budget or shared-face overlap.");
            Vector3[] input = source.vertices;
            var unique = new List<Vector3>(); var lookup = new Dictionary<Vector3, int>();
            var remap = new int[input.Length];
            for (int i = 0; i < input.Length; i++)
            {
                if (!lookup.TryGetValue(input[i], out int index))
                { index = unique.Count; unique.Add(input[i]); lookup.Add(input[i], index); }
                remap[i] = index;
            }
            var vertices = new double[unique.Count * 3];
            for (int i = 0; i < unique.Count; i++)
            { vertices[3 * i] = unique[i].x; vertices[3 * i + 1] = unique[i].y; vertices[3 * i + 2] = unique[i].z; }
            int[] triangles = source.triangles;
            for (int i = 0; i < triangles.Length; i++) triangles[i] = remap[triangles[i]];
            // Unity uses clockwise front faces; CoACD expects an outward
            // consistently oriented solid. Select the positive signed volume.
            double volume = 0;
            for (int i = 0; i < triangles.Length; i += 3)
                volume += Vector3.Dot(unique[triangles[i]], Vector3.Cross(unique[triangles[i + 1]], unique[triangles[i + 2]]));
            if (volume < 0)
                for (int i = 0; i < triangles.Length; i += 3)
                { int swap = triangles[i + 1]; triangles[i + 1] = triangles[i + 2]; triangles[i + 2] = swap; }
            var vertexHandle = GCHandle.Alloc(vertices, GCHandleType.Pinned);
            var triangleHandle = GCHandle.Alloc(triangles, GCHandleType.Pinned);
            NativeMeshes output = default;
            var result = new List<Mesh>();
            string priorThreads = Environment.GetEnvironmentVariable("OMP_NUM_THREADS");
            try
            {
                Environment.SetEnvironmentVariable("OMP_NUM_THREADS", "2");
                source.RecalculateBounds();
                Vector3 sourceSize = source.bounds.size;
                double halfExtent = Math.Max(sourceSize.x, Math.Max(sourceSize.y, sourceSize.z)) * 0.5;
                if (halfExtent <= 0) throw new InvalidOperationException("Empty source for convex decomposition.");
                // real_metric converts the concavity threshold only. CoACD's
                // documented shared-face extrusion uses normalized coordinates.
                double normalizedOverlap = sharedFaceOverlapM / halfExtent;
                var native = new NativeMesh { vertices = vertexHandle.AddrOfPinnedObject(), vertexCount = (ulong)unique.Count,
                    triangles = triangleHandle.AddrOfPinnedObject(), triangleCount = (ulong)triangles.Length / 3 };
                output = CoACD_run(ref native, thresholdM, -1, 0, preprocessResolution, 2000,
                    20, 100, 3, false, merge, true, maximumVertices, sharedFaceOverlapM > 0f, normalizedOverlap, 0, seed, true);
                if (output.meshes == IntPtr.Zero || output.count == 0 || output.count > BoatHullCollisionShape.MaximumParts)
                    throw new InvalidOperationException("CoACD returned no usable bounded compound: " + output.count);
                int size = Marshal.SizeOf<NativeMesh>();
                for (int i = 0; i < (int)output.count; i++)
                {
                    var part = Marshal.PtrToStructure<NativeMesh>(IntPtr.Add(output.meshes, i * size));
                    if (part.vertexCount < 4 || part.vertexCount > 120 || part.triangleCount < 4 || part.triangleCount > 236)
                        throw new InvalidOperationException("CoACD part exceeds the native convex cooking budget.");
                    var xyz = new double[checked((int)part.vertexCount * 3)];
                    var indices = new int[checked((int)part.triangleCount * 3)];
                    Marshal.Copy(part.vertices, xyz, 0, xyz.Length); Marshal.Copy(part.triangles, indices, 0, indices.Length);
                    var points = new Vector3[(int)part.vertexCount];
                    for (int j = 0; j < points.Length; j++)
                        points[j] = new Vector3((float)xyz[3 * j], (float)xyz[3 * j + 1], (float)xyz[3 * j + 2]);
                    var mesh = new Mesh { indexFormat = IndexFormat.UInt32, vertices = points, triangles = indices };
                    mesh.RecalculateBounds(); result.Add(mesh);
                }
                return result;
            }
            catch
            {
                foreach (var mesh in result) UnityEngine.Object.DestroyImmediate(mesh);
                throw;
            }
            finally
            {
                if (output.meshes != IntPtr.Zero) CoACD_freeMeshArray(output);
                vertexHandle.Free(); triangleHandle.Free();
                Environment.SetEnvironmentVariable("OMP_NUM_THREADS", priorThreads);
            }
        }
    }
}
