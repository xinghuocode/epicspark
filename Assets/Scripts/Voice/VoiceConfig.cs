// 语音对话模块的运行时配置。
// 迁移自 astrospark 项目的 AstroWar.Copilot.CopilotConfig，去掉了与飞船副驾驶 LLM 相关的字段：
// 本项目 NPC 回复统一走 EpicSpark.AI.OpenAI.ILlmService（与四层 Agent 共用），
// 这里只保留「语音识别（ASR）/ 语音合成（TTS）」所需的百炼(DashScope)配置与麦克风调参。
//
// 读取顺序（后者覆盖前者）：
//   1. 代码内置默认值
//   2. Assets/StreamingAssets/VoiceConfig.json
//   3. PlayerPrefs「EpicSpark.VoiceApiKey」（游戏内设置用，留空则忽略）
//   4. 环境变量 DASHSCOPE_API_KEY
using System;
using System.IO;
using UnityEngine;

namespace EpicSpark.Voice
{
    /// <summary>语音识别与合成的配置。纯数据类，可被控制器直接持有。</summary>
    [Serializable]
    public class VoiceConfig
    {
        /// <summary>百炼(DashScope) API Key：ASR 与 TTS 共用。</summary>
        public string apiKey = "";

        // ───────────── 语音识别 Paraformer realtime（/api-ws/v1/inference 的 run-task 协议） ─────────────
        public string asrUrl = "wss://dashscope.aliyuncs.com/api-ws/v1/inference";
        public string asrModel = "paraformer-realtime-v2";
        public int asrSampleRate = 16000;

        // ───────────── 语音合成 Qwen-TTS Realtime（/api-ws/v1/realtime 的 event 协议） ─────────────
        public string ttsUrl = "wss://dashscope.aliyuncs.com/api-ws/v1/realtime";
        public string ttsModel = "qwen3-tts-flash-realtime";
        public string ttsVoice = "Cherry";
        public int ttsSampleRate = 24000;
        public bool enableTts = true;
        [Range(0f, 1f)] public float ttsVolume = 1f;

        // ───────────── 交互与麦克风 ─────────────
        /// <summary>true：按住说话（按住 V 录音、松开识别）；false：常开麦克风由 VAD 断句（实验性）。</summary>
        public bool pushToTalk = true;
        /// <summary>RMS 高于此值视为「有人声」，用于音量条与 VAD。</summary>
        public float vadThreshold = 0.012f;
        /// <summary>句尾静音超过该秒数即结束一句（仅非按住说话模式使用）。</summary>
        public float silenceSeconds = 1.1f;
        /// <summary>单句最长录音时间，超时自动结束，避免误按后一直占用麦克风。</summary>
        public float maxUtteranceSeconds = 25f;
        /// <summary>麦克风设备名；空 = 系统默认设备。</summary>
        public string micDevice = "";
        /// <summary>对 NPC 台词做「可朗读化」清洗（去掉括号动作描写、表情符号、Markdown 符号）。</summary>
        public bool sanitizeSpeech = true;

        public bool HasKey { get { return !string.IsNullOrWhiteSpace(apiKey); } }

        /// <summary>读取配置。source 返回实际来源，便于在 HUD/Console 里诊断。</summary>
        public static VoiceConfig Load(out string source)
        {
            var cfg = new VoiceConfig();
            source = "内置默认值";

            try
            {
                string path = Path.Combine(Application.streamingAssetsPath, "VoiceConfig.json");
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    JsonUtility.FromJsonOverwrite(json, cfg);
                    source = path;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[语音] 配置读取失败，使用默认值: " + e.Message);
            }

            // 游戏内设置的 Key 优先于配置文件（方便展示机临时换 Key）
            try
            {
                string prefsKey = PlayerPrefs.GetString("EpicSpark.VoiceApiKey", "").Trim();
                if (prefsKey.Length > 0)
                {
                    cfg.apiKey = prefsKey;
                    source += " + PlayerPrefs";
                }
            }
            catch (Exception)
            {
                // PlayerPrefs 在极早期不可用时忽略即可
            }

            try
            {
                string env = Environment.GetEnvironmentVariable("DASHSCOPE_API_KEY");
                if (!string.IsNullOrEmpty(env))
                {
                    cfg.apiKey = env.Trim();
                    source += " + 环境变量DASHSCOPE_API_KEY";
                }
            }
            catch (Exception)
            {
            }

            cfg.apiKey = (cfg.apiKey ?? "").Trim();
            return cfg;
        }

        /// <summary>日志里安全地显示 Key（只露头尾）。</summary>
        public static string MaskKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return "(空)";
            if (key.Length <= 10) return key;
            return key.Substring(0, 5) + "…" + key.Substring(key.Length - 4);
        }
    }
}
