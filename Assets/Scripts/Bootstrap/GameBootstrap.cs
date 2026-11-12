// 启动与装配：把四层 Agent、知识库、模型库、玩家、HUD、锚点路由器全部接起来。
// 无 API Key 时一切走确定性兜底，游戏仍可完整游玩（离线模式）。
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using EpicSpark.Core;
using EpicSpark.AI.OpenAI;
using EpicSpark.AI.Knowledge;
using EpicSpark.Story;
using EpicSpark.Graybox;
using EpicSpark.Build;
using EpicSpark.AssetGen;
using EpicSpark.Npc.Dialogue;
using EpicSpark.Npc.Runtime;
using EpicSpark.Player;
using EpicSpark.Tools;
using EpicSpark.Anchors;
using EpicSpark.UI;
using EpicSpark.Loading;

namespace EpicSpark.Bootstrap
{
    [DisallowMultipleComponent]
    public class GameBootstrap : MonoBehaviour
    {
        public static GameBootstrap Instance { get; private set; }

        [Header("模型端点（留空 Key 则离线确定性游玩）")]
        public string baseUrl = "https://ai.gitee.com/v1";
        public string apiKey = "";
        public string chatModel = "Qwen3-32B";
        public bool forceOffline;

        [Header("开局")]
        public string defaultEvent = "法国大革命";
        [Range(0f, 1f)] public float initialTolerance = 0.5f;

        [Header("环境")]
        public bool createPlayerIfMissing = true;
        public bool createLightingIfMissing = true;

        // 自动化冒烟测试用：启动后跳过标题界面直接开局。
        // 注意：进入 Play Mode 会触发域重载，静态字段会被清空，因此测试参数走 PlayerPrefs。
        public static bool AutoStartOnBoot;
        public static string AutoStartEvent = "法国大革命";
        private const string TestAutoStartKey = "EpicSpark.TestAutoStart";
        private const string TestAutoStartEventKey = "EpicSpark.TestAutoStartEvent";

        /// <summary>神殿开始场景写入的启动参数：待开局的历史事件 / 是否继续存档。读取后立即删除。</summary>
        public const string BootEventKey = "EpicSpark.BootEvent";
        public const string BootResumeKey = "EpicSpark.BootResume";
        /// <summary>星火神殿设置页里调整的生成容忍度；开局时同步到 initialTolerance。</summary>
        public const string BootToleranceKey = "EpicSpark.BootTolerance";

        private GameDirector _director;
        private NpcDialogueService _dialogue;
        private KnowledgeService _knowledge;
        private ModelCatalog _catalog;
        private TitleScreen _title;
        private HudController _hud;
        private AnchorRouter _router;
        private ToleranceSliderUi _toleranceUi;
        private ApiSettingsScreen _settingsScreen;
        private LlmService _llm;
        private OpenAISettings _aiSettings;
        private KnowledgeSettings _knowledgeSettings;
        private AssetGenSettings _assetSettings;
        private ApiConfig _config;
        private bool _started;
        private float _busySeconds;
        private float _busyLastTick;
        private bool _busyTickerActive;
        private int _storyBudget = ApiConfig.DefaultTimeoutSeconds;
        private int _grayboxBudget = 30;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        public GameDirector Director { get { return _director; } }
        public bool ServicesReady { get { return Services.Ready; } }
        /// <summary>当前生效的端点/模型配置（设置界面读它）。</summary>
        public ApiConfig Config
        {
            get { return _config != null ? _config : ApiConfig.Defaults(); }
        }

        /// <summary>
        /// 兜底自举：万一在空场景 / 别的场景里点了 Play，也自动创建引导对象与相机，
        /// 不会出现"只有一个空场景、看不到界面"的情况。主场景里已有 EpicSpark 时不会重复创建。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoBootstrapIfMissing()
        {
            if (UnityEngine.Object.FindObjectOfType<GameBootstrap>() != null) return;

            // 神殿开始场景（StarTempleStart / StartScene）自带菜单、相机、光照与玩家，
            // 不需要 EpicSpark 引导对象，否则会多出一套标题界面与相机。
            if (UnityEngine.Object.FindObjectOfType<SparkCode.StartMenuController>() != null) return;
            if (UnityEngine.Object.FindObjectOfType<SparkCode.Gameplay.StarTempleWorldUiController>() != null) return;

            var go = new GameObject("EpicSpark (自动引导)");
            go.AddComponent<GameBootstrap>();
            EnsureFallbackCamera();
            Debug.LogWarning("[启动] 当前场景没有 EpicSpark 引导对象，已自动创建。"
                             + "建议直接打开 Assets/Scenes/EpicSpark.unity 再 Play。");
        }

