using UnityEngine;

namespace EpicSpark.AI.Knowledge
{
    /// <summary>
    /// 知识库检索服务配置。Project 窗口右键 → Create → EpicSpark → Knowledge Settings。
    /// 服务默认监听 http://127.0.0.1:8787（见 Tools/knowledge_rag/server.py）。
    /// </summary>
    [CreateAssetMenu(fileName = "KnowledgeSettings", menuName = "EpicSpark/Knowledge Settings")]
    public class KnowledgeSettings : ScriptableObject
    {
        [Tooltip("知识库检索服务地址，不带结尾斜杠，例如 http://127.0.0.1:8787")]
        public string baseUrl = "http://127.0.0.1:8787";

        [Tooltip("管理令牌（仅重建索引等管理接口需要）。普通检索可留空。")]
        public string adminToken = "";

        [Tooltip("单个请求超时时间(秒)。")]
        public int timeoutSeconds = 15;

        [Tooltip("默认返回条数。")]
        public int defaultTopK = 6;

        [Tooltip("拼装提示词上下文的最大字符数。")]
        public int contextBudgetChars = 2400;
    }
}
