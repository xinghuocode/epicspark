// 剧情布局解析器：把 剧情&核心 Agent 写的 layoutDescription（自由中文）解析成结构化地编数据。
//
// 为什么需要它：
//   剧情 Agent 会写出非常具体的地编蓝图，例如
//     「建筑：市政厅位于北侧，占地 X -20…+4、Z +16…+28，三层石构，南立面开九扇拱窗」
//     「道路：东西向主街“圣安托万街”宽10米，中心线 Z=+2，自 (-30,+2) 延伸至 (+30,+2)」
//   但灰盒的确定性规划器只会按 archetype 重新铺一遍网格，把这份蓝图整个丢掉，
//   于是最终场景"像乱拼"。这里把蓝图抽出来，让灰盒按剧情坐标放东西。
//
// 解析策略：不追求通用 NLP，只认中文叙述里稳定出现的几种句式坐标写法：
//   X a…b、Z c…d      → 占地矩形（…/～/../- 都当区间符）
//   (x,z)            → 点坐标
//   宽 N 米 / N×M 米 / 外径 N 米 / 高 N 米 → 尺寸
//   一排N个 / N 座 / N 辆 → 沿长边等分成 N 个槽位
// 解析不出来的一律退回原有 archetype 规划，绝不抛异常。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace EpicSpark.Story
{
    /// <summary>从布局文本里解析出的一个地标/道具。</summary>
    public class LayoutItem
    {
        public string name = "";
        public string raw = "";
        /// <summary>Building / Prop / Decoration。</summary>
        public string kind = "Decoration";
        public Vector2 center;
        public float width = 4f;
        public float depth = 4f;
        public float height;
        public float rotation;
        public bool hasFootprint;
        public int count = 1;
        /// <summary>连续街屋/排屋的分组 id；普通地标为空。</summary>
        public string rowId = "";
        /// <summary>该单元在排屋中的序号（0 起）；普通地标为 0。</summary>
        public int rowIndex;
        /// <summary>这一排应拆出的单元数；由 LayoutSpec 执行器完成实际拆分。</summary>
        public int rowCount = 1;
    }

    /// <summary>地面分区（广场、河滩、草地……）。</summary>
    public class LayoutZone
    {
        public string name = "";
        public string kind = "square";
        public float minX = -20f;
        public float maxX = 20f;
        public float minZ = -20f;
        public float maxZ = 20f;
    }

    /// <summary>街道 / 道路折线。</summary>
    public class LayoutPath
    {
        public string name = "";
        /// <summary>road / wall / fence：界墙与栅栏按墙体渲染，不铺路面。</summary>
        public string kind = "road";
        public float width = 6f;
        public readonly List<Vector2> nodes = new List<Vector2>();
    }

    /// <summary>一份布局文本的解析结果。</summary>
    public class StoryLayout
    {
        public readonly List<LayoutItem> items = new List<LayoutItem>();
        public readonly List<LayoutZone> zones = new List<LayoutZone>();
        public readonly List<LayoutPath> paths = new List<LayoutPath>();
        public string note = "";

        /// <summary>够不够撑起一版地编：至少 3 个地标，或 1 条路 + 2 个地标。</summary>
        public bool Usable
        {
            get { return items.Count >= 3 || (paths.Count >= 1 && items.Count >= 2) || (zones.Count >= 2 && items.Count >= 1); }
        }
    }

    public static class StoryLayoutParser
    {
        private const string Num = @"([+-]?\d+(?:\.\d+)?)";
        private static readonly Regex Footprint = new Regex(
            @"X\s*" + Num + @"\s*\.\.\s*" + Num + @"\s*,?\s*Z\s*" + Num + @"\s*\.\.\s*" + Num,
            RegexOptions.Compiled);
        private static readonly Regex ZRange = new Regex(@"Z\s*" + Num + @"\s*\.\.\s*" + Num, RegexOptions.Compiled);
        private static readonly Regex XRange = new Regex(@"X\s*" + Num + @"\s*\.\.\s*" + Num, RegexOptions.Compiled);
        private static readonly Regex Point = new Regex(@"\(\s*(?:x\s*)?" + Num + @"\s*,\s*(?:z\s*)?" + Num + @"\s*\)", RegexOptions.Compiled);
        // 确定性剧情常用点对矩形：(+16, -14)..(+26, -2)
        private static readonly Regex PointPair = new Regex(
            @"\(\s*(?:x\s*)?" + Num + @"\s*,\s*(?:z\s*)?" + Num + @"\s*\)\s*(?:\.\.|…|—|–|~|～|至|到)\s*\(\s*(?:x\s*)?" + Num + @"\s*,\s*(?:z\s*)?" + Num + @"\s*\)",
            RegexOptions.Compiled);
        // 「正立面在 Z=+21、面阔 18 米、进深 10 米」不是 X..Z 矩形，但同样是一个明确占地。
        private static readonly Regex AxisZ = new Regex(@"Z\s*=\s*" + Num, RegexOptions.Compiled);
        private static readonly Regex AxisX = new Regex(@"X\s*=\s*" + Num, RegexOptions.Compiled);
        private static readonly Regex Frontage = new Regex(@"面阔\s*(\d+(?:\.\d+)?)\s*米", RegexOptions.Compiled);
        private static readonly Regex DepthSize = new Regex(@"进深\s*(\d+(?:\.\d+)?)\s*米", RegexOptions.Compiled);
        private static readonly Regex SizeCross = new Regex(
            @"(\d+(?:\.\d+)?)\s*[x×X*]\s*(\d+(?:\.\d+)?)\s*米", RegexOptions.Compiled);
        private static readonly Regex SizeWidth = new Regex(@"宽\s*(\d+(?:\.\d+)?)\s*米", RegexOptions.Compiled);
        private static readonly Regex SizeRadius = new Regex(@"外径\s*(\d+(?:\.\d+)?)\s*米", RegexOptions.Compiled);
        private static readonly Regex SizeHeight = new Regex(@"高(?:约)?\s*(\d+(?:\.\d+)?)\s*米", RegexOptions.Compiled);
        private static readonly Regex CountPattern = new Regex(
            @"(?:一排)?([一二三四五六七八九十两\d]{1,3})\s*(?:个|座|口|辆|根|张|栋|排|处|只)", RegexOptions.Compiled);
        private static readonly Regex QuoteName = new Regex(@"[“""「《]([^”""」》]{1,16})[”""」》]", RegexOptions.Compiled);

        private static readonly string[] BuildingWords =
        {
            "楼", "堂", "厅", "塔", "城门", "门楼", "堡", "宫", "寺", "院", "桥", "城墙", "民居", "民宅", "住宅",
            "房屋", "店铺", "仓", "衙", "府", "署", "门", "屋", "房", "墙", "堤", "码头", "台阶", "石阶",
        };

        private static readonly string[] PropWords =
        {
            "井", "车", "摊", "箱", "绳", "告示", "长椅", "灯", "炮", "旗", "桌", "桶", "柱", "碑", "木栅", "棚",
            "板", "架", "篮", "袋", "柜台", "钟", "断头台",
        };

        private static readonly string[] ZoneWaterWords = { "河", "水", "堤", "岸", "渠", "湖", "塘", "海" };
        private static readonly string[] ZoneFieldWords = { "土", "地", "田", "草", "林", "泥", "场院", "野" };
        private static readonly string[] ZoneRoadWords = { "街", "路", "巷", "道" };

        private static readonly string[] NameMarkers =
        {
            "位于", "占地", "占", "正立面", "面阔", "进深", "门体", "停放于", "停放", "开在", "通向", "有", "为", "是", "自", "宽", "高", "(", "，", ",",
        };

        private static readonly string[] LeadingNoise =
        {
            "建筑", "道路", "地面", "边界", "区域", "锚点载体坐标", "玩家自出生点", "中心", "正门", "门洞",
            "北侧", "南侧", "东侧", "西侧", "东南", "西北", "东北", "西南", "广场", "一排",
            "停在", "停靠", "停放", "坐落在", "坐落", "矗立着", "矗立", "立着", "新建的", "旧的", "其中",
            "西北角", "东北角", "东南角", "西南角", "北端", "南端", "东端", "西端", "北侧", "南侧", "东侧", "西侧", "\u201c", "\u201d",
        };

        private static readonly string[] UselessNames =
        {
            "两条路", "三岔路", "路口", "南边界", "北边界", "东边界", "西边界", "边界", "地面", "中心", "上空",
            "周围", "四周", "场景", "广场",
        };

        /// <summary>解析布局文本；任何异常都返回空结果。</summary>
        public static StoryLayout Parse(string layoutDescription)
        {
            var layout = new StoryLayout();
            if (string.IsNullOrWhiteSpace(layoutDescription)) return layout;

            string normalized = Normalize(layoutDescription);
            string[] sentences = normalized.Split(new[] { '\n', '。' }, StringSplitOptions.RemoveEmptyEntries);
            var usedNames = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < sentences.Length; i++)
            {
                string sentence = sentences[i].Trim();
                if (sentence.Length < 4) continue;
                try
                {
                    ParseSentence(sentence, layout, usedNames);
                }
                catch (Exception)
                {
                    // 单句解析失败不影响整体
                }
            }

            layout.note = string.Format(CultureInfo.InvariantCulture,
                "剧情布局解析：地标 {0} 个、地面分区 {1} 个、道路 {2} 条", layout.items.Count, layout.zones.Count, layout.paths.Count);
            return layout;
        }

        // ─────────────────────────── 单句 ───────────────────────────

        private static void ParseSentence(string sentence, StoryLayout layout, HashSet<string> usedNames)
        {
            if (IsNoise(sentence)) return;

            // 1) 道路：「宽 N 米」+ 街道词 + 至少两个坐标
            if (IsStreet(sentence))
            {
                LayoutPath parsedPath = ParsePath(sentence);
                if (parsedPath != null && parsedPath.nodes.Count >= 2)
                {
                    layout.paths.Add(parsedPath);
                    // 「路两侧是背靠背的旧货铺与酒馆」这种只有路、建筑没坐标：沿路补两栋
                    if (sentence.IndexOf("占地", StringComparison.Ordinal) < 0 || !HasBuildingWords(sentence))
                    {
                        if (sentence.IndexOf("两侧", StringComparison.Ordinal) >= 0
                            || sentence.IndexOf("沿街", StringComparison.Ordinal) >= 0)
                            AddAlongRoadItems(layout, usedNames, sentence, parsedPath);
                        return;
                    }
                }
            }

            Match footprint = Footprint.Match(sentence);   // X a..b，Z c..d
            Match pointRect = PointPair.Match(sentence);   // (x0,z0)..(x1,z1)
            Match point = Point.Match(sentence);           // (x,z)

            bool hasFootprint = footprint.Success || pointRect.Success;
            bool axisFootprint = false;
            float axisX0 = 0f, axisX1 = 0f, axisZ0 = 0f, axisZ1 = 0f;
            if (!hasFootprint && !point.Success)
                axisFootprint = TryAxisFootprint(sentence, out axisX0, out axisX1, out axisZ0, out axisZ1);
            if (axisFootprint) hasFootprint = true;

            if (!hasFootprint && !point.Success && !ZRange.IsMatch(sentence) && !XRange.IsMatch(sentence)) return;

            string name = ExtractName(sentence);
            if (string.IsNullOrEmpty(name) || name == "广场" || name == "正中" || name == "中心")
                name = ExtractNameAfterPoint(sentence, name);
            string kind = ClassifyKind(name + " " + sentence);
            // 锚点线索只对「没有占地范围」的载体生效。
            // 否则「巴士底狱要塞……吊桥桥头坐标为 (22,25)」会因为句子里出现"桥头"被误判成锚点，
            // 50×35 的要塞整栋从建筑槽里消失（只剩一个 2.4m 锚点标记）。
            bool anchorCue = sentence.IndexOf("锚点载体", StringComparison.Ordinal) >= 0
                             || sentence.IndexOf("载体要求", StringComparison.Ordinal) >= 0
                             || sentence.IndexOf("台阶", StringComparison.Ordinal) >= 0
                             || sentence.IndexOf("桥头", StringComparison.Ordinal) >= 0
                             || sentence.IndexOf("门廊", StringComparison.Ordinal) >= 0
                             || sentence.IndexOf("马车停", StringComparison.Ordinal) >= 0;
            if (anchorCue && !hasFootprint) kind = "Anchor";

            // A0) 多行排布：「三排木摊台沿X轴排列于 z=3、z=7、z=11」→ 每行 3 个摊位。
            // 不能只看到「台」就拆：市政厅的「主台阶」也曾被当成摊台，导致整栋市政厅消失。
            if (sentence.IndexOf("摊", StringComparison.Ordinal) >= 0
                || (sentence.IndexOf("台", StringComparison.Ordinal) >= 0
                    && sentence.IndexOf("排", StringComparison.Ordinal) >= 0))
            {
                if (AddMultiRowStalls(layout, usedNames, name, sentence))
                {
                    if (sentence.IndexOf("棚", StringComparison.Ordinal) >= 0)
                    {
                        Match shed = PointPair.Match(sentence);
                        if (shed.Success)
                        {
                            float sx0 = F(shed.Groups[1].Value), sz0 = F(shed.Groups[2].Value);
                            float sx1 = F(shed.Groups[3].Value), sz1 = F(shed.Groups[4].Value);
                            AddFootprintItems(layout, usedNames, "菜市场棚", sentence, "Building",
                                (sx0 + sx1) * 0.5f, (sz0 + sz1) * 0.5f,
                                Mathf.Abs(sx1 - sx0), Mathf.Abs(sz1 - sz0), 0f);
                        }
                    }
                    return;
                }
            }

            // A1) 界墙留缺口：「自(-16,22)至(16,22)，中间留4米宽缺口」→ 断成两段墙
            if (sentence.IndexOf("缺口", StringComparison.Ordinal) >= 0)
            {
                Match gapA = PointPair.Match(sentence);
                Match gapWidth = SizeWidth.Match(sentence);
                if (gapA.Success && gapWidth.Success)
                {
                    float ax = F(gapA.Groups[1].Value), az = F(gapA.Groups[2].Value);
                    float bx = F(gapA.Groups[3].Value), bz = F(gapA.Groups[4].Value);
                    float gap = F(gapWidth.Groups[1].Value);
                    var dir = new Vector2(bx - ax, bz - az);
                    float len = dir.magnitude;
                    if (len > gap + 2f)
                    {
                        dir = dir / len;
                        var mid = new Vector2((ax + bx) * 0.5f, (az + bz) * 0.5f);
                        Vector2 g0 = mid - dir * (gap * 0.5f);
                        Vector2 g1 = mid + dir * (gap * 0.5f);
                        layout.paths.Add(new LayoutPath { name = name, kind = "wall", width = 1.2f, nodes = { new Vector2(ax, az), g0 } });
                        layout.paths.Add(new LayoutPath { name = name, kind = "wall", width = 1.2f, nodes = { g1, new Vector2(bx, bz) } });
                        return;
                    }
                }
            }

            // A2) 一句写多个占地（「占地分别(20,-6)—(44,4)与(20,10)—(44,20)」）：每个都成地标
            MatchCollection multi = PointPair.Matches(sentence);
            // 【地面】一句里常并列多个铺装范围；每个矩形都要成为地面分区，
            // 不能只取第一个，否则 200×200 大场景只剩中央广场一块地面。
            if (multi.Count >= 2 && sentence.IndexOf("【地面】", StringComparison.Ordinal) >= 0)
            {
                for (int i = 0; i < multi.Count; i++)
                {
                    Match pair = multi[i];
                    float px0 = F(pair.Groups[1].Value), pz0 = F(pair.Groups[2].Value);
                    float px1 = F(pair.Groups[3].Value), pz1 = F(pair.Groups[4].Value);
                    AddZone(layout, name, sentence,
                        Mathf.Min(px0, px1), Mathf.Max(px0, px1),
                        Mathf.Min(pz0, pz1), Mathf.Max(pz0, pz1));
                }
                return;
            }
            if (multi.Count >= 2 && hasFootprint && !LooksLikeGround(sentence, kind))
            {
                for (int i = 0; i < multi.Count; i++)
                {
                    Match pair = multi[i];
                    float px0 = F(pair.Groups[1].Value), pz0 = F(pair.Groups[2].Value);
                    float px1 = F(pair.Groups[3].Value), pz1 = F(pair.Groups[4].Value);
                    string baseName = string.IsNullOrEmpty(name) ? "沿街建筑" : name;
                    float pairW = Mathf.Abs(px1 - px0);
                    float pairD = Mathf.Abs(pz1 - pz0);
                    // 「北侧沿街民居……第一排占地(-40,52)—(-10,70)，第二排占地(10,52)—(40,70)」
                    // 是两个 30×18 的排屋地块，不能各放成一栋 30 米宽的大房子。
                    int pairSplit = 1;
                    if (IsRowName(baseName, sentence))
                    {
                        float longSide = Mathf.Max(pairW, pairD);
                        if (longSide >= 18f)
                            pairSplit = Mathf.Clamp(Mathf.RoundToInt(longSide / 9f), 2, 8);
                    }
                    AddFootprintItems(layout, usedNames, baseName + "_" + (i + 1).ToString(CultureInfo.InvariantCulture),
                        sentence, kind, (px0 + px1) * 0.5f, (pz0 + pz1) * 0.5f,
                        pairW, pairD, 0f, pairSplit);
                }
                return;
            }

            float x0 = 0f, x1 = 0f, z0 = 0f, z1 = 0f;
            if (footprint.Success)
            {
                x0 = F(footprint.Groups[1].Value);
                x1 = F(footprint.Groups[2].Value);
                z0 = F(footprint.Groups[3].Value);
                z1 = F(footprint.Groups[4].Value);
            }
            else if (pointRect.Success)
            {
                x0 = F(pointRect.Groups[1].Value);
                z0 = F(pointRect.Groups[2].Value);
                x1 = F(pointRect.Groups[3].Value);
                z1 = F(pointRect.Groups[4].Value);
            }
            else if (axisFootprint)
            {
                x0 = axisX0;
                x1 = axisX1;
                z0 = axisZ0;
                z1 = axisZ1;
            }
            if (hasFootprint)
            {
                float swap;
                if (x0 > x1) { swap = x0; x0 = x1; x1 = swap; }
                if (z0 > z1) { swap = z0; z0 = z1; z1 = swap; }
            }

            float centerX, centerZ, width, depth;
            if (hasFootprint)
            {
                width = Mathf.Abs(x1 - x0);
                depth = Mathf.Abs(z1 - z0);
                centerX = (x0 + x1) * 0.5f;
                centerZ = (z0 + z1) * 0.5f;
            }
            else if (point.Success)
            {
                centerX = F(point.Groups[1].Value);
                centerZ = F(point.Groups[2].Value);
                width = 4f;
                depth = 4f;
            }
            else
            {
                centerX = 0f;
                centerZ = 0f;
                width = 4f;
                depth = 4f;
            }

            // 2) 地面分区：有占地范围 + 描述是地面材质/水域/河堤
            if (hasFootprint && LooksLikeGround(sentence, kind))
            {
                AddZone(layout, name, sentence, x0, x1, z0, z1);
                return;
            }

            // 3) 只有 Z 区间或 X 区间的分区（如「南侧 Z -30..-26 为石砌河堤驳岸」）
            if (!hasFootprint && !point.Success)
            {
                Match zRange = ZRange.Match(sentence);
                Match xRange = XRange.Match(sentence);
                if (!LooksLikeGround(sentence, kind)) return;
                float zx0 = xRange.Success ? Mathf.Min(F(xRange.Groups[1].Value), F(xRange.Groups[2].Value)) : -GroundHalf;
                float zx1 = xRange.Success ? Mathf.Max(F(xRange.Groups[1].Value), F(xRange.Groups[2].Value)) : GroundHalf;
                float zz0 = zRange.Success ? Mathf.Min(F(zRange.Groups[1].Value), F(zRange.Groups[2].Value)) : -GroundHalf;
                float zz1 = zRange.Success ? Mathf.Max(F(zRange.Groups[1].Value), F(zRange.Groups[2].Value)) : GroundHalf;
                AddZone(layout, name, sentence, zx0, zx1, zz0, zz1);
                return;
            }

            // 4) 尺寸精修：N×M 米 / 直径 / 宽 / 高
            float sizeW = 0f, sizeD = 0f;
            bool explicitSize = false;
            Match cross = SizeCross.Match(sentence);
            if (cross.Success)
            {
                sizeW = F(cross.Groups[1].Value);
                sizeD = F(cross.Groups[2].Value);
                explicitSize = true;
            }
            Match radius = SizeRadius.Match(sentence);
            if (radius.Success)
            {
                sizeW = F(radius.Groups[1].Value);
                sizeD = sizeW;
                explicitSize = true;
            }
            Match widthMatch = SizeWidth.Match(sentence);
            if (sizeW <= 0f && widthMatch.Success)
            {
                sizeW = F(widthMatch.Groups[1].Value);
                explicitSize = true;
            }

            if (sizeW > 0f)
            {
                if (hasFootprint)
                {
                    // 已有占地范围时，只在范围明显不合理（<1 米）时才采用文字尺寸
                    if (width < 1f) width = sizeW;
                    if (depth < 1f) depth = sizeD > 0f ? sizeD : sizeW;
                }
                else
                {
                    width = sizeW;
                    depth = sizeD > 0f ? sizeD : sizeW;
                }
            }

            // 没有任何尺寸的建筑：按名称给个体量，别让全场都是 4×4 小方块
            if (kind == "Building" && !explicitSize && width <= 4.5f && depth <= 4.5f)
            {
                float guess = GuessBuildingSize(name, sentence);
                width = guess;
                depth = Mathf.Max(3f, guess * 0.6f);
            }

            width = Mathf.Clamp(width, 0.6f, 70f);
            depth = Mathf.Clamp(depth, 0.6f, 70f);

            // 「街道两侧为三层木筋墙民居」写的是一整排：按 9 米一间拆成多栋，否则会变成一栋巨型方盒子。
            // 深进深的长条（例如 50×20 的南侧民居群）保留成一个带 rowId/rowCount 的聚合体，
            // 交给 StoryLayoutSpecBuilder 先按道路切翼、再按开间拆分；旧版地编仍按一个整块处理（回退）。
            int pendingRowUnits = 0;
            if (hasFootprint && IsRowName(name, sentence))
            {
                float h = heightValue(sentence);
                int split = Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(width, depth) / 9f), 2, 8);
                if (Mathf.Max(width, depth) >= 18f && Mathf.Min(width, depth) <= 14f && sentence.IndexOf("棚", StringComparison.Ordinal) < 0)
                {
                    AddFootprintItems(layout, usedNames, name, sentence, kind, centerX, centerZ, width, depth, h, split);
                    return;
                }
                if (Mathf.Max(width, depth) >= 18f)
                    pendingRowUnits = split;
            }

            Match heightMatch = SizeHeight.Match(sentence);
            float height = heightMatch.Success ? F(heightMatch.Groups[1].Value) : 0f;

            // 5) 去重：空名/纯方位名直接丢；同名相近、或占地高度重合（配件落在主体里）也只留一个
            if (string.IsNullOrEmpty(name)) return;
            if (IsUselessName(name)) return;

            string key = name + "|" + Mathf.RoundToInt(centerX * 10f) + "|" + Mathf.RoundToInt(centerZ * 10f);
            if (!usedNames.Add(key)) return;
            for (int i = 0; i < layout.items.Count; i++)
            {
                LayoutItem existing = layout.items[i];
                if (existing == null) continue;
                if (Vector2.Distance(existing.center, new Vector2(centerX, centerZ)) <= 2.5f && NamesOverlap(existing.name, name))
                    return;
                if (OverlapRatio(existing, centerX, centerZ, width, depth) > 0.85f)
                    return;
            }

            int count = ParseCount(sentence, kind);
            if (count <= 1)
            {
                layout.items.Add(new LayoutItem
                {
                    name = name,
                    raw = TrimTo(sentence, 90),
                    kind = kind,
                    center = new Vector2(centerX, centerZ),
                    width = width,
                    depth = depth,
                    height = height,
                    rotation = 0f,
                    hasFootprint = hasFootprint,
                    count = 1,
                    rowId = pendingRowUnits > 1 ? name : "",
                    rowCount = pendingRowUnits > 1 ? pendingRowUnits : 1,
                });
                return;
            }

            // 一排 N 个：沿长边等分，避免一个 4×4 方块代表七个摊位
            bool alongX = width >= depth;
            float slotW = alongX ? width / count : width;
            float slotD = alongX ? depth : depth / count;
            for (int k = 0; k < count; k++)
            {
                float offset = k - (count - 1) * 0.5f;
                float cx = centerX + (alongX ? offset * slotW : 0f);
                float cz = centerZ + (alongX ? 0f : offset * slotD);
                layout.items.Add(new LayoutItem
                {
                    name = name + "_" + (k + 1).ToString(CultureInfo.InvariantCulture),
                    raw = TrimTo(sentence, 90),
                    kind = kind,
                    center = new Vector2(cx, cz),
                    width = Mathf.Max(1.5f, slotW),
                    depth = Mathf.Max(1.5f, slotD),
                    height = height,
                    rotation = 0f,
                    hasFootprint = hasFootprint,
                    count = count,
                });
            }
        }

        private const float GroundHalf = 46f;

        /// <summary>从任意描述文字里抽一个坐标：优先点对矩形（取中心），其次单个点。</summary>
        public static bool TryFindPoint(string text, out Vector2 point)
        {
            point = Vector2.zero;
            if (string.IsNullOrEmpty(text)) return false;
            string s = Normalize(text);
            Match pair = PointPair.Match(s);
            if (pair.Success)
            {
                float ax = F(pair.Groups[1].Value), az = F(pair.Groups[2].Value);
                float bx = F(pair.Groups[3].Value), bz = F(pair.Groups[4].Value);
                point = new Vector2((ax + bx) * 0.5f, (az + bz) * 0.5f);
                return true;
            }
            Match single = Point.Match(s);
            if (single.Success)
            {
                point = new Vector2(F(single.Groups[1].Value), F(single.Groups[2].Value));
                return true;
            }
            return false;
        }

        /// <summary>多行排布：从「z=3、z=7、z=11」这样的列表生成成排摊位。</summary>
        private static bool AddMultiRowStalls(StoryLayout layout, HashSet<string> usedNames, string name, string sentence)
        {
            var rows = new List<float>();
            foreach (Match m in Regex.Matches(sentence, @"z\s*=\s*(-?\d+(?:\.\d+)?)")) rows.Add(F(m.Groups[1].Value));
            if (rows.Count < 2) return false;

            Match xm = Regex.Match(sentence, @"x\s*=\s*(-?\d+(?:\.\d+)?)");
            float baseX = xm.Success ? F(xm.Groups[1].Value) : 0f;
            string baseName = string.IsNullOrEmpty(name) ? "摊台" : name;

            for (int r = 0; r < rows.Count && r < 6; r++)
            {
                for (int k = 0; k < 3; k++)
                {
                    float cx = baseX + (k - 1) * 4f;
                    string key = baseName + "|" + Mathf.RoundToInt(cx * 10f) + "|" + Mathf.RoundToInt(rows[r] * 10f);
                    if (usedNames != null && !usedNames.Add(key)) continue;
                    layout.items.Add(new LayoutItem
                    {
                        name = baseName + "_r" + (r + 1) + "_" + (k + 1),
                        raw = TrimTo(sentence, 90),
                        kind = "Prop",
                        center = new Vector2(cx, rows[r]),
                        width = 3f, depth = 3f, height = 0f,
                        rotation = 0f, hasFootprint = false, count = 3,
                    });
                }
            }
            return true;
        }

        /// <summary>沿一条已解析的道路补两栋沿街建筑（用于"路两侧是旧货铺与酒馆"这类无坐标描写）。</summary>
        private static void AddAlongRoadItems(StoryLayout layout, HashSet<string> usedNames, string sentence, LayoutPath road)
        {
            if (layout == null || road == null || road.nodes.Count < 2) return;
            Vector2 a = road.nodes[0];
            Vector2 b = road.nodes[road.nodes.Count - 1];
            var dir = b - a;
            if (dir.sqrMagnitude < 1f) return;
            dir.Normalize();
            var normal = new Vector2(-dir.y, dir.x) * (road.width * 0.5f + 4f);

            for (int k = 0; k < 2; k++)
            {
                Vector2 pos = Vector2.Lerp(a, b, 0.32f + k * 0.34f) + normal;
                string key = "沿街|" + Mathf.RoundToInt(pos.x * 10f) + "|" + Mathf.RoundToInt(pos.y * 10f);
                if (usedNames != null && !usedNames.Add(key)) continue;
                layout.items.Add(new LayoutItem
                {
                    name = "沿街店面_" + (k + 1),
                    raw = TrimTo(sentence, 90),
                    kind = "Decoration",
                    center = pos,
                    width = 8f, depth = 6f, height = 0f,
                    rotation = 0f, hasFootprint = false, count = 1,
                });
            }
        }

        private static bool HasBuildingWords(string sentence)
        {
            string[] words = { "占地", "民居", "民宅", "住宅", "房屋", "铺", "酒馆", "屋", "楼", "店", "棚", "厅", "堂", "仓", "厂" };
            for (int i = 0; i < words.Length; i++)
                if (sentence.IndexOf(words[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        private static bool IsRowName(string name, string sentence)
        {
            string[] words = { "成排", "连排", "排屋", "联排", "民居", "临街房屋", "沿街", "一排排" };
            string text = (name ?? "") + " " + (sentence ?? "");
            for (int i = 0; i < words.Length; i++)
                if (text.IndexOf(words[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        private static float heightValue(string sentence)
        {
            Match m = SizeHeight.Match(sentence);
            return m.Success ? F(m.Groups[1].Value) : 0f;
        }

        /// <summary>
        /// 「正立面在 Z=+21、面阔 18 米(X -9…+9)、进深 10 米」这种写法没有 X..Z 矩形，
        /// 但对北侧临街建筑是完整信息：面宽 + 进深 + 正立面所在轴线。
        /// </summary>
        private static bool TryAxisFootprint(
            string sentence, out float x0, out float x1, out float z0, out float z1)
        {
            x0 = x1 = z0 = z1 = 0f;
            Match zAxis = AxisZ.Match(sentence);
            Match xAxis = AxisX.Match(sentence);
            Match front = Frontage.Match(sentence);
            Match depth = DepthSize.Match(sentence);
            if ((!zAxis.Success && !xAxis.Success) || !depth.Success) return false;

            float d = F(depth.Groups[1].Value);
            if (d < 1f) return false;

            if (zAxis.Success)
            {
                float face = F(zAxis.Groups[1].Value);
                Match xr = XRange.Match(sentence);
                if (xr.Success)
                {
                    x0 = F(xr.Groups[1].Value);
                    x1 = F(xr.Groups[2].Value);
                }
                else if (front.Success)
                {
                    float w = F(front.Groups[1].Value);
                    x0 = -w * 0.5f;
                    x1 = w * 0.5f;
                }
                else return false;

                float dir = face >= 0f ? 1f : -1f;
                z0 = face;
                z1 = face + dir * d;
            }
            else
            {
                float face = F(xAxis.Groups[1].Value);
                Match zr = ZRange.Match(sentence);
                if (zr.Success)
                {
                    z0 = F(zr.Groups[1].Value);
                    z1 = F(zr.Groups[2].Value);
                }
                else if (front.Success)
                {
                    float w = F(front.Groups[1].Value);
                    z0 = -w * 0.5f;
                    z1 = w * 0.5f;
                }
                else return false;

                float dir = face >= 0f ? 1f : -1f;
                x0 = face;
                x1 = face + dir * d;
            }

            if (x0 > x1) { float t = x0; x0 = x1; x1 = t; }
            if (z0 > z1) { float t = z0; z0 = z1; z1 = t; }
            return Mathf.Abs(x1 - x0) >= 1f && Mathf.Abs(z1 - z0) >= 1f;
        }

        /// <summary>
        /// 「广场正中 (0,0) 立着新竖起来的断头台」这类句子，名字在坐标之后。
        /// 只靠前置名字会被剥成「广场」，最终整座断头台消失。
        /// </summary>
        private static string ExtractNameAfterPoint(string sentence, string fallback)
        {
            MatchCollection points = Point.Matches(sentence);
            if (points.Count == 0) return fallback;
            Match last = points[points.Count - 1];
            int end = last.Index + last.Length;
            if (end >= sentence.Length) return fallback;

            string tail = sentence.Substring(end).Trim(' ', ',', '，', ':', '；', ';', '、');
            string[] leading = { "立着", "矗立着", "放着", "堆着", "挂着", "新建的", "新竖起来的", "旧", "一座", "一个", "两座", "两个", "是", "为", "有", "在" };
            bool changed = true;
            while (changed && tail.Length > 1)
            {
                changed = false;
                for (int i = 0; i < leading.Length; i++)
                {
                    if (tail.StartsWith(leading[i], StringComparison.Ordinal))
                    {
                        tail = tail.Substring(leading[i].Length).Trim(' ', ',', '，', ':', '；', ';', '、');
                        changed = true;
                    }
                }
            }
            char[] stops = { ',', '，', ';', '；', '。' };
            int cut = tail.IndexOfAny(stops);
            if (cut > 0) tail = tail.Substring(0, cut);
            string cleaned = CleanName(tail);
            return string.IsNullOrEmpty(cleaned) ? fallback : cleaned;
        }

        /// <summary>把一个占地范围落成 1 个或多个地标（成排/连排时沿长边等分）。</summary>
        private static void AddFootprintItems(
            StoryLayout layout, HashSet<string> usedNames, string name, string sentence,
            string kind, float centerX, float centerZ, float width, float depth, float height, int split = 1)
        {
            if (layout == null) return;
            if (usedNames != null)
            {
                string key = name + "|" + Mathf.RoundToInt(centerX * 10f) + "|" + Mathf.RoundToInt(centerZ * 10f);
                if (!usedNames.Add(key)) return;
            }
            width = Mathf.Clamp(width, 0.6f, 70f);
            depth = Mathf.Clamp(depth, 0.6f, 70f);
            split = Mathf.Clamp(split, 1, 8);

            if (split <= 1)
            {
                layout.items.Add(new LayoutItem
                {
                    name = name, raw = TrimTo(sentence, 90), kind = kind,
                    center = new Vector2(centerX, centerZ),
                    width = width, depth = depth, height = height,
                    rotation = 0f, hasFootprint = true, count = 1,
                });
                return;
            }

            bool alongX = width >= depth;
            float slotW = alongX ? width / split : width;
            float slotD = alongX ? depth : depth / split;
            for (int k = 0; k < split; k++)
            {
                float offset = k - (split - 1) * 0.5f;
                layout.items.Add(new LayoutItem
                {
                    name = name + "_" + (k + 1).ToString(CultureInfo.InvariantCulture),
                    raw = TrimTo(sentence, 90), kind = kind,
                    center = new Vector2(centerX + (alongX ? offset * slotW : 0f),
                                         centerZ + (alongX ? 0f : offset * slotD)),
                    width = Mathf.Max(2f, slotW), depth = Mathf.Max(2f, slotD),
                    // width/depth 已是轴对齐尺寸，rotation 再转 90° 会让灰盒/搭建交换宽深并造成重叠。
                    height = height, rotation = 0f, hasFootprint = true, count = split,
                    rowId = name, rowIndex = k, rowCount = split,
                });
            }
        }

        private static void AddZone(StoryLayout layout, string name, string sentence, float x0, float x1, float z0, float z1)
        {
            // 「广场中心区域(0,-12)至(0,12)」这种是宽带描述，宽为 0 时给 20 米宽度才有意义
            if (Mathf.Abs(x1 - x0) < 2f)
            {
                float cx = (x0 + x1) * 0.5f;
                x0 = cx - 10f;
                x1 = cx + 10f;
            }
            if (Mathf.Abs(z1 - z0) < 2f)
            {
                float cz = (z0 + z1) * 0.5f;
                z0 = cz - 10f;
                z1 = cz + 10f;
            }
            layout.zones.Add(new LayoutZone
            {
                name = string.IsNullOrEmpty(name) ? "地面" : name,
                kind = ZoneKind(sentence),
                minX = Mathf.Min(x0, x1),
                maxX = Mathf.Max(x0, x1),
                minZ = Mathf.Min(z0, z1),
                maxZ = Mathf.Max(z0, z1),
            });
        }

        private static bool IsUselessName(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            string[] junk = { "出口", "边界", "坐标", "场地边界", "南侧边界", "北侧边界", "西侧出口" };
            for (int i = 0; i < junk.Length; i++)
                if (name.IndexOf(junk[i], StringComparison.Ordinal) >= 0) return true;
            for (int i = 0; i < UselessNames.Length; i++)
                if (string.Equals(name, UselessNames[i], StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>占地重合比例（交集 ÷ 较小者面积），用来发现"钟楼落在市政厅里"这类重复条目。</summary>
        private static float OverlapRatio(LayoutItem existing, float cx, float cz, float width, float depth)
        {
            if (existing == null) return 0f;
            float ax0 = existing.center.x - existing.width * 0.5f;
            float ax1 = existing.center.x + existing.width * 0.5f;
            float az0 = existing.center.y - existing.depth * 0.5f;
            float az1 = existing.center.y + existing.depth * 0.5f;
            float bx0 = cx - width * 0.5f;
            float bx1 = cx + width * 0.5f;
            float bz0 = cz - depth * 0.5f;
            float bz1 = cz + depth * 0.5f;

            float ix = Mathf.Min(ax1, bx1) - Mathf.Max(ax0, bx0);
            float iz = Mathf.Min(az1, bz1) - Mathf.Max(az0, bz0);
            if (ix <= 0f || iz <= 0f) return 0f;
            float inter = ix * iz;
            float areaA = Mathf.Max(0.01f, (ax1 - ax0) * (az1 - az0));
            float areaB = Mathf.Max(0.01f, width * depth);
            return inter / Mathf.Min(areaA, areaB);
        }

        private static bool NamesOverlap(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            if (a == b) return true;
            string shortName = a.Length <= b.Length ? a : b;
            string longName = a.Length <= b.Length ? b : a;
            if (shortName.Length < 2) return false;
            return longName.IndexOf(shortName, StringComparison.Ordinal) >= 0;
        }
        private static LayoutPath ParsePath(string sentence)
        {
            var path = new LayoutPath();
            MatchCollection pts = Point.Matches(sentence);
            if (pts.Count < 2) return null;
            for (int i = 0; i < pts.Count; i++)
                path.nodes.Add(new Vector2(F(pts[i].Groups[1].Value), F(pts[i].Groups[2].Value)));
            Match width = SizeWidth.Match(sentence);
            path.width = width.Success ? Mathf.Clamp(F(width.Groups[1].Value), 1.5f, 30f) : 6f;
            Match quote = QuoteName.Match(sentence);
            if (quote.Success) path.name = quote.Groups[1].Value;
            if (string.IsNullOrEmpty(path.name))
            {
                string stripped = StripPrefix(sentence);
                path.name = TrimTo(stripped, 10);
            }
            return path;
        }

        private static bool IsStreet(string sentence)
        {
            Match width = SizeWidth.Match(sentence);
            if (!width.Success) return false;
            // 「地面：广场中心区域…铺鹅卵石，其余为夯土路面」这类是地面铺装，不是道路
            if (sentence.IndexOf("铺地", StringComparison.Ordinal) >= 0
                || sentence.IndexOf("夯实", StringComparison.Ordinal) >= 0
                || sentence.IndexOf("夯土", StringComparison.Ordinal) >= 0
                || sentence.IndexOf("鹅卵石", StringComparison.Ordinal) >= 0) return false;
            for (int i = 0; i < ZoneRoadWords.Length; i++)
                if (sentence.IndexOf(ZoneRoadWords[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        /// <summary>有占地矩形时，判断这句写的是建筑还是地面。</summary>
        private static bool IsStructure(string sentence, string kind)
        {
            if (kind == "Building" || kind == "Prop") return true;
            for (int i = 0; i < BuildingWords.Length; i++)
                if (sentence.IndexOf(BuildingWords[i], StringComparison.Ordinal) >= 0) return true;
            for (int i = 0; i < PropWords.Length; i++)
                if (sentence.IndexOf(PropWords[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        private static string ClassifyKind(string text)
        {
            if (string.IsNullOrEmpty(text)) return "Decoration";

            // 「门前 5 级石阶」不是一栋楼，别让 GuessBuildingSize 把它猜成 8×4.8 的房子。
            bool isStep = text.IndexOf("台阶", StringComparison.Ordinal) >= 0
                          || text.IndexOf("石阶", StringComparison.Ordinal) >= 0;
            if (isStep)
            {
                string[] realBuilding = { "楼", "厅", "堂", "塔", "寺", "院", "殿", "府", "署", "仓", "厂", "店", "铺", "民居", "民宅", "住宅", "城墙", "城门", "门楼" };
                bool hasReal = false;
                for (int i = 0; i < realBuilding.Length; i++)
                    if (text.IndexOf(realBuilding[i], StringComparison.Ordinal) >= 0) { hasReal = true; break; }
                if (!hasReal) return "Prop";
            }

            for (int i = 0; i < BuildingWords.Length; i++)
                if (text.IndexOf(BuildingWords[i], StringComparison.Ordinal) >= 0) return "Building";
            for (int i = 0; i < PropWords.Length; i++)
                if (text.IndexOf(PropWords[i], StringComparison.Ordinal) >= 0) return "Prop";
            return "Decoration";
        }

        private static string ZoneKind(string sentence)
        {
            for (int i = 0; i < ZoneWaterWords.Length; i++)
                if (sentence.IndexOf(ZoneWaterWords[i], StringComparison.Ordinal) >= 0) return "water";
            for (int i = 0; i < ZoneFieldWords.Length; i++)
                if (sentence.IndexOf(ZoneFieldWords[i], StringComparison.Ordinal) >= 0) return "field";
            return "square";
        }

        private static float GuessBuildingSize(string name, string sentence)
        {
            string text = (name ?? "") + " " + (sentence ?? "");
            if (text.IndexOf("市政厅", StringComparison.Ordinal) >= 0 || text.IndexOf("议会", StringComparison.Ordinal) >= 0) return 20f;
            if (text.IndexOf("教堂", StringComparison.Ordinal) >= 0 || text.IndexOf("修道院", StringComparison.Ordinal) >= 0) return 14f;
            if (text.IndexOf("钟楼", StringComparison.Ordinal) >= 0 || text.IndexOf("塔", StringComparison.Ordinal) >= 0) return 6f;
            if (text.IndexOf("城门", StringComparison.Ordinal) >= 0 || text.IndexOf("门楼", StringComparison.Ordinal) >= 0) return 10f;
            if (text.IndexOf("店铺", StringComparison.Ordinal) >= 0 || text.IndexOf("民居", StringComparison.Ordinal) >= 0) return 8f;
            return 8f;
        }

        /// <summary>
        /// 只有"一排 N 个摊位/棚/井/车/灯/柱"这种明确排布才拆分。
        /// 之前把"一排十二个摊位"拆成 12 条 1.5 米窄条，也和别的地标大量重叠，反而更乱。
        /// </summary>
        private static int ParseCount(string sentence, string kind)
        {
            if (kind == "Building") return 1;
            Match m = CountPattern.Match(sentence);
            if (!m.Success) return 1;
            int value = ChineseNumber(m.Groups[1].Value);
            if (value < 2 || value > 7) return 1;

            int marker = m.Index + m.Length;
            string tail = marker < sentence.Length ? sentence.Substring(marker) : "";
            string head = m.Index > 0 ? sentence.Substring(Mathf.Max(0, m.Index - 2), Mathf.Min(2, m.Index)) : "";
            string unit = (head + tail).Trim();
            string[] allowed = { "摊", "棚", "井", "车", "灯", "柱", "椅", "桶" };
            for (int i = 0; i < allowed.Length; i++)
                if (unit.IndexOf(allowed[i], StringComparison.Ordinal) >= 0) return value;
            return 1;
        }

        private static int ChineseNumber(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int value;
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return value;
            const string digits = "零一二三四五六七八九";
            if (text.Length == 1)
            {
                int idx = digits.IndexOf(text[0]);
                if (idx >= 0) return idx;
                if (text[0] == '两') return 2;
                return 0;
            }
            if (text.Length == 2 && text[1] == '十')
            {
                int tens = text[0] == '两' ? 2 : Mathf.Max(0, digits.IndexOf(text[0]));
                return tens * 10;
            }
            if (text.Length == 2 && text[0] == '十')
            {
                int ones = digits.IndexOf(text[1]);
                return 10 + Mathf.Max(0, ones);
            }
            if (text.Length == 3 && text[1] == '十')
            {
                int tens = text[0] == '两' ? 2 : Mathf.Max(0, digits.IndexOf(text[0]));
                int ones = Mathf.Max(0, digits.IndexOf(text[2]));
                return tens * 10 + ones;
            }
            return 0;
        }

        // ─────────────────────────── 文本工具 ───────────────────────────

        /// <summary>统一标点：全角括号/逗号、省略号、波浪号都归一成解析器认的字符。</summary>
        private static string Normalize(string text)
        {
            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                switch (c)
                {
                    case '（': sb.Append('('); break;
                    case '）': sb.Append(')'); break;
                    case '，': sb.Append(','); break;
                    case '、': sb.Append(','); break;
                    case '：': sb.Append(':'); break;
                    case '；': sb.Append('\n'); break;
                    case '。': sb.Append('\n'); break;
                    case '～':
                    case '~':
                    case '－':
                        sb.Append("..");
                        break;
                    case '…':
                        sb.Append("..");
                        break;
                    case '\r':
                        break;
                    case '　':
                        sb.Append(' ');
                        break;
                    case '＋':
                        sb.Append('+');
                        break;
                    case '−':
                        sb.Append('-');
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        private static readonly string[] NoiseMarkers =
        {
            "玩家出生点", "玩家从", "出生点", "人物落位", "面朝场景中心", "十字相交", "相交", "四周由", "本幕焦点",
        };

        private static readonly string[] GroundPhrases =
        {
            "铺地", "地面", "河堤", "驳岸", "水面", "草地", "田地", "泥土地", "沙滩", "下沉",
        };

        private const string StructuralChars = "楼堂厅塔门井车棚摊墙柱街巷场桥屋房店仓府寺院坝堤";

        /// <summary>噪声句：出生点、人物落位、路口相交、边界描述——这些不是可摆放的地标。</summary>
        private static bool IsNoise(string sentence)
        {
            // 「南侧两栋民宅……中间缺口正对出生点 (0,-24)」同时包含建筑和出生点，
            // 不能因为出现「出生点」就把整排建筑丢掉。
            bool hasBuilding = HasBuildingWords(sentence);
            bool hasFootprint = Footprint.IsMatch(sentence)
                                || PointPair.Matches(sentence).Count > 0
                                || (AxisZ.IsMatch(sentence) && (Frontage.IsMatch(sentence) || DepthSize.IsMatch(sentence)))
                                || (AxisX.IsMatch(sentence) && (Frontage.IsMatch(sentence) || DepthSize.IsMatch(sentence)));
            if (hasBuilding && hasFootprint) return false;

            for (int i = 0; i < NoiseMarkers.Length; i++)
                if (sentence.IndexOf(NoiseMarkers[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        /// <summary>这句是在描述地面（广场铺装、河堤、草地）还是描述一个实体。</summary>
        private static bool LooksLikeGround(string sentence, string kind)
        {
            // 「【地面】中央石板广场：……石板铺地」里的“石板”会先命中 PropWords，
            // 但带【地面】标签的句子仍然是地面分区，不能变成 60×60 的“石板道具”。
            bool taggedGround = sentence.IndexOf("【地面】", StringComparison.Ordinal) >= 0;
            if (kind == "Prop" && !taggedGround) return false;
            string[] notGround = { "官署", "税署", "台阶", "喷泉", "水槽", "建筑", "小楼", "民居", "酒馆", "店铺", "棚" };
            for (int i = 0; i < notGround.Length; i++)
                if (sentence.IndexOf(notGround[i], StringComparison.Ordinal) >= 0) return false;
            for (int i = 0; i < GroundPhrases.Length; i++)
                if (sentence.IndexOf(GroundPhrases[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        /// <summary>去掉【标签】、截到最后一个结构词、去掉尾部虚词，得到干净的对象名。</summary>
        private static string CleanName(string text)
        {
            string s = text ?? "";
            s = Regex.Replace(s, @"【[^】]{0,14}】", "");
            s = Regex.Replace(s, @"\[[^\]]{0,14}\]", "");
            s = s.Trim(' ', '\u3000', '，', ',', ':', '·', '"', '“', '”', '、');

            // 「市政厅在广场北侧」→ 市政厅；「圣安托万门位于东边界」→ 圣安托万门
            int preposition = -1;
            for (int i = 2; i < s.Length - 1; i++)
            {
                if (s[i] != '\u5728' && s[i] != '\u4f4d') continue;
                if (s[i] == '\u4f4d' && (i + 1 >= s.Length || s[i + 1] != '\u4e8e')) continue;
                preposition = i;
                break;
            }
            if (preposition >= 2) s = s.Substring(0, preposition);

            int last = -1;
            for (int i = 0; i < s.Length && i < 24; i++)
                if (StructuralChars.IndexOf(s[i]) >= 0) last = i;
            if (last >= 1 && last < s.Length - 1) s = s.Substring(0, last + 1);

            s = TrimTrailing(s, new[] { "在", "由", "从", "的", "与", "和", "沿", "向", "朝", "并", "立着", "停放", "位于", "是" });
            s = s.Trim(' ', '\u3000', '，', ',', ':', '·');
            return TrimTo(s, 12);
        }

        private static string TrimTrailing(string text, string[] suffixes)
        {
            string s = text ?? "";
            bool changed = true;
            while (changed && s.Length > 1)
            {
                changed = false;
                for (int i = 0; i < suffixes.Length; i++)
                {
                    if (s.Length > suffixes[i].Length && s.EndsWith(suffixes[i], StringComparison.Ordinal))
                    {
                        s = s.Substring(0, s.Length - suffixes[i].Length);
                        changed = true;
                    }
                }
            }
            return s;
        }

        private static string ExtractName(string sentence)
        {
            string s = sentence;
            int colon = s.IndexOf(':');
            if (colon > 1 && colon <= 10) s = s.Substring(0, colon);

            int cut = -1;
            for (int i = 0; i < NameMarkers.Length; i++)
            {
                int idx = s.IndexOf(NameMarkers[i], StringComparison.Ordinal);
                if (idx > 0 && (cut < 0 || idx < cut)) cut = idx;
            }
            if (cut > 0) s = s.Substring(0, cut);
            s = s.Trim().Trim('"', '“', '”', '·', ' ');

            for (int i = 0; i < LeadingNoise.Length; i++)
            {
                if (s.StartsWith(LeadingNoise[i], StringComparison.Ordinal))
                {
                    s = s.Substring(LeadingNoise[i].Length);
                    i = -1;
                }
            }
            return CleanName(s);
        }

        private static string StripPrefix(string sentence)
        {
            string s = sentence;
            int colon = s.IndexOf(':');
            if (colon > 1 && colon <= 10) s = s.Substring(0, colon);
            return s.Trim();
        }

        private static string TrimTo(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string trimmed = text.Trim();
            return trimmed.Length <= max ? trimmed : trimmed.Substring(0, max);
        }

        private static float F(string text)
        {
            float value;
            if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return value;
            return 0f;
        }
    }
}