        private static void EnsureFallbackCamera()
        {
            if (Camera.main != null) return;
            var go = new GameObject("Main Camera (自动)");
            go.tag = "MainCamera";
            var cam = go.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.fieldOfView = 60f;
            go.transform.position = new Vector3(0f, 1.7f, -8f);
            go.AddComponent<AudioListener>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            // 端点配置优先级：设置界面存进 PlayerPrefs 的值 > 环境变量 > Inspector 字段
            _config = ApiConfig.Load().WithInspectorDefaults(apiKey, baseUrl, chatModel, forceOffline);

            // 星火神殿设置页里调到过容忍度时，开局沿用它。
            if (PlayerPrefs.HasKey(BootToleranceKey))
                initialTolerance = Mathf.Clamp01(PlayerPrefs.GetFloat(BootToleranceKey, initialTolerance));

            Application.targetFrameRate = 60;
            if (createLightingIfMissing) EnsureLighting();
            EnsureEnvironment();
        }

        private async void Start()
        {
            await BuildServicesAsync();
            BuildRuntimeObjects();
            WireEvents();
            ShowTitle();
        }

        // ─────────────────────────── 服务装配 ───────────────────────────

        private Task BuildServicesAsync()
        {
            ApiConfig cfg = Config;
            string resolvedKey = cfg.apiKey ?? "";
            string resolvedUrl = cfg.ResolvedBaseUrl;
            bool offline = cfg.offline || !cfg.HasKey;

            _aiSettings = ScriptableObject.CreateInstance<OpenAISettings>();
            _aiSettings.name = "RuntimeOpenAISettings";
            _aiSettings.baseUrl = resolvedUrl;
            _aiSettings.apiKey = resolvedKey;
            _aiSettings.chatModel = cfg.ResolvedChatModel;
            _aiSettings.embedModel = cfg.ResolvedEmbedModel;
            _aiSettings.offline = offline;
            _aiSettings.timeoutSeconds = cfg.ResolvedTimeoutSeconds;
            _aiSettings.maxTokens = cfg.ResolvedMaxTokens;
            // 推理型模型单次要几十秒，重试会把等待翻倍，这里只留 1 次（失败即走确定性兜底）
            _aiSettings.maxRetries = 1;

            _llm = new LlmService(_aiSettings);
            Services.Llm = _llm;
            Debug.Log("[启动] LLM 端点=" + resolvedUrl + " 模型=" + _aiSettings.chatModel
                      + " 远端可用=" + _llm.HasRemote + (offline ? "（离线确定性模式）" : ""));

            // 知识库（Unity 进程内 RAG，600 事件）
            _knowledgeSettings = ScriptableObject.CreateInstance<KnowledgeSettings>();
            _knowledgeSettings.name = "RuntimeKnowledgeSettings";
            _knowledgeSettings.baseUrl = resolvedUrl;
            _knowledgeSettings.apiKey = resolvedKey;
            _knowledgeSettings.embedModel = cfg.ResolvedEmbedModel;
            _knowledgeSettings.timeoutSeconds = cfg.ResolvedTimeoutSeconds;
            ApplyKnowledgeMode(_knowledgeSettings, cfg);
            _knowledge = new KnowledgeService(_knowledgeSettings);
            Services.Knowledge = _knowledge;

            // 当前为强制素模阶段：不创建/加载模型库，不装配资产生成服务，也不读取旧运行时资产目录。
            _catalog = null;
            _assetSettings = null;
            Services.Catalog = null;
            Services.AssetGenerator = null;
            Debug.Log("[启动] 美术资产库与资产生成已禁用；场景搭建只使用 PrimitiveKit 简单几何体。");

            // 四层 Agent
            Services.Story = new StoryAgent(_llm);
            Services.Graybox = new GrayboxAgent(_llm);
            ApplyAgentBudgets(cfg);
            Services.Build = new BuildAgent(_catalog);
            _dialogue = new NpcDialogueService(_llm);

            Services.Save = new GameSaveData();
            _director = new GameDirector(transform);
            _director.Tolerance = initialTolerance;
            _director.RemoteTimeoutSeconds = cfg.ResolvedTimeoutSeconds;

            // 预热知识库（不阻塞启动，失败不影响游玩）
            _ = WarmupKnowledgeAsync();
            return Task.CompletedTask;
        }

