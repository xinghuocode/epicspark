// 语音对话模块自检（编辑器 / 批处理）。不进入 Play 模式，只做静态与纯逻辑校验：
//   1) 迁移后的核心类型都在（AsrClient / TtsClient / MicCapture / PcmPlayback / MiniJson …）
//   2) VoiceConfig 能读到 StreamingAssets/VoiceConfig.json（只报告 Key 是否存在，不打印明文）
//   3) MiniJson 能正确解析 DashScope ASR / TTS 的典型事件帧
//   4) VoiceText 的朗读清洗有效
//   5) VoiceDialoguePanel / NpcDialogueTrigger 的公开 API 与控制器约定一致
//
// 批处理用法（Unity 关闭时）：
//   Unity.exe -batchmode -projectPath <工程> -executeMethod EpicSpark.EditorTools.EpicSparkVoiceSelfTest.Run -logFile Logs\voice-selftest.log
// 结果写入 Logs/voice-selftest.json；进程退出码 0 = 通过，2 = 有失败项。
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using EpicSpark.Npc.Runtime;
using EpicSpark.UI;
using EpicSpark.Voice;

namespace EpicSpark.EditorTools
{
    /// <summary>语音模块自检。也可在编辑器菜单 Tools/EpicSpark/语音模块自检 手动运行。</summary>
    public static class EpicSparkVoiceSelfTest
    {
        private static readonly StringBuilder Report = new StringBuilder();
        private static int _failed;

        [MenuItem("Tools/EpicSpark/语音模块自检")]
        public static void RunFromMenu()
        {
            Run(false);
        }

        /// <summary>批处理入口：完成后退出编辑器进程。</summary>
        public static void Run()
        {
            Run(true);
        }

        private static void Run(bool exitProcess)
        {
            Report.Length = 0;
            _failed = 0;

            Line("unity=" + Application.unityVersion);
            Line("streamingAssets=" + Application.streamingAssetsPath);

            CheckTypes();
            CheckConfig();
            CheckMiniJson();
            CheckVoiceText();
            CheckPanelApi();
            CheckTriggerApi();

            DirectoryInfo parent = Directory.GetParent(Application.dataPath);
            string projectRoot = parent != null ? parent.FullName : ".";
            string logDir = Path.Combine(projectRoot, "Logs");
            string output = Path.Combine(logDir, "voice-selftest.json");
            try
            {
                Directory.CreateDirectory(logDir);
                var json = new StringBuilder();
                json.Append("{\n");
                json.Append("  \"passed\": ").Append(_failed == 0 ? "true" : "false").Append(",\n");
                json.Append("  \"failedCount\": ").Append(_failed).Append(",\n");
                json.Append("  \"report\": [\n");
                string[] lines = Report.ToString().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < lines.Length; i++)
                {
                    json.Append("    \"").Append(Escape(lines[i])).Append("\"")
                        .Append(i == lines.Length - 1 ? "\n" : ",\n");
                }
                json.Append("  ]\n}\n");
                File.WriteAllText(output, json.ToString(), new UTF8Encoding(false));
                Debug.Log("[语音自检] 结果已写入 " + output);
            }
            catch (Exception e)
            {
                Debug.LogError("[语音自检] 写报告失败: " + e.Message);
            }

            if (_failed == 0)
            {
                Debug.Log("[语音自检] 全部通过");
            }
            else
            {
                Debug.LogError("[语音自检] 有 " + _failed + " 项失败，详见控制台与 " + output);
            }

            if (exitProcess) EditorApplication.Exit(_failed == 0 ? 0 : 2);
        }

        // ─────────────────────────── 各项检查 ───────────────────────────

        private static void CheckTypes()
        {
            Section("类型迁移");
            Type[] types =
            {
                typeof(VoiceDialogueController), typeof(VoiceConfig), typeof(VoiceText),
                typeof(AsrClient), typeof(TtsClient), typeof(MicCapture), typeof(PcmPlayback), typeof(MiniJson),
            };
            for (int i = 0; i < types.Length; i++)
            {
                Type type = types[i];
                bool ok = type != null && type.FullName != null && type.FullName.StartsWith("EpicSpark.Voice");
                Check(ok, "type " + (type != null ? type.Name : "null"));
            }
        }

        private static void CheckConfig()
        {
            Section("配置");
            string source;
            VoiceConfig config = VoiceConfig.Load(out source);
            Check(config != null, "VoiceConfig.Load()");
            Check(!string.IsNullOrEmpty(config.asrUrl) && config.asrUrl.StartsWith("wss://"), "asrUrl=" + config.asrUrl);
            Check(!string.IsNullOrEmpty(config.ttsUrl) && config.ttsUrl.StartsWith("wss://"), "ttsUrl=" + config.ttsUrl);
            Check(config.asrSampleRate == 16000, "asrSampleRate=" + config.asrSampleRate);
            Check(config.ttsSampleRate >= 8000, "ttsSampleRate=" + config.ttsSampleRate);
            Check(config.maxUtteranceSeconds > 1f, "maxUtteranceSeconds=" + config.maxUtteranceSeconds);
            Line("hasVoiceKey=" + config.HasKey + " key=" + VoiceConfig.MaskKey(config.apiKey));
            Line("configSource=" + source);
            // 配置文件不存在时仍然要有默认值，不视为失败；这里只提示。
            if (!source.Contains("VoiceConfig.json"))
                Line("note=未找到 Assets/StreamingAssets/VoiceConfig.json，使用内置默认值（没有 Key 时只能打字对话）");
        }

