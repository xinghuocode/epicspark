// 灰盒规划的确定性兜底实现。
// 设计文档：LLM 通过有限的灰盒编辑器 API 表达空间意图；远端不可用时必须仍能得到可用的灰盒骨架。
// 本文件按 SceneSpec 的中文关键词判断场景类型（城镇/广场/乡村/港口/宫廷/战场/教堂/集市），
// 用「网格 + 街道缝隙」的方式生成闭合、互不重叠、完全在场景边界内的功能区域，
// 道路从出生点连通到各区域，语义槽沿道路与区域内均匀分布（约 8~20 个），
// 并把 npcs / anchors / props 一一绑定成对应槽位与 NPC 动线。
// 随机数用场景 id 派生种子 + 自实现 xorshift，保证跨平台、跨次启动完全可复现。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using EpicSpark.Core;
using UnityEngine;

namespace EpicSpark.Graybox
{
    /// <summary>场景类型判定结果（决定区域模板与道具语料）。</summary>
    public enum SceneArchetype
    {
        Town = 0,
        Square = 1,
        Countryside = 2,
        Harbor = 3,
        Palace = 4,
        Battlefield = 5,
        Church = 6,
        Market = 7,
    }

    /// <summary>确定性伪随机数（xorshift32）。同一个 seed 在任何平台产生完全相同的序列。</summary>
    public sealed class GrayboxRandom
    {
        private uint state;

        public GrayboxRandom(int seed)
        {
            state = (uint)seed;
            if (state == 0u) state = 0x9E3779B9u;
            // 预热，避免低位种子(1,2,3...)前几个数过于线性
            NextUInt();
            NextUInt();
            NextUInt();
        }

        public uint NextUInt()
        {
            uint x = state;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            state = x;
            return x;
        }

        /// <summary>[0,1)。</summary>
        public float Next01()
        {
            return (NextUInt() & 0xFFFFFFu) / 16777216f;
        }

        /// <summary>[min,max)。</summary>
        public float Range(float min, float max)
        {
            if (max <= min) return min;
            return min + Next01() * (max - min);
        }

        /// <summary>[minInclusive,maxExclusive)。</summary>
        public int Range(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive) return minInclusive;
            return minInclusive + (int)(NextUInt() % (uint)(maxExclusive - minInclusive));
        }