        /// <summary>
        /// 给两个"会思考很久"的 Agent 分配时间预算：
        /// 剧情吃满设置里的请求超时；灰盒不限时（推理模型单轮可能要 1–3 分钟），
        /// 只受取消令牌与 HTTP 超时约束。异常/失败时仍会回落到确定性实现。
        /// </summary>
        private void ApplyAgentBudgets(ApiConfig cfg)
        {
            int story = cfg != null ? cfg.ResolvedTimeoutSeconds : ApiConfig.DefaultTimeoutSeconds;
            int graybox = 0;   // 0 = 灰盒不限时（推理模型单轮 60–120 秒很正常，到点就砍只会白等）

            _storyBudget = story;
            _grayboxBudget = graybox;

            var storyAgent = Services.Story as StoryAgent;
            if (storyAgent != null) storyAgent.RemoteTimeoutSeconds = story;

            var grayboxAgent = Services.Graybox as GrayboxAgent;
            if (grayboxAgent != null) grayboxAgent.RemoteTimeoutSeconds = graybox;
        }

        /// <summary>
        /// 知识库默认只用进程内本地向量（秒级）；只有显式打开「知识库远端向量」才会在首次检索时
        /// 用远端 /embeddings 给全部 3600+ 文本块建索引（数百次请求，可能数分钟）。
        /// </summary>
        private static void ApplyKnowledgeMode(KnowledgeSettings settings, ApiConfig cfg)
        {
            bool remote = cfg != null && cfg.knowledgeRemoteEmbedding && cfg.RemoteActive;
            settings.embedProvider = remote ? KnowledgeProviderMode.OpenAI : KnowledgeProviderMode.Local;
            settings.rerankProvider = remote ? KnowledgeProviderMode.OpenAI : KnowledgeProviderMode.Local;
            if (remote)
                Debug.LogWarning("[知识库] 已启用远端向量：首次检索要分批请求 embedding 建索引，会明显变慢；" +
                                 "若只是检索用，建议保持关闭。");
        }

        private async Task WarmupKnowledgeAsync()
        {
            try
            {
                await _knowledge.InitializeAsync(_cts.Token);
                Debug.Log("[启动] 知识库预热完成");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[启动] 知识库预热失败: " + e.Message);
            }
        }

        // ─────────────────────────── 运行时对象 ───────────────────────────

        private void BuildRuntimeObjects()
        {
            // 玩家
            if (createPlayerIfMissing && EpicSparkPlayerController.Instance == null
                && UnityEngine.Object.FindObjectOfType<EpicSparkPlayerController>() == null)
            {
                var playerGo = new GameObject("Player");
                playerGo.transform.position = new Vector3(0f, 1.2f, 0f);
                playerGo.AddComponent<EpicSparkPlayerController>();
                Debug.Log("[启动] 已创建第一人称玩家");
            }

            // HUD（各自建 Canvas，必须挂在根对象上）
            var hudGo = new GameObject("Hud");
            _hud = hudGo.AddComponent<HudController>();
            _hud.showCrosshair = true;

            // 传送锚点路由器
            var routerGo = new GameObject("AnchorRouter");
            _router = routerGo.AddComponent<AnchorRouter>();

            // 滑块（标题界面显示）
            _toleranceUi = ToleranceSliderUi.Instance;
            if (_toleranceUi == null) _toleranceUi = ToleranceSliderUi.Create(null);
            _toleranceUi.SetValueSilent(initialTolerance);
            _toleranceUi.SetVisible(true);

            // 标题界面
            _title = GetComponent<TitleScreen>();
            if (_title == null) _title = gameObject.AddComponent<TitleScreen>();

            // 设置面板（标题界面按钮 / 游戏内 F10 打开）
            var settingsGo = new GameObject("ApiSettings");
            settingsGo.transform.SetParent(transform, false);
            _settingsScreen = settingsGo.AddComponent<ApiSettingsScreen>();
            _settingsScreen.Saved += OnApiSettingsSaved;
            _settingsScreen.Closed += OnApiSettingsClosed;
        }

