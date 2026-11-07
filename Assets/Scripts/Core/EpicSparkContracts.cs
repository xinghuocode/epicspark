// EpicSpark 服务契约
// 所有模块只依赖本文件中的接口；具体实现由 Bootstrap 装配。
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace EpicSpark.Core
{
    /// <summary>推理 LLM 服务。全部 Agent 共用同一模型，仅 systemPrompt 不同（设计文档末节）。</summary>
    public interface ILlmService
    {
        /// <summary>是否已配置可用的远端端点（有 API Key 且非离线模式）。</summary>
        bool HasRemote { get; }

        /// <summary>单轮补全。失败时返回 null，调用方必须回退到确定性本地逻辑。</summary>
        Task<string> CompleteAsync(string systemPrompt, string userPrompt, float temperature, CancellationToken ct);

        /// <summary>带工具调用的补全。返回模型原始文本；工具调用细节由实现方解析。</summary>
        Task<LlmToolResponse> CompleteWithToolsAsync(
            string systemPrompt,
            string userPrompt,
            IReadOnlyList<LlmToolDef> tools,
            float temperature,
            CancellationToken ct);

        /// <summary>词嵌入。失败时返回 null，调用方回退到本地确定性向量。</summary>
        Task<float[]> EmbedAsync(string text, CancellationToken ct);
    }

    /// <summary>工具定义，用于 OpenAI tools 字段。</summary>
    public class LlmToolDef
    {
        public string name = "";
        public string description = "";
        /// <summary>JSON Schema 文本（object 根）。</summary>
        public string parametersJson = "{\"type\":\"object\",\"properties\":{}}";
    }

    /// <summary>工具调用响应。</summary>
    public class LlmToolResponse
    {
        public string content = "";
        public string error = "";
        public List<LlmToolCall> calls = new List<LlmToolCall>();
        public bool HasError { get { return !string.IsNullOrEmpty(error); } }
    }

    public class LlmToolCall
    {
        public string id = "";
        public string name = "";
        public string argumentsJson = "";
    }

    /// <summary>剧情&核心 Agent：生成场景与流程、读日志推断意图、随传送锚点推进剧情。</summary>
    public interface IStoryAgent
    {
        Task<StoryTurn> BeginAsync(string userEvent, string knowledgeContext, CancellationToken ct);
        Task<StoryTurn> ContinueAsync(StoryContinueRequest request, CancellationToken ct);
    }

    /// <summary>灰盒 Agent：把文字描述翻译成灰盒空间骨架。</summary>
    public interface IGrayboxAgent
    {
        Task<GrayboxPlan> PlanAsync(GrayboxRequest request, CancellationToken ct);
    }

    /// <summary>搭建 Agent：按语义槽检索/生成美术资产并绑定剧情单位。</summary>
    public interface IBuildAgent
    {
        Task<BuildResult> BuildAsync(BuildRequest request, CancellationToken ct);
    }

    /// <summary>缓存模型库：词嵌入 + 余弦相似度检索。</summary>
    public interface IModelCatalog
    {
        bool IsLoaded { get; }
        Task LoadAsync(CancellationToken ct);
        Task<IReadOnlyList<ModelEntry>> AllAsync();
        /// <summary>返回最相似的一条及其相似度（0..1）。</summary>
        Task<ModelMatch> FindBestAsync(string query, CancellationToken ct);
        /// <summary>
        /// 带类别偏好的检索（建筑槽别匹配到石阶）。
        /// 传入 null/空集合等价于 FindBestAsync(query, ct)。
        /// </summary>
        Task<ModelMatch> FindBestAsync(string query, ICollection<string> preferredCategories, CancellationToken ct);
    }

    public struct ModelMatch
    {
        public ModelEntry entry;
        public float similarity;
        public bool HasEntry { get { return entry != null; } }
    }

    /// <summary>资产生成 Agent：z-image-turbo → RMBG-2.0 → Hunyuan3D-2。</summary>
    public interface IAssetGenerator
    {
        /// <summary>生成一条新资产并写入模型库，返回新条目；失败返回 null。</summary>
        Task<ModelEntry> GenerateAsync(string prompt, string category, CancellationToken ct);
    }

    /// <summary>知识库 RAG（历史事件检索）。</summary>
    public interface IKnowledgeService
    {
        Task InitializeAsync(CancellationToken ct);
        /// <summary>开局事件卡片上下文，可直接拼入提示词。</summary>
        Task<string> GetContextAsync(string userEvent, CancellationToken ct);
        /// <summary>任意追问检索。</summary>
        Task<string> SearchAsync(string query, int topK, CancellationToken ct);
    }

    // ─────────────────────────── 事件总线 ───────────────────────────

    /// <summary>全局游戏事件。各模块通过它解耦（日志、HUD、加载动画等）。</summary>
    public static class GameEvents
    {
        /// <summary>任意主观能动者的操作被记录时触发（已写入日志文件）。</summary>
        public static event Action<PlayerActionRecord> ActionRecorded;
        /// <summary>场景阶段推进（灰盒→搭建→显现→游玩）。</summary>
        public static event Action<ScenePhase> PhaseChanged;
        /// <summary>给玩家看的提示文字（同时打印到 Console）。</summary>
        public static event Action<string> NoticePosted;
        /// <summary>剧情文字推送（含 NPC 回复）。</summary>
        public static event Action<string> NarrativePosted;
        /// <summary>NPC 血条变化。</summary>
        public static event Action<string, float, float> NpcHealthChanged;

        public static void RaiseAction(PlayerActionRecord record)
        {
            var h = ActionRecorded;
            if (h != null) h(record);
        }

        public static void RaisePhase(ScenePhase phase)
        {
            var h = PhaseChanged;
            if (h != null) h(phase);
        }

        public static void RaiseNotice(string message)
        {
            Debug.Log("[EpicSpark] " + message);
            var h = NoticePosted;
            if (h != null) h(message);
        }

        public static void RaiseNarrative(string text)
        {
            var h = NarrativePosted;
            if (h != null) h(text);
        }

        public static void RaiseNpcHealth(string npcId, float current, float max)
        {
            var h = NpcHealthChanged;
            if (h != null) h(npcId, current, max);
        }
    }

    // ─────────────────────────── 服务定位 ───────────────────────────

    /// <summary>由 Bootstrap 装配的服务容器。未装配的服务为 null，调用方必须判空。</summary>
    public static class Services
    {
        public static ILlmService Llm;
        public static IStoryAgent Story;
        public static IGrayboxAgent Graybox;
        public static IBuildAgent Build;
        public static IModelCatalog Catalog;
        public static IAssetGenerator AssetGenerator;
        public static IKnowledgeService Knowledge;
        /// <summary>当前会话存档。</summary>
        public static GameSaveData Save;

        public static bool Ready
        {
            get { return Story != null && Graybox != null && Build != null; }
        }

        public static void Reset()
        {
            Llm = null;
            Story = null;
            Graybox = null;
            Build = null;
            Catalog = null;
            AssetGenerator = null;
            Knowledge = null;
            Save = null;
        }
    }
}
