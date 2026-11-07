using System;

namespace EpicSpark.AI.Knowledge
{
    // ============ 响应侧 DTO（JsonUtility 字段名必须与 JSON 完全一致，故用 snake_case）============

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

    /// <summary>用户输入 -> 事件卡片的匹配结果。confidence 为 0~1。</summary>
    [Serializable]
    public class KnowledgeMatch
    {
        public string event_id;
        public string title;
        public float confidence;
        public string matched_by;
    }

    [Serializable]
    public class KnowledgeEventInfo
    {
        public string id;
        public string title;
        public string[] aliases;
        public string era;
        public string region;
        public string period;
        public int start_year;
        public int end_year;
        public string one_liner;
        public string[] keywords;
        public KnowledgeTimelineItem[] timeline;
        public string[] gameplay_hooks;
        public KnowledgeSource[] sources;
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

    /// <summary>
    /// 剧情 Agent 开局使用的完整载荷：匹配信息 + 事件卡片 + 证据块 + 可直接拼进提示词的上下文。
    /// </summary>
    [Serializable]
    public class KnowledgeEventCard
    {
        public string query;
        public float took_ms;
        public KnowledgeMatch matched;
        public KnowledgeEventInfo @event;
        public KnowledgeEvidence[] evidences;
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
        public KnowledgeItem[] items;
        public string context;
        public int total_candidates;
        public bool rerank_used;
    }

    // ============ 请求侧 DTO ============

    [Serializable]
    public class KnowledgeSearchRequest
    {
        public string query;
        public int top_k;
        public bool use_rerank;
    }

    [Serializable]
    public class KnowledgeEventRequest
    {
        public string name;
        public int top_k;
        public int budget_chars;
    }
}
