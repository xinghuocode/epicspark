// 中英双语概念匹配：模型库里的 prompt 全是英文（z-image-turbo 产物），
// 而灰盒给出的是中文语义描述。纯向量检索在离线模式（本地特征哈希）下几乎不可能命中，
// 因此这里提供一层确定性的「概念重合」打分，作为余弦相似度的下限补充。
//
// 打分规则：
//   查询概念集 ∩ 条目概念集 = 0：类别命中给 0.35（低于默认门槛，会触发资产生成），否则 0
//   命中 1 个概念 0.62（≥ 默认门槛 0.5，直接复用美术库），每多命中一个 +0.12，类别命中再 +0.08，上限 0.96
using System;
using System.Collections.Generic;
using UnityEngine;
using EpicSpark.Core;

namespace EpicSpark.Build
{
    public static class BilingualMatcher
    {
        private sealed class Concept
        {
            public readonly string[] English;
            public readonly string[] Chinese;
            public readonly string Category;

            public Concept(string category, string[] english, string[] chinese)
            {
                Category = category;
                English = english;
                Chinese = chinese;
            }
        }

        // 与 Assets/StreamingAssets/Models/model_library.json 的 43 条资产一一对应
        private static readonly Concept[] Concepts = new[]
        {
            new Concept("lighting", new[] { "torch", "flame" }, new[] { "火炬", "火把" }),
            new Concept("lighting", new[] { "lantern", "streetlamp", "oil lamp" }, new[] { "灯笼", "街灯", "路灯", "油灯", "提灯", "灯罩" }),
            new Concept("lighting", new[] { "candle" }, new[] { "蜡烛", "烛台", "烛" }),
            new Concept("lighting", new[] { "campfire", "bonfire" }, new[] { "营火", "篝火", "火堆" }),
            new Concept("container", new[] { "barrel", "cask" }, new[] { "木桶", "酒桶", "大桶" }),
            new Concept("container", new[] { "crate", "crate of" }, new[] { "木箱", "板条箱", "货箱", "箱子" }),
            new Concept("container", new[] { "sack", "burlap" }, new[] { "麻袋", "粮袋", "布袋", "沙袋" }),
            new Concept("container", new[] { "bucket", "pail" }, new[] { "水桶", "吊桶", "木桶桶" }),
            new Concept("furniture", new[] { "chair", "stool" }, new[] { "椅子", "座椅", "凳子" }),
            new Concept("furniture", new[] { "table", "desk" }, new[] { "桌子", "长桌", "木桌", "书桌" }),
            new Concept("furniture", new[] { "bench" }, new[] { "长凳", "长椅", "条凳" }),
            new Concept("furniture", new[] { "bed", "mattress" }, new[] { "床", "床铺", "床架" }),
            new Concept("furniture", new[] { "bookshelf", "shelf", "books" }, new[] { "书架", "书柜", "书" }),
            new Concept("weapon", new[] { "cannon", "artillery" }, new[] { "大炮", "火炮", "加农炮", "炮台" }),
            new Concept("weapon", new[] { "guillotine" }, new[] { "断头台" }),
            new Concept("weapon", new[] { "musket", "rifle", "flintlock" }, new[] { "步枪", "火枪", "滑膛枪", "长枪" }),
            new Concept("weapon", new[] { "sword", "longsword", "blade" }, new[] { "长剑", "剑", "刀剑", "佩剑" }),
            new Concept("vehicle", new[] { "carriage", "coach" }, new[] { "马车", "四轮马车", "轿车" }),
            new Concept("vehicle", new[] { "cart", "handcart" }, new[] { "手推车", "推车", "板车" }),
            new Concept("vehicle", new[] { "boat", "rowing" }, new[] { "小船", "划艇", "渡船", "木船" }),
            new Concept("structure", new[] { "wall", "rampart" }, new[] { "石墙", "城墙", "围墙", "矮墙" }),
            new Concept("structure", new[] { "fence", "palisade" }, new[] { "栅栏", "围栏", "篱笆" }),
            new Concept("structure", new[] { "well", "water well" }, new[] { "水井", "井" }),
            new Concept("structure", new[] { "arch", "gateway", "archway" }, new[] { "拱门", "门洞", "牌坊", "城门" }),
            new Concept("structure", new[] { "stairs", "steps", "staircase" }, new[] { "台阶", "阶梯", "楼梯" }),
            new Concept("prop", new[] { "flag", "banner", "pole" }, new[] { "旗帜", "旗", "横幅", "旗杆" }),
            new Concept("prop", new[] { "signpost", "signboard", "direction boards" }, new[] { "路牌", "指示牌", "木牌" }),
            new Concept("prop", new[] { "anvil" }, new[] { "铁砧", "砧" }),
            new Concept("prop", new[] { "poster", "notice", "paper" }, new[] { "海报", "告示", "布告", "传单" }),
            new Concept("prop", new[] { "painting", "portrait", "frame" }, new[] { "油画", "画像", "画框", "画" }),
            new Concept("nature", new[] { "tree", "branches" }, new[] { "枯树", "树木", "树" }),
            new Concept("nature", new[] { "rock", "boulder" }, new[] { "岩石", "巨石", "石块", "石头" }),
            new Concept("nature", new[] { "bush", "shrub" }, new[] { "灌木", "树丛" }),
            new Concept("building", new[] { "house", "cottage", "dwelling" }, new[] { "房屋", "民居", "民宅", "住宅", "房子" }),
            new Concept("building", new[] { "tavern", "inn" }, new[] { "酒馆", "客栈", "旅店" }),
            new Concept("building", new[] { "blacksmith", "forge", "smithy" }, new[] { "铁匠铺", "铁匠", "锻造" }),
            new Concept("building", new[] { "church", "chapel", "bell tower" }, new[] { "教堂", "礼拜堂", "钟楼" }),
            new Concept("building", new[] { "barn" }, new[] { "谷仓", "牲口棚", "仓房" }),
            new Concept("building", new[] { "windmill", "sails" }, new[] { "风车" }),
            new Concept("building", new[] { "watchtower", "lookout" }, new[] { "瞭望塔", "望楼", "箭楼" }),
            new Concept("building", new[] { "castle", "keep", "turret" }, new[] { "城堡", "主楼", "要塞" }),
            new Concept("building", new[] { "watermill", "water wheel" }, new[] { "水车", "水磨坊", "磨坊" }),
            new Concept("building", new[] { "guardhouse", "barracks" }, new[] { "卫兵室", "岗哨", "守卫屋" }),
        };

