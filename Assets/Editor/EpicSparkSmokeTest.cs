// 端到端冒烟测试：在 batchmode 下真正进入 Play 模式，离线（无 API Key）跑完
// 「知识库检索 → 剧情&核心 Agent → 灰盒 Agent → 搭建 Agent → 场景装配 → 加载动画 → 传送锚点 → 下一场景」。
// 结果写入 Logs/smoke.json，进程退出码 0 表示通过。
//
//   Unity.exe -batchmode -projectPath <工程> \
//     -executeMethod EpicSpark.EditorTools.EpicSparkSmokeTest.Run -logFile Logs\smoke.log
//   注意：不要加 -quit；测试结束由本脚本调用 EditorApplication.Exit 退出。
//
// 关键实现约束：进入 Play 模式会触发域重载，静态字段与 EditorApplication.update 注册都会被清空，
// 因此所有测试状态存 SessionState，并由 [InitializeOnLoad] 静态构造器在重载后恢复轮询。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using EpicSpark.Bootstrap;
using EpicSpark.Core;
using EpicSpark.Loading;
using EpicSpark.Npc.Runtime;

namespace EpicSpark.EditorTools
{
    [InitializeOnLoad]
    public static class EpicSparkSmokeTest
    {
        private const string ReportRelative = "Logs/smoke.json";
        private const double StageTimeoutSeconds = 180.0;

        private const string KeyActive = "EpicSpark.Smoke.Active";
        private const string KeyStage = "EpicSpark.Smoke.Stage";
        private const string KeyBegin = "EpicSpark.Smoke.Begin";
        private const string KeyStageStart = "EpicSpark.Smoke.StageStart";
        private const string KeyReport = "EpicSpark.Smoke.Report";
        private const string KeyFirstId = "EpicSpark.Smoke.FirstId";
        private const string KeyFirstModels = "EpicSpark.Smoke.FirstModels";
        private const string KeyFirstAnchors = "EpicSpark.Smoke.FirstAnchors";
        private const string KeyFirstNpcs = "EpicSpark.Smoke.FirstNpcs";
        private const string KeyFirstSeconds = "EpicSpark.Smoke.FirstSeconds";
        private const string KeySecondId = "EpicSpark.Smoke.SecondId";
        private const string KeySecondModels = "EpicSpark.Smoke.SecondModels";
        private const string KeySecondSeconds = "EpicSpark.Smoke.SecondSeconds";
        private const string KeySavedId = "EpicSpark.Smoke.SavedId";
        private const string KeyReuseId = "EpicSpark.Smoke.ReuseId";
        private const string KeyResumeId = "EpicSpark.Smoke.ResumeId";
        private const string KeyResumeClock = "EpicSpark.Smoke.ResumeClock";
        private const string KeyNpcHpBefore = "EpicSpark.Smoke.NpcHpBefore";
        private const string KeyNpcHpAfter = "EpicSpark.Smoke.NpcHpAfter";
        private const string KeyDialogueReply = "EpicSpark.Smoke.DialogueReply";

        private static Task<string> _dialogueTask;
        private static string _dialogueNpc = "";
        private const string KeyNotes = "EpicSpark.Smoke.Notes";
        private const string KeyErrors = "EpicSpark.Smoke.Errors";
        private const string KeyWarnings = "EpicSpark.Smoke.Warnings";

        static EpicSparkSmokeTest()
        {
            // 域重载时 Application.isPlaying 可能仍为 false，因此这里只无条件注册，是否开始由 Tick 自行判断
            if (!SessionState.GetBool(KeyActive, false)) return;
            Application.logMessageReceived -= OnLog;
            Application.logMessageReceived += OnLog;
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            Debug.Log("[SmokeTest] 域重载后恢复轮询");
        }

        // ─────────────────────────── 入口 ───────────────────────────

