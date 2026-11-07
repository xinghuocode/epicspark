using System;
using System.Collections.Generic;

namespace EpicSpark.AI.Knowledge
{
    /// <summary>模型提供方。Auto 表示有 API Key 时优先远端，否则使用 Unity 内本地实现。</summary>
    public enum KnowledgeProviderMode
    {
        Auto = 0,
        OpenAI = 1,
        Local = 2,
    }

    [Serializable]
    public class KnowledgeHealth
    {
        public string status;
        public string service;
        public string version;
        public int events;
        public int chunks;
        public bool index_ready;
        public KnowledgeEmbeddingInfo embedding;
        public KnowledgeRerankerInfo reranker;
        public string server_time;
    }

    [Serializable]
    public class KnowledgeEmbeddingInfo
    {
        public string kind;
        public string model;
        public int dim;
        public bool degraded;
        public string degraded_reason;
    }

    [Serializable]
    public class KnowledgeRerankerInfo
    {
        public string kind;
        public string model;
        public bool degraded;
        public string degraded_reason;
    }

    [Serializable]
    public class KnowledgeMatch
    {
        public string event_id;
        public string title;
        public float confidence;
        public string matched_by;
    }

    /// <summary>事件章节。字段与 Resources/Knowledge/Events 下的 JSONL 一一对应。</summary>
    [Serializable]
    public class KnowledgeSections
    {
        public string background = "";
        public string process = "";
        public string impact = "";
        public string figures = "";
        public string terms = "";
        public string sites = "";
        public string misconceptions = "";
    }

    [Serializable]
    public class KnowledgeEventInfo
    {
        public string id;
        public string title;
        public string[] aliases = new string[0];
        public string era;
        public string region;
        public string period;
        public int start_year;
        public int end_year;
        public string one_liner;
        public KnowledgeSections sections = new KnowledgeSections();
        public string[] keywords = new string[0];
        public KnowledgeTimelineItem[] timeline = new KnowledgeTimelineItem[0];
        public string[] gameplay_hooks = new string[0];
        public KnowledgeSource[] sources = new KnowledgeSource[0];
    }

    [Serializable]
    public class KnowledgeTimelineItem
    {
        public int year;
        public string text;
    }

    [Serializable]
    public class KnowledgeSource
    {
        public string title;
        public string url;
    }

    [Serializable]
    public class KnowledgeEvidence
    {
        public string chunk_id;
        public string section;
        public string section_label;
        public string text;
        public float score;
    }

    /// <summary>剧情 Agent 开局载荷：事件卡片、证据块和可直接拼进提示词的上下文。</summary>
    [Serializable]
    public class KnowledgeEventCard
    {
        public string query;
        public float took_ms;
        public KnowledgeMatch matched;
        public KnowledgeEventInfo @event;
        public KnowledgeEvidence[] evidences = new KnowledgeEvidence[0];
        public KnowledgeItem[] items = new KnowledgeItem[0];
        public string context;

        public bool HasEvent => @event != null && !string.IsNullOrEmpty(@event.id);
    }

    [Serializable]
    public class KnowledgeScores
    {
        public float dense;
        public float sparse;
        public float fused;
        public float rerank;
        public float final;
    }

    [Serializable]
    public class KnowledgeItem
    {
        public int rank;
        public string chunk_id;
        public string event_id;
        public string title;
        public string section;
        public string section_label;
        public string era;
        public string region;
        public string period;
        public int start_year;
        public int end_year;
        public string text;
        public string[] keywords;
        public KnowledgeScores scores;
        public KnowledgeSource[] sources;
    }

    [Serializable]
    public class KnowledgeSearchResponse
    {
        public string query;
        public float took_ms;
        public KnowledgeMatch resolved;
        public KnowledgeItem[] items = new KnowledgeItem[0];
        public string context;
        public int total_candidates;
        public bool rerank_used;
        public string rerank_provider;
        public string degraded_reason;
    }

    /// <summary>可选检索过滤器；null/空字段表示不限制。</summary>
    [Serializable]
    public class KnowledgeFilter
    {
        public string era;
        public string region;
        public string event_id;
        public int? year;
        public int? year_from;
        public int? year_to;

        public bool IsEmpty =>
            string.IsNullOrEmpty(era) && string.IsNullOrEmpty(region) && string.IsNullOrEmpty(event_id)
            && !year.HasValue && !year_from.HasValue && !year_to.HasValue;
    }

    /// <summary>引擎使用的纯 C# 配置，便于在编辑器测试中脱离 ScriptableObject 构造。</summary>
    [Serializable]
    public class KnowledgeOptions
    {
        public string baseUrl = "https://ai.gitee.com/v1";
        public string apiKey = "";
        public string embedModel = "Qwen3-Embedding-8B";
        public string rerankModel = "Qwen3-Reranker-8B";
        public KnowledgeProviderMode embedProvider = KnowledgeProviderMode.Auto;
        public KnowledgeProviderMode rerankProvider = KnowledgeProviderMode.Auto;
        public int timeoutSeconds = 30;
        public int embeddingBatchSize = 16;
        public int remoteAttempts = 3;
        public int defaultTopK = 6;
        public int candidates = 40;
        public int rerankTopN = 24;
        public int rrfK = 60;
        public int maxChunksPerEvent = 2;
        public int contextBudgetChars = 2400;
        public float relativeCutoff = 0.45f;
        public int localEmbedDim = 1024;

        public bool UseRemoteEmbeddings =>
            embedProvider == KnowledgeProviderMode.OpenAI
            || (embedProvider == KnowledgeProviderMode.Auto && !string.IsNullOrEmpty(apiKey));

        public bool UseRemoteRerank =>
            rerankProvider == KnowledgeProviderMode.OpenAI
            || (rerankProvider == KnowledgeProviderMode.Auto && !string.IsNullOrEmpty(apiKey));
    }

    // ---- 内部索引记录（不作为主程序契约） ----

    internal sealed class KnowledgeChunk
    {
        public string chunkId;
        public string eventId;
        public string title;
        public string section;
        public string sectionLabel;
        public string text;
        public string era;
        public string region;
        public string period;
        public int startYear;
        public int endYear;
        public string[] keywords;
        public KnowledgeEventInfo sourceEvent;
    }

    internal struct KnowledgeScoredId
    {
        public string id;
        public float score;

        public KnowledgeScoredId(string id, float score)
        {
            this.id = id;
            this.score = score;
        }
    }

    internal struct KnowledgeScoredPosition
    {
        public int position;
        public float score;

        public KnowledgeScoredPosition(int position, float score)
        {
            this.position = position;
            this.score = score;
        }
    }

    internal sealed class KnowledgeHit
    {
        public KnowledgeChunk chunk;
        public float dense;
        public float sparse;
        public float fused;
        public float rerank;
        public bool hasRerank;
        public float final;

        public KnowledgeItem ToItem(int rank)
        {
            return new KnowledgeItem
            {
                rank = rank,
                chunk_id = chunk.chunkId,
                event_id = chunk.eventId,
                title = chunk.title,
                section = chunk.section,
                section_label = chunk.sectionLabel,
                era = chunk.era,
                region = chunk.region,
                period = chunk.period,
                start_year = chunk.startYear,
                end_year = chunk.endYear,
                text = chunk.text,
                keywords = chunk.keywords,
                sources = chunk.sourceEvent != null ? chunk.sourceEvent.sources : new KnowledgeSource[0],
                scores = new KnowledgeScores
                {
                    dense = dense,
                    sparse = sparse,
                    fused = fused,
                    rerank = hasRerank ? rerank : 0f,
                    final = final,
                },
            };
        }
    }
}
