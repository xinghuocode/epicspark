// 游戏总导演：把四层 Agent 串成可玩的循环。
//  剧情&核心 Agent → 灰盒 Agent → 搭建 Agent →（必要时）资产生成 Agent → 加载动画 → 游玩 → 传送锚点 → 下一轮
// 同时负责：场景复用、行为日志、游戏内时间、存档、NPC 对话接线。
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using EpicSpark.Core;
using EpicSpark.Loading;
using EpicSpark.Player;
using EpicSpark.Anchors;
using EpicSpark.Tools;
using EpicSpark.UI;

namespace EpicSpark.Bootstrap
{
    public sealed class GameDirector
    {
        private readonly Transform _sceneParent;
        private readonly SceneCache _cache;
        private readonly Dictionary<string, SceneSpec> _specs = new Dictionary<string, SceneSpec>(StringComparer.Ordinal);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        private SceneSpec _currentSpec;
        private SceneInstance _currentInstance;
        private bool _busy;
        private SceneAnchorSpec _pendingAnchor;
        private float _tolerance = 0.5f;
        private string _lastNarrative = "";

        public GameDirector(Transform sceneParent)
        {
            _sceneParent = sceneParent;
            _cache = new SceneCache(sceneParent);
        }

        public SceneSpec CurrentSpec { get { return _currentSpec; } }
        public SceneInstance CurrentInstance { get { return _currentInstance; } }
        public float Tolerance { get { return _tolerance; } set { _tolerance = Mathf.Clamp01(value); } }
        public bool IsBusy { get { return _busy; } }
        public int BuiltSceneCount { get { return _cache.Count; } }
        public string LastNarrative { get { return _lastNarrative; } }

        /// <summary>单个远端 Agent 调用的超时（秒），由 Bootstrap 按设置里的请求超时同步。</summary>
        public int RemoteTimeoutSeconds = 45;