        private static void CheckMiniJson()
        {
            Section("MiniJson 事件解析");
            string asrFrame =
                "{\"header\":{\"event\":\"result-generated\",\"task_id\":\"t1\"}," +
                "\"payload\":{\"output\":{\"sentence\":{\"text\":\"你好，这里是塞西尔。\"," +
                "\"sentence_end\":true,\"heartbeat\":false}}}}";
            object asr = MiniJson.Parse(asrFrame);
            Check(asr != null, "ASR 帧可解析");
            Check(MiniJson.GetString(asr, "header", "event") == "result-generated", "ASR event=result-generated");
            Check(MiniJson.GetString(asr, "payload", "output", "sentence", "text") == "你好，这里是塞西尔。", "ASR sentence 文本");
            Check(MiniJson.GetBool(asr, "payload", "output", "sentence", "sentence_end"), "ASR sentence_end");
            Check(!MiniJson.GetBool(asr, "payload", "output", "sentence", "heartbeat"), "ASR heartbeat=false");

            string ttsFrame = "{\"type\":\"response.audio.delta\",\"delta\":\"AQIDBA==\"}";
            object tts = MiniJson.Parse(ttsFrame);
            Check(tts != null && MiniJson.GetString(tts, "type") == "response.audio.delta", "TTS event=response.audio.delta");
            string delta = MiniJson.GetString(tts, "delta");
            byte[] pcm = null;
            try { pcm = Convert.FromBase64String(delta); }
            catch (Exception) { }
            Check(pcm != null && pcm.Length == 4, "TTS delta base64 解码=" + (pcm != null ? pcm.Length : 0) + " 字节");

            string failFrame = "{\"header\":{\"event\":\"task-failed\",\"error_message\":\"invalid api key\"}}";
            object fail = MiniJson.Parse(failFrame);
            Check(MiniJson.GetString(fail, "header", "error_message") == "invalid api key", "ASR task-failed 错误信息");
        }

        private static void CheckVoiceText()
        {
            Section("朗读清洗");
            string raw = "（他后退半步，声音压低）别在这儿站着——传令的人最怕被问第二遍。**跟我走**。";
            string clean = VoiceText.SanitizeForSpeech(raw);
            Check(clean.IndexOf('（') < 0 && clean.IndexOf('）') < 0, "去掉括号动作描写: " + clean);
            Check(clean.IndexOf('*') < 0, "去掉 Markdown 符号");
            Check(clean.Contains("别在这儿站着"), "保留正文字句");
            Check(VoiceText.SanitizeForSpeech("😀👍").Length == 0, "表情符号清洗后为空");
            Check(VoiceText.SanitizeForSpeech("（只有动作）").Length == 0, "纯动作描写清洗后为空");
        }

        private static void CheckPanelApi()
        {
            Section("VoiceDialoguePanel API");
            Type panel = typeof(VoiceDialoguePanel);
            Check(panel != null, "类型存在");
            string[] methods =
            {
                "Show", "Hide", "SetStatus", "SetPlayerLine", "SetNpcLine", "SetHint",
                "SetMicLevel", "FocusTextInput", "BlurTextInput", "ClearConversation",
            };
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = panel.GetMethod(methods[i], BindingFlags.Public | BindingFlags.Instance);
                Check(method != null, "method " + methods[i]);
            }
            Check(panel.GetMethod("Create", BindingFlags.Public | BindingFlags.Static) != null, "static Create(parent)");
            Check(panel.GetProperty("IsVisible") != null, "property IsVisible");
            Check(panel.GetProperty("IsTextInputActive") != null, "property IsTextInputActive");
            Check(panel.GetEvent("TextSubmitted") != null, "event TextSubmitted");
            Check(panel.GetEvent("TextInputCancelled") != null, "event TextInputCancelled");
            Check(Enum.IsDefined(typeof(VoicePanelTone), "Error"), "enum VoicePanelTone");
        }

        private static void CheckTriggerApi()
        {
            Section("NpcDialogueTrigger 对话 API");
            Type trigger = typeof(NpcDialogueTrigger);
            MethodInfo submit = trigger.GetMethod("SubmitPlayerLine", BindingFlags.Public | BindingFlags.Instance);
            Check(submit != null, "method SubmitPlayerLine");
            Check(submit != null && submit.ReturnType == typeof(bool), "SubmitPlayerLine 返回 bool");
            Check(trigger.GetEvent("ReplyReceived") != null, "event ReplyReceived");
            FieldInfo handler = trigger.GetField("ReplyHandler", BindingFlags.Public | BindingFlags.Static);
            Check(handler != null, "static ReplyHandler");
            Check(trigger.GetProperty("IsAwaitingReply") != null, "property IsAwaitingReply");
        }

        // ─────────────────────────── 工具 ───────────────────────────

        private static void Section(string title)
        {
            Line("── " + title + " ──");
        }

        private static void Check(bool ok, string label)
        {
            if (!ok) _failed++;
            Line((ok ? "[通过] " : "[失败] ") + label);
            if (!ok) Debug.LogError("[语音自检] 失败: " + label);
        }

        private static void Line(string text)
        {
            Report.Append(text).Append('\n');
            Debug.Log("[语音自检] " + text);
        }

        private static string Escape(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\t", " ");
        }
    }
}
