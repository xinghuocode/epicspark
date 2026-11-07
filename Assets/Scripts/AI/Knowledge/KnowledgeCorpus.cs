using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace EpicSpark.AI.Knowledge
{
    /// <summary>把结构化事件卡片切为带事件上下文的检索块，并解析用户输入到事件。</summary>
    internal static class KnowledgeCorpus
    {
        private static readonly string[] SectionOrder =
        {
            "process", "impact", "figures", "terms", "sites", "misconceptions",
        };

        public static string SectionLabel(string section)
        {
            switch (section)
            {
                case "background": return "背景与起因";
                case "process": return "经过与关键节点";
                case "impact": return "影响与意义";
                case "figures": return "关键人物";
                case "terms": return "核心概念与制度";
                case "sites": return "地理与遗存";
                case "misconceptions": return "常见误区与史学辨析";
                case "timeline": return "时间线";
                case "aliases": return "别名与关键词";
                default: return section;
            }
        }

        public static List<KnowledgeChunk> BuildChunks(IList<KnowledgeEventInfo> events)
        {
            var chunks = new List<KnowledgeChunk>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (KnowledgeEventInfo item in events)
            {
                foreach (KnowledgeChunk chunk in BuildChunks(item))
                    if (ids.Add(chunk.chunkId)) chunks.Add(chunk);
            }
            return chunks;
        }

        public static List<KnowledgeChunk> BuildChunks(KnowledgeEventInfo item)
        {
            var chunks = new List<KnowledgeChunk>();
            if (item == null) return chunks;
            if (item.sections == null) item.sections = new KnowledgeSections();

            string background = item.sections.background ?? string.Empty;
            if (!string.IsNullOrEmpty(item.one_liner))
                background = item.one_liner + "。" + background;
            AddChunk(chunks, item, "background", background);

            foreach (string section in SectionOrder)
                AddChunk(chunks, item, section, GetSection(item.sections, section));

            if (item.timeline != null && item.timeline.Length > 0)
            {
                var timeline = new StringBuilder();
                foreach (KnowledgeTimelineItem point in item.timeline)
                {
                    if (point == null || string.IsNullOrEmpty(point.text)) continue;
                    if (timeline.Length > 0) timeline.Append("；");
                    timeline.Append(KnowledgeText.FormatYear(point.year)).Append(' ').Append(point.text);
                }
                AddChunk(chunks, item, "timeline", timeline.ToString());
            }

            var aliases = new StringBuilder();
            if (item.aliases != null && item.aliases.Length > 0)
                aliases.Append("别名：").Append(string.Join("、", item.aliases));
            if (item.keywords != null && item.keywords.Length > 0)
            {
                if (aliases.Length > 0) aliases.Append('；');
                aliases.Append("关键词：").Append(string.Join("、", item.keywords));
            }
            if (!string.IsNullOrEmpty(item.one_liner))
            {
                if (aliases.Length > 0) aliases.Append('；');
                aliases.Append("概要：").Append(item.one_liner);
            }
            AddChunk(chunks, item, "aliases", aliases.ToString());
            return chunks;
        }

        private static void AddChunk(List<KnowledgeChunk> chunks, KnowledgeEventInfo item, string section, string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return;
            string label = SectionLabel(section);
            string header = "《" + item.title + "》（" + item.period + "｜" + item.era + "｜" + item.region + "）";
            chunks.Add(new KnowledgeChunk
            {
                chunkId = item.id + "##" + section,
                eventId = item.id,
                title = item.title,
                section = section,
                sectionLabel = label,
                text = header + label + "：" + body.Trim(),
                era = item.era,
                region = item.region,
                period = item.period,
                startYear = item.start_year,
                endYear = item.end_year,
                keywords = item.keywords ?? new string[0],
                sourceEvent = item,
            });
        }

        private static string GetSection(KnowledgeSections sections, string section)
        {
            switch (section)
            {
                case "background": return sections.background;
                case "process": return sections.process;
                case "impact": return sections.impact;
                case "figures": return sections.figures;
                case "terms": return sections.terms;
                case "sites": return sections.sites;
                case "misconceptions": return sections.misconceptions;
                default: return string.Empty;
            }
        }

        public static string Fingerprint(IList<KnowledgeChunk> chunks)
        {
            using (var sha = SHA256.Create())
            {
                foreach (KnowledgeChunk chunk in chunks)
                {
                    byte[] id = Encoding.UTF8.GetBytes(chunk.chunkId + "\u001f");
                    byte[] text = Encoding.UTF8.GetBytes(chunk.text + "\u001e");
                    sha.TransformBlock(id, 0, id.Length, null, 0);
                    sha.TransformBlock(text, 0, text.Length, null, 0);
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        public static KnowledgeMatch Resolve(IList<KnowledgeEventInfo> events, string query)
        {
            string raw = KnowledgeText.Normalize(query);
            if (raw.Length == 0) return null;
            string strippedQuery = KnowledgeText.StripFillers(query);
            KnowledgeMatch best = null;

            foreach (KnowledgeEventInfo item in events)
            {
                ConsiderId(item, raw, strippedQuery, ref best);
                if (best != null && best.confidence >= 1f) continue;

                if (item.aliases == null) item.aliases = new string[0];
                // 标题是事件身份，别名只是线索：新专题《武昌起义》必须优先于旧事件的“武昌起义”别名。
                ConsiderName(item, item.title, raw, strippedQuery, false, true, ref best);
                foreach (string alias in item.aliases)
                    ConsiderName(item, alias, raw, strippedQuery, false, false, ref best);
                if (item.keywords != null)
                    foreach (string keyword in item.keywords)
                        ConsiderName(item, keyword, raw, strippedQuery, true, false, ref best);

                var queryTokens = KnowledgeText.TokenSet(strippedQuery);
                string joinedNames = item.title + " " + string.Join(" ", item.aliases);
                var nameTokens = KnowledgeText.TokenSet(KnowledgeText.StripFillers(joinedNames));
                float coverage = KnowledgeText.Coverage(queryTokens, nameTokens);
                if (coverage >= 0.5f)
                {
                    // 中文二元组可能产生“国革”这类跨界巧合，覆盖分必须低于标题相似度，
                    // 否则“法国的革命”会被“英国革命”抢走。
                    Consider(item, 0.55f + 0.25f * coverage, "coverage", ref best);
                }

                float similarity = KnowledgeText.Similarity(
                    strippedQuery,
                    KnowledgeText.StripFillers(item.title));
                if (similarity >= 0.55f)
                {
                    float confidence = 0.5f + 0.45f * (similarity - 0.55f) / 0.45f;
                    Consider(item, confidence, "similarity", ref best);
                }
            }
            return best;
        }

        private static void ConsiderId(
            KnowledgeEventInfo item,
            string raw,
            string stripped,
            ref KnowledgeMatch best)
        {
            string id = KnowledgeText.Normalize(item.id);
            if (raw == id || stripped == id) Consider(item, 1f, "id", ref best);
        }

        private static void ConsiderName(
            KnowledgeEventInfo item,
            string name,
            string raw,
            string stripped,
            bool keyword,
            bool isTitle,
            ref KnowledgeMatch best)
        {
            string normalized = KnowledgeText.Normalize(name);
            if (normalized.Length == 0) return;
            string strippedName = KnowledgeText.StripFillers(name);

            if (keyword)
            {
                if (raw == normalized || stripped == strippedName)
                    Consider(item, 0.82f, "keyword", ref best);
                else if (normalized.Length >= 3 && raw.Contains(normalized))
                    Consider(item, 0.72f, "keyword_contains", ref best);
                return;
            }

            if (raw == normalized || stripped == strippedName)
                Consider(item, isTitle ? 1f : 0.99f, isTitle ? "title_exact" : "exact", ref best);
            else if (normalized.Length >= 2 && raw.Contains(normalized))
                Consider(item, (isTitle ? 0.94f : 0.92f) + Math.Min(0.06f, normalized.Length / 200f), "contains", ref best);
            else if (strippedName.Length >= 2 && stripped.Contains(strippedName))
                Consider(item, (isTitle ? 0.92f : 0.90f) + Math.Min(0.06f, strippedName.Length / 200f), "contains", ref best);
            else if (raw.Length >= 2 && normalized.Contains(raw))
                Consider(item, isTitle ? 0.90f : 0.88f, "contained_by", ref best);
            else if (stripped.Length >= 2 && strippedName.Contains(stripped))
                Consider(item, isTitle ? 0.88f : 0.86f, "contained_by", ref best);
        }

        private static void Consider(
            KnowledgeEventInfo item,
            float confidence,
            string matchedBy,
            ref KnowledgeMatch best)
        {
            if (best != null && best.confidence >= confidence) return;
            best = new KnowledgeMatch
            {
                event_id = item.id,
                title = item.title,
                confidence = confidence,
                matched_by = matchedBy,
            };
        }
    }
}
