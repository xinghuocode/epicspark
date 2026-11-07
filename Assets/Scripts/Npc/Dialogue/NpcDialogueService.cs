// NPC 对话 LLM 服务。
//  · ReplyAsync：以 npc.tag / persona / storyContext 为上下文生成有身份、不跑题的回复；
//  · SummarizeAsync：把整段对话「概括并转述」成第三人称简报交给剧情 Agent（防止对话主体混乱）。
// 没有 API Key 或远程失败时返回确定性兜底台词 / 结构化简报，绝不抛异常。
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EpicSpark.AI.OpenAI;
using EpicSpark.Core;
using UnityEngine;

namespace EpicSpark.Npc.Dialogue
{
    /// <summary>NPC 对话与对话简报。纯逻辑类，可被 Bootstrap 直接 new 出来注入。</summary>
    public class NpcDialogueService
    {
        private readonly Dictionary<string, int> replyCursors = new Dictionary<string, int>(StringComparer.Ordinal);
        private ILlmService llm;

        public NpcDialogueService()
            : this(null)
        {
        }

        public NpcDialogueService(ILlmService llm)
        {
            this.llm = llm;
        }

        /// <summary>未显式注入时回落到 Service 容器。</summary>
        public ILlmService Llm
        {
            get
            {
                if (llm == null) llm = Services.Llm;
                return llm;
            }
            set { llm = value; }
        }

        public bool EnableRemote { get; set; } = true;
        public float ReplyTemperature { get; set; } = 0.9f;
        public float SummaryTemperature { get; set; } = 0.3f;

        /// <summary>最近一次回复是否来自远程模型。</summary>
        public bool LastReplyFromRemote { get; private set; }

        /// <summary>最近一次简报是否来自远程模型。</summary>
        public bool LastSummaryFromRemote { get; private set; }

        /// <summary>最近一次降级原因。</summary>
        public string LastError { get; private set; }

        // ─────────────────────────── 回复 ───────────────────────────

        /// <summary>生成 NPC 对玩家这句话的回复。永远返回非空字符串。</summary>
        public async Task<string> ReplyAsync(
            NpcSpec npc,
            string playerLine,
            string storyContext,
            IReadOnlyList<string> history,
            CancellationToken ct)
        {
            string fallback = NextFallback(npc, playerLine);
            try
            {
                ILlmService service = Llm;
                if (!EnableRemote || service == null || !service.HasRemote || ct.IsCancellationRequested)
                {
                    LastReplyFromRemote = false;
                    return fallback;
                }

                string system = NpcDialoguePrompts.ReplySystemPrompt(npc, storyContext);
                string user = NpcDialoguePrompts.ReplyUserPrompt(playerLine, history);
                string raw = await service.CompleteAsync(system, user, ReplyTemperature, ct);
                string cleaned = CleanReply(raw, npc);
                if (string.IsNullOrEmpty(cleaned))
                {
                    LastError = "NPC 回复为空或不可用（" + RemoteErrorOf(service) + "）";
                    LastReplyFromRemote = false;
                    return fallback;
                }

                LastReplyFromRemote = true;
                LastError = "";
                return cleaned;
            }
            catch (Exception e)
            {
                LastError = e.GetType().Name + ": " + e.Message;
                LastReplyFromRemote = false;
                Debug.LogWarning("[NpcDialogue] ReplyAsync 异常，使用兜底台词: " + e.Message);
                return fallback;
            }
        }

        // ─────────────────────────── 简报 ───────────────────────────

        /// <summary>把一段对话概括转述成第三人称简报。永远返回非空字符串。</summary>
        public async Task<string> SummarizeAsync(NpcSpec npc, IReadOnlyList<string> history, CancellationToken ct)
        {
            string fallback = NpcDialoguePrompts.FallbackSummary(npc, history);
            try
            {
                ILlmService service = Llm;
                if (!EnableRemote || service == null || !service.HasRemote || ct.IsCancellationRequested)
                {
                    LastSummaryFromRemote = false;
                    return fallback;
                }

                string system = NpcDialoguePrompts.SummarizeSystemPrompt(npc);
                string user = NpcDialoguePrompts.SummarizeUserPrompt(npc, history, null);
                string raw = await service.CompleteAsync(system, user, SummaryTemperature, ct);
                string cleaned = CleanSummary(raw);
                if (string.IsNullOrEmpty(cleaned))
                {
                    LastError = "对话简报为空或不可用（" + RemoteErrorOf(service) + "）";
                    LastSummaryFromRemote = false;
                    return fallback;
                }

                LastSummaryFromRemote = true;
                LastError = "";
                return cleaned;
            }
            catch (Exception e)
            {
                LastError = e.GetType().Name + ": " + e.Message;
                LastSummaryFromRemote = false;
                Debug.LogWarning("[NpcDialogue] SummarizeAsync 异常，使用兜底简报: " + e.Message);
                return fallback;
            }
        }