        // ─────────────────────────── 事件接线 ───────────────────────────

        private void WireEvents()
        {
            TitleScreen.StartRequested += OnStartRequested;
            TitleScreen.ResumeRequested += OnResumeRequested;
            TitleScreen.SettingsRequested += OpenSettings;

            if (_router != null)
                _router.AnchorEntered += OnAnchorEntered;

            NpcDialogueTrigger.ReplyHandler = OnNpcReplyAsync;
            NpcDialogueTrigger.ConversationEnded += OnConversationEnded;

            ToolInventory.ToolEquipped += OnToolEquipped;

            if (_toleranceUi != null) ToleranceSliderUi.ValueChanged += OnToleranceChanged;
        }

        private async void OnStartRequested(string userEvent)
        {
            if (_started)
            {
                GameEvents.RaiseNotice("游戏已经开始，请使用场景内的传送锚点推进剧情。");
                return;
            }
            _started = true;
            if (_title != null) _title.Hide();
            if (_toleranceUi != null) _toleranceUi.SetVisible(false);

            bool ok;
            try
            {
                ok = await _director.StartNewGameAsync(userEvent);
            }
            catch (Exception e)
            {
                Debug.LogError("[启动] 开局异常: " + e);
                ok = false;
            }

            if (!ok)
            {
                _started = false;
                if (_title != null)
                {
                    _title.Show(userEvent, "开局失败，请检查 API Key 与模型配置，或直接以离线模式重试。");
                }
                return;
            }

            EnterPlayUi("WASD 移动 · Q 奔跑 · E 呼出/锁定鼠标 · 空格跳跃 · 左键攻击 · 右键交互/对话 · Tab 调整容忍度");
        }

        private async void OnResumeRequested()
        {
            if (_started)
            {
                GameEvents.RaiseNotice("游戏已经开始，请使用场景内的传送锚点推进剧情。");
                return;
            }
            _started = true;
            if (_title != null) _title.Hide();
            if (_toleranceUi != null) _toleranceUi.SetVisible(false);

            bool ok;
            try
            {
                ok = await _director.ResumeAsync();
            }
            catch (Exception e)
            {
                Debug.LogError("[启动] 读档异常: " + e);
                ok = false;
            }

            if (!ok)
            {
                _started = false;
                GameSaveData save = SaveService.Load();
                if (_title != null)
                {
                    _title.Show(save != null ? save.userEvent : defaultEvent,
                        "读档失败：存档可能已损坏，可重新开始一局。");
                }
                return;
            }

            EnterPlayUi("已恢复存档：" + GameClock.Display() + " · 走入传送锚点继续推进");
        }

        /// <summary>开局/读档成功后统一的界面与输入状态。</summary>
        private void EnterPlayUi(string notice)
        {
            EpicSparkPlayerController player = EpicSparkPlayerController.Instance;
            if (player != null)
            {
                player.SetInputEnabled(true);
                player.SetCursorLocked(true);
            }
            GameEvents.RaiseNotice(notice);
        }

        private void OnAnchorEntered(SceneAnchorSpec spec)
        {
            if (_director == null || spec == null) return;
            _ = RunAnchorAsync(spec);
        }

        private async Task RunAnchorAsync(SceneAnchorSpec spec)
        {
            try { await _director.HandleAnchorAsync(spec); }
            catch (Exception e) { Debug.LogError("[启动] 锚点推进异常: " + e); }
        }

        private async Task<string> OnNpcReplyAsync(NpcSpec npc, string playerLine)
        {
            if (_dialogue == null) return null;
            try
            {
                string context = BuildStoryContext();
                var history = new List<string>();
                return await _dialogue.ReplyAsync(npc, playerLine, context, history, _cts.Token);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[启动] NPC 回复失败: " + e.Message);
                return null; // NPC 端会走本地降级文案
            }
        }