        /// <summary>
        /// 给远端 Agent 调用套一个硬超时：超时就用本地兜底继续，绝不让整局游戏卡在一个请求上。
        /// 被放弃的任务仍在后台跑，其结果会被丢弃。
        /// </summary>
        private async Task<T> GuardAsync<T>(Task<T> task, int seconds, string label, T fallback)
        {
            if (task == null) return fallback;
            int limit = Mathf.Max(2, seconds);
            Task winner = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(limit)));
            if (winner != task)
            {
                Debug.LogWarning("[超时] " + label + " 超过 " + limit + " 秒未返回，改用本地确定性兜底继续。"
                                 + "（可在设置里调大请求超时，或换更快的模型）");
                GameEvents.RaiseNotice(label + "超时（" + limit + "s），已用本地逻辑继续");
                return fallback;
            }
            try
            {
                return await task;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[失败] " + label + ": " + e.Message + "，改用本地兜底");
                return fallback;
            }
        }

        public async Task<bool> StartNewGameAsync(string userEvent)
        {
            if (_busy) return false;
            _busy = true;
            try
            {
                if (string.IsNullOrWhiteSpace(userEvent)) userEvent = "法国大革命";
                userEvent = userEvent.Trim();

                Services.Save = new GameSaveData { userEvent = userEvent };
                _specs.Clear();
                _cache.Clear();

                GameEvents.RaiseNotice("正在检索历史知识库：" + userEvent + "（本地索引，数秒）");
                string knowledgeContext = "";
                int startYear = 0;
                if (Services.Knowledge != null)
                {
                    try
                    {
                        var concrete = Services.Knowledge as KnowledgeService;
                        knowledgeContext = await GuardAsync(
                            Services.Knowledge.GetContextAsync(userEvent, _cts.Token), 15, "知识库检索", "");
                        if (concrete != null)
                            startYear = await GuardAsync(concrete.TryGetStartYearAsync(userEvent, _cts.Token), 10,
                                "知识库年份识别", 0);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning("[导演] 知识库检索失败，继续生成: " + e.Message);
                    }
                }
                GameClock.Reset(startYear > 0 ? startYear : 1789);
                Debug.Log("[导演] 事件=" + userEvent + " 起始年=" + GameClock.BaseYear
                          + " 知识上下文长度=" + (knowledgeContext != null ? knowledgeContext.Length : 0));

                if (Services.Story == null)
                {
                    GameEvents.RaiseNotice("剧情 Agent 未装配，无法开始。");
                    return false;
                }

                GameEvents.RaisePhase(ScenePhase.Preparing);
                GameEvents.RaiseNotice("剧情&核心 Agent 正在推理第一个场景…");
                StoryTurn turn = await Services.Story.BeginAsync(userEvent, knowledgeContext, _cts.Token);
                if (turn == null || turn.scenes == null || turn.scenes.Count == 0)
                {
                    GameEvents.RaiseNotice("剧情 Agent 没有产出场景，请检查模型配置。");
                    return false;
                }

                RegisterTurn(turn);
                SceneSpec first = PickScene(turn);
                return await EnterSceneAsync(first, turn.narrative);
            }
            catch (Exception e)
            {
                Debug.LogError("[导演] 开局失败: " + e);
                GameEvents.RaiseNotice("开局失败：" + e.Message);
                return false;
            }
            finally
            {
                _busy = false;
                SetInputEnabled(true);
                DrainPendingAnchor();
            }
        }

        // ─────────────────────────── 传送锚点推进 ───────────────────────────

        public async Task HandleAnchorAsync(SceneAnchorSpec anchor)
        {
            if (anchor == null) return;
            if (_busy)
            {
                // 生成过程中玩家又走进了另一个锚点：排队，等当前流水线结束后立即处理，不丢事件
                _pendingAnchor = anchor;
                return;
            }
            _busy = true;
            try
            {
                string sceneId = _currentSpec != null ? _currentSpec.sceneId : "";
                GameLog.Record(sceneId, "enter_anchor", anchor.anchorId, anchor.displayName);
                SetInputEnabled(false);

                if (!string.IsNullOrEmpty(anchor.timeDelta) && GameClock.AddDelta(anchor.timeDelta))
                    GameEvents.RaiseNotice("时间推进：" + anchor.timeDelta + " → " + GameClock.Display());

                SceneSpec target = ResolveTarget(anchor);
                string narrative = "";
                if (target == null)
                {
                    GameEvents.RaisePhase(ScenePhase.Preparing);
                    GameEvents.RaiseNotice("剧情&核心 Agent 正在推理接下来发生的事…");
                    var request = new StoryContinueRequest
                    {
                        currentSceneId = sceneId,
                        currentSceneName = _currentSpec != null ? _currentSpec.sceneName : "",
                        anchorId = anchor.anchorId,
                        anchorName = anchor.displayName,
                        anchorDescription = anchor.description,
                        anchorRequirement = anchor.requirement,
                        anchorTimeDelta = anchor.timeDelta,
                        gameMinutes = GameClock.TotalMinutes,
                        recentActions = GameLog.Recent(sceneId, 30),
                    };
                    StoryTurn turn = await Services.Story.ContinueAsync(request, _cts.Token);
                    if (turn == null)
                    {
                        GameEvents.RaiseNotice("剧情 Agent 推理失败，留在当前场景。");
                        return;
                    }
                    if (turn.gameOver)
                    {
                        await EndGameAsync(turn);
                        return;
                    }
                    if (turn.scenes == null || turn.scenes.Count == 0)
                    {
                        GameEvents.RaiseNotice("剧情 Agent 没有给出下一个场景，留在当前场景。");
                        return;
                    }
                    RegisterTurn(turn);
                    target = PickScene(turn);
                    narrative = turn.narrative;
                }
                else
                {
                    GameEvents.RaiseNotice("前往已生成的场景：" + target.sceneName);
                }

                DetachCurrent();
                await EnterSceneAsync(target, narrative);
            }
            catch (Exception e)
            {
                Debug.LogError("[导演] 锚点推进失败: " + e);
                GameEvents.RaiseNotice("场景推进失败：" + e.Message);
            }
            finally
            {
                _busy = false;
                SetInputEnabled(true);
                SaveNow();
                DrainPendingAnchor();
            }
        }

        /// <summary>把生成期间排队的锚点请求接着处理掉（只处理最近一次）。</summary>
        private void DrainPendingAnchor()
        {
            if (_pendingAnchor == null || _busy) return;
            SceneAnchorSpec next = _pendingAnchor;
            _pendingAnchor = null;
            _ = HandleAnchorAsync(next);
        }

        // ─────────────────────────── 进入场景 ───────────────────────────

        private async Task<bool> EnterSceneAsync(SceneSpec spec, string narrative)
        {
            if (spec == null) return false;
            _currentSpec = spec;
            _lastNarrative = narrative ?? "";
            _specs[spec.sceneId] = spec;

            // 调试录制：剧情 Agent 的提示词 / 原始输出 / 推理过程 / 详细描述
            string debugDir = EpicSpark.Debugging.SceneDebugRecorder.BeginTurn(spec.sceneId);
            EpicSpark.Debugging.SceneDebugRecorder.WriteStory(
                debugDir, Services.Story as EpicSpark.Story.StoryAgent, spec, _lastNarrative);

            if (Services.Save != null) Services.Save.currentSceneId = spec.sceneId;
            RuntimeContext.SetScene(spec.sceneId, spec.sceneName);
            if (HudController.Instance != null) HudController.Instance.SetSceneInfo(spec.sceneId, spec.sceneName);
            if (!string.IsNullOrEmpty(narrative)) GameEvents.RaiseNarrative(narrative);

            // 场景复用：拼好的场景是现成的，不重走灰盒与搭建
            if (_cache.Contains(spec.sceneId))
            {
                SceneInstance reused = _cache.Get(spec.sceneId);
                _cache.Activate(reused);
                _currentInstance = reused;
                TeleportPlayer(reused.spawnPoint);
                SceneLoadingSequence.RevealImmediate(reused);
                SceneAssembler.RefreshActors(reused, spec, null);
                MarkVisited(spec.sceneId);
                Debug.Log("[导演] 复用既有场景 " + spec.sceneId);
                GameEvents.RaiseNotice("回到熟悉的场景：" + spec.sceneName);
                return true;
            }

            SetInputEnabled(false);
            try
            {
                // 灰盒阶段
                GameEvents.RaisePhase(ScenePhase.Graybox);
                GameEvents.RaiseNotice("灰盒 Agent 正在规划场景空间…");
                var grayboxRequest = new GrayboxRequest
                {
                    scene = spec,
                    knowledgeContext = "",
                    seed = StableHash(spec.sceneId),
                };
                GrayboxPlan plan = null;
                try
                {
                    plan = await Services.Graybox.PlanAsync(grayboxRequest, _cts.Token);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[导演] 灰盒规划失败: " + e.Message);
                }
                if (plan == null)
                {
                    GameEvents.RaiseNotice("灰盒规划失败，无法生成场景。");
                    return false;
                }
                EpicSpark.Debugging.SceneDebugRecorder.WriteGrayboxPlan(debugDir, plan);

                // 搭建阶段
                GameEvents.RaisePhase(ScenePhase.Build);
                GameEvents.RaiseNotice("搭建 Agent 正在挑选美术资产…");
                var buildRequest = new BuildRequest { scene = spec, plan = plan, sliderValue = _tolerance };
                BuildResult build = null;
                try
                {
                    build = await Services.Build.BuildAsync(buildRequest, _cts.Token);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[导演] 搭建失败: " + e.Message);
                }
                if (build == null)
                {
                    GameEvents.RaiseNotice("搭建失败，无法生成场景。");
                    return false;
                }
                EpicSpark.Debugging.SceneDebugRecorder.WriteBuild(debugDir, build);
                Debug.Log("[导演] 场景 " + spec.sceneId + " 放置 " + build.placed.Count
                          + " 个物体，新生成 " + build.generationCount + " 个资产，警告 " + build.warnings.Count + " 条");

                // 装配 + 加载动画
                SceneInstance instance = await SceneAssembler.AssembleAsync(spec, plan, build, _sceneParent, _cts.Token);
                _currentInstance = instance;
                TeleportPlayer(instance.spawnPoint);
                await SceneLoadingSequence.PlayAsync(instance, instance.spawnPoint, _cts.Token);

                MarkVisited(spec.sceneId);
                return true;
            }
            finally
            {
                SetInputEnabled(true);
            }
        }

        private void DetachCurrent()
        {
            if (_currentInstance == null) return;
            if (_currentSpec != null) MarkVisited(_currentSpec.sceneId);
            _cache.Store(_currentInstance);
            _currentInstance = null;
        }

        private void MarkVisited(string sceneId)
        {
            if (string.IsNullOrEmpty(sceneId)) return;
            if (Services.Save == null) return;
            if (!Services.Save.visitedSceneIds.Contains(sceneId)) Services.Save.visitedSceneIds.Add(sceneId);
            GameSaveData save = Services.Save;
            bool known = false;
            for (int i = 0; i < save.knownScenes.Count; i++)
                if (save.knownScenes[i].sceneId == sceneId) { known = true; break; }
            if (!known && _specs.ContainsKey(sceneId)) save.knownScenes.Add(_specs[sceneId]);
        }

        // ─────────────────────────── 结束 ───────────────────────────

        private async Task EndGameAsync(StoryTurn turn)
        {
            GameEvents.RaisePhase(ScenePhase.Ending);
            SetInputEnabled(false);
            string text = string.IsNullOrEmpty(turn.endingSummary) ? "这一程到此为止。" : turn.endingSummary;
            GameEvents.RaiseNarrative(text);
            GameEvents.RaiseNotice("游戏结束");
            if (HudController.Instance != null) HudController.Instance.SetSceneInfo("游戏结束");
            GameLog.Record(_currentSpec != null ? _currentSpec.sceneId : "", "game_over", "", text);
            if (!string.IsNullOrEmpty(turn.endingAchievement)) GameEvents.RaiseNarrative("成就：" + turn.endingAchievement);
            SaveNow();
            await Task.Yield();
            GameEvents.RaisePhase(ScenePhase.Finished);
        }

        // ─────────────────────────── 工具方法 ───────────────────────────

        private void RegisterTurn(StoryTurn turn)
        {
            if (turn == null || turn.scenes == null) return;
            foreach (SceneSpec scene in turn.scenes)
            {
                if (scene == null || string.IsNullOrEmpty(scene.sceneId)) continue;
                _specs[scene.sceneId] = scene;
                if (!_cache.Contains(scene.sceneId)) _pendingSpecs[scene.sceneId] = scene;
            }
        }

        private readonly Dictionary<string, SceneSpec> _pendingSpecs = new Dictionary<string, SceneSpec>(StringComparer.Ordinal);

        private SceneSpec PickScene(StoryTurn turn)
        {
            if (turn == null || turn.scenes == null || turn.scenes.Count == 0) return null;
            int index = Mathf.Clamp(turn.activeSceneIndex, 0, turn.scenes.Count - 1);
            return turn.scenes[index];
        }

        /// <summary>优先按剧情 Agent 预测的锚点目标场景走；否则交给剧情 Agent 现推。</summary>
        private SceneSpec ResolveTarget(SceneAnchorSpec anchor)
        {
            if (anchor == null || string.IsNullOrEmpty(anchor.targetSceneId)) return null;
            if (_currentSpec != null && anchor.targetSceneId == _currentSpec.sceneId) return null;

            SceneSpec spec;
            if (!_specs.TryGetValue(anchor.targetSceneId, out spec))
            {
                // 目标一定是剧情 Agent 预测过的场景；若没登记过，就交给剧情 Agent 现场推理
                return null;
            }
            // 复用既有场景，或使用本轮一次性预生成的场景
            if (_cache.Contains(spec.sceneId)) return spec;
            if (_pendingSpecs.ContainsKey(spec.sceneId)) return spec;
            return null;
        }

        private void TeleportPlayer(Vector3 spawnPoint)
        {
            EpicSparkPlayerController player = EpicSparkPlayerController.Instance;
            if (player == null) player = RuntimeContext.Player;
            if (player == null)
            {
                Debug.LogWarning("[导演] 场景里没有玩家控制器，跳过传送。");
                return;
            }
            player.Teleport(spawnPoint, Quaternion.identity);
        }

        private void SetInputEnabled(bool enabled)
        {
            EpicSparkPlayerController player = EpicSparkPlayerController.Instance;
            if (player == null) player = RuntimeContext.Player;
            if (player != null) player.SetInputEnabled(enabled);
        }

        public void SaveNow()
        {
            if (Services.Save == null) return;
            GameSaveData save = Services.Save;
            save.gameMinutes = GameClock.TotalMinutes;
            save.actionLog = GameLog.Recent("", 300);
            save.knownScenes.Clear();
            foreach (KeyValuePair<string, SceneSpec> pair in _specs)
            {
                if (pair.Value != null) save.knownScenes.Add(pair.Value);
            }
            if (_currentSpec != null) save.currentSceneId = _currentSpec.sceneId;
            EpicSparkPlayerController player = EpicSparkPlayerController.Instance;
            if (player != null)
            {
                save.playerHp = player.CurrentHp;
                if (player.Tools != null) save.heldTool = player.Tools.Current;
                save.visitedSceneIds = new List<string>(save.visitedSceneIds);
            }
            SaveService.Save(save);
        }

        /// <summary>
        /// 按 sceneId 进入场景（存档恢复 / 剧情指定目标 / 调试用）。
        /// 若该场景已在缓存中，会走复用路径：不重跑灰盒与搭建。
        /// </summary>
        public async Task<bool> EnterSceneByIdAsync(string sceneId)
        {
            if (string.IsNullOrEmpty(sceneId)) return false;
            if (_busy)
            {
                Debug.LogWarning("[导演] 正在生成中，忽略进入场景请求: " + sceneId);
                return false;
            }
            SceneSpec spec;
            if (!_specs.TryGetValue(sceneId, out spec))
            {
                // 存档里可能有场景定义
                if (Services.Save != null && Services.Save.knownScenes != null)
                {
                    for (int i = 0; i < Services.Save.knownScenes.Count; i++)
                    {
                        SceneSpec known = Services.Save.knownScenes[i];
                        if (known != null && known.sceneId == sceneId) { spec = known; _specs[sceneId] = known; break; }
                    }
                }
            }
            if (spec == null)
            {
                Debug.LogWarning("[导演] 找不到场景定义: " + sceneId);
                return false;
            }

            _busy = true;
            try
            {
                DetachCurrent();
                return await EnterSceneAsync(spec, "");
            }
            finally
            {
                _busy = false;
                SetInputEnabled(true);
                DrainPendingAnchor();
            }
        }

        // ─────────────────────────── 读档 ───────────────────────────

        /// <summary>从存档恢复：时钟、行为日志、玩家状态、存档记录的场景定义，然后重建当前场景。</summary>
        public async Task<bool> ResumeAsync()
        {
            GameSaveData save = SaveService.Load();
            if (save == null || string.IsNullOrEmpty(save.userEvent))
            {
                GameEvents.RaiseNotice("没有可继续的存档。");
                return false;
            }
            if (_busy) return false;
            _busy = true;
            try
            {
                Services.Save = save;
                _specs.Clear();
                _pendingSpecs.Clear();
                // 读档要彻底重建世界：先销毁当前场景，再清空复用缓存
                if (_currentInstance != null)
                {
                    SceneAssembler.DestroyInstance(_currentInstance);
                    _currentInstance = null;
                }
                _cache.Clear();
                _currentSpec = null;
                GameLog.Seed(save.actionLog);

                string knowledgeContext = "";
                int startYear = 0;
                if (Services.Knowledge != null)
                {
                    try
                    {
                        knowledgeContext = await GuardAsync(Services.Knowledge.GetContextAsync(save.userEvent, _cts.Token), 15, "知识库检索", "");
                        var concrete = Services.Knowledge as KnowledgeService;
                        if (concrete != null) startYear = await GuardAsync(concrete.TryGetStartYearAsync(save.userEvent, _cts.Token), 10, "知识库年份识别", 0);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning("[导演] 读档知识检索失败: " + e.Message);
                    }
                }
                GameClock.SetFromSave(save.gameMinutes, startYear > 0 ? startYear : 1789);

                // 存档里保存了场景定义，直接按 sceneId 还原；没有再退回剧情首幕
                for (int i = 0; i < save.knownScenes.Count; i++)
                {
                    SceneSpec known = save.knownScenes[i];
                    if (known == null || string.IsNullOrEmpty(known.sceneId)) continue;
                    _specs[known.sceneId] = known;
                }
                SceneSpec target = null;
                _specs.TryGetValue(save.currentSceneId ?? "", out target);
                if (target == null && Services.Story != null)
                {
                    GameEvents.RaiseNotice("正在恢复剧情进度…");
                    StoryTurn turn = await Services.Story.BeginAsync(save.userEvent, knowledgeContext, _cts.Token);
                    if (turn != null && turn.scenes != null && turn.scenes.Count > 0)
                    {
                        RegisterTurn(turn);
                        target = PickScene(turn);
                    }
                }
                if (target == null)
                {
                    GameEvents.RaiseNotice("存档损坏：找不到可恢复的场景。");
                    return false;
                }

                RestorePlayerState(save);
                Debug.Log("[导演] 读档：" + save.userEvent + " → " + target.sceneId + " @ " + GameClock.Display()
                          + "，历史行为 " + (save.actionLog != null ? save.actionLog.Count : 0) + " 条");
                return await EnterSceneAsync(target, "【继续】" + save.userEvent + " · " + GameClock.Display());
            }
            catch (Exception e)
            {
                Debug.LogError("[导演] 读档失败: " + e);
                return false;
            }
            finally
            {
                _busy = false;
                SetInputEnabled(true);
                DrainPendingAnchor();
            }
        }

        private static void RestorePlayerState(GameSaveData save)
        {
            EpicSparkPlayerController player = EpicSparkPlayerController.Instance;
            if (player == null) return;
            if (save.playerHp > 0f) player.CurrentHp = Mathf.Clamp(save.playerHp, 1f, player.MaxHp);
            if (player.Tools != null && save.heldTool != ToolKind.None) player.Tools.EquipSilent(save.heldTool);
        }

        public void Shutdown()
        {
            try { _cts.Cancel(); } catch (Exception) { }
            _cache.Clear();
        }

        private static int StableHash(string text)
        {
            if (string.IsNullOrEmpty(text)) return 17;
            unchecked
            {
                int hash = 23;
                for (int i = 0; i < text.Length; i++) hash = hash * 31 + text[i];
                return hash & 0x7FFFFFFF;
            }
        }
    }
}