        /// <summary>供上层直接取兜底台词（例如离线调试）。</summary>
        public string PeekFallback(NpcSpec npc, string playerLine)
        {
            return NpcDialoguePrompts.FallbackReply(npc, playerLine, CursorOf(npc));
        }

        // ─────────────────────────── 内部 ───────────────────────────

        private string NextFallback(NpcSpec npc, string playerLine)
        {
            string key = CursorKey(npc);
            int cursor;
            replyCursors.TryGetValue(key, out cursor);
            replyCursors[key] = cursor + 1;
            return NpcDialoguePrompts.FallbackReply(npc, playerLine, cursor);
        }

        private int CursorOf(NpcSpec npc)
        {
            int cursor;
            replyCursors.TryGetValue(CursorKey(npc), out cursor);
            return cursor;
        }

        private static string CursorKey(NpcSpec npc)
        {
            if (npc == null) return "_npc";
            if (!string.IsNullOrEmpty(npc.npcId)) return npc.npcId;
            return "_npc_" + (npc.displayName ?? "") + "_" + (npc.tag ?? "");
        }

        private static string RemoteErrorOf(ILlmService service)
        {
            LlmService concrete = service as LlmService;
            if (concrete == null) return "";
            return concrete.LastError ?? "";
        }

        private static string CleanReply(string raw, NpcSpec npc)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string text = raw.Trim();

            // 去掉整段代码围栏
            if (text.StartsWith("```", StringComparison.Ordinal))
            {
                int newline = text.IndexOf('\n');
                if (newline > 0) text = text.Substring(newline + 1);
                int fence = text.LastIndexOf("```", StringComparison.Ordinal);
                if (fence > 0) text = text.Substring(0, fence);
                text = text.Trim();
            }

            // 去掉模型自作主张加上的「名字：」或「NPC：」前缀
            text = StripSpeakerPrefix(text, npc);
            if (text.Length == 0) return "";

            // 单行显示：把换行压成空格，保留括号里的动作描写
            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            while (text.Contains("  ")) text = text.Replace("  ", " ");

            // 去掉包裹引号
            if (text.Length >= 2)
            {
                char first = text[0];
                char last = text[text.Length - 1];
                if ((first == '"' && last == '"') || (first == '「' && last == '」') ||
                    (first == '“' && last == '”') || (first == '\'' && last == '\''))
                    text = text.Substring(1, text.Length - 2).Trim();
            }

            if (text.Length > 320) text = text.Substring(0, 320);
            return text;
        }

        private static string StripSpeakerPrefix(string text, NpcSpec npc)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string[] prefixes = new string[4];
            prefixes[0] = npc != null ? (npc.displayName ?? "") : "";
            prefixes[1] = "NPC";
            prefixes[2] = "旁白";
            prefixes[3] = "角色";
            for (int i = 0; i < prefixes.Length; i++)
            {
                string prefix = prefixes[i];
                if (string.IsNullOrEmpty(prefix)) continue;
                if (text.StartsWith(prefix + "：", StringComparison.Ordinal))
                    return text.Substring(prefix.Length + 1).Trim();
                if (text.StartsWith(prefix + ":", StringComparison.Ordinal))
                    return text.Substring(prefix.Length + 1).Trim();
            }
            return text;
        }

        private static string CleanSummary(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string text = raw.Trim();
            if (text.StartsWith("```", StringComparison.Ordinal))
            {
                int newline = text.IndexOf('\n');
                if (newline > 0) text = text.Substring(newline + 1);
                int fence = text.LastIndexOf("```", StringComparison.Ordinal);
                if (fence > 0) text = text.Substring(0, fence);
                text = text.Trim();
            }
            if (text.Length < 12) return "";
            if (text.Length > 1200) text = text.Substring(0, 1200);
            return text;
        }
    }
}