        private static string _queryCacheKey = "";

        /// <summary>综合打分：取「概念重合分」与「中文名称覆盖率」中的较大者。</summary>
        public static float Score(string query, ModelEntry entry)
        {
            if (entry == null) return 0f;
            string entryText = ((entry.prompt ?? "") + " " + (entry.name ?? "") + " " + (entry.category ?? "")
                                + " " + (entry.id ?? "")).ToLowerInvariant();
            float concept = ScoreText(query, entryText, entry.category);
            float nameCoverage = NameCoverage(query, entry.name);
            return Mathf.Max(concept, nameCoverage);
        }

        /// <summary>
        /// 条目 name 是中文（例如「铁制路灯」），灰盒语义也是中文：
        /// 只要条目名称的中文二元组大部分出现在 query 里，就认为语义命中了该资产。
        /// 这一层能泛化掉同义词（路灯 / 街灯 / 提灯），不必把词表写全。
        /// </summary>
        private static float NameCoverage(string query, string entryName)
        {
            if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(entryName)) return 0f;
            string q = query.ToLowerInvariant();
            string name = entryName.ToLowerInvariant();

            var grams = new List<string>(8);
            int runStart = -1;
            for (int i = 0; i <= name.Length; i++)
            {
                bool cjk = i < name.Length && IsCjk(name[i]);
                if (cjk)
                {
                    if (runStart < 0) runStart = i;
                }
                else if (runStart >= 0)
                {
                    AppendGrams(grams, name, runStart, i);
                    runStart = -1;
                }
            }
            if (grams.Count == 0) return 0f;