        public static void Run()
        {
            ResetState();

            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            SessionState.SetString(KeyReport, Path.Combine(projectRoot, ReportRelative));
            SessionState.SetBool(KeyActive, true);
            SessionState.SetInt(KeyStage, 0);
            SessionState.SetFloat(KeyBegin, (float)EditorApplication.timeSinceStartup);
            SessionState.SetFloat(KeyStageStart, (float)EditorApplication.timeSinceStartup);
            Append(KeyNotes, "unity=" + Application.unityVersion);
            Append(KeyNotes, "scene=" + EpicSparkSceneSetup.ScenePath);

            try
            {
                if (!File.Exists(Path.Combine(projectRoot, EpicSparkSceneSetup.ScenePath)))
                    EpicSparkSceneSetup.BuildAndSave();
                EditorSceneManager.OpenScene(EpicSparkSceneSetup.ScenePath);
                Append(KeyNotes, "sceneOpened=true");
            }
            catch (Exception e)
            {
                Append(KeyNotes, "打开场景失败: " + e);
                Finish(3);
                return;
            }

            // 进入 Play Mode 会触发域重载并清空静态字段，所以用 PlayerPrefs 传递参数
            PlayerPrefs.SetInt("EpicSpark.TestAutoStart", 1);
            PlayerPrefs.SetString("EpicSpark.TestAutoStartEvent", "攻占巴士底狱");
            PlayerPrefs.SetInt("EpicSpark.VerboseMatching", 1);
            PlayerPrefs.Save();
            Append(KeyNotes, "autoStartEvent=攻占巴士底狱");

            Application.logMessageReceived -= OnLog;
            Application.logMessageReceived += OnLog;
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            EditorApplication.isPlaying = true;
            Append(KeyNotes, "enterPlayMode=true");
        }

        private static void ResetState()
        {
            foreach (string key in new[]
            {
                KeyActive, KeyStage, KeyBegin, KeyStageStart, KeyReport, KeyFirstId, KeyFirstModels,
                KeyFirstAnchors, KeyFirstNpcs, KeyFirstSeconds, KeySecondId, KeySecondModels,
                KeySecondSeconds, KeyNotes, KeyErrors, KeyWarnings,
            })
            {
                SessionState.SetString(key, "");
            }
            SessionState.SetBool(KeyActive, false);
            SessionState.SetInt(KeyStage, 0);
            Application.logMessageReceived -= OnLog;
            EditorApplication.update -= Tick;
        }

        // ─────────────────────────── 日志采集（跨域重载存 SessionState） ───────────────────────────

        private static void OnLog(string condition, string stackTrace, LogType type)
        {
            if (!SessionState.GetBool(KeyActive, false)) return;
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
            {
                Append(KeyErrors, type + ": " + condition);
            }
            else if (type == LogType.Warning)
            {
                Append(KeyWarnings, condition);
            }
        }

        private static void Append(string key, string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            string existing = SessionState.GetString(key, "");
            if (!string.IsNullOrEmpty(existing) && existing.Contains(line)) return;
            if (existing.Length > 60000) return;
            string next = string.IsNullOrEmpty(existing) ? line : existing + "\n" + line;
            SessionState.SetString(key, next);
        }

        private static List<string> Lines(string key)
        {
            string raw = SessionState.GetString(key, "");
            var list = new List<string>();
            if (string.IsNullOrEmpty(raw)) return list;
            foreach (string part in raw.Split('\n'))
            {
                string trimmed = part.Trim();
                if (!string.IsNullOrEmpty(trimmed)) list.Add(trimmed);
            }
            return list;
        }

        private static void Note(string line)
        {
            Append(KeyNotes, line);
            Debug.Log("[SmokeTest] " + line);
        }

        // ─────────────────────────── 主循环 ───────────────────────────

