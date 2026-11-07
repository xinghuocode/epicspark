// OpenAI 兼容端点配置。
// Project 窗口右键 → Create → EpicSpark → OpenAI Settings，然后在 Bootstrap 里赋给 LlmService。
using UnityEngine;

namespace EpicSpark.AI.OpenAI
{
    [CreateAssetMenu(fileName = "OpenAISettings", menuName = "EpicSpark/OpenAI Settings")]
    public class OpenAISettings : ScriptableObject
    {
        [Header("端点")]
        [Tooltip("OpenAI 兼容 Base URL，不带结尾斜杠。例如 https://ai.gitee.com/v1 或 http://127.0.0.1:1234/v1")]
        public string baseUrl = "https://ai.gitee.com/v1";

        [Tooltip("API Key。留空时 HasRemote = false，全部走确定性本地兜底，绝不发请求。")]
        public string apiKey = "";

        [Header("模型")]
        [Tooltip("对话 / 工具调用模型。")]
        public string chatModel = "Qwen3-32B";

        [Tooltip("词嵌入模型，按设计固定为 Qwen3-Embedding-8B。")]
        public string embedModel = "Qwen3-Embedding-8B";

        [Header("请求行为")]
        [Tooltip("单次 HTTP 请求超时（秒）。")]
        public int timeoutSeconds = 90;

        [Tooltip("最大尝试次数（含首次），指数退避。")]
        public int maxRetries = 3;

        [Tooltip("单次回复的 token 上限。推理型模型会把预算花在 reasoning 上，太小会拿到空 content。")]
        public int maxTokens = 4096;

        [Tooltip("未显式传入 temperature 时使用的默认值。")]
        [Range(0f, 2f)] public float defaultTemperature = 0.85f;

        [Tooltip("是否打印请求失败细节。")]
        public bool verboseLogging = true;

        [Tooltip("强制离线：即使有 API Key 也不发请求。")]
        public bool offline = false;

        public string ResolvedBaseUrl
        {
            get
            {
                string url = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
                return url;
            }
        }
    }
}
