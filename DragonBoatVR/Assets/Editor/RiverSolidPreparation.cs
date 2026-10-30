using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DragonBoatVR.EditorTools
{
    public static class RiverSolidPreparation
    {
        [Serializable] public class Entry
        {
            public string scene, path, mesh;
            public int flagsBefore, flagsAfter, vertices;
            public bool changed, sameMesh, readable;
        }
        [Serializable] public class Report
        {
            public string scope = "Support the existing rear tree row with terrain derived from original bank/hill bounds; clear only static render batching on registered terrain. Preserve original meshes, other flags and all scene parameters.";
            public List<Entry> entries = new List<Entry>();
        }
        public static void Run()
        {
            var report = new Report();
            foreach (string boat in new[] { BoatModelChoice.Majesty, BoatModelChoice.Ornament })
            {
                string path = "Assets/Scenes/" + BoatModelChoice.SceneName(boat) + ".unity";
                var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                bool dirty = false;
                foreach (GameObject root in scene.GetRootGameObjects())
                    foreach (Transform parent in root.GetComponentsInChildren<Transform>(true))
                    {
                        if (parent.name != "RiverCourse_XiangjiangOrangeIsland" && parent.name != "RiverCourse_ShunDeFengjian") continue;
                        dirty |= RiverCourseBuilder.EnsureShoreGround(parent);
                        foreach (string name in RiverEnvironmentController.SolidSceneMeshes)
                        {
                            Transform part = parent.Find(name);
                            if (part == null) continue;
                            Mesh mesh = part.GetComponent<MeshFilter>()?.sharedMesh;
                            if (mesh == null || !mesh.isReadable) throw new InvalidOperationException("Missing readable original river mesh: " + name);
                            var row = new Entry { scene = path, path = parent.name + "/" + name, mesh = mesh.name,
                                flagsBefore = (int)GameObjectUtility.GetStaticEditorFlags(part.gameObject), vertices = mesh.vertexCount, readable = mesh.isReadable };
                            row.changed = RiverCourseBuilder.PreserveSolidMesh(part.gameObject);
                            row.flagsAfter = (int)GameObjectUtility.GetStaticEditorFlags(part.gameObject);
                            row.sameMesh = part.GetComponent<MeshFilter>().sharedMesh == mesh;
                            if (!row.sameMesh || (row.flagsAfter & (int)StaticEditorFlags.BatchingStatic) != 0
                                || (row.flagsBefore & ~(int)StaticEditorFlags.BatchingStatic) != row.flagsAfter)
                                throw new InvalidOperationException("Unexpected river mesh/flag change.");
                            report.entries.Add(row); dirty |= row.changed;
                        }
                    }
                if (dirty && !EditorSceneManager.SaveScene(scene)) throw new IOException("Cannot save " + path);
            }
            if (report.entries.Count != 18) throw new InvalidOperationException("Expected nine terrain meshes in each boat scene.");
            string[] args = Environment.GetCommandLineArgs();
            string log = args[Array.IndexOf(args, "-logFile") + 1];
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(log), "river_solid_preparation.json"), JsonUtility.ToJson(report, true));
            Debug.Log("[RIVER_SOLID_PREPARATION] completed=" + report.entries.Count);
        }
    }
}
