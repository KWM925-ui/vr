using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DragonBoatVR.EditorTools
{
    /// <summary>Open the shipped scene unchanged and keep its ordinary Game view rendering.</summary>
    [InitializeOnLoad]
    public static class ClosedLoopRuntimeLauncher
    {
        const string Key = "DragonBoatVR.ClosedLoopRuntimeLauncher.Active";
        static EditorWindow _gameView;
        static ClosedLoopRuntimeLauncher()
        {
            if (SessionState.GetBool(Key, false)) EditorApplication.update += Update;
        }
        public static void Run()
        {
            if (ClosedLoopRuntimeCheck.RequestedArtifacts() == null) throw new ArgumentException("-closedLoopArtifacts is required.");
            SessionState.SetBool(Key, true);
            SessionState.SetFloat(Key + ".Started", (float)EditorApplication.timeSinceStartup);
            EditorSceneManager.OpenScene("Assets/Scenes/Demo.unity", OpenSceneMode.Single);
            EnsureGameView();
            EditorApplication.update -= Update; EditorApplication.update += Update;
            EditorApplication.EnterPlaymode();
        }
        static void Update()
        {
            if (!SessionState.GetBool(Key, false)) return;
            if (EditorApplication.timeSinceStartup - SessionState.GetFloat(Key + ".Started", 0) > 130)
            {
                Debug.LogError("[CLOSEDLOOP] Launcher timeout before a completed runtime report.");
                Exit(1); return;
            }
            if (!Application.isPlaying) return;
            EnsureGameView();
            EditorApplication.QueuePlayerLoopUpdate();
            _gameView.Repaint();
            var check = UnityEngine.Object.FindObjectOfType<ClosedLoopRuntimeCheck>();
            if (check != null && check.Completed) Exit(check.Passed ? 0 : 1);
        }
        static void EnsureGameView()
        {
            if (_gameView == null)
            {
                Type type = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
                _gameView = EditorWindow.GetWindow(type, false, "Game", false);
                _gameView.minSize = new Vector2(960, 600);
                _gameView.position = new Rect(0, 0, 1280, 760);
            }
        }
        static void Exit(int code)
        {
            SessionState.SetBool(Key, false);
            EditorApplication.update -= Update;
            EditorApplication.Exit(code);
        }
    }
}