            int hit = 0;
            for (int i = 0; i < grams.Count; i++)
            {
                if (q.IndexOf(grams[i], StringComparison.Ordinal) >= 0) hit++;
            }
            float coverage = (float)hit / grams.Count;
            if (coverage < 0.67f) return 0f;
            return 0.55f + 0.35f * coverage;
        }

        private static void AppendGrams(List<string> grams, string text, int start, int end)
        {
            int length = end - start;
            if (length <= 0) return;
            if (length == 1)
            {
                grams.Add(text.Substring(start, 1));
                return;
            }
            for (int i = start; i < end - 1; i++) grams.Add(text.Substring(i, 2));
        }

        private static bool IsCjk(char c)
        {
            return c >= 0x3400 && c <= 0x9FFF;
        }

        public static float ScoreText(string query, string entryText)
        {
            return ScoreText(query, entryText, null);
        }

        public static float ScoreText(string query, string entryText, string entryCategory)
        {
            if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(entryText)) return 0f;
            string q = query.ToLowerInvariant();
            string e = entryText.ToLowerInvariant();

            List<int> queryHits = MatchConcepts(q);
            if (queryHits.Count == 0) return 0f;

            int overlap = 0;
            for (int i = 0; i < queryHits.Count; i++)
            {
                if (ConceptInEntry(Concepts[queryHits[i]], e)) overlap++;
            }

            bool categoryMatch = !string.IsNullOrEmpty(entryCategory)
                                 && q.IndexOf(entryCategory.ToLowerInvariant(), StringComparison.Ordinal) >= 0;

            if (overlap == 0) return categoryMatch ? 0.35f : 0f;

            float score = 0.62f + 0.12f * (overlap - 1);
            if (categoryMatch) score += 0.08f;
            return Mathf.Clamp(score, 0f, 0.96f);
        }

        private static List<int> MatchConcepts(string lowerQuery)
        {
            if (_queryCacheKey == lowerQuery && _queryIndices.Count > 0) return _queryIndices;
            var hits = new List<int>(4);
            for (int i = 0; i < Concepts.Length; i++)
            {
                Concept concept = Concepts[i];
                if (ConceptInText(concept, lowerQuery)) hits.Add(i);
            }
            _queryCacheKey = lowerQuery;
            _queryIndices = hits;
            return hits;
        }

        private static List<int> _queryIndices = new List<int>();

        private static bool ConceptInText(Concept concept, string lowerText)
        {
            for (int i = 0; i < concept.Chinese.Length; i++)
            {
                if (lowerText.IndexOf(concept.Chinese[i], StringComparison.Ordinal) >= 0) return true;
            }
            for (int i = 0; i < concept.English.Length; i++)
            {
                if (ContainsWord(lowerText, concept.English[i])) return true;
            }
            return false;
        }

        private static bool ConceptInEntry(Concept concept, string lowerEntry)
        {
            for (int i = 0; i < concept.English.Length; i++)
            {
                if (ContainsWord(lowerEntry, concept.English[i])) return true;
            }
            for (int i = 0; i < concept.Chinese.Length; i++)
            {
                if (lowerEntry.IndexOf(concept.Chinese[i], StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        private static bool ContainsWord(string haystack, string word)
        {
            if (string.IsNullOrEmpty(word)) return false;
            int index = haystack.IndexOf(word, StringComparison.Ordinal);
            while (index >= 0)
            {
                bool leftOk = index == 0 || !IsWordChar(haystack[index - 1]);
                int end = index + word.Length;
                bool rightOk = end >= haystack.Length || !IsWordChar(haystack[end]);
                if (leftOk && rightOk) return true;
                index = haystack.IndexOf(word, index + 1, StringComparison.Ordinal);
            }
            return false;
        }

        private static bool IsWordChar(char c)
        {
            return (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
        }
    }
}