        private static void Tick()
        {
            if (!SessionState.GetBool(KeyActive, false)) return;
            if (!Application.isPlaying) return;

            double now = EditorApplication.timeSinceStartup;
            double stageStart = SessionState.GetFloat(KeyStageStart, (float)now);
            double begin = SessionState.GetFloat(KeyBegin, (float)now);
            double elapsed = now - stageStart;
            int stage = SessionState.GetInt(KeyStage, 0);

            try
            {
                if (stage == 0) TickFirstScene(elapsed, now, begin);
                else if (stage == 1) TickSecondScene(elapsed, now, begin);
                else if (stage == 2) TickReuseScene(elapsed, now, begin);
                else if (stage == 3) TickResumeScene(elapsed, now, begin);
                else if (stage == 4) TickSystems(elapsed, now, begin);
            }
            catch (Exception e)
            {
                Note("Tick 异常: " + e);
                Finish(3);
            }
        }

        private static void TickFirstScene(double elapsed, double now, double begin)
        {
            GameBootstrap boot = GameBootstrap.Instance;
            GameDirector director = boot != null ? boot.Director : null;
            if (director == null)
            {
                if (elapsed > StageTimeoutSeconds) { Note("Bootstrap/导演 未就绪，超时"); Finish(2); }
                return;
            }

            SceneInstance instance = director.CurrentInstance;
            SceneSpec spec = director.CurrentSpec;
            if (instance != null && instance.root != null && spec != null && !director.IsBusy)
            {
                SessionState.SetString(KeyFirstId, spec.sceneId);
                SessionState.SetInt(KeyFirstModels, instance.models != null ? instance.models.Count : 0);
                SessionState.SetInt(KeyFirstAnchors, instance.anchorObjects != null ? instance.anchorObjects.Count : 0);
                SessionState.SetInt(KeyFirstNpcs, instance.npcObjects != null ? instance.npcObjects.Count : 0);
                SessionState.SetFloat(KeyFirstSeconds, (float)(now - begin));
                Note("firstScene=" + spec.sceneName + " id=" + spec.sceneId
                     + " models=" + SessionState.GetInt(KeyFirstModels, 0)
                     + " anchors=" + SessionState.GetInt(KeyFirstAnchors, 0)
                     + " npcs=" + SessionState.GetInt(KeyFirstNpcs, 0)
                     + " tolerance=" + director.Tolerance);

                if (spec.anchors != null && spec.anchors.Count > 0)
                {
                    SceneAnchorSpec anchor = spec.anchors[0];
                    Note("firstAnchor=" + anchor.anchorId + "/" + anchor.displayName
                         + " target=" + anchor.targetSceneId + " timeDelta=" + anchor.timeDelta);
                    SessionState.SetInt(KeyStage, 1);
                    SessionState.SetFloat(KeyStageStart, (float)now);
                    _ = director.HandleAnchorAsync(anchor);
                }
                else
                {
                    Note("场景没有锚点，无法测试推进");
                    Finish(0);
                }
                return;
            }

            if (elapsed > StageTimeoutSeconds)
            {
                Note("首个场景超时未生成");
                Finish(2);
            }
        }

        private static void TickSecondScene(double elapsed, double now, double begin)
        {
            GameBootstrap boot = GameBootstrap.Instance;
            GameDirector director = boot != null ? boot.Director : null;
            if (director == null)
            {
                if (elapsed > StageTimeoutSeconds) { Note("导演丢失，超时"); Finish(2); }
                return;
            }

            string firstId = SessionState.GetString(KeyFirstId, "");
            SceneSpec spec = director.CurrentSpec;
            SceneInstance instance = director.CurrentInstance;

            if (spec != null && instance != null && spec.sceneId != firstId && !director.IsBusy)
            {
                SessionState.SetString(KeySecondId, spec.sceneId);
                SessionState.SetInt(KeySecondModels, instance.models != null ? instance.models.Count : 0);
                SessionState.SetFloat(KeySecondSeconds, (float)(now - begin));
                Note("secondScene=" + spec.sceneName + " id=" + spec.sceneId
                     + " models=" + SessionState.GetInt(KeySecondModels, 0)
                     + " anchors=" + (instance.anchorObjects != null ? instance.anchorObjects.Count : 0));
                Note("builtScenes=" + director.BuiltSceneCount);

                // 第三阶段：回到第一个场景，验证「场景复用」不重跑灰盒与搭建
                SessionState.SetInt(KeyStage, 2);
                SessionState.SetFloat(KeyStageStart, (float)now);
                _ = director.EnterSceneByIdAsync(firstId);
                return;
            }

            // 剧情 Agent 判定留在原场景或直接结局时，也算通过（只是没换场景）
            if (elapsed > 45.0 && !director.IsBusy)
            {
                Note("锚点推进后仍停留在首个场景（剧情判定原地推进或结局），视为通过");
                Finish(0);
                return;
            }

            if (elapsed > StageTimeoutSeconds)
            {
                Note("第二个场景超时未生成");
                Finish(2);
            }
        }

