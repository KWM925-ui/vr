using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace DragonBoatVR
{
    /// <summary>
    /// Opt-in desktop exhibition shell (-presentation). uGUI owns visible
    /// controls; DemoExperienceController owns user commands. Camera and physics
    /// domains retain their existing contracts.
    /// </summary>
    public sealed class DemoPresentationUi : MonoBehaviour
    {
        public enum Page { None, Home, Settings, Help, Data }
        public static DemoPresentationUi Instance { get; private set; }
        public static bool Active => Instance != null && Instance.isActiveAndEnabled
            && !UnityEngine.XR.XRSettings.enabled;
        public static bool BlocksCameraInput => Active && (Instance._page != Page.None || PointerOverControls);
        public static bool PointerOverControls => Active && EventSystem.current != null
            && EventSystem.current.IsPointerOverGameObject();

        static readonly Color Ink = Hex(0x102D34), Panel = Hex(0x153B43), Muted = Hex(0xABC4C4);
        static readonly Color White = Hex(0xF1F5EE), Gold = Hex(0xE2BE7F), Teal = Hex(0x69C9B4);
        static readonly Color Border = Hex(0x31545B);
        static readonly string[] GuideTitles = {
            "两艘船，两种风格", "水况与河景", "划桨、推进与惯性",
            "走进船舱，环绕整船", "同步声画与舒适选项", "观察完整反馈链路", "结束与重新开始"
        };
        static readonly string[] GuideBodies = {
            "选择经典木船或雕饰龙舟。每艘船保留自己的外形、桨位与船舱空间，切船会重新建立体验。",
            "选择湘江或逢简水乡，再比较平静与有风水面。浪、船体起伏和水声随同一物理过程变化。",
            "观察真实模拟桨的入水、划水与出水。暂停划桨后，波浪继续、船仍可惯性滑行；这与结束体验不同。",
            "左窗：右键转头，WASD或方向键在船内移动，R回到划桨位。\n右窗：左键拖动环绕，滚轮调整距离。",
            "观察泡沫、水花、尾迹和水声。舒适选项只调节周边视野，默认关闭；它不会改变船体运动或平台目标。",
            "查看输入、水动力、三绳和平台请求与回执。当前设备执行为软件模拟；目标、回执和实际观测分别保留来源。",
            "结束体验会停止当前闭环并撤销输出。重新开始会建立新会话、重置航程统计并回到划桨位置。"
        };

        DemoExperienceController _commands;
        Canvas _canvas;
        RectTransform _root, _content;
        Font _font;
        Texture2D _roundTexture, _shadeTexture, _legacyTexture;
        Sprite _roundSprite;
        GUIStyle _legacyStyle;
        Page _page = Page.Home;
        bool _guide, _dirty = true;
        int _guideStep, _width, _height;
        double _nextRefresh, _toastUntil, _nextEvidence;
        string _toast, _evidenceDirectory;
        bool _lastActive, _lastMetrics;
        Text _statusText, _contextText, _paddlingText, _endText, _metricText, _speedText, _headingText;
        readonly List<Text> _dataText = new List<Text>();
        readonly Dictionary<string, RectTransform> _controls = new Dictionary<string, RectTransform>();
        readonly Dictionary<string, Button> _buttons = new Dictionary<string, Button>();
        readonly List<UiAction> _actions = new List<UiAction>();

        [Serializable] public sealed class ControlObservation
        { public string name; public Rect rect; public bool interactable; }
        [Serializable] public sealed class UiAction
        { public double realtime; public string name, value; }
        [Serializable] public sealed class UiObservation
        {
            public string page, boat, river, water, inputMode, controllerSessionId;
            public bool guideVisible, experienceActive, paddling, comfort, metrics, diagnostics, controllerReady, feedbackFresh;
            public int guideStep, screenWidth, screenHeight;
            public double realtime, simulationTime;
            public float currentSpeedMps, currentHeadingDegrees;
            public string source = "Observed uGUI layout and existing domain state; no injected commands";
            public List<ControlObservation> controls = new List<ControlObservation>();
            public List<UiAction> actions;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void StartIfRequested()
        {
            if (Application.isEditor || Instance != null
                || Array.IndexOf(System.Environment.GetCommandLineArgs(), "-presentation") < 0) return;
            var root = new GameObject("DragonBoat presentation");
            DontDestroyOnLoad(root);
            root.AddComponent<DemoExperienceController>();
            root.AddComponent<DemoPresentationUi>();
        }

        void Awake()
        {
            Instance = this;
            _commands = GetComponent<DemoExperienceController>();
            _commands.SceneReady += SceneReady;
            _commands.ActionInvoked += CommandObserved;
            _font = Font.CreateDynamicFontFromOSFont(
                new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Noto Sans SC", "Segoe UI", "Arial" }, 18);
            _roundTexture = RoundedTexture(Color.white);
            _roundSprite = Sprite.Create(_roundTexture, new Rect(0, 0, 64, 64), new Vector2(.5f, .5f),
                100f, 0, SpriteMeshType.FullRect, new Vector4(18, 18, 18, 18));
            _shadeTexture = new Texture2D(1, 64, TextureFormat.RGBA32, false);
            for (int y = 0; y < 64; y++)
                _shadeTexture.SetPixel(0, y, new Color(.025f, .09f, .11f, Mathf.Lerp(.91f, .13f, y / 63f)));
            _shadeTexture.Apply();
            _shadeTexture.wrapMode = TextureWrapMode.Clamp;
            var canvasObject = new GameObject("Exhibition canvas", typeof(RectTransform), typeof(Canvas),
                typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(transform, false);
            _canvas = canvasObject.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 40;
            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1600, 900);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = .5f;
            _root = canvasObject.GetComponent<RectTransform>();
            if (EventSystem.current == null)
            {
                var events = new GameObject("Presentation input", typeof(EventSystem), typeof(StandaloneInputModule));
                events.transform.SetParent(transform, false);
            }
            var args = System.Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "-presentationEvidence");
            if (index >= 0 && index + 1 < args.Length)
            {
                _evidenceDirectory = Path.GetFullPath(args[index + 1]);
                Directory.CreateDirectory(_evidenceDirectory);
            }
        }

        void SceneReady()
        {
            SynchronizePanel();
            _dirty = true;
        }

        void CommandObserved(string name, string value)
        {
            _actions.Add(new UiAction { realtime = Time.realtimeSinceStartupAsDouble, name = name, value = value });
            if (!string.IsNullOrEmpty(_commands.Status)) Toast(_commands.Status);
            // Keep the same slider alive throughout an actual pointer drag.
            if (name != "current") _dirty = true;
        }

        void SynchronizePanel()
        {
            if (_commands.Environment != null)
                _commands.Environment.SetPresentationPanelVisible(_page != Page.None);
        }

        public void ToggleSettings() => SetPage(_page == Page.Settings ? Page.None : Page.Settings);

        public void SetPage(Page page)
        {
            _page = page;
            if (page != Page.None && _commands.Hud != null && _commands.Hud.visible)
                _commands.SetDiagnosticsVisible(false);
            SynchronizePanel();
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
            _actions.Add(new UiAction { realtime = Time.realtimeSinceStartupAsDouble, name = "page", value = page.ToString() });
            _dirty = true;
        }

        public void StartGuide()
        {
            if ((_page == Page.Home || !_commands.ExperienceActive) && !_commands.BeginExperience()) return;
            _guide = true; _guideStep = 0;
            SetPage(Page.None);
            _actions.Add(new UiAction { realtime = Time.realtimeSinceStartupAsDouble, name = "guide", value = "begin" });
        }

        void Begin()
        {
            if (_commands.BeginExperience()) { _guide = false; SetPage(Page.None); }
        }

        void Update()
        {
            _canvas.enabled = !UnityEngine.XR.XRSettings.enabled;
            if (!_canvas.enabled) return;
            if (Screen.width != _width || Screen.height != _height)
            { _width = Screen.width; _height = Screen.height; _dirty = true; }
            if (Input.GetKeyDown(KeyCode.H)) SetPage(_page == Page.Help ? Page.None : Page.Help);
            if (Input.GetKeyDown(KeyCode.Escape) && _page != Page.None) SetPage(Page.None);
            if (Input.GetKeyDown(KeyCode.Return) && _page == Page.Home) Begin();
            if (RideHud.DiagnosticsVisible && _page == Page.Data) SetPage(Page.None);
            if (_dirty) { _dirty = false; Rebuild(); }
            double now = Time.realtimeSinceStartupAsDouble;
            if (now >= _nextRefresh) { _nextRefresh = now + .1; Refresh(); }
            if (_evidenceDirectory != null && now >= _nextEvidence)
            { _nextEvidence = now + .25; WriteObservation(); }
        }

        void Rebuild()
        {
            if (_content != null) { _content.gameObject.SetActive(false); Destroy(_content.gameObject); }
            _controls.Clear(); _buttons.Clear(); _dataText.Clear();
            _statusText = _contextText = _paddlingText = _endText = _metricText = _speedText = _headingText = null;
            _content = RectNode(_root, "Presentation content", new Rect(0, 0, _root.rect.width, _root.rect.height));
            float width = _root.rect.width, height = _root.rect.height;
            if (width < 10f || height < 10f) { _dirty = true; return; }
            BuildBars(width, height);
            if (_page == Page.None)
            {
                if (_guide && !RideHud.DiagnosticsVisible) BuildGuide(height);
                if (!_commands.ExperienceActive && _commands.Ready) BuildEnded(width, height);
            }
            else
            {
                PanelAt(_content, "Modal shade", new Rect(0, 0, width, height), new Color(.02f, .075f, .09f, .83f), false, true);
                if (_page == Page.Home) BuildHome(width, height);
                else if (_page == Page.Settings) BuildSettings(width, height);
                else if (_page == Page.Help) BuildHelp(width, height);
                else BuildData(width, height);
            }
            if (_toast != null && Time.realtimeSinceStartupAsDouble < _toastUntil)
            {
                float toastWidth = Mathf.Min(660f, width - 48f);
                var panel = PanelAt(_content, "Message", new Rect((width - toastWidth) / 2f, height - 138f, toastWidth, 42f), Panel);
                Label(panel, "Message text", new Rect(16, 4, toastWidth - 32, 34), _toast, 14, Gold, TextAnchor.MiddleCenter);
            }
            Refresh();
        }

        void BuildBars(float width, float height)
        {
            var header = PanelAt(_content, "Header", new Rect(0, 0, width, 72), new Color(.03f, .12f, .145f, .92f), false);
            Label(header, "Brand", new Rect(28, 12, 84, 46), "龙舟", 28, White);
            Label(header, "Edition", new Rect(112, 19, 196, 30), "D R A G O N B O A T", 12, Gold);
            _contextText = Label(header, "Current experience", new Rect(338, 20, Mathf.Max(160, width - 840), 30), "", 15, Muted);
            ButtonAt(header, "home", new Rect(width - 464, 17, 66, 38), "首页", () => SetPage(Page.Home), Panel);
            ButtonAt(header, "metrics", new Rect(width - 388, 17, 88, 38), "数据 F1",
                () => { _commands.SetMetricsVisible(!_commands.MetricsVisible); _dirty = true; }, Panel);
            _statusText = Label(header, "Connection status", new Rect(width - 280, 20, 256, 30), "", 13, Teal, TextAnchor.MiddleRight);
            float mainWidth = width * .68f;
            var split = FindObjectOfType<SplitViewController>();
            if (split != null) mainWidth = width * Mathf.Clamp(split.mainWidthRatio, .4f, .9f);
            Label(_content, "Cabin label", new Rect(24, 87, 260, 28), "01  舱内观察", 12, White);
            Label(_content, "Outside label", new Rect(mainWidth + 20, 87, 240, 28), "02  船外环绕", 12, White);
            float toolbarWidth = Mathf.Min(1096, width - 48), gap = 8;
            var toolbar = PanelAt(_content, "Action bar", new Rect((width - toolbarWidth) / 2f, height - 82, toolbarWidth, 62), new Color(.035f, .14f, .16f, .97f), true, true);
            float cell = (toolbarWidth - 24 - gap * 5) / 6f;
            ButtonAt(toolbar, "settings", new Rect(12, 8, cell, 46), "选船与水况  F2", ToggleSettings, Panel);
            ButtonAt(toolbar, "guide", new Rect(12 + (cell + gap), 8, cell, 46), "演示导览", StartGuide, Panel);
            ButtonAt(toolbar, "help", new Rect(12 + (cell + gap) * 2, 8, cell, 46), "操作说明  H", () => SetPage(Page.Help), Panel);
            var paddling = ButtonAt(toolbar, "paddling", new Rect(12 + (cell + gap) * 3, 8, cell, 46), "",
                () => _commands.SetPaddling(!_commands.Paddling), Panel);
            _paddlingText = paddling.GetComponentInChildren<Text>();
            ButtonAt(toolbar, "seat-reset", new Rect(12 + (cell + gap) * 4, 8, cell, 46), "回到桨位  R", () => _commands.ResetSeatView(), Panel);
            var end = ButtonAt(toolbar, "end", new Rect(12 + (cell + gap) * 5, 8, cell, 46), "",
                () => { if (_commands.ExperienceActive) _commands.EndExperience(); else _commands.RepositionExperience(); _dirty = true; }, Panel);
            _endText = end.GetComponentInChildren<Text>();
            if (_commands.MetricsVisible && _page == Page.None)
            {
                var metrics = PanelAt(_content, "Compact sailing data", new Rect(24, 123, 402, 49), new Color(.025f, .13f, .15f, .9f));
                _metricText = Label(metrics, "Sailing values", new Rect(16, 7, 370, 35), "", 15, White);
            }
        }

        void BuildHome(float width, float height)
        {
            float cardWidth = Mathf.Min(1360, width - 64), cardHeight = Mathf.Min(746, height - 78);
            var card = PanelAt(_content, "Welcome", new Rect((width - cardWidth) / 2f, (height - cardHeight) / 2f, cardWidth, cardHeight), Ink, true, true);
            float heroWidth = cardWidth * .44f, innerHeight = cardHeight - 40;
            var hero = PanelAt(card, "River portrait", new Rect(20, 20, heroWidth, innerHeight), Panel);
            hero.gameObject.AddComponent<RectMask2D>();
            var raw = new GameObject("Actual boat portrait", typeof(RectTransform), typeof(RawImage));
            var portrait = raw.GetComponent<RectTransform>(); portrait.SetParent(hero, false); Stretch(portrait);
            raw.GetComponent<RawImage>().texture = Resources.Load<Texture2D>("Presentation/" +
                (_commands.BoatChoice != null && _commands.BoatChoice.modelId == BoatModelChoice.Ornament ? "ornament" : "majesty"));
            raw.GetComponent<RawImage>().raycastTarget = false;
            var portraitTexture = raw.GetComponent<RawImage>().texture;
            if (portraitTexture != null)
            {
                var fit = raw.AddComponent<AspectRatioFitter>();
                fit.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
                fit.aspectRatio = portraitTexture.width / (float)portraitTexture.height;
            }
            var gradient = new GameObject("Portrait shade", typeof(RectTransform), typeof(RawImage));
            var shade = gradient.GetComponent<RectTransform>(); shade.SetParent(hero, false); Stretch(shade);
            gradient.GetComponent<RawImage>().texture = _shadeTexture; gradient.GetComponent<RawImage>().raycastTarget = false;
            Label(hero, "Welcome eyebrow", new Rect(30, 34, heroWidth - 60, 25), "DRAGONBOAT  /  INTERACTIVE DEMO", 12, Gold);
            Label(hero, "Welcome title", new Rect(28, 84, heroWidth - 56, 94), "沿水而行", 52, White);
            Label(hero, "Welcome description", new Rect(32, 187, heroWidth - 64, 62), "从一次划桨开始，\n感受水面、船体与声画的共同变化。", 17, White);
            Label(hero, "Welcome features", new Rect(32, innerHeight - 140, heroWidth - 64, 92),
                "双视角观察   /   舱内移动与船外环绕\n同步声画      /   水花、泡沫与水声\n模拟闭环      /   桨、水动力与执行反馈", 14, White);
            float x = heroWidth + 56, areaWidth = cardWidth - x - 36;
            Label(card, "Prepare title", new Rect(x, 36, areaWidth, 43), "准备你的航程", 28, White);
            Label(card, "Prepare subtitle", new Rect(x, 88, areaWidth, 32), "选择船型和水况，然后开始体验。", 14, Muted);
            BuildSelections(card, x, 142, areaWidth);
            float bottom = cardHeight - 132;
            ButtonAt(card, "begin", new Rect(x, bottom, areaWidth, 54), "开始体验  →", Begin, Teal, Ink, 18);
            ButtonAt(card, "begin-guide", new Rect(x, bottom + 66, areaWidth, 43), "按步骤演示全部功能", StartGuide, Panel, White, 15);
        }

        void BuildSelections(RectTransform parent, float x, float y, float width)
        {
            float half = (width - 12) / 2f;
            Label(parent, "Boat selection label", new Rect(x, y, width, 22), "船型", 13, Gold);
            ButtonAt(parent, "boat-majesty", new Rect(x, y + 34, half, 70), "经典木船\n木纹船体 · 原始体验",
                () => _commands.SelectBoat(BoatModelChoice.Majesty), Panel, White, 15);
            ButtonAt(parent, "boat-ornament", new Rect(x + half + 12, y + 34, half, 70), "雕饰龙舟\n龙头雕饰 · 独立船舱",
                () => _commands.SelectBoat(BoatModelChoice.Ornament), Panel, White, 15);
            Label(parent, "River selection label", new Rect(x, y + 128, width, 22), "河景", 13, Gold);
            ButtonAt(parent, "river-xiangjiang", new Rect(x, y + 162, half, 58), "湘江 · 橘子洲",
                () => _commands.SelectRiver(RiverEnvironmentController.SceneProfile.XiangjiangOrangeIsland), Panel);
            ButtonAt(parent, "river-shunde", new Rect(x + half + 12, y + 162, half, 58), "顺德 · 逢简水乡",
                () => _commands.SelectRiver(RiverEnvironmentController.SceneProfile.ShundeFengjian), Panel);
            Label(parent, "Water selection label", new Rect(x, y + 244, width, 22), "水况", 13, Gold);
            ButtonAt(parent, "water-calm", new Rect(x, y + 278, half, 47), "平静水面",
                () => _commands.SelectWater(RiverEnvironmentController.WaterCondition.Calm), Panel);
            ButtonAt(parent, "water-windy", new Rect(x + half + 12, y + 278, half, 47), "有风水面",
                () => _commands.SelectWater(RiverEnvironmentController.WaterCondition.Windy), Panel);
            ButtonAt(parent, "comfort", new Rect(x, y + 342, width, 44),
                ComfortVignette.UserEnabled ? "✓  舒适模式已开启 · 周边视野收缩" : "○  舒适模式关闭 · 保留完整视野",
                () => _commands.SetComfort(!ComfortVignette.UserEnabled), Panel, Muted, 14);
        }

        RectTransform ModalCard(float width, float height, float desiredWidth, float desiredHeight, string title, string subtitle)
        {
            float cw = Mathf.Min(desiredWidth, width - 64), ch = Mathf.Min(desiredHeight, height - 78);
            var card = PanelAt(_content, title, new Rect((width - cw) / 2, (height - ch) / 2, cw, ch), Ink, true, true);
            Label(card, title + " heading", new Rect(34, 24, cw - 120, 46), title, 28, White);
            Label(card, title + " subtitle", new Rect(36, 79, cw - 72, 44), subtitle, 14, Muted);
            ButtonAt(card, "close", new Rect(cw - 74, 26, 40, 40), "×", () => SetPage(Page.None), Panel, Muted, 24);
            return card;
        }

        void BuildSettings(float width, float height)
        {
            var card = ModalCard(width, height, 866, 766, "选船与水况", "切船会重新开始体验；水况和舒适选项继续保留。");
            float cw = card.rect.width, aw = cw - 72;
            BuildSelections(card, 36, 142, aw);
            var env = _commands.Environment;
            _speedText = Label(card, "Current speed", new Rect(36, 557, aw * .47f, 24), "", 13, Muted);
            _headingText = Label(card, "Current heading", new Rect(36 + aw * .53f, 557, aw * .47f, 24), "", 13, Muted);
            SliderAt(card, "current-speed", new Rect(36, 589, aw * .47f, 28), 0, env != null ? env.MaximumCurrentSpeedMps : .35f,
                env != null ? env.TargetCurrentSpeedMps : .08f,
                value => _commands.SetCurrent(value, _commands.Environment.TargetCurrentHeadingDegrees));
            SliderAt(card, "current-heading", new Rect(36 + aw * .53f, 589, aw * .47f, 28), 0, 359.9f,
                env != null ? env.TargetCurrentHeadingDegrees : 0,
                value => _commands.SetCurrent(_commands.Environment.TargetCurrentSpeedMps, value));
            ButtonAt(card, "settings-done", new Rect(36, card.rect.height - 87, aw, 49), "返回体验", () => SetPage(Page.None), Teal, Ink, 17);
        }

        void BuildGuide(float height)
        {
            float top = _commands.MetricsVisible ? 187 : 130;
            var card = PanelAt(_content, "Guided demonstration", new Rect(24, top, 418, Mathf.Min(426, height - top - 100)), Ink, true, true);
            Label(card, "Guide progress", new Rect(24, 19, 306, 26), $"演示导览  {_guideStep + 1:00} / {GuideTitles.Length:00}", 12, Gold);
            ButtonAt(card, "guide-close", new Rect(366, 14, 34, 34), "×", () => { _guide = false; _dirty = true; }, Panel, Muted, 21);
            PanelAt(card, "Guide track", new Rect(24, 57, 370, 3), Border, false);
            PanelAt(card, "Guide completed", new Rect(24, 57, 370f * (_guideStep + 1) / GuideTitles.Length, 3), Teal, false);
            Label(card, "Guide title", new Rect(24, 78, 370, 54), GuideTitles[_guideStep], 23, White);
            Label(card, "Guide body", new Rect(24, 148, 370, 124), GuideBodies[_guideStep], 15, Muted);
            var actions = GuideActions();
            ButtonAt(card, "guide-action-a", new Rect(24, 283, 179, 42), actions.Item1, actions.Item2, Panel, Teal, 14);
            ButtonAt(card, "guide-action-b", new Rect(215, 283, 179, 42), actions.Item3, actions.Item4, Panel, Teal, 14);
            var previous = ButtonAt(card, "guide-previous", new Rect(24, 344, 112, 43), "上一步", () => AdvanceGuide(-1), Panel, Muted, 14);
            previous.interactable = _guideStep > 0;
            ButtonAt(card, "guide-next", new Rect(148, 344, 246, 43),
                _guideStep == GuideTitles.Length - 1 ? "完成导览  ✓" : "下一步  →", () => AdvanceGuide(1), Teal, Ink, 15);
        }

        Tuple<string, Action, string, Action> GuideActions()
        {
            switch (_guideStep)
            {
                case 0: return Tuple.Create<string, Action, string, Action>("选择船型", ToggleSettings, "查看操作说明", () => SetPage(Page.Help));
                case 1: return Tuple.Create<string, Action, string, Action>("切换到有风", () => _commands.SelectWater(RiverEnvironmentController.WaterCondition.Windy),
                    "切换到平静", () => _commands.SelectWater(RiverEnvironmentController.WaterCondition.Calm));
                case 2: return Tuple.Create<string, Action, string, Action>("暂停划桨", () => _commands.SetPaddling(false), "恢复划桨", () => _commands.SetPaddling(true));
                case 3: return Tuple.Create<string, Action, string, Action>("回到划桨位", () => _commands.ResetSeatView(), "查看移动方法", () => SetPage(Page.Help));
                case 4: return Tuple.Create<string, Action, string, Action>("开启舒适模式", () => _commands.SetComfort(true), "关闭舒适模式", () => _commands.SetComfort(false));
                case 5: return Tuple.Create<string, Action, string, Action>("查看反馈链路", () => SetPage(Page.Data), "查看原始采集", ShowAcquisition);
                default: return Tuple.Create<string, Action, string, Action>("结束体验", () => _commands.EndExperience(), "重新开始", () => _commands.RepositionExperience());
            }
        }

        void AdvanceGuide(int delta)
        {
            if (_guideStep + delta >= GuideTitles.Length) _guide = false;
            else _guideStep = Mathf.Clamp(_guideStep + delta, 0, GuideTitles.Length - 1);
            _actions.Add(new UiAction { realtime = Time.realtimeSinceStartupAsDouble, name = "guide-step", value = _guideStep.ToString() });
            _dirty = true;
        }

        void BuildHelp(float width, float height)
        {
            var card = ModalCard(width, height, 1136, 740, "怎样体验与演示", "常用操作都有按钮；快捷键可以随时配合使用。");
            float left = card.rect.width * .54f, rightWidth = card.rect.width - left - 62;
            string[,] rows = {
                { "开始与选择", "首页选船和水况，点击开始；F2打开设置。" },
                { "船内移动", "左窗右键转头，WASD或方向键移动；R回位。" },
                { "船外环绕", "右窗左键拖动环绕，滚轮调整距离。" },
                { "暂停划桨", "F8停止／恢复划桨意图，波浪与船体继续。" },
                { "运行数据", "F1显示航行数据；反馈链路有独立查看入口。" },
                { "结束与恢复", "F10结束体验；F11重新定位并开始新会话。" },
            };
            for (int i = 0; i < rows.GetLength(0); i++)
            {
                float y = 148 + i * 66;
                Label(card, "Help label " + i, new Rect(36, y, 116, 25), rows[i, 0], 15, Gold);
                Label(card, "Help detail " + i, new Rect(166, y, left - 192, 48), rows[i, 1], 14, White);
            }
            var tracking = PanelAt(card, "Head tracking explanation", new Rect(left, 143, rightWidth, 411), Panel);
            Label(tracking, "Tracking title", new Rect(24, 23, rightWidth - 48, 34), "VR怎样跟随实际移动？", 20, White);
            Label(tracking, "Tracking content", new Rect(24, 83, rightWidth - 48, 287),
                "PICO内置环境追踪摄像头与惯性传感器，提供头部的三维位置和朝向。\n\n"
                + "实际移动 → 头部位姿 → 配准与平台补偿 → 虚拟视点更新。\n\n"
                + "真实走动需要对应的实体空间。坐在平台上浏览更远船舱，属于另外的虚拟移动功能。\n\n"
                + "当前桌面用鼠标和键盘代替头动；PICO实机接入仍待完成。", 14, Muted);
            ButtonAt(card, "help-guide", new Rect(36, card.rect.height - 102, left - 64, 49), "开始分步演示", StartGuide, Teal, Ink, 16);
            ButtonAt(card, "help-data", new Rect(left, card.rect.height - 102, rightWidth, 49), "查看运行数据与联调入口", () => SetPage(Page.Data), Panel, White, 15);
        }

        void BuildData(float width, float height)
        {
            var card = ModalCard(width, height, 1188, 754, "运行数据", "读取同一物理链的请求与观测；当前为桌面软件模拟。");
            float gap = 14, cw = (card.rect.width - 72 - 2 * gap) / 3f;
            string[] titles = { "01  桨输入", "02  水动力请求", "03  三绳执行反馈", "04  平台请求与反馈", "05  航行与统计", "06  采样与通信" };
            for (int i = 0; i < titles.Length; i++)
            {
                var cell = PanelAt(card, "Data " + i, new Rect(36 + (cw + gap) * (i % 3), 145 + (i / 3) * 204, cw, 189), Panel);
                Label(cell, "Data heading " + i, new Rect(20, 16, cw - 40, 30), titles[i], 14, Gold);
                _dataText.Add(Label(cell, "Data values " + i, new Rect(20, 60, cw - 40, 112), "", 14, White));
            }
            Label(card, "Acceptance scope", new Rect(36, 565, card.rect.width - 72, 42),
                "三绳保真仍未通过；真实设备、PICO与真人体验待验收。请求、回执与模拟反馈分别显示。", 13, Gold);
            float buttonsWidth = (card.rect.width - 100) / 3f;
            ButtonAt(card, "full-diagnostics", new Rect(36, 625, buttonsWidth, 48), "完整诊断  F9", ShowDiagnostics, Border, White, 15);
            ButtonAt(card, "raw-acquisition", new Rect(50 + buttonsWidth, 625, buttonsWidth, 48), "原始采集  CH1—CH4", ShowAcquisition, Border, White, 15);
            ButtonAt(card, "input-mode", new Rect(64 + buttonsWidth * 2, 625, buttonsWidth, 48),
                _commands.Driver != null && _commands.Driver.mode == PaddleDriver.Mode.VirtualPaddler
                    ? "联调：切换外部UDP输入" : "恢复桌面模拟桨", () => _commands.CycleInputMode(), Border, White, 14);
            Label(card, "External input note", new Rect(36, 694, card.rect.width - 72, 30),
                "外部输入需有效发送器和配准；未收到有效样本时输出失效保护继续生效。", 12, Muted);
        }

        void ShowDiagnostics() { SetPage(Page.None); _guide = false; _commands.SetDiagnosticsVisible(true); _dirty = true; }
        void ShowAcquisition() { SetPage(Page.None); _guide = false; _commands.ShowAcquisitionDetails(); _dirty = true; }

        void BuildEnded(float width, float height)
        {
            var card = PanelAt(_content, "Experience ended", new Rect((width - 432) / 2, (height - 194) / 2, 432, 194), Ink, true, true);
            Label(card, "Ended title", new Rect(28, 25, 376, 35), "本次体验已结束", 24, White);
            Label(card, "Ended description", new Rect(28, 77, 376, 30), "重新定位后，开始一段新的航程。", 14, Muted);
            ButtonAt(card, "restart", new Rect(28, 128, 376, 44), "重新开始  F11",
                () => { _commands.RepositionExperience(); _dirty = true; }, Teal, Ink, 16);
        }

        void Refresh()
        {
            if (_lastActive != _commands.ExperienceActive || _lastMetrics != _commands.MetricsVisible)
            {
                _lastActive = _commands.ExperienceActive;
                _lastMetrics = _commands.MetricsVisible;
                _dirty = true;
            }
            if (_contextText != null) _contextText.text = _commands.BoatLabel + "  /  " + _commands.RiverLabel;
            var frame = _commands.ObservedFrame;
            var loop = frame != null ? frame.closedLoop : null;
            string rig = TendonRigProfiles.CadDesignRequested ? "CAD机构模拟" : "桌面模拟";
            if (_statusText != null)
            {
                bool live = _commands.ExperienceActive && loop != null && loop.ready && loop.feedbackFresh;
                _statusText.text = rig + "  ·  " + (!_commands.ExperienceActive ? "已结束" : live ? "闭环运行中" : "准备／等待输入");
                _statusText.color = live ? Teal : Gold;
            }
            if (_paddlingText != null) _paddlingText.text = _commands.Paddling ? "暂停划桨  F8" : "恢复划桨  F8";
            if (_endText != null) _endText.text = _commands.ExperienceActive ? "结束体验  F10" : "重新开始  F11";
            var hud = _commands.Hud;
            if (_metricText != null)
            {
                _metricText.text = hud == null ? "正在等待航行数据"
                    : $"{hud.BoatSpeedMetersPerSecond:F2} m/s     {hud.Distance:F1} m     {hud.StrokeCount} 桨";
                if (!_commands.MetricsVisible) _dirty = true;
            }
            else if (_page == Page.None && _commands.MetricsVisible) _dirty = true;
            if (_speedText != null && _commands.Environment != null)
                _speedText.text = $"水流速度  {_commands.Environment.TargetCurrentSpeedMps:F2} m/s";
            if (_headingText != null && _commands.Environment != null)
                _headingText.text = $"水流方向  {_commands.Environment.TargetCurrentHeadingDegrees:F0}°";
            foreach (var pair in _buttons)
            {
                if (pair.Key.StartsWith("boat-") || pair.Key.StartsWith("river-") || pair.Key.StartsWith("water-"))
                    pair.Value.interactable = _commands.CanConfigure;
            }
            if (_buttons.TryGetValue("begin", out Button begin)) begin.interactable = _commands.Ready;
            if (_buttons.TryGetValue("begin-guide", out Button beginGuide)) beginGuide.interactable = _commands.Ready;
            if (_buttons.TryGetValue("paddling", out Button paddling)) paddling.interactable = _commands.ExperienceActive
                && _commands.Driver != null && _commands.Driver.mode == PaddleDriver.Mode.VirtualPaddler;
            SelectHighlight("boat-majesty", _commands.BoatChoice != null && _commands.BoatChoice.modelId == BoatModelChoice.Majesty);
            SelectHighlight("boat-ornament", _commands.BoatChoice != null && _commands.BoatChoice.modelId == BoatModelChoice.Ornament);
            SelectHighlight("river-xiangjiang", _commands.Environment != null && _commands.Environment.SelectedScene == RiverEnvironmentController.SceneProfile.XiangjiangOrangeIsland);
            SelectHighlight("river-shunde", _commands.Environment != null && _commands.Environment.SelectedScene == RiverEnvironmentController.SceneProfile.ShundeFengjian);
            SelectHighlight("water-calm", _commands.Environment != null && _commands.Environment.SelectedCondition == RiverEnvironmentController.WaterCondition.Calm);
            SelectHighlight("water-windy", _commands.Environment != null && _commands.Environment.SelectedCondition == RiverEnvironmentController.WaterCondition.Windy);
            if (_dataText.Count == 6)
            {
                var driver = _commands.Driver;
                _dataText[0].text = frame == null || frame.paddles == null || frame.paddles.Length == 0 ? "等待有效物理帧"
                    : $"样本 {frame.paddles[0].sequence}\n桨叶 {frame.paddles[0].bladePosition:F2} m\n输入有效：{(frame.paddles[0].valid ? "是" : "否")}\n来源：{frame.paddles[0].source}";
                var forceRequest = frame != null && frame.feedbackRequests != null && frame.feedbackRequests.Length > 0
                    ? frame.feedbackRequests[0] : default(ForceFeedbackRequest);
                _dataText[1].text = frame == null ? "等待水动力请求"
                    : $"当前请求命令 {forceRequest.commandSequence}\n合力 {forceRequest.forceWorldN.magnitude:F2} N\n握把力矩 {forceRequest.torqueAtGripWorldNm.magnitude:F2} N·m\n耗散功率 {forceRequest.dissipativePowerW:F2} W";
                var cf = frame != null ? frame.cableFeedback : null;
                _dataText[2].text = cf == null ? "等待执行回执" : $"回执命令 {cf.commandSequence}\n{(cf.simulated ? "模拟" : "设备报告")}张力：{Tensions(cf.tensionsN)} N\n合力残差 {cf.forceResidualN:F2} N\n力矩残差 {cf.torqueResidualNm:F2} N·m";
                var pf = frame != null ? frame.platformFeedback : null;
                _dataText[3].text = frame == null ? "等待平台请求"
                    : $"请求 P {frame.platformRequest.eulerDegrees.x:F2}° / R {frame.platformRequest.eulerDegrees.z:F2}°\n"
                        + (pf == null ? "等待平台回执" : $"回执命令 {pf.commandSequence}\n{(pf.simulated ? "模拟" : "设备报告")} P {pf.eulerDegrees.x:F2}° / R {pf.eulerDegrees.z:F2}°\n位移 {pf.translationM:F3} m");
                _dataText[4].text = hud == null ? "等待航行数据"
                    : $"船速 {hud.BoatSpeedMetersPerSecond:F2} m/s\n航程 {hud.Distance:F1} m\n划桨 {hud.StrokeCount} 次\n桨频 {hud.StrokeRateSpm:F1} spm";
                _dataText[5].text = $"输入模式：{(driver != null ? driver.mode.ToString() : "等待")}\n"
                    + (loop == null ? "等待独立控制器" : $"回执命令 {loop.acknowledgedCommandSequence}\n新鲜反馈：{(loop.feedbackFresh ? "是" : "否")}\n物理时间 {frame.timeSeconds:F2} s");
            }
        }

        static string Tensions(float[] values)
        {
            if (values == null || values.Length == 0) return "—";
            var formatted = new string[values.Length];
            for (int i = 0; i < values.Length; i++) formatted[i] = values[i].ToString("F1");
            return string.Join(" / ", formatted);
        }

        void SelectHighlight(string key, bool selected)
        {
            if (!_buttons.TryGetValue(key, out Button button)) return;
            var colors = button.colors;
            colors.normalColor = selected ? Hex(0x37605F) : Panel;
            colors.highlightedColor = selected ? Hex(0x477570) : Hex(0x28505A);
            button.colors = colors;
            var label = button.GetComponentInChildren<Text>();
            if (label != null) label.color = selected ? Gold : White;
        }

        void Toast(string message) { _toast = message; _toastUntil = Time.realtimeSinceStartupAsDouble + 4; _dirty = true; }

        void WriteObservation()
        {
            var frame = _commands.ObservedFrame;
            var loop = frame != null ? frame.closedLoop : null;
            var observation = new UiObservation {
                page = _page.ToString(), guideVisible = _guide, guideStep = _guideStep,
                screenWidth = Screen.width, screenHeight = Screen.height,
                realtime = Time.realtimeSinceStartupAsDouble,
                boat = _commands.BoatChoice != null ? _commands.BoatChoice.modelId : "",
                river = _commands.Environment != null ? _commands.Environment.SelectedScene.ToString() : "",
                water = _commands.Environment != null ? _commands.Environment.SelectedCondition.ToString() : "",
                currentSpeedMps = _commands.Environment != null ? _commands.Environment.TargetCurrentSpeedMps : 0,
                currentHeadingDegrees = _commands.Environment != null ? _commands.Environment.TargetCurrentHeadingDegrees : 0,
                inputMode = _commands.Driver != null ? _commands.Driver.mode.ToString() : "",
                controllerReady = loop != null && loop.ready, feedbackFresh = loop != null && loop.feedbackFresh,
                controllerSessionId = loop != null ? loop.sessionId : "",
                experienceActive = _commands.ExperienceActive, paddling = _commands.Paddling,
                comfort = ComfortVignette.UserEnabled, metrics = _commands.MetricsVisible,
                diagnostics = RideHud.DiagnosticsVisible,
                simulationTime = _commands.Session != null ? _commands.Session.SimulationTime : 0,
                actions = _actions
            };
            foreach (var pair in _controls)
            {
                if (pair.Value == null || !pair.Value.gameObject.activeInHierarchy) continue;
                var corners = new Vector3[4]; pair.Value.GetWorldCorners(corners);
                var button = pair.Value.GetComponent<Button>();
                var slider = pair.Value.GetComponent<Slider>();
                observation.controls.Add(new ControlObservation { name = pair.Key,
                    rect = new Rect(corners[0].x, Screen.height - corners[1].y,
                        corners[2].x - corners[0].x, corners[1].y - corners[0].y),
                    interactable = button != null ? button.interactable : slider == null || slider.interactable });
            }
            string path = Path.Combine(_evidenceDirectory, "presentation_ui.json");
            string temporary = path + ".next";
            File.WriteAllText(temporary, JsonUtility.ToJson(observation, false), new System.Text.UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }

        RectTransform RectNode(Transform parent, string name, Rect rectangle)
        {
            var node = new GameObject(name, typeof(RectTransform));
            var rect = node.GetComponent<RectTransform>(); rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0, 1); rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = new Vector2(rectangle.x, -rectangle.y); rect.sizeDelta = rectangle.size;
            return rect;
        }

        RectTransform PanelAt(Transform parent, string name, Rect rect, Color color, bool rounded = true, bool raycast = false)
        {
            var node = RectNode(parent, name, rect);
            var image = node.gameObject.AddComponent<Image>(); image.color = color; image.raycastTarget = raycast;
            if (rounded) { image.sprite = _roundSprite; image.type = Image.Type.Sliced; }
            return node;
        }

        Text Label(Transform parent, string name, Rect rect, string value, int size, Color color, TextAnchor alignment = TextAnchor.MiddleLeft)
        {
            var node = RectNode(parent, name, rect);
            var text = node.gameObject.AddComponent<Text>(); text.font = _font; text.fontSize = size;
            text.color = color; text.text = value; text.alignment = alignment; text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Wrap; text.verticalOverflow = VerticalWrapMode.Overflow;
            text.lineSpacing = 1.14f;
            return text;
        }

        Button ButtonAt(Transform parent, string name, Rect rect, string label, Action action, Color color,
            Color? textColor = null, int size = 15)
        {
            var node = PanelAt(parent, name, rect, Color.white, true, true);
            var button = node.gameObject.AddComponent<Button>();
            button.targetGraphic = node.GetComponent<Image>();
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            var colors = button.colors; colors.normalColor = color; colors.highlightedColor = Color.Lerp(color, White, .12f);
            colors.pressedColor = Color.Lerp(color, Ink, .18f); colors.selectedColor = color;
            colors.disabledColor = Color.Lerp(color, Ink, .5f); colors.fadeDuration = .1f; button.colors = colors;
            Label(node, name + " label", new Rect(10, 3, rect.width - 20, rect.height - 6), label, size,
                textColor ?? White, TextAnchor.MiddleCenter);
            button.onClick.AddListener(() => { action(); if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null); _dirty = true; });
            _controls[name] = node; _buttons[name] = button;
            return button;
        }

        void SliderAt(Transform parent, string name, Rect rect, float minimum, float maximum, float value, Action<float> changed)
        {
            var node = RectNode(parent, name, rect);
            // The entire slider area receives pointer input, including the track.
            var inputSurface = node.gameObject.AddComponent<Image>();
            inputSurface.color = Color.clear; inputSurface.raycastTarget = true;
            PanelAt(node, "Track", new Rect(0, 12, rect.width, 4), Border);
            var fillArea = RectNode(node, "Fill area", new Rect(7, 12, rect.width - 14, 4));
            var fill = PanelAt(fillArea, "Fill", new Rect(0, 0, fillArea.rect.width, 4), Teal);
            Stretch(fill);
            var handleArea = RectNode(node, "Handle area", new Rect(7, 0, rect.width - 14, rect.height));
            var handle = PanelAt(handleArea, "Handle", new Rect(0, 5, 18, 18), White, true, true);
            handle.anchorMin = new Vector2(0, 0); handle.anchorMax = new Vector2(0, 1);
            handle.pivot = new Vector2(.5f, .5f); handle.sizeDelta = new Vector2(18, -10);
            handle.anchoredPosition = Vector2.zero;
            var slider = node.gameObject.AddComponent<Slider>();
            slider.direction = Slider.Direction.LeftToRight; slider.minValue = minimum; slider.maxValue = maximum;
            slider.fillRect = fill; slider.handleRect = handle; slider.targetGraphic = handle.GetComponent<Image>();
            slider.navigation = new Navigation { mode = Navigation.Mode.None }; slider.value = value;
            slider.interactable = _commands.CanConfigure;
            slider.onValueChanged.AddListener(v => changed(v));
            _controls[name] = node;
        }

        static void Stretch(RectTransform rect)
        { rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = rect.offsetMax = Vector2.zero; }
        static Color Hex(uint rgb) => new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1);

        static Texture2D RoundedTexture(Color color)
        {
            var texture = new Texture2D(64, 64, TextureFormat.RGBA32, false);
            for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++)
            {
                float dx = Mathf.Max(Mathf.Abs(x - 31.5f) - 15.5f, 0), dy = Mathf.Max(Mathf.Abs(y - 31.5f) - 15.5f, 0);
                var pixel = color; pixel.a *= Mathf.Clamp01(16f - Mathf.Sqrt(dx * dx + dy * dy) + .5f);
                texture.SetPixel(x, y, pixel);
            }
            texture.Apply(); texture.wrapMode = TextureWrapMode.Clamp; texture.filterMode = FilterMode.Bilinear;
            return texture;
        }

        public static GUIStyle LegacyPanelStyle(Font font)
        {
            if (!Active) return GUI.skin.box;
            var ui = Instance;
            if (ui._legacyTexture == null) ui._legacyTexture = RoundedTexture(new Color(.045f, .16f, .185f, .97f));
            if (ui._legacyStyle == null)
                ui._legacyStyle = new GUIStyle(GUI.skin.box) { border = new RectOffset(18, 18, 18, 18),
                    padding = new RectOffset(10, 10, 10, 10) };
            ui._legacyStyle.normal.background = ui._legacyTexture; ui._legacyStyle.font = font;
            return ui._legacyStyle;
        }

        void OnApplicationQuit() { if (_evidenceDirectory != null) WriteObservation(); }
        void OnDestroy()
        {
            if (_commands != null) { _commands.SceneReady -= SceneReady; _commands.ActionInvoked -= CommandObserved; }
            if (_commands != null && _commands.Environment != null) _commands.Environment.SetPresentationPanelVisible(false);
            if (Instance == this) Instance = null;
            if (_font != null) Destroy(_font);
            if (_roundSprite != null) Destroy(_roundSprite);
            if (_roundTexture != null) Destroy(_roundTexture);
            if (_shadeTexture != null) Destroy(_shadeTexture);
            if (_legacyTexture != null) Destroy(_legacyTexture);
        }
    }
}