        private void OnConversationEnded(NpcSpec npc, List<string> history)
        {
            _ = SummarizeConversationAsync(npc, history);
        }

        private async Task SummarizeConversationAsync(NpcSpec npc, List<string> history)
        {
            if (_dialogue == null || npc == null || history == null || history.Count == 0) return;
            try
            {
                string summary = await _dialogue.SummarizeAsync(npc, history, _cts.Token);
                if (string.IsNullOrWhiteSpace(summary)) return;
                // 概括转述后作为一条行为记录交回剧情 Agent，避免对话主体混淆
                SceneSpec current = _director != null ? _director.CurrentSpec : null;
                GameLog.Record(current != null ? current.sceneId : "", "talk_summary", npc.npcId, summary);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[启动] 对话概括失败: " + e.Message);
            }
        }

        private void OnToolEquipped(ToolKind kind)
        {
            SceneSpec current = _director != null ? _director.CurrentSpec : null;
            GameLog.Record(current != null ? current.sceneId : "", "equip", "", "切换工具为 " + kind);
            if (Services.Save != null) Services.Save.heldTool = kind;
        }

        private void OnToleranceChanged(float value)
        {
            if (_director != null) _director.Tolerance = value;
            GameEvents.RaiseNotice("资产匹配容忍度滑块：" + Mathf.RoundToInt(value * 100f) + "%（越高越严格，越容易触发新资产生成）");
        }

        private string BuildStoryContext()
        {
            string narrative = _director != null ? _director.LastNarrative : "";
            SceneSpec spec = _director != null ? _director.CurrentSpec : null;
            string sceneName = spec != null ? spec.sceneName : "";
            string era = spec != null ? spec.era : "";
            string userEvent = Services.Save != null ? Services.Save.userEvent : "";
            return "历史事件：" + userEvent + "\n当前场景：" + sceneName + "（" + era + "）\n当前时间："
                   + GameClock.Display() + "\n最近剧情：" + narrative;
        }

        // ─────────────────────────── 环境 ───────────────────────────

        private void ShowTitle()
        {
            // 从神殿开始场景（StartScene）进入：直接开局 / 读档，不再显示旧的标题界面。
            if (PlayerPrefs.GetInt(BootResumeKey, 0) == 1)
            {
                PlayerPrefs.DeleteKey(BootResumeKey);
                PlayerPrefs.DeleteKey(BootEventKey);
                Debug.Log("[启动] 从神殿开始场景进入：继续上次存档");
                OnResumeRequested();
                return;
            }
            string bootEvent = PlayerPrefs.GetString(BootEventKey, "");
            if (!string.IsNullOrEmpty(bootEvent))
            {
                PlayerPrefs.DeleteKey(BootEventKey);
                PlayerPrefs.DeleteKey(BootResumeKey);
                Debug.Log("[启动] 从神殿开始场景进入：" + bootEvent);
                OnStartRequested(bootEvent);
                return;
            }

            bool remote = Services.Llm != null && Services.Llm.HasRemote;
            string status = remote
                ? "远端模型已连接，将由四层 Agent 实时生成场景。"
                : StatusLine();
            if (_title != null) _title.Show(defaultEvent, status);
            if (_toleranceUi != null) _toleranceUi.SetVisible(true);
            var player = EpicSparkPlayerController.Instance;
            if (player != null) player.SetCursorLocked(false);

            if (PlayerPrefs.GetInt(TestAutoStartKey, 0) == 1)
            {
                AutoStartOnBoot = true;
                AutoStartEvent = PlayerPrefs.GetString(TestAutoStartEventKey, AutoStartEvent);
            }

            if (AutoStartOnBoot)
            {
                AutoStartOnBoot = false;
                PlayerPrefs.DeleteKey(TestAutoStartKey);
                PlayerPrefs.DeleteKey(TestAutoStartEventKey);
                string auto = string.IsNullOrEmpty(AutoStartEvent) ? defaultEvent : AutoStartEvent;
                Debug.Log("[启动] 自动开局：" + auto);
                OnStartRequested(auto);
            }
        }