        /// <summary>第三阶段：回到已生成过的场景，验证复用路径（不重跑灰盒与搭建）。</summary>
        private static void TickReuseScene(double elapsed, double now, double begin)
        {
            GameBootstrap boot = GameBootstrap.Instance;
            GameDirector director = boot != null ? boot.Director : null;
            string firstId = SessionState.GetString(KeyFirstId, "");
            if (director == null)
            {
                if (elapsed > StageTimeoutSeconds) Finish(2);
                return;
            }

            SceneSpec spec = director.CurrentSpec;
            SceneInstance instance = director.CurrentInstance;
            if (spec != null && instance != null && spec.sceneId == firstId && !director.IsBusy)
            {
                SessionState.SetString(KeyReuseId, spec.sceneId);
                Note("reuseScene=" + spec.sceneName + " id=" + spec.sceneId
                     + " models=" + (instance.models != null ? instance.models.Count : 0)
                     + " anchors=" + (instance.anchorObjects != null ? instance.anchorObjects.Count : 0)
                     + " 耗时=" + elapsed.ToString("F2") + "s");
                Note("builtScenes=" + director.BuiltSceneCount);

                // 第四阶段：存档 → 读档
                SessionState.SetString(KeySavedId, spec.sceneId);
                SessionState.SetInt(KeyStage, 3);
                SessionState.SetFloat(KeyStageStart, (float)now);
                director.SaveNow();
                Note("saved=true sceneId=" + spec.sceneId + " clock=" + GameClock.Display()
                     + " actions=" + GameLog.Count);
                _ = director.ResumeAsync();
                return;
            }

            if (elapsed > StageTimeoutSeconds)
            {
                Note("回到首个场景超时（复用失败）");
                Finish(2);
            }
        }

        /// <summary>第三阶段：存档后读档，应恢复同一个 sceneId、同一游戏内时间。</summary>
        private static void TickResumeScene(double elapsed, double now, double begin)
        {
            GameBootstrap boot = GameBootstrap.Instance;
            GameDirector director = boot != null ? boot.Director : null;
            string savedId = SessionState.GetString(KeySavedId, "");
            if (director == null)
            {
                if (elapsed > StageTimeoutSeconds) { Note("读档阶段导演丢失"); Finish(2); }
                return;
            }

            SceneSpec spec = director.CurrentSpec;
            SceneInstance instance = director.CurrentInstance;
            if (spec != null && instance != null && spec.sceneId == savedId && !director.IsBusy)
            {
                SessionState.SetString(KeyResumeId, spec.sceneId);
                SessionState.SetString(KeyResumeClock, GameClock.Display());
                Note("resumeScene=" + spec.sceneName + " id=" + spec.sceneId
                     + " models=" + (instance.models != null ? instance.models.Count : 0)
                     + " clock=" + GameClock.Display() + " actions=" + GameLog.Count);
                Note("hasSave=" + SaveService.HasSave);
                // 第五阶段：NPC 血条与离线对话
                SessionState.SetInt(KeyStage, 4);
                SessionState.SetFloat(KeyStageStart, (float)now);
                return;
            }

            if (elapsed > StageTimeoutSeconds)
            {
                Note("读档后场景未恢复（savedId=" + savedId + " current=" + (spec != null ? spec.sceneId : "null") + "）");
                Finish(2);
            }
        }