        public bool Chance(float probability)
        {
            return Next01() < probability;
        }
    }

    /// <summary>
    /// 灰盒确定性规划器。所有输出都落在 x∈[-width/2,width/2]、y∈[-length/2,length/2]（原点在场景中心），
    /// 多边形为闭合环（首点=末点），区域两两不重叠。
    /// </summary>
    public class GrayboxPlanner
    {
        private const int DefaultSceneSide = 60;
        private const float RegionInset = 1.2f;
        private const float MainRoadWidth = 6f;
        private const float BranchRoadWidth = 3.2f;
        private const float MinRegionArea = 4f;

        /// <summary>区域内槽位分布模板（归一化比例）。</summary>
        private static readonly Vector2[] SlotPatterns =
        {
            new Vector2(0.50f, 0.50f),
            new Vector2(0.27f, 0.30f),
            new Vector2(0.73f, 0.70f),
            new Vector2(0.72f, 0.28f),
            new Vector2(0.28f, 0.72f),
            new Vector2(0.50f, 0.25f),
            new Vector2(0.50f, 0.75f),
            new Vector2(0.24f, 0.52f),
            new Vector2(0.76f, 0.52f),
        };

        private static readonly string[] RoadsidePhrases =
        {
            "铁制路灯，玻璃灯罩，立在路边",
            "火把插在木杆上，火焰跳动",
            "木制路牌，两块方向板",
            "石砌水井，木顶与辘轳",
            "木制长凳，路边休息用",
            "破旧布旗挂在木杆上",
            "木制告示板，贴有纸张",
            "橡木桶与木箱堆在路边，陈旧",
        };

        private static readonly string[] ResidentialProps =
        {
            "橡木桶，陈旧，铁箍，摆在墙边",
            "木制长凳，靠墙摆放，木纹磨损",
            "木箱堆叠，装货用，粗糙木板",
            "木制晾衣杆，挂着布衣",
            "石砌水井，木顶与辘轳",
            "木制告示板，贴有纸张",
        };

        private static readonly string[] SquareProps =
        {
            "石板路缘与铺装，几何纹样",
            "石砌喷泉，中央雕像，圆形水池",
            "石质长椅与灯柱",
            "木制临时摊位，粗布篷顶",
        };

        private static readonly string[] MarketProps =
        {
            "木制摊位，粗布篷顶，木桌与货箱",
            "摊位上的蔬果筐与麻袋，木架",
            "遮阳布篷与木柱，集市摊位",
            "木箱与麻袋堆，装货用",
        };

        private static readonly string[] WaterProps =
        {
            "木栈桥伸入水面，木桩与绳索",
            "石砌驳岸，水面平静，石阶入水",
            "系船柱与缆绳，湿木",
        };

        private static readonly string[] FieldProps =
        {
            "木栅栏围起的农田，麦垛",
            "草垛与木犁，泥土小路",
            "枯树与岩石，荒地边缘",
        };

        private static readonly string[] HarborProps =
        {
            "木制栈桥与系船柱，岸边木箱",
            "小木船停靠石砌码头，缆绳",
            "码头货箱与吊木架，粗麻绳",
        };

        private static readonly string[] MilitaryProps =
        {
            "土垒与木制拒马，防御工事",
            "木制武器架，长枪与盾",
            "营房前的木桶与草垫",
            "石砌哨位，木制岗亭",
        };

        private static readonly string[] ChurchProps =
        {
            "石砌墓碑与低矮围墙，墓园",
            "木制长椅与石阶，教堂前庭",
            "石砌十字架与枯树",
        };

        private static readonly string[] CemeteryProps =
        {
            "石砌墓碑，低矮围墙，枯树",
            "生锈铁栅栏与石碑",
        };

        private static readonly string[] PalaceProps =
        {
            "石砌柱廊与台阶，大理石地面",
            "花园石凳与修剪灌木",
            "石砌喷泉，雕像基座",
        };

        private static readonly string[] GenericProps =
        {
            "木制杂物，陈旧，粗糙木纹",
            "石砌台阶，磨损边缘",
        };

        private static readonly string[] NpcPreferredTown = { "square", "market", "residential" };
        private static readonly string[] NpcPreferredCountryside = { "field", "residential", "market" };
        private static readonly string[] NpcPreferredHarbor = { "harbor", "market", "residential" };
        private static readonly string[] NpcPreferredPalace = { "square", "palace", "church" };
        private static readonly string[] NpcPreferredBattlefield = { "military", "field", "square" };
        private static readonly string[] NpcPreferredChurch = { "church", "cemetery", "square" };
        private static readonly string[] NpcPreferredMarket = { "market", "square", "residential" };
        private static readonly string[] NpcPreferredSquare = { "square", "market", "residential" };

        private sealed class ArchetypeProfile
        {
            public string[] RegionKinds;
            public string BoundaryKind = string.Empty;
            public bool HasTrench;
        }

        // ─────────────────────────── 主入口 ───────────────────────────

        /// <summary>生成确定性灰盒规划。永远返回可用结果，不抛异常。</summary>
        /// <summary>给槽位打流式加载分段号：60 米内第 0 段，60–120 第 1 段，以此类推。</summary>
        public static void AssignStreamingSegments(GrayboxPlan plan)
        {
            if (plan == null || plan.slots == null) return;
            // 素模阶段不流式：全部构件一次落地。
            // 旧策略按"离出生点 60 米"切段，实测第一帧只搭了 30 个槽位里的 12 个
            // （城堡/城门/教堂/菜市全在第 1 段），玩家一进场就是空场 → "零散"的主因之一。
            if (EpicSparkFeatureFlags.UsePrimitiveScene)
            {
                for (int i = 0; i < plan.slots.Count; i++)
                    if (plan.slots[i] != null) plan.slots[i].segment = 0;
                plan.segmentCount = 1;
                return;
            }
            int maxSegment = 0;
            for (int i = 0; i < plan.slots.Count; i++)
            {
                GrayboxSlot slot = plan.slots[i];
                if (slot == null) continue;
                int segment = Mathf.FloorToInt(Vector2.Distance(slot.position, plan.spawnPoint) / 60f);
                slot.segment = Mathf.Max(0, segment);
                if (slot.segment > maxSegment) maxSegment = slot.segment;
            }
            plan.segmentCount = Mathf.Max(1, maxSegment + 1);
        }

        public GrayboxPlan Plan(GrayboxRequest request)
        {
            SceneSpec scene = (request != null && request.scene != null) ? request.scene : new SceneSpec();
            int seed = ResolveSeed(request);
            var rng = new GrayboxRandom(seed);
            SceneArchetype archetype = Classify(scene);
            string style = EraStyle(scene);
            float width = NormalizeSide(scene.width, DefaultSceneSide);
            float length = NormalizeSide(scene.length, DefaultSceneSide);
            float halfW = width * 0.5f;
            float halfL = length * 0.5f;

            var plan = new GrayboxPlan
            {
                sceneId = string.IsNullOrEmpty(scene.sceneId) ? "scene_auto" : scene.sceneId,
                width = width,
                length = length,
                seed = seed,
            };
            plan.spawnPoint = ResolveSpawn(scene, halfW, halfL);

            // 剧情 Agent 给的布局文本（含具体坐标）优先：照着它做地编，而不是按 archetype 重铺一遍。
            string layoutNote;
            if (StoryLayoutPlanBuilder.TryBuild(scene, request, plan, out layoutNote))
            {
                plan.plannerNote = layoutNote;
                Debug.Log("[灰盒] " + plan.plannerNote);
                return plan;
            }

            plan.regions = BuildRegions(archetype, style, rng, halfW, halfL);
            plan.paths = BuildPaths(archetype, plan.regions, plan.spawnPoint, rng, halfW, halfL);
            plan.slots = new List<GrayboxSlot>();

            int boundCount = (scene.npcs != null ? scene.npcs.Count : 0)
                + (scene.anchors != null ? scene.anchors.Count : 0)
                + (scene.props != null ? scene.props.Count : 0);
            int budget = ComputeSlotBudget(width, length);
            int regularBudget = Mathf.Clamp(budget - boundCount, 4, 16);
            int regionSlotCount = Mathf.Clamp(Mathf.RoundToInt(regularBudget * 0.7f), 3, 14);
            int roadSlotCount = Mathf.Clamp(regularBudget - regionSlotCount, 0, 8);
            AddRegionSlots(plan, archetype, style, regionSlotCount, rng);
            AddRoadSlots(plan, style, roadSlotCount, rng);
            AddBoundSlots(plan, scene, archetype, style, rng);
            plan.npcPaths = BuildNpcPaths(scene, plan.regions, archetype, rng);

            plan.plannerNote = string.Format(CultureInfo.InvariantCulture,
                "确定性灰盒规划（本地兜底）：{0} / {1}，区域 {2} 个，条状物 {3} 条，语义槽 {4} 个，NPC 动线 {5} 条。",
                archetype, style, plan.regions.Count, plan.paths.Count, plan.slots.Count, plan.npcPaths.Count);
            Debug.Log("[灰盒] " + plan.plannerNote);
            return plan;
        }

        // ─────────────────────────── 场景判定 ───────────────────────────

        private static readonly string[] HarborKeywords = { "港", "码头", "船坞", "水手", "帆船", "harbor", "harbour", "dock", "port", "quay" };
        private static readonly string[] BattlefieldKeywords = { "战场", "战争", "战役", "战斗", "攻城", "围城", "军营", "营地", "军阵", "炮兵", "要塞", "battle", "war", "siege", "camp", "battlefield" };
        private static readonly string[] PalaceKeywords = { "宫廷", "王宫", "皇宫", "宫殿", "城堡", "palace", "royal", "castle" };
        private static readonly string[] ChurchKeywords = { "教堂", "修道", "礼拜", "圣堂", "墓园", "church", "cathedral", "monastery", "chapel", "abbey" };
        private static readonly string[] MarketKeywords = { "集市", "市场", "市集", "商贩", "摊贩", "街市", "market", "bazaar", "fair" };
        private static readonly string[] SquareKeywords = { "广场", "城中心", "中央广场", "喷泉", "square", "plaza" };
        private static readonly string[] CountrysideKeywords = { "乡村", "村庄", "村子", "农田", "田园", "农庄", "牧场", "庄园", "田野", "village", "countryside", "farm", "rural" };
        private static readonly string[] TownKeywords = { "城镇", "城市", "小镇", "街区", "街道", "民居", "住宅", "town", "city", "street" };

        /// <summary>
        /// 按 sceneName（权重 3）/ era（权重 2）/ 其余描述（权重 1）累计关键词得分选场景原型，分高者胜。
        /// 同分时按「港口 &gt; 战场 &gt; 宫廷 &gt; 教堂 &gt; 集市 &gt; 广场 &gt; 乡村 &gt; 城镇」的特定性优先，避免「街市」这类词把场景误判成港口。
        /// </summary>
        public static SceneArchetype Classify(SceneSpec scene)
        {
            if (scene == null) return SceneArchetype.Town;
            string nameText = (scene.sceneName ?? string.Empty).ToLowerInvariant();
            string eraText = (scene.era ?? string.Empty).ToLowerInvariant();
            string bodyText = ((scene.region ?? string.Empty) + " "
                + (scene.layoutDescription ?? string.Empty) + " "
                + (scene.buildRequirement ?? string.Empty)).ToLowerInvariant();

            SceneArchetype[] order =
            {
                SceneArchetype.Harbor, SceneArchetype.Battlefield, SceneArchetype.Palace,
                SceneArchetype.Church, SceneArchetype.Market, SceneArchetype.Square,
                SceneArchetype.Countryside, SceneArchetype.Town,
            };
            string[][] tables =
            {
                HarborKeywords, BattlefieldKeywords, PalaceKeywords, ChurchKeywords,
                MarketKeywords, SquareKeywords, CountrysideKeywords, TownKeywords,
            };

            SceneArchetype best = SceneArchetype.Town;
            int bestScore = 0;
            for (int i = 0; i < order.Length; i++)
            {
                int score = CountHits(nameText, tables[i]) * 3
                    + CountHits(eraText, tables[i]) * 2
                    + CountHits(bodyText, tables[i]);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = order[i];
                }
            }
            return bestScore == 0 ? SceneArchetype.Town : best;
        }

        private static int CountHits(string text, string[] keywords)
        {
            if (string.IsNullOrEmpty(text) || keywords == null) return 0;
            int hits = 0;
            for (int i = 0; i < keywords.Length; i++)
            {
                if (string.IsNullOrEmpty(keywords[i])) continue;
                if (text.IndexOf(keywords[i], StringComparison.Ordinal) >= 0) hits++;
            }
            return hits;
        }

        /// <summary>从时代/场景文字推断艺术风格前缀，供语义槽拼成「…，中世纪西欧风格，…」。</summary>
        public static string EraStyle(SceneSpec scene)
        {
            if (scene == null) return "欧洲古典";
            string text = ((scene.era ?? string.Empty) + " "
                + (scene.sceneName ?? string.Empty) + " "
                + (scene.region ?? string.Empty) + " "
                + (scene.layoutDescription ?? string.Empty)).ToLowerInvariant();

            if (ContainsAny(text, new[] { "中世纪", "城堡", "骑士", "修道", "十字军", "中古" })) return "中世纪西欧";
            if (ContainsAny(text, new[] { "大革命", "革命", "启蒙", "18世纪", "1789", "1793", "革命战争" })) return "18世纪欧洲";
            if (ContainsAny(text, new[] { "工业", "蒸汽", "工厂", "维多利亚", "19世纪" })) return "工业时代";
            if (ContainsAny(text, new[] { "罗马", "希腊", "古典", "帝国", "罗马帝国" })) return "古典时代";
            if (ContainsAny(text, new[] { "东方", "中国", "汉", "唐", "宋", "明", "清", "王朝", "寺庙", "江南" })) return "东方古典";
            if (ContainsAny(text, new[] { "未来", "赛博", "科幻", "太空" })) return "未来主义";
            return "欧洲古典";
        }

        private static bool ContainsAny(string text, string[] keywords)
        {
            if (string.IsNullOrEmpty(text) || keywords == null) return false;
            for (int i = 0; i < keywords.Length; i++)
            {
                if (string.IsNullOrEmpty(keywords[i])) continue;
                if (text.IndexOf(keywords[i], StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        /// <summary>随机种子：优先用请求里的 seed，否则由 sceneId 确定性派生（保证可复现）。</summary>
        public static int ResolveSeed(GrayboxRequest request)
        {
            if (request == null) return 1;
            if (request.seed != 0) return request.seed;
            string key = string.Empty;
            if (request.scene != null)
            {
                key = request.scene.sceneId ?? string.Empty;
                if (string.IsNullOrEmpty(key)) key = request.scene.sceneName ?? string.Empty;
            }
            if (string.IsNullOrEmpty(key)) key = "epicspark";
            return (int)(StableHash(key) % 1000003u) + 1;
        }

        private static ArchetypeProfile GetProfile(SceneArchetype archetype)
        {
            switch (archetype)
            {
                case SceneArchetype.Harbor:
                    return new ArchetypeProfile
                    {
                        RegionKinds = new[] { "harbor", "market", "residential", "water", "residential", "field", "church", "military" },
                    };
                case SceneArchetype.Battlefield:
                    return new ArchetypeProfile
                    {
                        RegionKinds = new[] { "military", "field", "military", "field", "water", "church", "residential", "square" },
                        BoundaryKind = "wall",
                        HasTrench = true,
                    };
                case SceneArchetype.Palace:
                    return new ArchetypeProfile
                    {
                        RegionKinds = new[] { "square", "palace", "church", "field", "residential", "water", "market", "military" },
                        BoundaryKind = "wall",
                    };
                case SceneArchetype.Church:
                    return new ArchetypeProfile
                    {
                        RegionKinds = new[] { "church", "cemetery", "square", "residential", "field", "market", "water", "residential" },
                        BoundaryKind = "fence",
                    };
                case SceneArchetype.Market:
                    return new ArchetypeProfile
                    {
                        RegionKinds = new[] { "market", "square", "residential", "church", "field", "water", "residential", "military" },
                    };
                case SceneArchetype.Square:
                    return new ArchetypeProfile
                    {
                        RegionKinds = new[] { "square", "market", "church", "residential", "residential", "field", "water", "military" },
                    };
                case SceneArchetype.Countryside:
                    return new ArchetypeProfile
                    {
                        RegionKinds = new[] { "field", "residential", "field", "water", "market", "church", "field", "residential" },
                        BoundaryKind = "fence",
                    };
                default:
                    return new ArchetypeProfile
                    {
                        RegionKinds = new[] { "square", "residential", "market", "church", "residential", "field", "residential", "water", "residential" },
                    };
            }
        }

        // ─────────────────────────── 区域 ───────────────────────────

        private List<GrayboxRegion> BuildRegions(SceneArchetype archetype, string style, GrayboxRandom rng, float halfW, float halfL)
        {
            var regions = new List<GrayboxRegion>();
            float width = halfW * 2f;
            float length = halfL * 2f;
            float minSide = Mathf.Min(width, length);

            int cols = minSide < 34f ? 2 : 3;
            int rows = minSide < 34f ? 2 : (minSide < 52f ? 2 : 3);
            float margin = Mathf.Clamp(minSide * 0.07f, 3f, 12f);
            float gutter = Mathf.Clamp(minSide * 0.08f, 3f, 9f);
            float usableW = Mathf.Max(8f, width - margin * 2f);
            float usableH = Mathf.Max(8f, length - margin * 2f);
            while (cols > 1 && (usableW - gutter * (cols - 1)) / cols < 6f) cols--;
            while (rows > 1 && (usableH - gutter * (rows - 1)) / rows < 6f) rows--;

            float cellW = (usableW - gutter * (cols - 1)) / cols;
            float cellH = (usableH - gutter * (rows - 1)) / rows;
            if (cellW < 4f || cellH < 4f) return regions;

            List<Vector2Int> order = CellOrder(cols, rows);
            ArchetypeProfile profile = GetProfile(archetype);
            for (int i = 0; i < order.Count; i++)
            {
                Vector2Int cell = order[i];
                float x0 = -halfW + margin + cell.x * (cellW + gutter) + RegionInset;
                float x1 = x0 + cellW - RegionInset * 2f;
                float y0 = -halfL + margin + cell.y * (cellH + gutter) + RegionInset;
                float y1 = y0 + cellH - RegionInset * 2f;
                if (x1 - x0 < 3f || y1 - y0 < 3f) continue;

                string kind = profile.RegionKinds[i % profile.RegionKinds.Length];
                regions.Add(new GrayboxRegion
                {
                    regionId = "region_" + kind + "_" + i.ToString(CultureInfo.InvariantCulture),
                    kind = kind,
                    description = RegionDescription(kind, style),
                    style = RegionStyleText(kind, style),
                    polygon = RectRing(x0, y0, x1, y1),
                    groundHeight = 0f,
                    areaWeight = 1f,
                });
            }

            if (regions.Count > 0)
            {
                float total = 0f;
                for (int i = 0; i < regions.Count; i++) total += Mathf.Abs(PolygonArea(regions[i].polygon));
                float average = total / regions.Count;
                if (average > 0.001f)
                {
                    for (int i = 0; i < regions.Count; i++)
                        regions[i].areaWeight = Mathf.Clamp(Mathf.Abs(PolygonArea(regions[i].polygon)) / average, 0.2f, 3f);
                }
            }
            return regions;
        }

        /// <summary>网格中心优先的取格顺序：重要区域（广场/集市）落在场景中心，外围是居民区/农田/水域。</summary>
        private static List<Vector2Int> CellOrder(int cols, int rows)
        {
            var result = new List<Vector2Int>(cols * rows);
            var used = new bool[cols * rows];
            float centerX = (cols - 1) * 0.5f;
            float centerY = (rows - 1) * 0.5f;
            for (int n = 0; n < cols * rows; n++)
            {
                int bestIndex = -1;
                float bestDistance = float.MaxValue;
                for (int c = 0; c < cols; c++)
                {
                    for (int r = 0; r < rows; r++)
                    {
                        int index = r * cols + c;
                        if (used[index]) continue;
                        float dx = c - centerX;
                        float dy = r - centerY;
                        float distance = dx * dx + dy * dy;
                        if (bestIndex < 0 || distance < bestDistance)
                        {
                            bestIndex = index;
                            bestDistance = distance;
                        }
                    }
                }
                if (bestIndex < 0) break;
                used[bestIndex] = true;
                result.Add(new Vector2Int(bestIndex % cols, bestIndex / cols));
            }
            return result;
        }

        // ─────────────────────────── 道路 / 墙 / 战壕 ───────────────────────────

        private List<GrayboxPath> BuildPaths(SceneArchetype archetype, List<GrayboxRegion> regions, Vector2 spawn, GrayboxRandom rng, float halfW, float halfL)
        {
            var paths = new List<GrayboxPath>();

            var main = new GrayboxPath { pathId = "path_main", kind = "road", width = MainRoadWidth, nodes = new List<Vector2>() };
            main.nodes.Add(ClampPoint(spawn, halfW, halfL, 1f));
            main.nodes.Add(ClampPoint(new Vector2(spawn.x * 0.45f, -halfL * 0.35f), halfW, halfL, 1f));
            main.nodes.Add(new Vector2(0f, 0f));
            main.nodes.Add(new Vector2(0f, halfL * 0.78f));
            paths.Add(main);

            var cross = new GrayboxPath { pathId = "path_cross", kind = "road", width = BranchRoadWidth + 0.8f, nodes = new List<Vector2>() };
            cross.nodes.Add(ClampPoint(new Vector2(-halfW * 0.86f, 0f), halfW, halfL, 1f));
            cross.nodes.Add(new Vector2(0f, 0f));
            cross.nodes.Add(ClampPoint(new Vector2(halfW * 0.86f, 0f), halfW, halfL, 1f));
            paths.Add(cross);

            for (int i = 0; i < regions.Count; i++)
            {
                GrayboxRegion region = regions[i];
                if (region == null || region.polygon == null || region.polygon.Count < 3) continue;
                float minX, maxX, minY, maxY;
                GetBounds(region.polygon, out minX, out maxX, out minY, out maxY);
                float cx = (minX + maxX) * 0.5f;
                float cy = (minY + maxY) * 0.5f;
                if (Mathf.Abs(cx) < 1.5f && Mathf.Abs(cy) < 1.5f) continue; // 中心区域已被主干道覆盖

                var nodes = new List<Vector2>();
                if (Mathf.Abs(cx) >= Mathf.Abs(cy))
                {
                    nodes.Add(ClampPoint(new Vector2(0f, cy), halfW, halfL, 1f));
                    nodes.Add(ClampPoint(new Vector2(cx * 0.5f, cy), halfW, halfL, 1f));
                }
                else
                {
                    nodes.Add(ClampPoint(new Vector2(cx, 0f), halfW, halfL, 1f));
                    nodes.Add(ClampPoint(new Vector2(cx, cy * 0.5f), halfW, halfL, 1f));
                }
                nodes.Add(ClampPoint(new Vector2(cx * 0.8f, cy * 0.8f), halfW, halfL, 1f));
                paths.Add(new GrayboxPath
                {
                    pathId = "path_branch_" + i.ToString(CultureInfo.InvariantCulture),
                    kind = "road",
                    width = BranchRoadWidth,
                    nodes = nodes,
                });
            }

            ArchetypeProfile profile = GetProfile(archetype);
            if (!string.IsNullOrEmpty(profile.BoundaryKind))
            {
                float inset = 1.6f;
                paths.Add(new GrayboxPath
                {
                    pathId = "path_boundary",
                    kind = profile.BoundaryKind,
                    width = profile.BoundaryKind == "wall" ? 1.4f : 0.7f,
                    nodes = RectRing(-halfW + inset, -halfL + inset, halfW - inset, halfL - inset),
                });
            }

            if (profile.HasTrench)
            {
                var trench = new GrayboxPath { pathId = "path_trench", kind = "trench", width = 2.4f, nodes = new List<Vector2>() };
                trench.nodes.Add(ClampPoint(new Vector2(-halfW * 0.8f, halfL * 0.30f), halfW, halfL, 1f));
                trench.nodes.Add(ClampPoint(new Vector2(0f, halfL * 0.22f), halfW, halfL, 1f));
                trench.nodes.Add(ClampPoint(new Vector2(halfW * 0.8f, halfL * 0.28f), halfW, halfL, 1f));
                paths.Add(trench);
            }
            return paths;
        }

        // ─────────────────────────── 语义槽 ───────────────────────────

        private void AddRegionSlots(GrayboxPlan plan, SceneArchetype archetype, string style, int count, GrayboxRandom rng)
        {
            if (plan.regions.Count == 0 || count <= 0) return;
            var perRegionIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            int assigned = 0;
            int cursor = 0;
            int guard = count * 4 + 8;
            while (assigned < count && guard-- > 0)
            {
                GrayboxRegion region = plan.regions[cursor % plan.regions.Count];
                cursor++;
                if (region == null || region.polygon == null || region.polygon.Count < 3) continue;
                string key = region.regionId ?? string.Empty;
                int index;
                perRegionIndex.TryGetValue(key, out index);
                perRegionIndex[key] = index + 1;
                AddRegionSlot(plan, region, index, archetype, style, rng);
                assigned++;
            }
        }

        private void AddRegionSlot(GrayboxPlan plan, GrayboxRegion region, int index, SceneArchetype archetype, string style, GrayboxRandom rng)
        {
            Vector2 position = PointInRegion(region, index, rng);
            SlotKind kind = SlotKindForRegion(region.kind, index);
            string semantic = SlotSemantic(region.kind, kind, index, style, archetype, rng);
            float rotation = (rng.Next01() * 2f - 1f) * 180f;
            float sizeX;
            float sizeY;
            if (kind == SlotKind.Building)
            {
                sizeX = rng.Range(6f, 13f);
                sizeY = sizeX * rng.Range(0.62f, 0.95f);
                rotation = Mathf.Round(rng.Next01() * 3f) * 90f;
            }
            else
            {
                sizeX = rng.Range(0.8f, 3.2f);
                sizeY = sizeX * rng.Range(0.6f, 1.2f);
            }

            plan.slots.Add(new GrayboxSlot
            {
                slotId = "slot_" + region.kind + "_" + plan.slots.Count.ToString(CultureInfo.InvariantCulture),
                regionId = region.regionId,
                semantic = semantic,
                position = position,
                rotation = Mathf.DeltaAngle(0f, rotation),
                sizeX = sizeX,
                sizeY = sizeY,
                kind = kind,
                bindId = string.Empty,
                expectedModelHint = string.Empty,
            });
        }

        private void AddRoadSlots(GrayboxPlan plan, string style, int maxSlots, GrayboxRandom rng)
        {
            if (maxSlots <= 0) return;
            int inserted = 0;
            for (int p = 0; p < plan.paths.Count && inserted < maxSlots; p++)
            {
                GrayboxPath path = plan.paths[p];
                if (path == null || path.kind != "road" || path.nodes == null || path.nodes.Count < 2) continue;
                float spacing = (path.pathId == "path_main" || path.pathId == "path_cross") ? 13f : 17f;
                float length = PolylineLength(path.nodes);
                if (length < spacing) continue;
                float side = 1f;
                for (float distance = spacing * 0.5f; distance < length - 2f && inserted < maxSlots; distance += spacing)
                {
                    Vector2 point;
                    Vector2 tangent;
                    if (!PointAlong(path.nodes, distance, out point, out tangent)) break;
                    Vector2 normal = new Vector2(-tangent.y, tangent.x);
                    Vector2 position = point + normal * ((path.width * 0.5f + 1.1f) * side);
                    side = -side;
                    position = ClampPoint(position, plan.width * 0.5f, plan.length * 0.5f, 0.8f);
                    plan.slots.Add(new GrayboxSlot
                    {
                        slotId = "slot_street_" + plan.slots.Count.ToString(CultureInfo.InvariantCulture),
                        regionId = NearestRegionId(plan.regions, position),
                        semantic = ApplyStyle(RoadsidePhrases[inserted % RoadsidePhrases.Length], style),
                        position = position,
                        rotation = Mathf.DeltaAngle(0f, Mathf.Atan2(tangent.y, tangent.x) * Mathf.Rad2Deg),
                        sizeX = 1.2f,
                        sizeY = 1.2f,
                        kind = SlotKind.Prop,
                        bindId = string.Empty,
                        expectedModelHint = string.Empty,
                    });
                    inserted++;
                }
            }
        }

        private void AddBoundSlots(GrayboxPlan plan, SceneSpec scene, SceneArchetype archetype, string style, GrayboxRandom rng)
        {
            if (scene == null) return;
            float halfW = plan.width * 0.5f;
            float halfL = plan.length * 0.5f;

            if (scene.npcs != null)
            {
                for (int i = 0; i < scene.npcs.Count; i++)
                {
                    NpcSpec npc = scene.npcs[i];
                    if (npc == null) continue;
                    GrayboxRegion region = FindRegionForNpc(plan.regions, npc, archetype);
                    Vector2 position = region != null ? PointInRegion(region, i + 1, rng) : new Vector2(0f, 0f);
                    plan.slots.Add(new GrayboxSlot
                    {
                        slotId = "slot_npc_" + Slug(npc.npcId, i),
                        regionId = region != null ? region.regionId : string.Empty,
                        semantic = NpcSemantic(npc, style),
                        position = ClampPoint(position, halfW, halfL, 0.8f),
                        rotation = Mathf.DeltaAngle(0f, (rng.Next01() * 2f - 1f) * 180f),
                        sizeX = 0.6f,
                        sizeY = 0.6f,
                        kind = SlotKind.Npc,
                        bindId = npc.npcId ?? string.Empty,
                        expectedModelHint = "人物，全身像",
                    });
                }
            }

            if (scene.anchors != null)
            {
                for (int i = 0; i < scene.anchors.Count; i++)
                {
                    SceneAnchorSpec anchor = scene.anchors[i];
                    if (anchor == null) continue;
                    Vector2 position = AnchorPosition(plan, i, halfW, halfL);
                    plan.slots.Add(new GrayboxSlot
                    {
                        slotId = "slot_anchor_" + Slug(anchor.anchorId, i),
                        regionId = NearestRegionId(plan.regions, position),
                        semantic = AnchorSemantic(anchor, style),
                        position = position,
                        rotation = Mathf.DeltaAngle(0f, Mathf.Atan2(-position.y, -position.x) * Mathf.Rad2Deg),
                        sizeX = 2.4f,
                        sizeY = 2.4f,
                        kind = SlotKind.Anchor,
                        bindId = anchor.anchorId ?? string.Empty,
                        expectedModelHint = !string.IsNullOrEmpty(anchor.modelHint) ? anchor.modelHint : (anchor.displayName ?? string.Empty),
                    });
                }
            }

            if (scene.props != null)
            {
                for (int i = 0; i < scene.props.Count; i++)
                {
                    PropInteraction prop = scene.props[i];
                    if (prop == null) continue;
                    GrayboxRegion region = (plan.regions.Count > 0) ? plan.regions[i % plan.regions.Count] : null;
                    Vector2 position = region != null ? PointInRegion(region, i + 2, rng) : new Vector2(0f, 0f);
                    plan.slots.Add(new GrayboxSlot
                    {
                        slotId = "slot_prop_" + Slug(prop.propId, i),
                        regionId = region != null ? region.regionId : string.Empty,
                        semantic = PropSemantic(prop, style),
                        position = ClampPoint(position, halfW, halfL, 0.8f),
                        rotation = Mathf.DeltaAngle(0f, (rng.Next01() * 2f - 1f) * 180f),
                        sizeX = 2.2f,
                        sizeY = 2.2f,
                        kind = SlotKind.Prop,
                        bindId = prop.propId ?? string.Empty,
                        expectedModelHint = prop.displayName ?? string.Empty,
                    });
                }
            }
        }

        /// <summary>锚点位置：交替放在场景边缘（供玩家走入）与建筑区域旁边（供剧情触发）。</summary>
        private static Vector2 AnchorPosition(GrayboxPlan plan, int index, float halfW, float halfL)
        {
            float inset = 2.6f;
            if (index % 2 == 1 && plan.regions.Count > 0)
            {
                GrayboxRegion region = plan.regions[index % plan.regions.Count];
                if (region != null && region.polygon != null && region.polygon.Count >= 3)
                {
                    float minX, maxX, minY, maxY;
                    GetBounds(region.polygon, out minX, out maxX, out minY, out maxY);
                    float cx = (minX + maxX) * 0.5f;
                    float cy = (minY + maxY) * 0.5f;
                    Vector2 direction = new Vector2(-cx, -cy);
                    if (direction.sqrMagnitude < 0.01f) direction = new Vector2(0f, -1f);
                    direction.Normalize();
                    float extent = Mathf.Min(maxX - minX, maxY - minY) * 0.5f;
                    Vector2 position = new Vector2(cx, cy) + direction * (extent + 1.4f);
                    return ClampPoint(position, halfW, halfL, 1.6f);
                }
            }

            float rectW = Mathf.Max(4f, plan.width - inset * 2f);
            float rectH = Mathf.Max(4f, plan.length - inset * 2f);
            float fraction = 0.08f + 0.13f * (index / 2);
            fraction = Mathf.Repeat(fraction, 0.95f);
            return ClampPoint(PointOnRect(rectW, rectH, fraction), halfW, halfL, 1.6f);
        }

        /// <summary>沿矩形周长取点（fraction ∈ [0,1)，从底边中点为起点顺时针）。</summary>
        private static Vector2 PointOnRect(float width, float length, float fraction)
        {
            float w = Mathf.Max(0.01f, width);
            float h = Mathf.Max(0.01f, length);
            float perimeter = 2f * (w + h);
            float distance = Mathf.Clamp01(fraction) * perimeter;
            if (distance < w)
                return new Vector2(-w * 0.5f + distance, -h * 0.5f);
            distance -= w;
            if (distance < h)
                return new Vector2(w * 0.5f, -h * 0.5f + distance);
            distance -= h;
            if (distance < w)
                return new Vector2(w * 0.5f - distance, h * 0.5f);
            distance -= w;
            return new Vector2(-w * 0.5f, h * 0.5f - distance);
        }

        private Vector2 PointInRegion(GrayboxRegion region, int index, GrayboxRandom rng)
        {
            float minX, maxX, minY, maxY;
            GetBounds(region.polygon, out minX, out maxX, out minY, out maxY);
            Vector2 fraction = SlotPatterns[index % SlotPatterns.Length];
            float fx = Mathf.Clamp(fraction.x + (rng.Next01() * 2f - 1f) * 0.05f, 0.14f, 0.86f);
            float fy = Mathf.Clamp(fraction.y + (rng.Next01() * 2f - 1f) * 0.05f, 0.14f, 0.86f);
            return new Vector2(
                LerpAxis(minX + RegionInset, maxX - RegionInset, fx),
                LerpAxis(minY + RegionInset, maxY - RegionInset, fy));
        }

        // ─────────────────────────── NPC 动线 ───────────────────────────

        private List<NpcPath> BuildNpcPaths(SceneSpec scene, List<GrayboxRegion> regions, SceneArchetype archetype, GrayboxRandom rng)
        {
            var result = new List<NpcPath>();
            if (scene == null || scene.npcs == null) return result;

            for (int i = 0; i < scene.npcs.Count; i++)
            {
                NpcSpec npc = scene.npcs[i];
                if (npc == null) continue;
                var path = new NpcPath
                {
                    npcId = npc.npcId ?? string.Empty,
                    loop = true,
                    speed = SpeedForNpc(npc),
                };

                GrayboxRegion region = FindRegionForNpc(regions, npc, archetype);
                if (region == null || region.polygon == null || region.polygon.Count < 3)
                {
                    path.waypoints.Add(new Vector2(-2f, 0f));
                    path.waypoints.Add(new Vector2(2f, 0f));
                }
                else
                {
                    float minX, maxX, minY, maxY;
                    GetBounds(region.polygon, out minX, out maxX, out minY, out maxY);
                    float inset = RegionInset + 0.8f;
                    float[] fractions = { 0.24f, 0.76f, 0.76f, 0.24f };
                    float[] fractionsY = { 0.24f, 0.24f, 0.76f, 0.76f };
                    for (int k = 0; k < 4; k++)
                    {
                        float fx = Mathf.Clamp(fractions[k] + (rng.Next01() * 2f - 1f) * 0.05f, 0.1f, 0.9f);
                        float fy = Mathf.Clamp(fractionsY[k] + (rng.Next01() * 2f - 1f) * 0.05f, 0.1f, 0.9f);
                        path.waypoints.Add(new Vector2(
                            LerpAxis(minX + inset, maxX - inset, fx),
                            LerpAxis(minY + inset, maxY - inset, fy)));
                    }
                }
                result.Add(path);
            }
            return result;
        }

        private static float SpeedForNpc(NpcSpec npc)
        {
            string text = ((npc.displayName ?? string.Empty) + " " + (npc.tag ?? string.Empty) + " " + (npc.persona ?? string.Empty)).ToLowerInvariant();
            if (ContainsAny(text, new[] { "孩子", "少年", "孩童", "跑" })) return 1.9f;
            if (ContainsAny(text, new[] { "守卫", "卫兵", "士兵", "军", "哨" })) return 1.0f;
            if (ContainsAny(text, new[] { "老人", "长者", "老太", "牧师" })) return 0.8f;
            if (ContainsAny(text, new[] { "马", "狗", "奔" })) return 2.2f;
            if (ContainsAny(text, new[] { "商人", "摊", "贩" })) return 1.15f;
            return 1.2f;
        }

        private static GrayboxRegion FindRegionForNpc(List<GrayboxRegion> regions, NpcSpec npc, SceneArchetype archetype)
        {
            if (regions == null || regions.Count == 0) return null;
            string wanted = npc != null ? (npc.startingRegionId ?? string.Empty) : string.Empty;
            if (!string.IsNullOrEmpty(wanted))
            {
                for (int i = 0; i < regions.Count; i++)
                    if (regions[i] != null && regions[i].regionId == wanted) return regions[i];
                for (int i = 0; i < regions.Count; i++)
                    if (regions[i] != null && regions[i].kind == wanted) return regions[i];
            }

            string[] preferred = PreferredNpcRegionKinds(archetype);
            for (int k = 0; k < preferred.Length; k++)
            {
                for (int i = 0; i < regions.Count; i++)
                    if (regions[i] != null && regions[i].kind == preferred[k]) return regions[i];
            }
            return regions[0];
        }

        private static string[] PreferredNpcRegionKinds(SceneArchetype archetype)
        {
            switch (archetype)
            {
                case SceneArchetype.Countryside: return NpcPreferredCountryside;
                case SceneArchetype.Harbor: return NpcPreferredHarbor;
                case SceneArchetype.Palace: return NpcPreferredPalace;
                case SceneArchetype.Battlefield: return NpcPreferredBattlefield;
                case SceneArchetype.Church: return NpcPreferredChurch;
                case SceneArchetype.Market: return NpcPreferredMarket;
                case SceneArchetype.Square: return NpcPreferredSquare;
                default: return NpcPreferredTown;
            }
        }

        // ─────────────────────────── 语义文本 ───────────────────────────

        private static SlotKind SlotKindForRegion(string regionKind, int index)
        {
            switch (regionKind)
            {
                case "residential":
                    return index % 3 == 0 ? SlotKind.Building : (index % 3 == 1 ? SlotKind.Prop : SlotKind.Decoration);
                case "church":
                    return index == 0 ? SlotKind.Building : (index % 2 == 0 ? SlotKind.Decoration : SlotKind.Prop);
                case "market":
                    return index % 3 == 0 ? SlotKind.Prop : SlotKind.Decoration;
                case "water":
                    return SlotKind.Decoration;
                case "field":
                    return index == 0 ? SlotKind.Building : SlotKind.Decoration;
                case "harbor":
                    return index % 3 == 0 ? SlotKind.Building : SlotKind.Prop;
                case "military":
                    return index % 2 == 0 ? SlotKind.Building : SlotKind.Prop;
                case "square":
                    return index == 0 ? SlotKind.Decoration : SlotKind.Prop;
                case "cemetery":
                    return SlotKind.Decoration;
                case "palace":
                    return index % 2 == 0 ? SlotKind.Building : SlotKind.Decoration;
                default:
                    return index % 2 == 0 ? SlotKind.Prop : SlotKind.Decoration;
            }
        }

        private static string SlotSemantic(string regionKind, SlotKind kind, int index, string style, SceneArchetype archetype, GrayboxRandom rng)
        {
            if (kind == SlotKind.Building) return ApplyStyle(MainStructurePhrase(regionKind), style);
            string[] pool = PropPhrases(regionKind);
            string phrase = pool[(index + rng.Range(0, pool.Length)) % pool.Length];
            return ApplyStyle(phrase, style);
        }

        private static string MainStructurePhrase(string regionKind)
        {
            switch (regionKind)
            {
                case "residential": return "两层木构民居，石砌地基，瓦屋顶，木格窗";
                case "church": return "石砌教堂，带尖顶钟楼，拱形彩窗";
                case "market": return "木结构集市大棚，开放式木柱与棚顶";
                case "harbor": return "石砌码头仓库，厚木门与吊货木梁";
                case "military": return "石砌卫兵室，厚木门，窄窗，石阶";
                case "field": return "风车磨坊，石砌塔身，四片木制叶片";
                case "palace": return "石砌宫殿主楼，柱廊与拱门，石阶";
                case "cemetery": return "石砌小礼拜堂，尖拱窗，低矮石墙";
                case "water": return "石砌水井，木顶与辘轳";
                case "square": return "石砌喷泉，中央雕像，圆形水池";
                default: return "石砌建筑，木门木窗，瓦屋顶";
            }
        }

        private static string[] PropPhrases(string regionKind)
        {
            switch (regionKind)
            {
                case "residential": return ResidentialProps;
                case "square": return SquareProps;
                case "market": return MarketProps;
                case "water": return WaterProps;
                case "field": return FieldProps;
                case "harbor": return HarborProps;
                case "military": return MilitaryProps;
                case "church": return ChurchProps;
                case "cemetery": return CemeteryProps;
                case "palace": return PalaceProps;
                default: return GenericProps;
            }
        }

        /// <summary>把风格插到第一个逗号之后：「石砌教堂，中世纪西欧风格，带尖顶钟楼」。</summary>
        private static string ApplyStyle(string phrase, string style)
        {
            string text = (phrase ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(style)) return text;
            string styleText = style + "风格";
            if (text.IndexOf(styleText, StringComparison.Ordinal) >= 0) return text;
            int comma = text.IndexOf('，');
            if (comma < 0) return text.Length == 0 ? styleText : text + "，" + styleText;
            return text.Substring(0, comma) + "，" + styleText + text.Substring(comma);
        }

        private static string RegionDescription(string regionKind, string style)
        {
            switch (regionKind)
            {
                case "residential": return "居民区：民居沿路排布，街巷串联，是场景里的生活区。";
                case "square": return "广场：石板铺地，人流集散，剧情事件多在此发生。";
                case "church": return "教堂区：石砌教堂与钟楼，周围是前庭与低矮围墙。";
                case "market": return "集市区：木制摊位与布篷，货箱推车靠边堆放。";
                case "water": return "水域：石砌驳岸与木栈桥，供船只停靠或取水。";
                case "field": return "农田：栅栏圈出的耕地与草垛，边缘有风车磨坊。";
                case "harbor": return "码头区：栈桥、系船柱与货箱，小船沿岸停靠。";
                case "military": return "军事区：营房、炮位与拒马，土垒围绕。";
                case "cemetery": return "墓园：低矮石墙、石碑与枯树，安静阴冷。";
                case "palace": return "宫殿区：柱廊主楼与石阶广场，等级森严。";
                default: return "功能区域：" + regionKind;
            }
        }

        private static string RegionStyleText(string regionKind, string style)
        {
            string prefix = string.IsNullOrEmpty(style) ? "欧洲古典" : style;
            switch (regionKind)
            {
                case "residential": return prefix + "居民区，居民楼沿路而建，二层木构与石基，屋檐错落";
                case "square": return prefix + "广场，石板铺地与石阶，中央有喷泉或纪念碑";
                case "church": return prefix + "教堂区，石砌教堂带尖顶与拱窗，前庭有石阶";
                case "market": return prefix + "集市区，木制摊位与粗布篷，人流密集";
                case "water": return prefix + "水域，石砌驳岸与木栈桥，水面平静";
                case "field": return prefix + "农田，木栅栏与麦垛，泥土小路";
                case "harbor": return prefix + "码头区，木栈桥与系船柱，货箱堆积";
                case "military": return prefix + "军事区，土垒与拒马，营房与炮位";
                case "cemetery": return prefix + "墓园，低矮石墙与石碑，枯树";
                case "palace": return prefix + "宫殿区，石砌柱廊与台阶，大理石地面";
                default: return prefix + "区域，砖石与木构混合";
            }
        }

        private static string NpcSemantic(NpcSpec npc, string style)
        {
            string name = (npc.displayName ?? string.Empty).Trim();
            string tag = (npc.tag ?? string.Empty).Trim();
            string persona = (npc.persona ?? string.Empty).Trim();
            string phrase = "人物，全身像";
            if (tag.Length > 0) phrase = tag + "，全身像";
            else if (name.Length > 0) phrase = name + "，全身像";
            if (persona.Length > 0) phrase += "，" + Truncate(persona, 26);
            return ApplyStyle(phrase, style);
        }

        private static string AnchorSemantic(SceneAnchorSpec anchor, string style)
        {
            string phrase = (anchor.displayName ?? string.Empty).Trim();
            if (phrase.Length == 0) phrase = "传送锚点";
            string requirement = (anchor.requirement ?? string.Empty).Trim();
            string hint = (anchor.modelHint ?? string.Empty).Trim();
            if (requirement.Length > 0) phrase += "，" + Truncate(requirement, 24);
            else if (hint.Length > 0) phrase += "，" + Truncate(hint, 24);
            string description = (anchor.description ?? string.Empty).Trim();
            if (description.Length > 0) phrase += "，" + Truncate(description, 24);
            return ApplyStyle(phrase, style);
        }

        private static string PropSemantic(PropInteraction prop, string style)
        {
            string name = (prop.displayName ?? string.Empty).Trim();
            string description = (prop.description ?? string.Empty).Trim();
            string phrase = name;
            if (description.Length > 0) phrase = phrase.Length == 0 ? description : phrase + "，" + Truncate(description, 26);
            if (phrase.Length == 0) phrase = "可互动道具，木制，陈旧";
            return ApplyStyle(phrase, style);
        }

        private static string Truncate(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength) return text ?? string.Empty;
            return text.Substring(0, maxLength) + "…";
        }

        private static string Slug(string value, int fallbackIndex)
        {
            if (string.IsNullOrEmpty(value)) return fallbackIndex.ToString(CultureInfo.InvariantCulture);
            var builder = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (char.IsLetterOrDigit(c) || c == '_') builder.Append(c);
            }
            if (builder.Length == 0) builder.Append(fallbackIndex);
            return builder.ToString();
        }

        // ─────────────────────────── 剧情单位补齐 ───────────────────────────

        /// <summary>
        /// 远端工具调用产出的 plan 可能漏掉 NPC 动线 / NPC / 锚点 / 道具槽位，
        /// 这里按 SceneSpec 补齐，保证搭建 Agent 一定能拿到全部剧情单位的落位信息。
        /// </summary>
        public void CompleteBindings(GrayboxPlan plan, SceneSpec scene)
        {
            if (plan == null) return;
            if (plan.regions == null) plan.regions = new List<GrayboxRegion>();
            if (plan.paths == null) plan.paths = new List<GrayboxPath>();
            if (plan.slots == null) plan.slots = new List<GrayboxSlot>();
            if (plan.npcPaths == null) plan.npcPaths = new List<NpcPath>();

            float width = NormalizeSide(plan.width, scene != null ? NormalizeSide(scene.width, DefaultSceneSide) : DefaultSceneSide);
            float length = NormalizeSide(plan.length, scene != null ? NormalizeSide(scene.length, DefaultSceneSide) : DefaultSceneSide);
            plan.width = width;
            plan.length = length;
            float halfW = width * 0.5f;
            float halfL = length * 0.5f;

            SceneArchetype archetype = Classify(scene);
            string style = EraStyle(scene);
            var rng = new GrayboxRandom(unchecked(plan.seed * 31 + 17));

            if (!HasUsableRegion(plan.regions)) plan.regions = BuildRegions(archetype, style, rng, halfW, halfL);
            if (!HasUsablePath(plan.paths)) plan.paths = BuildPaths(archetype, plan.regions, plan.spawnPoint, rng, halfW, halfL);
            if (scene == null) return;

            // NPC：缺动线补动线，缺槽位补槽位
            if (scene.npcs != null)
            {
                for (int i = 0; i < scene.npcs.Count; i++)
                {
                    NpcSpec npc = scene.npcs[i];
                    if (npc == null) continue;
                    string npcId = npc.npcId ?? string.Empty;
                    if (!HasNpcPath(plan.npcPaths, npcId))
                    {
                        GrayboxRegion region = FindRegionForNpc(plan.regions, npc, archetype);
                        var path = new NpcPath { npcId = npcId, loop = true, speed = SpeedForNpc(npc) };
                        if (region != null)
                        {
                            float minX, maxX, minY, maxY;
                            GetBounds(region.polygon, out minX, out maxX, out minY, out maxY);
                            float inset = RegionInset + 0.8f;
                            path.waypoints.Add(new Vector2(LerpAxis(minX + inset, maxX - inset, 0.25f), LerpAxis(minY + inset, maxY - inset, 0.25f)));
                            path.waypoints.Add(new Vector2(LerpAxis(minX + inset, maxX - inset, 0.75f), LerpAxis(minY + inset, maxY - inset, 0.25f)));
                            path.waypoints.Add(new Vector2(LerpAxis(minX + inset, maxX - inset, 0.75f), LerpAxis(minY + inset, maxY - inset, 0.75f)));
                            path.waypoints.Add(new Vector2(LerpAxis(minX + inset, maxX - inset, 0.25f), LerpAxis(minY + inset, maxY - inset, 0.75f)));
                        }
                        else
                        {
                            path.waypoints.Add(new Vector2(-2f, 0f));
                            path.waypoints.Add(new Vector2(2f, 0f));
                        }
                        plan.npcPaths.Add(path);
                    }

                    if (!HasBoundSlot(plan.slots, SlotKind.Npc, npcId))
                    {
                        GrayboxRegion region = FindRegionForNpc(plan.regions, npc, archetype);
                        Vector2 position = region != null ? PointInRegion(region, i + 1, rng) : new Vector2(0f, 0f);
                        plan.slots.Add(new GrayboxSlot
                        {
                            slotId = "slot_npc_" + Slug(npcId, i),
                            regionId = region != null ? region.regionId : string.Empty,
                            semantic = NpcSemantic(npc, style),
                            position = ClampPoint(position, halfW, halfL, 0.8f),
                            rotation = Mathf.DeltaAngle(0f, (rng.Next01() * 2f - 1f) * 180f),
                            sizeX = 0.6f,
                            sizeY = 0.6f,
                            kind = SlotKind.Npc,
                            bindId = npcId,
                            expectedModelHint = "人物，全身像",
                        });
                    }
                }
            }

            if (scene.anchors != null)
            {
                for (int i = 0; i < scene.anchors.Count; i++)
                {
                    SceneAnchorSpec anchor = scene.anchors[i];
                    if (anchor == null) continue;
                    string anchorId = anchor.anchorId ?? string.Empty;
                    if (HasBoundSlot(plan.slots, SlotKind.Anchor, anchorId)) continue;
                    Vector2 position = AnchorPosition(plan, i, halfW, halfL);
                    plan.slots.Add(new GrayboxSlot
                    {
                        slotId = "slot_anchor_" + Slug(anchorId, i),
                        regionId = NearestRegionId(plan.regions, position),
                        semantic = AnchorSemantic(anchor, style),
                        position = position,
                        rotation = Mathf.DeltaAngle(0f, Mathf.Atan2(-position.y, -position.x) * Mathf.Rad2Deg),
                        sizeX = 2.4f,
                        sizeY = 2.4f,
                        kind = SlotKind.Anchor,
                        bindId = anchorId,
                        expectedModelHint = !string.IsNullOrEmpty(anchor.modelHint) ? anchor.modelHint : (anchor.displayName ?? string.Empty),
                    });
                }
            }

            if (scene.props != null)
            {
                for (int i = 0; i < scene.props.Count; i++)
                {
                    PropInteraction prop = scene.props[i];
                    if (prop == null) continue;
                    string propId = prop.propId ?? string.Empty;
                    if (HasBoundSlot(plan.slots, SlotKind.Prop, propId)) continue;
                    GrayboxRegion region = (plan.regions.Count > 0) ? plan.regions[i % plan.regions.Count] : null;
                    Vector2 position = region != null ? PointInRegion(region, i + 2, rng) : new Vector2(0f, 0f);
                    plan.slots.Add(new GrayboxSlot
                    {
                        slotId = "slot_prop_" + Slug(propId, i),
                        regionId = region != null ? region.regionId : string.Empty,
                        semantic = PropSemantic(prop, style),
                        position = ClampPoint(position, halfW, halfL, 0.8f),
                        rotation = Mathf.DeltaAngle(0f, (rng.Next01() * 2f - 1f) * 180f),
                        sizeX = 2.2f,
                        sizeY = 2.2f,
                        kind = SlotKind.Prop,
                        bindId = propId,
                        expectedModelHint = prop.displayName ?? string.Empty,
                    });
                }
            }
        }

        /// <summary>至少有一个面积达标的区域才算「可用」，避免远端返回的空壳区域挡住本地补齐。</summary>
        private static bool HasUsableRegion(List<GrayboxRegion> regions)
        {
            if (regions == null) return false;
            for (int i = 0; i < regions.Count; i++)
            {
                GrayboxRegion region = regions[i];
                if (region == null || region.polygon == null || region.polygon.Count < 3) continue;
                if (Mathf.Abs(PolygonArea(region.polygon)) >= MinRegionArea) return true;
            }
            return false;
        }

        /// <summary>至少有一条 2 个节点以上的条状物才算「可用」，避免单节点脏数据挡住本地补齐。</summary>
        private static bool HasUsablePath(List<GrayboxPath> paths)
        {
            if (paths == null) return false;
            for (int i = 0; i < paths.Count; i++)
            {
                GrayboxPath path = paths[i];
                if (path != null && path.nodes != null && path.nodes.Count >= 2) return true;
            }
            return false;
        }

        private static bool HasNpcPath(List<NpcPath> paths, string npcId)
        {
            if (paths == null) return false;
            for (int i = 0; i < paths.Count; i++)
                if (paths[i] != null && paths[i].npcId == npcId && paths[i].waypoints != null && paths[i].waypoints.Count >= 2) return true;
            return false;
        }

        private static bool HasBoundSlot(List<GrayboxSlot> slots, SlotKind kind, string bindId)
        {
            if (slots == null || string.IsNullOrEmpty(bindId)) return false;
            for (int i = 0; i < slots.Count; i++)
                if (slots[i] != null && slots[i].kind == kind && slots[i].bindId == bindId) return true;
            return false;
        }

        // ─────────────────────────── 清洗 / 校验 ───────────────────────────

        /// <summary>
        /// 无论规划来自远端还是本地，落位前都过一遍清洗：坐标夹紧、多边形闭合、尺寸/宽度 clamp、
        /// id 去重、不合法枚举回退。上层可以直接拿这些数据生成几何体而不会越界。
        /// </summary>
        public static void Sanitize(GrayboxPlan plan, SceneSpec scene)
        {
            if (plan == null) return;
            float width = NormalizeSide(plan.width, scene != null ? NormalizeSide(scene.width, DefaultSceneSide) : DefaultSceneSide);
            float length = NormalizeSide(plan.length, scene != null ? NormalizeSide(scene.length, DefaultSceneSide) : DefaultSceneSide);
            plan.width = width;
            plan.length = length;
            float halfW = width * 0.5f;
            float halfL = length * 0.5f;

            if (string.IsNullOrEmpty(plan.sceneId) && scene != null) plan.sceneId = scene.sceneId ?? string.Empty;
            if (plan.regions == null) plan.regions = new List<GrayboxRegion>();
            if (plan.paths == null) plan.paths = new List<GrayboxPath>();
            if (plan.slots == null) plan.slots = new List<GrayboxSlot>();
            if (plan.npcPaths == null) plan.npcPaths = new List<NpcPath>();

            plan.spawnPoint = ClampPoint(plan.spawnPoint, halfW, halfL, 1.5f);

            var regionIds = new HashSet<string>(StringComparer.Ordinal);
            var regions = new List<GrayboxRegion>();
            for (int i = 0; i < plan.regions.Count; i++)
            {
                GrayboxRegion region = plan.regions[i];
                if (region == null) continue;
                region.polygon = NormalizePolygon(region.polygon, halfW, halfL);
                if (region.polygon.Count < 4)
                {
                    Debug.LogWarning("[灰盒] 区域 " + (i + 1) + " 多边形不足 3 个顶点，已丢弃。");
                    continue;
                }
                if (Mathf.Abs(PolygonArea(region.polygon)) < MinRegionArea)
                {
                    Debug.LogWarning("[灰盒] 区域 " + (region.regionId ?? ("#" + i)) + " 面积过小，已丢弃。");
                    continue;
                }
                string id = string.IsNullOrEmpty(region.regionId) ? "region_auto_" + i.ToString(CultureInfo.InvariantCulture) : region.regionId;
                if (regionIds.Contains(id)) id = id + "_" + i.ToString(CultureInfo.InvariantCulture);
                region.regionId = id;
                regionIds.Add(id);
                if (string.IsNullOrEmpty(region.kind)) region.kind = "residential";
                if (string.IsNullOrEmpty(region.description)) region.description = RegionDescription(region.kind, string.Empty);
                if (string.IsNullOrEmpty(region.style)) region.style = RegionStyleText(region.kind, string.Empty);
                region.groundHeight = Mathf.Clamp(SafeFloat(region.groundHeight, 0f), -30f, 30f);
                region.areaWeight = Mathf.Clamp(SafeFloat(region.areaWeight, 1f), 0.05f, 5f);
                regions.Add(region);
            }
            plan.regions = regions;

            var pathIds = new HashSet<string>(StringComparer.Ordinal);
            var paths = new List<GrayboxPath>();
            for (int i = 0; i < plan.paths.Count; i++)
            {
                GrayboxPath path = plan.paths[i];
                if (path == null) continue;
                path.nodes = NormalizeNodes(path.nodes, halfW, halfL, 2);
                if (path.nodes.Count < 2)
                {
                    Debug.LogWarning("[灰盒] 条状物 " + (i + 1) + " 节点不足 2 个，已丢弃。");
                    continue;
                }
                path.width = Mathf.Clamp(SafeFloat(path.width, 3f), 0.2f, 20f);
                if (!IsValidPathKind(path.kind)) path.kind = "road";
                string id = string.IsNullOrEmpty(path.pathId) ? "path_auto_" + i.ToString(CultureInfo.InvariantCulture) : path.pathId;
                if (pathIds.Contains(id)) id = id + "_" + i.ToString(CultureInfo.InvariantCulture);
                path.pathId = id;
                pathIds.Add(id);
                paths.Add(path);
            }
            plan.paths = paths;

            var slotIds = new HashSet<string>(StringComparer.Ordinal);
            var slots = new List<GrayboxSlot>();
            for (int i = 0; i < plan.slots.Count; i++)
            {
                GrayboxSlot slot = plan.slots[i];
                if (slot == null) continue;
                slot.position = ClampPoint(slot.position, halfW, halfL, 0.5f);
                slot.rotation = Mathf.DeltaAngle(0f, SafeFloat(slot.rotation, 0f));
                // 规格驱动时允许剧情给的真实体量（例如 50×35 的要塞），旧模式仍限制 40m 防巨型方块。
                // 大场景按场景尺寸放宽，否则 200×200 里的 70m 集市棚会被削成 60m。
                float maxSlotSize = plan.specDriven
                    ? Mathf.Clamp(Mathf.Max(60f, Mathf.Max(plan.width, plan.length) * 0.9f), 60f, 180f)
                    : 40f;
                slot.sizeX = Mathf.Clamp(SafeFloat(slot.sizeX, 2f), 0.2f, maxSlotSize);
                slot.sizeY = Mathf.Clamp(SafeFloat(slot.sizeY, 2f), 0.2f, maxSlotSize);
                if (!IsValidSlotKind(slot.kind)) slot.kind = SlotKind.Prop;
                if (string.IsNullOrEmpty(slot.semantic))
                    slot.semantic = string.IsNullOrEmpty(slot.expectedModelHint) ? "灰色占位方块，基础几何体" : slot.expectedModelHint;
                if (!string.IsNullOrEmpty(slot.regionId) && !regionIds.Contains(slot.regionId))
                    slot.regionId = NearestRegionId(plan.regions, slot.position);
                string id = string.IsNullOrEmpty(slot.slotId) ? "slot_auto_" + i.ToString(CultureInfo.InvariantCulture) : slot.slotId;
                if (slotIds.Contains(id)) id = id + "_" + i.ToString(CultureInfo.InvariantCulture);
                slot.slotId = id;
                slotIds.Add(id);
                slots.Add(slot);
            }
            plan.slots = slots;

            var npcIds = new HashSet<string>(StringComparer.Ordinal);
            var npcPaths = new List<NpcPath>();
            for (int i = 0; i < plan.npcPaths.Count; i++)
            {
                NpcPath npcPath = plan.npcPaths[i];
                if (npcPath == null) continue;
                npcPath.waypoints = NormalizeNodes(npcPath.waypoints, halfW, halfL, 2);
                if (npcPath.waypoints.Count < 2) continue;
                string id = string.IsNullOrEmpty(npcPath.npcId) ? "npc_auto_" + i.ToString(CultureInfo.InvariantCulture) : npcPath.npcId;
                if (npcIds.Contains(id)) continue;
                npcPath.npcId = id;
                npcIds.Add(id);
                npcPath.speed = Mathf.Clamp(SafeFloat(npcPath.speed, 1.2f), 0.2f, 5f);
                npcPaths.Add(npcPath);
            }
            plan.npcPaths = npcPaths;
        }

        /// <summary>校验规划是否可拿去生成几何体；失败时给出中文原因。</summary>
        public static bool Validate(GrayboxPlan plan, SceneSpec scene, out string reason)
        {
            reason = string.Empty;
            if (plan == null)
            {
                reason = "plan 为空";
                return false;
            }
            float halfW = Mathf.Max(2f, plan.width * 0.5f);
            float halfL = Mathf.Max(2f, plan.length * 0.5f);
            if (plan.regions == null || plan.regions.Count == 0)
            {
                reason = "没有任何功能区域";
                return false;
            }
            if (plan.slots == null || plan.slots.Count < 3)
            {
                reason = "语义槽不足 3 个";
                return false;
            }
            if (Mathf.Abs(plan.spawnPoint.x) > halfW + 0.01f || Mathf.Abs(plan.spawnPoint.y) > halfL + 0.01f)
            {
                reason = "出生点越界";
                return false;
            }
            for (int i = 0; i < plan.regions.Count; i++)
            {
                GrayboxRegion region = plan.regions[i];
                if (region == null || region.polygon == null || region.polygon.Count < 4)
                {
                    reason = "区域多边形未闭合";
                    return false;
                }
                if (Mathf.Abs(PolygonArea(region.polygon)) < MinRegionArea * 0.5f)
                {
                    reason = "区域面积过小";
                    return false;
                }
                for (int k = 0; k < region.polygon.Count; k++)
                {
                    Vector2 point = region.polygon[k];
                    if (Mathf.Abs(point.x) > halfW + 0.01f || Mathf.Abs(point.y) > halfL + 0.01f)
                    {
                        reason = "区域顶点越界";
                        return false;
                    }
                }
            }
            for (int i = 0; i < plan.slots.Count; i++)
            {
                GrayboxSlot slot = plan.slots[i];
                if (slot == null) continue;
                if (Mathf.Abs(slot.position.x) > halfW + 0.01f || Mathf.Abs(slot.position.y) > halfL + 0.01f)
                {
                    reason = "语义槽位置越界";
                    return false;
                }
                float maxSlotSize = Mathf.Clamp(
                    Mathf.Max(60f, Mathf.Max(plan.width, plan.length) * 0.9f), 60f, 180f);
                if (slot.sizeX <= 0f || slot.sizeY <= 0f || slot.sizeX > maxSlotSize || slot.sizeY > maxSlotSize)
                {
                    reason = "语义槽尺寸异常";
                    return false;
                }
            }
            return true;
        }

        // ─────────────────────────── 枚举解析 ───────────────────────────

        /// <summary>LLM 给的槽位种类字符串 → SlotKind；未知一律当 prop。</summary>
        public static SlotKind ParseSlotKind(string text)
        {
            if (string.IsNullOrEmpty(text)) return SlotKind.Prop;
            switch (text.Trim().ToLowerInvariant())
            {
                case "building":
                case "建筑":
                    return SlotKind.Building;
                case "prop":
                case "道具":
                    return SlotKind.Prop;
                case "npc":
                case "人物":
                case "角色":
                    return SlotKind.Npc;
                case "anchor":
                case "锚点":
                case "传送锚点":
                    return SlotKind.Anchor;
                case "decoration":
                case "decor":
                case "装饰":
                    return SlotKind.Decoration;
                default:
                    return SlotKind.Prop;
            }
        }

        public static bool IsValidSlotKind(SlotKind kind)
        {
            int value = (int)kind;
            return value >= 0 && value <= 4;
        }

        public static bool IsValidPathKind(string kind)
        {
            if (string.IsNullOrEmpty(kind)) return false;
            switch (kind.Trim().ToLowerInvariant())
            {
                case "road":
                case "wall":
                case "fence":
                case "trench":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>把任意路径类型字符串规范成 4 种合法值。</summary>
        public static string NormalizePathKind(string kind)
        {
            if (string.IsNullOrEmpty(kind)) return "road";
            switch (kind.Trim().ToLowerInvariant())
            {
                case "wall":
                case "fence":
                case "trench":
                case "road":
                    return kind.Trim().ToLowerInvariant();
                default:
                    return "road";
            }
        }

        // ─────────────────────────── 几何工具 ───────────────────────────

        /// <summary>闭合矩形环（首点=末点），逆时针。</summary>
        public static List<Vector2> RectRing(float x0, float y0, float x1, float y1)
        {
            float minX = Mathf.Min(x0, x1);
            float maxX = Mathf.Max(x0, x1);
            float minY = Mathf.Min(y0, y1);
            float maxY = Mathf.Max(y0, y1);
            return new List<Vector2>
            {
                new Vector2(minX, minY),
                new Vector2(maxX, minY),
                new Vector2(maxX, maxY),
                new Vector2(minX, maxY),
                new Vector2(minX, minY),
            };
        }

        /// <summary>鞋带公式面积（自动处理闭合/未闭合环）。</summary>
        public static float PolygonArea(List<Vector2> polygon)
        {
            if (polygon == null || polygon.Count < 3) return 0f;
            double sum = 0;
            int count = polygon.Count;
            for (int i = 0; i < count; i++)
            {
                Vector2 a = polygon[i];
                Vector2 b = polygon[(i + 1) % count];
                sum += (double)a.x * b.y - (double)b.x * a.y;
            }
            return (float)(sum * 0.5);
        }

        public static void GetBounds(List<Vector2> polygon, out float minX, out float maxX, out float minY, out float maxY)
        {
            minX = 0f;
            maxX = 0f;
            minY = 0f;
            maxY = 0f;
            if (polygon == null || polygon.Count == 0) return;
            minX = float.MaxValue;
            maxX = float.MinValue;
            minY = float.MaxValue;
            maxY = float.MinValue;
            for (int i = 0; i < polygon.Count; i++)
            {
                Vector2 point = polygon[i];
                if (point.x < minX) minX = point.x;
                if (point.x > maxX) maxX = point.x;
                if (point.y < minY) minY = point.y;
                if (point.y > maxY) maxY = point.y;
            }
        }

        public static Vector2 ClampPoint(Vector2 point, float halfW, float halfL, float margin)
        {
            float limitX = Mathf.Max(0.1f, halfW - margin);
            float limitY = Mathf.Max(0.1f, halfL - margin);
            float x = SafeFloat(point.x, 0f);
            float y = SafeFloat(point.y, 0f);
            return new Vector2(Mathf.Clamp(x, -limitX, limitX), Mathf.Clamp(y, -limitY, limitY));
        }

        private static float LerpAxis(float a, float b, float t)
        {
            if (b < a)
            {
                float mid = (a + b) * 0.5f;
                a = mid;
                b = mid;
            }
            if (Mathf.Abs(b - a) < 0.01f) return (a + b) * 0.5f;
            return Mathf.Lerp(a, b, t);
        }

        private static List<Vector2> NormalizePolygon(List<Vector2> polygon, float halfW, float halfL)
        {
            var result = new List<Vector2>();
            if (polygon == null || polygon.Count < 3) return result;
            for (int i = 0; i < polygon.Count; i++)
            {
                Vector2 point = polygon[i];
                if (float.IsNaN(point.x) || float.IsNaN(point.y) || float.IsInfinity(point.x) || float.IsInfinity(point.y)) continue;
                result.Add(ClampPoint(point, halfW, halfL, 0.2f));
            }
            if (result.Count < 3) return new List<Vector2>();
            // 去掉重复点（包括已闭合的末点）
            var dedup = new List<Vector2>(result.Count);
            for (int i = 0; i < result.Count; i++)
            {
                if (dedup.Count > 0 && (dedup[dedup.Count - 1] - result[i]).sqrMagnitude < 0.0001f) continue;
                dedup.Add(result[i]);
            }
            if (dedup.Count >= 2 && (dedup[0] - dedup[dedup.Count - 1]).sqrMagnitude < 0.0001f) dedup.RemoveAt(dedup.Count - 1);
            if (dedup.Count < 3) return new List<Vector2>();
            // 闭合：末点 = 首点
            dedup.Add(dedup[0]);
            return dedup;
        }

        private static List<Vector2> NormalizeNodes(List<Vector2> nodes, float halfW, float halfL, int minCount)
        {
            var result = new List<Vector2>();
            if (nodes == null) return result;
            for (int i = 0; i < nodes.Count; i++)
            {
                Vector2 point = nodes[i];
                if (float.IsNaN(point.x) || float.IsNaN(point.y) || float.IsInfinity(point.x) || float.IsInfinity(point.y)) continue;
                Vector2 clamped = ClampPoint(point, halfW, halfL, 0.2f);
                if (result.Count > 0 && (result[result.Count - 1] - clamped).sqrMagnitude < 0.0001f) continue;
                result.Add(clamped);
            }
            if (result.Count < minCount) return new List<Vector2>();
            return result;
        }

        private static float PolylineLength(List<Vector2> nodes)
        {
            if (nodes == null || nodes.Count < 2) return 0f;
            float total = 0f;
            for (int i = 1; i < nodes.Count; i++) total += Vector2.Distance(nodes[i - 1], nodes[i]);
            return total;
        }

        private static bool PointAlong(List<Vector2> nodes, float distance, out Vector2 point, out Vector2 tangent)
        {
            point = Vector2.zero;
            tangent = Vector2.right;
            if (nodes == null || nodes.Count < 2) return false;
            float remaining = Mathf.Max(0f, distance);
            for (int i = 1; i < nodes.Count; i++)
            {
                Vector2 a = nodes[i - 1];
                Vector2 b = nodes[i];
                float segment = Vector2.Distance(a, b);
                if (segment <= 0.0001f) continue;
                if (remaining <= segment)
                {
                    float t = remaining / segment;
                    point = Vector2.Lerp(a, b, t);
                    tangent = (b - a) / segment;
                    return true;
                }
                remaining -= segment;
            }
            Vector2 last = nodes[nodes.Count - 1];
            Vector2 previous = nodes[nodes.Count - 2];
            point = last;
            tangent = (last - previous).normalized;
            return true;
        }

        private static string NearestRegionId(List<GrayboxRegion> regions, Vector2 position)
        {
            if (regions == null || regions.Count == 0) return string.Empty;
            string bestId = string.Empty;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < regions.Count; i++)
            {
                GrayboxRegion region = regions[i];
                if (region == null || region.polygon == null || region.polygon.Count < 3) continue;
                float minX, maxX, minY, maxY;
                GetBounds(region.polygon, out minX, out maxX, out minY, out maxY);
                float cx = (minX + maxX) * 0.5f;
                float cy = (minY + maxY) * 0.5f;
                float dx = position.x - cx;
                float dy = position.y - cy;
                float distance = dx * dx + dy * dy;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestId = region.regionId ?? string.Empty;
                }
            }
            return bestId;
        }

        // ─────────────────────────── 数值工具 ───────────────────────────

        private static float NormalizeSide(float value, float fallback)
        {
            return SceneSize.SideOrDefault(value, fallback);
        }

        private static int ComputeSlotBudget(float width, float length)
        {
            float area = Mathf.Max(16f, width * length);
            return Mathf.Clamp(Mathf.RoundToInt(8f + Mathf.Sqrt(area) / 12f), 8, 20);
        }

        private static Vector2 ResolveSpawn(SceneSpec scene, float halfW, float halfL)
        {
            float x = 0f;
            float y = -halfL + 4f;
            if (scene != null)
            {
                Vector3 spawn = scene.spawnPoint;
                if (Mathf.Abs(spawn.x) >= 0.5f || Mathf.Abs(spawn.z) >= 0.5f)
                {
                    x = spawn.x;
                    y = spawn.z;
                }
            }
            return ClampPoint(new Vector2(x, y), halfW, halfL, 1.5f);
        }

        private static float SafeFloat(float value, float fallback)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return fallback;
            return value;
        }

        private static uint StableHash(string text)
        {
            uint hash = 2166136261u;
            if (string.IsNullOrEmpty(text)) return hash;
            for (int i = 0; i < text.Length; i++)
            {
                hash ^= text[i];
                hash *= 16777619u;
            }
            return hash;
        }
    }
}
