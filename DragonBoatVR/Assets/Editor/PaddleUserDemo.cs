using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DragonBoatVR.EditorTools
{
    [InitializeOnLoad]
    public static class PaddleUserDemo
    {
        const string Key = "DragonBoatVR.PaddleUserDemo";
        static PaddleUserSimulator _simulator;
        static double _started;
        static EditorWindow _gameView;
        static object _sizeGroup;
        static int _temporarySize = -1, _previousSize;
        static PaddleUserDemo()
        {
            if (SessionState.GetBool(Key, false)) EditorApplication.update += Update;
        }
        public static void Run()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(arguments, "-paddleArtifacts");
            if (index < 0 || index + 1 >= arguments.Length) throw new ArgumentException("-paddleArtifacts is required.");
            SessionState.SetString(Key + ".Artifacts", Path.GetFullPath(arguments[index + 1]));
            SessionState.SetBool(Key, true);
            EditorSceneManager.OpenScene("Assets/Scenes/Demo.unity", OpenSceneMode.Single);
            var bridge = UnityEngine.Object.FindObjectOfType<HardwareUdpBridge>();
            if (bridge == null) throw new InvalidOperationException("Demo is missing its paddle bridge.");
            bridge.listenPort = FreePort(); bridge.remotePort = FreePort(); bridge.remoteHost = "127.0.0.1";
            var cable = UnityEngine.Object.FindObjectOfType<CableDriveTwin>(); if (cable != null) cable.sendUdp = false;
            var motion = UnityEngine.Object.FindObjectOfType<MotionTelemetry>(); if (motion != null) motion.enabled = false;
            EditorApplication.update -= Update; EditorApplication.update += Update;
            // Unsaved scene changes affect this dedicated process only.
            EditorApplication.EnterPlaymode();
        }
        static int FreePort()
        {
            using (var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
                return ((IPEndPoint)socket.Client.LocalEndPoint).Port;
        }
        static void Update()
        {
            if (!SessionState.GetBool(Key, false)) return;
            if (!Application.isPlaying) return;
            if (_simulator == null)
            {
                ConfigureGameView();
                _simulator = new GameObject("DevelopmentSimulatedUser").AddComponent<PaddleUserSimulator>();
                _simulator.artifactsDirectory = SessionState.GetString(Key + ".Artifacts", "");
                _started = EditorApplication.timeSinceStartup;
            }
            EditorApplication.QueuePlayerLoopUpdate();
            if (_gameView != null) _gameView.Repaint();
            if (_simulator.Completed || EditorApplication.timeSinceStartup - _started > 90f)
            {
                bool pass = _simulator.Completed && _simulator.Passed;
                SessionState.SetBool(Key, false);
                EditorApplication.update -= Update;
                RestoreGameView();
                EditorApplication.Exit(pass ? 0 : 1);
            }
        }
        static void ConfigureGameView()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type type = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
            _gameView = EditorWindow.GetWindow(type, false, "Game", false);
            Type sizes = type.Assembly.GetType("UnityEditor.GameViewSizes");
            object instance = sizes.BaseType.GetProperty("instance", BindingFlags.Public | BindingFlags.Static).GetValue(null);
            _sizeGroup = sizes.GetProperty("currentGroup", flags).GetValue(instance);
            _temporarySize = (int)_sizeGroup.GetType().GetMethod("GetTotalCount", flags).Invoke(_sizeGroup, null);
            Type sizeType = type.Assembly.GetType("UnityEditor.GameViewSize");
            Type kind = type.Assembly.GetType("UnityEditor.GameViewSizeType");
            object size = Activator.CreateInstance(sizeType, flags, null,
                new object[] { Enum.ToObject(kind, 1), 1280, 720, "Temporary paddle demo 1280x720" }, null);
            _sizeGroup.GetType().GetMethod("AddCustomSize", flags).Invoke(_sizeGroup, new[] { size });
            PropertyInfo selected = type.GetProperty("selectedSizeIndex", flags);
            _previousSize = (int)selected.GetValue(_gameView); selected.SetValue(_gameView, _temporarySize);
            _gameView.minSize = new Vector2(1280, 750); _gameView.position = new Rect(0, 0, 1280, 750);
            _gameView.Repaint();
        }
        static void RestoreGameView()
        {
            if (_sizeGroup == null || _temporarySize < 0) return;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            _gameView.GetType().GetProperty("selectedSizeIndex", flags).SetValue(_gameView, _previousSize);
            _sizeGroup.GetType().GetMethod("RemoveCustomSize", flags).Invoke(_sizeGroup, new object[] { _temporarySize });
        }
    }
}