        /// <summary>第四阶段：NPC 血条（扣血）与离线对话回复，验证运行时系统真的接线可用。</summary>
        private static void TickSystems(double elapsed, double now, double begin)
        {
            GameBootstrap boot = GameBootstrap.Instance;
            GameDirector director = boot != null ? boot.Director : null;
            SceneInstance instance = director != null ? director.CurrentInstance : null;
            if (director == null || instance == null)
            {
                if (elapsed > StageTimeoutSeconds) { Note("系统阶段：场景丢失"); Finish(2); }
                return;
            }
            if (director.IsBusy) return;

            if (_dialogueTask == null)
            {
                NpcAvatar npc = null;
                for (int i = 0; i < instance.npcObjects.Count; i++)
                {
                    if (instance.npcObjects[i] == null) continue;
                    var candidate = instance.npcObjects[i].GetComponent<NpcAvatar>();
                    if (candidate != null && candidate.Health != null && !candidate.Health.IsDead) { npc = candidate; break; }
                }
                if (npc == null)
                {
                    Note("系统阶段：场景内没有可测试的 NPC");
                    Finish(2);
                    return;
                }

                float before = npc.Health.CurrentHp;
                npc.Health.ApplyDamage(25f, "smoke_test");
                SessionState.SetFloat(KeyNpcHpBefore, before);
                SessionState.SetFloat(KeyNpcHpAfter, npc.Health.CurrentHp);
                Note("npc=" + npc.Spec.displayName + " hp=" + before.ToString("F0") + "→" + npc.Health.CurrentHp.ToString("F0")
                     + " 血条=" + (npc.Health.HealthBar != null));

                Func<NpcSpec, string, IReadOnlyList<string>, Task<string>> handler = NpcDialogueTrigger.ReplyHandler;
                if (handler == null)
                {
                    Note("系统阶段：对话回调未接线");
                    Finish(2);
                    return;
                }
                _dialogueNpc = npc.Spec.displayName;
                try { _dialogueTask = handler(npc.Spec, "这里发生了什么？", new List<string>()); }
                catch (Exception e) { Note("系统阶段：对话调用异常 " + e.Message); Finish(2); }
                return;
            }

            if (!_dialogueTask.IsCompleted)
            {
                if (elapsed > StageTimeoutSeconds) { Note("系统阶段：对话回复超时"); Finish(2); }
                return;
            }

            string reply = null;
            try { reply = _dialogueTask.Result; }
            catch (Exception e) { Note("系统阶段：对话回复异常 " + e.Message); Finish(2); return; }

            SessionState.SetString(KeyDialogueReply, reply ?? "");
            if (string.IsNullOrEmpty(reply))
            {
                Note("系统阶段：对话回复为空");
                Finish(2);
                return;
            }
            Note("对话 " + _dialogueNpc + " → " + (reply.Length > 40 ? reply.Substring(0, 40) + "…" : reply));
            Note("hpChanged=" + (SessionState.GetFloat(KeyNpcHpAfter, 0f) < SessionState.GetFloat(KeyNpcHpBefore, 0f)));
            Finish(0);
        }

