using UnityEngine;

namespace EpicSpark.AI.OpenAI
{
    /// <summary>
    /// OpenAI 兼容端点配置。通过 CreateAssetMenu 创建实例后,
    /// 在 Inspector 中填入 Base URL / API Key / 模型名即可。
    /// </summary>
    [CreateAssetMenu(fileName = "OpenAISettings", menuName = "EpicSpark/OpenAI Settings")]
    public class OpenAISettings : ScriptableObject
    {
        [Tooltip("OpenAI 兼容端点 Base URL, 例如 http://127.0.0.1:1234/v1 (LM Studio) 或 https://ai.gitee.com/v1")]
        public string baseUrl = "http://127.0.0.1:1234/v1";

        [Tooltip("API Key。本地端点(如 LM Studio)通常无需鉴权, 可留空。")]
        public string apiKey = "";

        [Tooltip("默认模型名, 需与端点 /v1/models 返回的 id 一致。")]
        public string model = "qwen/qwen3-coder-30b";

        [Tooltip("单个请求超时时间(秒)。")]
        public int timeoutSeconds = 120;
    }
}