        private void EnsureLighting()
        {
            Light existing = UnityEngine.Object.FindObjectOfType<Light>();
            if (existing != null)
            {
                if (existing.type == LightType.Directional) return;
            }
            var go = new GameObject("Sun");
            var light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(1f, 0.96f, 0.88f);
            light.intensity = 1.15f;
            light.shadows = LightShadows.Soft;
            go.transform.rotation = Quaternion.Euler(48f, 35f, 0f);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.42f, 0.45f, 0.5f);
            RenderSettings.fog = true;
            RenderSettings.fogColor = new Color(0.62f, 0.66f, 0.72f);
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogStartDistance = 60f;
            RenderSettings.fogEndDistance = 320f;
            Debug.Log("[启动] 已创建方向光与环境设置");
        }

        private void EnsureEnvironment()
        {
            if (Camera.main == null)
            {
                var camGo = new GameObject("Main Camera");
                var cam = camGo.AddComponent<Camera>();
                cam.tag = "MainCamera";
                cam.clearFlags = CameraClearFlags.Skybox;
                camGo.transform.position = new Vector3(0f, 1.6f, 0f);
                if (UnityEngine.Object.FindObjectOfType<AudioListener>() == null)
                    camGo.AddComponent<AudioListener>();
            }
        }

        // ─────────────────────────── 模型端点设置 ───────────────────────────

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F10))
            {
                if (_settingsScreen != null && _settingsScreen.IsVisible) CloseSettings();
                else OpenSettings();
            }
            // F11：场景生成调试录制开关（剧情推理/输出/灰盒截图/拼装截图）
            if (Input.GetKeyDown(KeyCode.F11))
            {
                bool next = !EpicSpark.Debugging.SceneDebugRecorder.Enabled;
                EpicSpark.Debugging.SceneDebugRecorder.Enabled = next;
                GameEvents.RaiseNotice(next
                    ? "调试录制：开（每轮场景写到 persistentDataPath/EpicSpark/debug）"
                    : "调试录制：关");
                Debug.Log("[调试] 录制开关 = " + (next ? "开" : "关")
                          + " · 目录: " + EpicSpark.Debugging.SceneDebugRecorder.PersistentRoot());
            }

            TickBusyTicker();
        }

        /// <summary>
        /// 生成/推理期间每秒刷新一次「已等待 N 秒」副标题：推理模型一次可能要 30–90 秒，
        /// 没有计时会让人以为卡死了。
        /// </summary>
        private void TickBusyTicker()
        {
            bool busy = _director != null && _director.IsBusy;
            if (!busy)
            {
                if (_busyTickerActive)
                {
                    _busyTickerActive = false;
                    _busySeconds = 0f;
                    ScenePhaseOverlay overlay = ScenePhaseOverlay.Instance;
                    if (overlay != null) overlay.SetSubText("");
                }
                return;
            }

            _busyTickerActive = true;
            _busySeconds += Time.unscaledDeltaTime;
            if (_busySeconds - _busyLastTick < 1f) return;
            _busyLastTick = _busySeconds;

            ScenePhaseOverlay current = ScenePhaseOverlay.Instance;
            if (current == null) return;
            ApiConfig cfg = Config;
            string hint = cfg.RemoteActive
                ? "远端模型 " + cfg.ResolvedChatModel + " · 剧情≤" + _storyBudget + "s / 灰盒不限时（推理模型 1–3 分钟），超时自动本地兜底"
                : "本地确定性模式";
            current.SetSubText("已等待 " + Mathf.FloorToInt(_busySeconds) + " 秒 · " + hint);
        }

        /// <summary>打开设置面板（标题界面按钮或 F10）。</summary>
        public void OpenSettings()
        {
            if (_settingsScreen == null) return;
            if (_settingsScreen.IsVisible) return;
            _settingsScreen.Show(Config);
            // 开局后打开设置要放开输入与鼠标，关闭时再恢复
            if (_started)
            {
                EpicSparkPlayerController player = EpicSparkPlayerController.Instance;
                if (player != null)
                {
                    player.SetInputEnabled(false);
                    player.SetCursorLocked(false);
                }
            }
        }

        private void CloseSettings()
        {
            if (_settingsScreen == null) return;
            _settingsScreen.Hide();
        }

        private void OnApiSettingsClosed()
        {
            if (!_started) return;
            EpicSparkPlayerController player = EpicSparkPlayerController.Instance;
            if (player != null)
            {
                player.SetInputEnabled(true);
                player.SetCursorLocked(true);
            }
        }

        private void OnApiSettingsSaved(ApiConfig cfg)
        {
            if (cfg == null) return;
            _config = cfg.Clone();
            if (_director != null) _director.RemoteTimeoutSeconds = _config.ResolvedTimeoutSeconds;

            // 1) LLM：换客户端，后续请求立刻用新 Key / 端点 / 模型
            ApplyAgentBudgets(cfg);

            if (_aiSettings != null)
            {
                _aiSettings.baseUrl = cfg.ResolvedBaseUrl;
                _aiSettings.apiKey = cfg.apiKey;
                _aiSettings.chatModel = cfg.ResolvedChatModel;
                _aiSettings.embedModel = cfg.ResolvedEmbedModel;
                _aiSettings.offline = cfg.offline || !cfg.HasKey;
                _aiSettings.timeoutSeconds = cfg.ResolvedTimeoutSeconds;
                _aiSettings.maxTokens = cfg.ResolvedMaxTokens;
                _aiSettings.maxRetries = 1;
                if (_llm != null) _llm.Reconfigure(_aiSettings);
                Services.Llm = _llm;
            }

            // 2) 知识库：丢掉旧客户端，下次检索按新配置重建
            if (_knowledgeSettings != null)
            {
                _knowledgeSettings.baseUrl = cfg.ResolvedBaseUrl;
                _knowledgeSettings.apiKey = cfg.apiKey;
                _knowledgeSettings.embedModel = cfg.ResolvedEmbedModel;
                _knowledgeSettings.timeoutSeconds = cfg.ResolvedTimeoutSeconds;
                ApplyKnowledgeMode(_knowledgeSettings, cfg);
                if (_knowledge != null) _knowledge.Invalidate();
            }

            // 3) 资产生成：设置对象是引用共享的，改完落盘即可
            if (_assetSettings != null)
            {
                _assetSettings.baseUrl = cfg.ResolvedBaseUrl;
                _assetSettings.apiKey = cfg.apiKey;
                _assetSettings.textToImageModel = cfg.ResolvedImageModel;
                _assetSettings.imageTo3DModel = cfg.Resolved3DModel;
                try { _assetSettings.Save(); }
                catch (Exception e) { Debug.LogWarning("[设置] 资产配置落盘失败: " + e.Message); }
            }

            string mode = cfg.RemoteActive ? "已切换为远端模式" : "当前为离线确定性模式";
            GameEvents.RaiseNotice("模型设置已保存：" + mode + "（端点 " + cfg.ResolvedBaseUrl + "）");
            Debug.Log("[设置] 应用完成: " + cfg.Describe());

            if (!_started && _title != null) _title.SetStatus(StatusLine());
        }

        private string StatusLine()
        {
            ApiConfig cfg = Config;
            if (cfg.RemoteActive)
            {
                return "远端模型已配置：对话 " + cfg.ResolvedChatModel + " · 端点 " + cfg.ResolvedBaseUrl
                       + "（F10 或「设置」可修改）";
            }
            return "未配置 API Key：以确定性本地逻辑离线游玩（点「设置」或按 F10 填 Key 与模型）。";
        }

        private void OnDestroy()
        {
            TitleScreen.StartRequested -= OnStartRequested;
            TitleScreen.ResumeRequested -= OnResumeRequested;
            TitleScreen.SettingsRequested -= OpenSettings;
            if (_settingsScreen != null)
            {
                _settingsScreen.Saved -= OnApiSettingsSaved;
                _settingsScreen.Closed -= OnApiSettingsClosed;
            }
            NpcDialogueTrigger.ReplyHandler = null;
            NpcDialogueTrigger.ConversationEnded -= OnConversationEnded;
            ToolInventory.ToolEquipped -= OnToolEquipped;
            ToleranceSliderUi.ValueChanged -= OnToleranceChanged;
            if (_director != null) _director.Shutdown();
            try { _cts.Cancel(); } catch (Exception) { }
            if (_knowledge != null) _knowledge.Dispose();
            if (Instance == this) Instance = null;
        }

        private void OnApplicationQuit()
        {
            if (_director != null) _director.SaveNow();
        }
    }
}