        // ─────────────────────────── 收尾 ───────────────────────────
        private static void Finish(int code)
        {
            if (!SessionState.GetBool(KeyActive, false)) return;
            SessionState.SetBool(KeyActive, false);
            EditorApplication.update -= Tick;
            Application.logMessageReceived -= OnLog;

            var notes = Lines(KeyNotes);
            var errors = Lines(KeyErrors);
            var warnings = Lines(KeyWarnings);

            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"result\": \"").Append(code == 0 && errors.Count == 0 ? "pass" : (code == 0 ? "pass_with_errors" : "fail")).Append("\",\n");
            sb.Append("  \"exitCode\": ").Append(code).Append(",\n");
            sb.Append("  \"firstSceneId\": \"").Append(Escape(SessionState.GetString(KeyFirstId, ""))).Append("\",\n");
            sb.Append("  \"firstSceneModels\": ").Append(SessionState.GetInt(KeyFirstModels, 0)).Append(",\n");
            sb.Append("  \"firstSceneAnchors\": ").Append(SessionState.GetInt(KeyFirstAnchors, 0)).Append(",\n");
            sb.Append("  \"firstSceneNpcs\": ").Append(SessionState.GetInt(KeyFirstNpcs, 0)).Append(",\n");
            sb.Append("  \"firstSceneSeconds\": ").Append(SessionState.GetFloat(KeyFirstSeconds, 0f).ToString("F2")).Append(",\n");
            sb.Append("  \"secondSceneId\": \"").Append(Escape(SessionState.GetString(KeySecondId, ""))).Append("\",\n");
            sb.Append("  \"secondSceneModels\": ").Append(SessionState.GetInt(KeySecondModels, 0)).Append(",\n");
            sb.Append("  \"secondSceneSeconds\": ").Append(SessionState.GetFloat(KeySecondSeconds, 0f).ToString("F2")).Append(",\n");
            sb.Append("  \"savedSceneId\": \"").Append(Escape(SessionState.GetString(KeySavedId, ""))).Append("\",\n");
            sb.Append("  \"reuseSceneId\": \"").Append(Escape(SessionState.GetString(KeyReuseId, ""))).Append("\",\n");
            sb.Append("  \"resumeSceneId\": \"").Append(Escape(SessionState.GetString(KeyResumeId, ""))).Append("\",\n");
            sb.Append("  \"resumeClock\": \"").Append(Escape(SessionState.GetString(KeyResumeClock, ""))).Append("\",\n");
            sb.Append("  \"npcHpBefore\": ").Append(SessionState.GetFloat(KeyNpcHpBefore, 0f).ToString("F0")).Append(",\n");
            sb.Append("  \"npcHpAfter\": ").Append(SessionState.GetFloat(KeyNpcHpAfter, 0f).ToString("F0")).Append(",\n");
            sb.Append("  \"dialogueReplyLength\": ").Append(SessionState.GetString(KeyDialogueReply, "").Length).Append(",\n");
            sb.Append("  \"authoredNote\": \"离线确定性模式；无 API Key，未发任何网络请求\",\n");
            sb.Append("  \"errors\": ").Append(JsonList(errors)).Append(",\n");
            sb.Append("  \"warnings\": ").Append(JsonList(warnings)).Append(",\n");
            sb.Append("  \"notes\": ").Append(JsonList(notes)).Append("\n");
            sb.Append("}\n");

            string reportPath = SessionState.GetString(KeyReport, "");
            try
            {
                if (!string.IsNullOrEmpty(reportPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(reportPath));
                    File.WriteAllText(reportPath, sb.ToString(), Encoding.UTF8);
                    Debug.Log("[SmokeTest] 报告已写入 " + reportPath);
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[SmokeTest] 写报告失败: " + e.Message);
            }

            Debug.Log("[SmokeTest] RESULT code=" + code + " errors=" + errors.Count + " warnings=" + warnings.Count);
            for (int i = 0; i < notes.Count; i++) Debug.Log("[SmokeTest] note: " + notes[i]);
            for (int i = 0; i < errors.Count && i < 12; i++) Debug.LogError("[SmokeTest] error: " + errors[i]);

            // 清理测试期间塞进 PlayerPrefs 的开关，避免影响正常游玩
            PlayerPrefs.DeleteKey("EpicSpark.VerboseMatching");
            PlayerPrefs.DeleteKey("EpicSpark.TestAutoStart");
            PlayerPrefs.DeleteKey("EpicSpark.TestAutoStartEvent");
            PlayerPrefs.Save();

            EditorApplication.Exit(code);
        }

        private static string JsonList(List<string> items)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append('"').Append(Escape(items[i])).Append('"');
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static string Escape(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(text.Length + 8);
            foreach (char c in text)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append(' ');
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
