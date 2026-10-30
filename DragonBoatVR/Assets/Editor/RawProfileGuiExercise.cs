using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace DragonBoatVR.EditorTools
{
    /// <summary>Queue real game input and observe normal IMGUI repaints. No action or
    /// text value is assigned directly; Tick never invokes OnGUI or advances physics.</summary>
    public sealed class RawProfileGuiExercise
    {
        enum Step { Acquisition, AcquisitionExpanded, RawExpanded, SelectAll, Clear, Type, Load, Loaded }

        [Serializable] public sealed class GuiEventEvidence
        {
            public string action, eventType, keyCode, observedPath;
            public int character, queuedFrame, queuedRepaint, observedFrame, observedRepaint;
            public Vector2 gamePosition, wheelDelta;
            public double queuedEditorSeconds;
        }

        public bool Completed { get; private set; }
        public string Failure { get; private set; }
        public int EventsSent { get; private set; }
        public string EnteredPath { get; private set; }
        public string LoadedSource { get; private set; }
        public int LoadedPort { get; private set; }
        public readonly List<GuiEventEvidence> Events = new List<GuiEventEvidence>();

        RideHud _hud;
        string _path, _source;
        int _port, _characterIndex, _waitRepaint;
        double _started;
        bool _begun, _mouseUpPending;
        Vector2 _mousePosition;
        string _clickAction;
        Step _step, _afterClick;
        GuiEventEvidence _lastEvent;

        public RawProfileGuiExercise() { }
        public RawProfileGuiExercise(RideHud hud, string path, string expectedSource, int expectedPort)
        { Begin(hud, path, expectedSource, expectedPort); }

        public void Begin(RideHud hud, string path, string expectedSource, int expectedPort)
        {
            if (_begun) throw new InvalidOperationException("Use a new GUI exercise for each attempt.");
            if (hud == null) throw new ArgumentNullException(nameof(hud));
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("An explicit profile path is required.", nameof(path));
            if (string.IsNullOrEmpty(expectedSource)) throw new ArgumentException("An expected source is required.", nameof(expectedSource));
            if (expectedPort < 1 || expectedPort > 65535) throw new ArgumentOutOfRangeException(nameof(expectedPort));
            _begun = true; _hud = hud; _path = path; _source = expectedSource; _port = expectedPort;
            _started = EditorApplication.timeSinceStartup;
            _waitRepaint = hud.EditorGuiRepaintCount + 1;
            _step = Step.Acquisition;
        }

        public void Tick()
        {
            if (!_begun) throw new InvalidOperationException("Begin must precede Tick.");
            if (Completed) return;
            try
            {
                if (!Application.isPlaying || _hud == null || !_hud.isActiveAndEnabled)
                    throw new InvalidOperationException("GUI exercise requires an active HUD in normal Play.");
                EnteredPath = _hud.rawAcquisitionProfilePath;
                if (EditorApplication.timeSinceStartup - _started > 10)
                    throw new TimeoutException("Ten-second raw profile GUI deadline at " + _step
                        + "; events=" + EventsSent + "; path=" + EnteredPath);
                if (_hud.EditorGuiRepaintCount < _waitRepaint) return;
                if (_lastEvent != null && _lastEvent.observedRepaint == 0)
                {
                    _lastEvent.observedRepaint = _hud.EditorGuiRepaintCount;
                    _lastEvent.observedFrame = Time.frameCount;
                    _lastEvent.observedPath = EnteredPath;
                }
                if (_mouseUpPending)
                {
                    _mouseUpPending = false;
                    Queue(new Event { type = EventType.MouseUp, button = 0, clickCount = 1, mousePosition = _mousePosition },
                        _clickAction + "/up", 2);
                    _step = _afterClick;
                    return;
                }
                switch (_step)
                {
                    case Step.Acquisition:
                        Click("acquisition", false, Step.AcquisitionExpanded);
                        break;
                    case Step.AcquisitionExpanded:
                        if (!_hud.detailsExpanded) throw new InvalidOperationException("The four-channel button did not expand HUD details.");
                        if (_hud.EditorRawProfileExpanded) _step = Step.RawExpanded;
                        else Click("rawExpand", true, Step.RawExpanded);
                        break;
                    case Step.RawExpanded:
                        if (!_hud.EditorRawProfileExpanded) throw new InvalidOperationException("The raw data button did not open its controls.");
                        Click("rawPath", true, Step.SelectAll);
                        break;
                    case Step.SelectAll:
                        Queue(new Event { type = EventType.KeyDown, keyCode = KeyCode.A,
                            modifiers = EventModifiers.Control, character = '\0' }, "path/select-all");
                        _step = Step.Clear;
                        break;
                    case Step.Clear:
                        Queue(new Event { type = EventType.KeyDown, keyCode = KeyCode.Backspace, character = '\b' }, "path/clear");
                        _step = Step.Type;
                        break;
                    case Step.Type:
                        string prefix = _path.Substring(0, _characterIndex);
                        if (!string.Equals(EnteredPath ?? "", prefix, StringComparison.Ordinal))
                            throw new InvalidOperationException("IMGUI text input differs after character " + _characterIndex
                                + "; expected=" + prefix + "; observed=" + EnteredPath);
                        if (_characterIndex == _path.Length) { _step = Step.Load; break; }
                        char character = _path[_characterIndex++];
                        // character preserves UTF-16, including the Chinese workspace path.
                        Queue(new Event { type = EventType.KeyDown, keyCode = KeyCode.None, character = character },
                            "path/character-" + _characterIndex);
                        break;
                    case Step.Load:
                        if (!string.Equals(EnteredPath, _path, StringComparison.Ordinal))
                            throw new InvalidOperationException("The full entered path changed before the load click.");
                        Click("rawLoad", true, Step.Loaded);
                        break;
                    case Step.Loaded:
                        var session = _hud.session;
                        LoadedSource = session?.acquisition?.CurrentProfile?.sourceId;
                        LoadedPort = session?.acquisition?.LocalPort ?? 0;
                        if (!string.Equals(EnteredPath, _path, StringComparison.Ordinal)
                            || !string.Equals(LoadedSource, _source, StringComparison.Ordinal) || LoadedPort != _port)
                            throw new InvalidOperationException("The real load button did not publish the expected profile; source="
                                + LoadedSource + "; port=" + LoadedPort + "; status=" + session?.RawAcquisitionProfileStatus);
                        Completed = true;
                        break;
                }
            }
            catch (Exception error) { Failure = error.ToString(); Completed = true; }
        }

        void Click(string control, bool insideScroll, Step afterClick)
        {
            if (!_hud.EditorControlScreenRects.TryGetValue(control, out Rect target)) return;
            Rect game = _hud.EditorGameScreenRect;
            Rect visible = insideScroll ? Intersection(_hud.EditorScrollViewportScreenRect, game) : game;
            if (!Usable(game) || !Usable(visible) || !Usable(target)) return;
            // Do not click a clipped edge or a different control under the same screen point.
            if (target.center.x < visible.xMin + 3 || target.center.x > visible.xMax - 3
                || target.center.y < visible.yMin + 5 || target.center.y > visible.yMax - 5)
            {
                if (!insideScroll) return;
                float distance = target.center.y < visible.yMin + 5
                    ? target.center.y - visible.yMin - 15 : target.center.y - visible.yMax + 15;
                float wheel = Mathf.Sign(distance) * Mathf.Clamp(Mathf.Abs(distance) / 30f, 1f, 8f);
                Queue(new Event { type = EventType.ScrollWheel, mousePosition = GamePoint(visible.center, game),
                    delta = new Vector2(0, wheel) }, "scroll-to/" + control, 2);
                return;
            }
            _mousePosition = GamePoint(target.center, game);
            _mouseUpPending = true; _afterClick = afterClick; _clickAction = "click/" + control;
            Queue(new Event { type = EventType.MouseDown, button = 0, clickCount = 1, mousePosition = _mousePosition },
                _clickAction + "/down");
        }

        void Queue(Event input, string action, int repaints = 1)
        {
            input.displayIndex = 0;
            var evidence = new GuiEventEvidence { action = action, eventType = input.type.ToString(),
                keyCode = input.keyCode.ToString(), character = input.character, gamePosition = input.mousePosition,
                wheelDelta = input.delta,
                queuedEditorSeconds = EditorApplication.timeSinceStartup, queuedFrame = Time.frameCount,
                queuedRepaint = _hud.EditorGuiRepaintCount };
            // This public Editor API queues runtime game events. The following normal
            // PlayerLoop handles them; it does not send events to the Editor toolbar.
            EditorGUIUtility.QueueGameViewInputEvent(input);
            Events.Add(evidence); _lastEvent = evidence; EventsSent++;
            _waitRepaint = _hud.EditorGuiRepaintCount + repaints;
        }

        static Vector2 GamePoint(Vector2 screenPoint, Rect game)
        {
            return new Vector2((screenPoint.x - game.xMin) * Screen.width / game.width,
                (screenPoint.y - game.yMin) * Screen.height / game.height);
        }
        static bool Usable(Rect rect) => Finite(rect.x) && Finite(rect.y) && Finite(rect.width)
            && Finite(rect.height) && rect.width > 0 && rect.height > 0;
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static Rect Intersection(Rect a, Rect b)
        {
            float left = Mathf.Max(a.xMin, b.xMin), right = Mathf.Min(a.xMax, b.xMax);
            float top = Mathf.Max(a.yMin, b.yMin), bottom = Mathf.Min(a.yMax, b.yMax);
            return right > left && bottom > top ? Rect.MinMaxRect(left, top, right, bottom) : new Rect();
        }
    }
}
