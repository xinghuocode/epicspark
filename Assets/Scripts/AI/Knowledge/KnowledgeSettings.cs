using UnityEngine;

namespace EpicSpark.AI.Knowledge
{
    /// <summary>
    /// Unity 进程内知识库配置。Project 窗口右键 → Create → EpicSpark → Knowledge Settings。
    /// baseUrl 只用于 OpenAI 兼容的 embedding / rerank 模型端点；检索本身始终在 Unity 内执行。
    /// </summary>
    [CreateAssetMenu(fileName = "KnowledgeSettings", menuName = "EpicSpark/Knowledge Settings")]
    public class KnowledgeSettings : ScriptableObject
    {
        [Header("模型端点（可选）")]
        [Tooltip("OpenAI 兼容模型 Base URL，不带结尾斜杠，例如 https://ai.gitee.com/v1 或 http://127.0.0.1:1234/v1")]
        public string baseUrl = "https://ai.gitee.com/v1";

        [Tooltip("模型 API Key。留空时 Auto 模式使用 Unity 内向量与重排，无需联网。")]
        public string apiKey = "";

        public string embedModel = "Qwen3-Embedding-8B";
        public string rerankModel = "Qwen3-Reranker-8B";
        public KnowledgeProviderMode embedProvider = KnowledgeProviderMode.Auto;
        public KnowledgeProviderMode rerankProvider = KnowledgeProviderMode.Auto;

        [Tooltip("单个模型请求超时时间（秒）。")]
        public int timeoutSeconds = 30;
        public int embeddingBatchSize = 16;
        public int remoteAttempts = 3;

        [Header("检索参数")]
        public int defaultTopK = 6;
        public int candidates = 40;
        public int rerankTopN = 24;
        public int rrfK = 60;
        public int maxChunksPerEvent = 2;
        public int contextBudgetChars = 2400;
        [Range(0f, 1f)] public float relativeCutoff = 0.45f;
        public int localEmbedDim = 1024;

        [Header("Unity 资源与缓存")]
        [Tooltip("Resources 下的语料目录；每个 .txt 文件为 JSONL，每行一个事件。")]
        public string resourcePath = "Knowledge/Events";

        [Tooltip("向量索引文件名，保存在 Application.persistentDataPath/Knowledge 下。")]
        public string indexFileName = "vectors.bin";

        public KnowledgeOptions ToOptions()
        {
            return new KnowledgeOptions
            {
                baseUrl = (baseUrl ?? string.Empty).TrimEnd('/'),
                apiKey = apiKey ?? string.Empty,
                embedModel = embedModel,
                rerankModel = rerankModel,
                embedProvider = embedProvider,
                rerankProvider = rerankProvider,
                timeoutSeconds = timeoutSeconds,
                embeddingBatchSize = embeddingBatchSize,
                remoteAttempts = remoteAttempts,
                defaultTopK = defaultTopK,
                candidates = candidates,
                rerankTopN = rerankTopN,
                rrfK = rrfK,
                maxChunksPerEvent = maxChunksPerEvent,
                contextBudgetChars = contextBudgetChars,
                relativeCutoff = relativeCutoff,
                localEmbedDim = localEmbedDim,
            };
        }
    }
}
