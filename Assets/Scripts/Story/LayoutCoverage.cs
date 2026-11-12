// 剧情 → 灰盒 的「覆盖审计 + 补全」。
//
// 为什么需要它：
//   剧情&核心 Agent 的几何信息只写在自然语言 layoutDescription 里，灰盒是用正则去"猜"的
//   （StoryLayoutParser → StoryLayoutSpecBuilder → GrayboxSpecExecutor）。正则只认少数写法，
//   猜不到的写法要么被丢（无坐标的地标），要么被降级（4×4 默认体量、Prop 默认尺寸），
//   而 GrayboxSpecCompliance 校验的是「LayoutSpec ↔ 槽位」——Spec 本身就是正则的产物，
//   于是丢失发生在校验上游，校验永远报 pass（实测 08:57 记录：喷泉整条消失、地面 4 分区只剩 2、
//   钟楼没有、边界墙 0 条，但日志写"合规通过 · 规格项 20 · 槽位 20"）。
//
// 这一层的职责（不改变原有链路，只做第二遍独立核对）：
//   1) 独立于 StoryLayoutParser 再读一遍散文，抽出「剧情点名的实体 + 它给的几何」；
//   2) 和 LayoutSpec.items / GrayboxPlan.regions 对账：
//        - 收得进来的 → covered
//        - 没进来但几何可推 → 补进 Spec（source=coverage）/ 补进 plan.regions
//        - 没进来且无法定位 → 进 unresolved 报告（绝不静默）
//   3) 汇总成一行摘要，写进 06 记录与日志，让"合规通过"不再可能掩盖漏项。
//
// 回退：环境变量 EPICSPARK_COVERAGE=0，或 LayoutCoverage.Enabled = false。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using EpicSpark.Core;
using UnityEngine;

namespace EpicSpark.Story
{
    public static class LayoutCoverage
    {
        public static bool Enabled = ResolveEnabled();

        private static bool ResolveEnabled()
        {
            string env = Environment.GetEnvironmentVariable("EPICSPARK_COVERAGE");
            if (!string.IsNullOrEmpty(env))
                return !(env == "0" || string.Equals(env, "false", StringComparison.OrdinalIgnoreCase));
            return true;
        }

        /// <summary>最近一次审计摘要（给 06 调试记录/HUD 用）。</summary>
        public static string LastSummary = "";

        /// <summary>最近一次审计明细（给探针/日志用）。</summary>
        public static string LastDetail = "";

        // ─────────────────────────── 数据结构 ───────────────────────────

        public class Entity
        {
            public string name = "";
            public string clause = "";
            public bool isZone;
            public bool isPath;
            public bool isBorder;
            /// <summary>挂在某个主体上的子结构（如门楣上方的钟楼），本体没有独立坐标。</summary>
            public bool isSub;
            public string parentName = "";
            public bool hasRect;
            public float minX, maxX, minZ, maxZ;
            public bool hasPoint;
            public Vector2 point;
            public bool sizeFromText;
            public float sizeX, sizeZ;
            public string kindGuess = "Prop";
        }

        public class Report
        {
            public List<Entity> entities = new List<Entity>();
            public List<string> addedItems = new List<string>();
            public List<string> addedZones = new List<string>();
            public List<string> unresolved = new List<string>();
            public List<string> approximate = new List<string>();
            public int covered;

            public bool HasFindings
            {
                get { return addedItems.Count + addedZones.Count + unresolved.Count > 0; }
            }

            public string Compact()
            {
                return "覆盖 " + covered + "/" + entities.Count
                       + " · 补项 " + addedItems.Count
                       + " · 补区 " + addedZones.Count
                       + " · 未定位 " + unresolved.Count
                       + (approximate.Count > 0 ? " · 近似 " + approximate.Count : "");
            }

            public string Detail()
            {
                var sb = new StringBuilder();
                sb.AppendLine("覆盖审计：" + Compact());
                if (addedItems.Count > 0)
                {
                    sb.AppendLine("  [补进施工图]");
                    for (int i = 0; i < addedItems.Count; i++) sb.AppendLine("    + " + addedItems[i]);
                }
                if (addedZones.Count > 0)
                {
                    sb.AppendLine("  [补进灰盒分区]");
                    for (int i = 0; i < addedZones.Count; i++) sb.AppendLine("    + " + addedZones[i]);
                }
                if (unresolved.Count > 0)
                {
                    sb.AppendLine("  [剧情写了但无法定位——需要剧情补坐标或人工确认]");
                    for (int i = 0; i < unresolved.Count; i++) sb.AppendLine("    ! " + unresolved[i]);
                }
                if (approximate.Count > 0)
                {
                    sb.AppendLine("  [近似]");
                    for (int i = 0; i < approximate.Count; i++) sb.AppendLine("    ~ " + approximate[i]);
                }
                return sb.ToString();
            }
        }

        // ─────────────────────────── 对外入口 ───────────────────────────

        /// <summary>
        /// 把剧情里点名、但施工图没收到的东西补进 LayoutSpec。返回审计报告。
        /// repair=false 时只审计不改写：施工图由剧情 Agent 直给时，补丁等于把模型的输出改掉，
        /// 灰盒又会「听话地」执行一份我们自己编的图——这正是要根治的问题之一。
        /// </summary>
        public static Report Apply(SceneSpec scene, LayoutSpec spec, GrayboxPlan plan, bool repair = true)
        {
            var report = new Report();
            LastSummary = "";
            LastDetail = "";
            if (!Enabled || scene == null || spec == null) return report;

            report.entities = Extract(scene.layoutDescription);
            if (report.entities.Count == 0) return report;

            for (int i = 0; i < report.entities.Count; i++)
            {
                Entity e = report.entities[i];
                if (e == null || string.IsNullOrEmpty(e.name)) continue;

                if (e.isSub)
                {
                    if (FindItem(spec, e.name) != null) { report.covered++; continue; }   // 幂等：重复审计不再补
                    LayoutItemSpec parent = FindItem(spec, e.parentName);
                    if (parent != null && parent.size.x >= 1f)
                    {
                        if (!repair)
                        {
                            report.unresolved.Add("「" + e.name + "」（挂在「" + parent.name + "」上，施工图未列此项）");
                            continue;
                        }
                        float w = Mathf.Clamp(e.sizeX <= 0f ? 2f : e.sizeX, 1f, Mathf.Max(1f, parent.size.x));
                        float d = Mathf.Clamp(e.sizeZ <= 0f ? 2f : e.sizeZ, 1f, Mathf.Max(1f, parent.size.y));
                        spec.items.Add(new LayoutItemSpec
                        {
                            id = "coverage_" + SafeId(e.name),
                            name = e.name,
                            semantic = Truncate(e.clause, 60),
                            kind = e.kindGuess == "Building" ? SlotKind.Building : SlotKind.Prop,
                            center = parent.center,
                            size = new Vector2(w, d),
                            required = true,
                            source = "coverage",
                            notes = "挂在「" + parent.name + "」上（无独立坐标，按主体中心近似）",
                        });
                        report.approximate.Add(e.name + " 无独立坐标 → 挂到「" + parent.name + "」中心近似放置");
                        report.covered++;
                    }
                    else
                    {
                        report.unresolved.Add("「" + e.name + "」（挂在「" + e.parentName + "」上，但主体没落图）");
                    }
                    continue;
                }

                if (e.isBorder)
                {
                    if (BorderCovered(spec, plan)) report.covered++;
                    else report.unresolved.Add("边界/围合「" + Truncate(e.name, 18) + "」（没有任何墙线规格）");
                    continue;
                }

                if (e.isPath)
                {
                    if (PathCovered(e, spec, plan))
                    {
                        report.covered++;
                        continue;
                    }
                    // 道路没落图 = 剧情给了走法但灰盒没有通路，必须报出来
                    report.unresolved.Add("通路「" + e.name + "」（施工图里找不到对应路径）");
                    continue;
                }

                if (e.isZone)
                {
                    LayoutZoneSpec hit = FindZoneSpec(spec, e);
                    if (hit != null)
                    {
                        ReconcileZoneKind(hit, e, report, repair);
                        if (plan != null) MirrorZone(plan, hit);
                        report.covered++;
                        continue;
                    }
                    if (e.hasRect && repair)
                    {
                        LayoutZoneSpec zone = AddZoneSpec(spec, e);
                        if (plan != null && zone != null) MirrorZone(plan, zone);
                        report.addedZones.Add(e.name + " · " + RectText(e));
                        report.covered++;
                    }
                    else if (e.hasRect)
                    {
                        report.unresolved.Add("地面分区「" + e.name + "」（施工图未列此分区）");
                    }
                    else
                    {
                        report.unresolved.Add("地面分区「" + e.name + "」（没有可用的范围）");
                    }
                    continue;
                }

                if (ItemCovered(e, spec))
                {
                    report.covered++;
                    ReconcileSize(e, spec, report, repair);
                    continue;
                }

                if ((e.hasRect || e.hasPoint) && repair)
                {
                    LayoutItemSpec item = AddItem(spec, e);
                    report.addedItems.Add(item.name + " · " + RectText(e)
                                          + (e.sizeFromText ? "" : "（尺寸按类别估计）"));
                    report.covered++;
                }
                else if (e.hasRect || e.hasPoint)
                {
                    report.unresolved.Add("「" + e.name + "」（剧情写了，施工图未列此项）");
                }
                else
                {
                    report.unresolved.Add("「" + e.name + "」（剧情只说位置关系，没有坐标）");
                }
            }

            LastSummary = report.Compact();
            LastDetail = report.Detail();
            // 缺口写进施工图本身：灰盒、合规、调试记录都读同一份，不再依赖静态字段。
            if (spec.gaps == null) spec.gaps = new List<string>();
            spec.gaps.Clear();
            for (int i = 0; i < report.unresolved.Count; i++) spec.gaps.Add(report.unresolved[i]);
            if (report.HasFindings)
                SafeLog("[灰盒覆盖] " + report.Detail());
            return report;
        }

        // ─────────────────────────── 散文抽取 ───────────────────────────

        private static readonly Regex TagRegex = new Regex(@"【[^】]{0,8}】", RegexOptions.Compiled);
        private static readonly Regex PairRegex = new Regex(
            @"\(\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*\)\s*[—\-–~至到]+\s*\(\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*\)",
            RegexOptions.Compiled);
        private static readonly Regex PointRegex = new Regex(
            @"\(\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*\)", RegexOptions.Compiled);
        private static readonly Regex DiameterRegex = new Regex(@"直径\s*(\d+(?:\.\d+)?)\s*米", RegexOptions.Compiled);
        private static readonly Regex CrossRegex = new Regex(
            @"(\d+(?:\.\d+)?)\s*[×xX*]\s*(\d+(?:\.\d+)?)\s*米", RegexOptions.Compiled);
        private static readonly Regex WidthRegex = new Regex(@"(?<!直径)宽\s*(\d+(?:\.\d+)?)\s*米", RegexOptions.Compiled);
        private static readonly Regex HeightRegex = new Regex(@"高\s*(\d+(?:\.\d+)?)\s*米", RegexOptions.Compiled);
        private static readonly Regex SubRegex = new Regex(
            @"([\u4e00-\u9fa5]{0,6}(?:钟楼|塔楼|楼阁|亭子|水井|钟架|门楼))[，、]?\s*底边\s*(\d+(?:\.\d+)?)\s*米",
            RegexOptions.Compiled);

        private static readonly string[] ZoneWords =
        {
            "地面", "铺地", "铺石", "石板", "夯土", "碎石", "泥土", "泥地", "草地", "水面", "水域", "河滩", "场地"
        };

        private static readonly string[] BuildingWords =
        {
            "教堂", "官署", "市政厅", "民居", "房屋", "楼房", "塔楼", "钟楼", "城门", "门楼", "城墙", "院墙", "要塞"
        };

        /// <summary>独立解析：按标点切子句，逐句抽「名字 + 几何」。不依赖 StoryLayoutParser。</summary>
        public static List<Entity> Extract(string text)
        {
            var list = new List<Entity>();
            if (string.IsNullOrEmpty(text)) return list;

            string[] lines = text.Replace("\r", "").Split('\n');
            string pendingTag = "";
            var seen = new HashSet<string>(StringComparer.Ordinal);

            for (int li = 0; li < lines.Length; li++)
            {
                string line = lines[li];
                if (string.IsNullOrEmpty(line.Trim())) continue;

                string[] clauses = line.Split(new[] { '；', ';', '。' }, StringSplitOptions.RemoveEmptyEntries);
                for (int ci = 0; ci < clauses.Length; ci++)
                {
                    string raw = clauses[ci];
                    // 段首的【标签】对后面同一行的子句继续生效（"【地面】…；广场南侧为…"）
                    Match tag = TagRegex.Match(raw);
                    string clauseTag = pendingTag;
                    if (tag.Success)
                    {
                        clauseTag = tag.Value;
                        pendingTag = clauseTag;
                    }

                    string clause = TagRegex.Replace(raw, "").Trim();
                    if (clause.Length < 3) continue;

                    List<Entity> found = ParseClause(clause, clauseTag);
                    for (int i = 0; i < found.Count; i++)
                    {
                        if (found[i] == null) continue;
                        string key = found[i].name + "|" + Mathf.RoundToInt(found[i].minX) + "|"
                                     + Mathf.RoundToInt(found[i].minZ) + "|" + Mathf.RoundToInt(found[i].maxX) + "|"
                                     + Mathf.RoundToInt(found[i].maxZ) + "|" + found[i].isZone;
                        if (!seen.Add(key)) continue;
                        list.Add(found[i]);
                    }
                }
                pendingTag = "";
            }
            return list;
        }

        private static List<Entity> ParseClause(string clause, string tag)
        {
            var list = new List<Entity>();
            bool border = IsBorder(clause, out bool hasGeometry);
            string name = ExtractName(clause, out string rest);
            if (string.IsNullOrEmpty(name)) return list;

            // "北侧 (0,96) 是圣安东门……"：北侧只是方位，不是实体。方位词后面接"是/有/为"的
            // 才是有名字的东西；纯方位句（"四周由院墙围合"）交给边界判定，不当成漏项。
            if (IsDirectionalName(name) && !border)
            {
                string refined = RefineDirectionalName(rest);
                if (string.IsNullOrEmpty(refined)) return list;
                name = refined;
            }

            bool zone = false;
            bool path = false;
            if (!border)
            {
                // 标签优先：【道路】的行就是通路，【地面】的行就是分区；没有标签才靠关键词猜。
                if (tag.IndexOf("道路", StringComparison.Ordinal) >= 0 || tag.IndexOf("街", StringComparison.Ordinal) >= 0)
                    path = true;
                else if (tag.IndexOf("地面", StringComparison.Ordinal) >= 0)
                    zone = true;
                else if (clause.IndexOf("道路", StringComparison.Ordinal) >= 0
                         || clause.IndexOf("路面", StringComparison.Ordinal) >= 0
                         || clause.IndexOf("车行", StringComparison.Ordinal) >= 0
                         || clause.IndexOf("大街", StringComparison.Ordinal) >= 0)
                    path = true;
                else if (IsZone(tag, clause))
                    zone = true;
                else if (IsPath(clause))
                    path = true;
            }

            MatchCollection pairs = PairRegex.Matches(clause);
            if (pairs.Count > 0)
            {
                for (int i = 0; i < pairs.Count; i++)
                {
                    Match m = pairs[i];
                    var e = new Entity
                    {
                        name = name,
                        clause = clause,
                        isZone = zone,
                        isPath = path,
                        isBorder = border,
                        hasRect = true,
                        minX = F(m.Groups[1].Value),
                        minZ = F(m.Groups[2].Value),
                        maxX = F(m.Groups[3].Value),
                        maxZ = F(m.Groups[4].Value),
                        kindGuess = KindText(zone, path, border, name),
                    };
                    NormalizeRect(e);
                    ApplyTextSize(e, clause);
                    list.Add(e);
                }
            }
            else if (border)
            {
                list.Add(new Entity
                {
                    name = name,
                    clause = clause,
                    isBorder = true,
                    hasRect = false,
                    kindGuess = "Border",
                });
            }
            else
            {
                Match point = PointRegex.Match(clause);
                if (point.Success)
                {
                    var e = new Entity
                    {
                        name = name,
                        clause = clause,
                        isZone = zone,
                        isPath = path,
                        hasPoint = true,
                        point = new Vector2(F(point.Groups[1].Value), F(point.Groups[2].Value)),
                        kindGuess = KindText(zone, path, false, name),
                    };
                    ApplyTextSize(e, clause);
                    list.Add(e);
                }
                else
                {
                    // 有名字、有尺寸但没坐标 → 仍然登记，交给 unresolved
                    Match cross = CrossRegex.Match(clause);
                    Match diameter = DiameterRegex.Match(clause);
                    Match widthM = WidthRegex.Match(clause);
                    Match heightM = HeightRegex.Match(clause);
                    if (cross.Success || diameter.Success || widthM.Success || heightM.Success)
                    {
                        list.Add(new Entity
                        {
                            name = name,
                            clause = clause,
                            isZone = zone,
                            isPath = path,
                            hasRect = false,
                            hasPoint = false,
                            kindGuess = KindText(zone, path, false, name),
                        });
                    }
                }
            }

            AddSubStructures(clause, name, list);
            return list;
        }

        /// <summary>
        /// 「门楣上方有石砌钟楼，底边3米」这类子结构：主体有点位、子结构只给了尺寸。
        /// StoryLayoutParser 会整句丢掉，这里单独登记，后续挂到主体中心并明确标注是近似。
        /// </summary>
        private static void AddSubStructures(string clause, string parentName, List<Entity> list)
        {
            MatchCollection subs = SubRegex.Matches(clause);
            for (int i = 0; i < subs.Count; i++)
            {
                string subName = subs[i].Groups[1].Value.Trim(' ', '\u3000', '，', '、', '的');
                int verb = subName.IndexOf('有');
                if (verb > 0 && verb < subName.Length - 2) subName = subName.Substring(verb + 1);
                if (subName.Length < 2) continue;
                if (Normalize(subName) == Normalize(parentName)) continue;
                list.Add(new Entity
                {
                    name = subName,
                    clause = clause,
                    isSub = true,
                    parentName = parentName,
                    hasRect = false,
                    hasPoint = false,
                    sizeFromText = true,
                    sizeX = F(subs[i].Groups[2].Value),
                    sizeZ = F(subs[i].Groups[2].Value),
                    kindGuess = "Building",
                });
            }
        }

        private static string KindText(bool zone, bool path, bool border, string name)
        {
            if (border) return "Border";
            if (path) return "Path";
            if (zone) return "Zone";
            return GuessKind(name);
        }

        private static bool IsPath(string clause)
        {
            return clause.IndexOf("道路", StringComparison.Ordinal) >= 0
                   || clause.IndexOf("路面", StringComparison.Ordinal) >= 0
                   || clause.IndexOf("车行", StringComparison.Ordinal) >= 0
                   || clause.IndexOf("大街", StringComparison.Ordinal) >= 0
                   || clause.IndexOf("小巷", StringComparison.Ordinal) >= 0
                   || clause.IndexOf("巷") >= 0
                   || clause.IndexOf("街道", StringComparison.Ordinal) >= 0;
        }

        private static bool IsBorder(string clause, out bool hasGeometry)
        {
            hasGeometry = PairRegex.IsMatch(clause) || PointRegex.IsMatch(clause);
            return clause.IndexOf("围合", StringComparison.Ordinal) >= 0
                   || clause.IndexOf("封边", StringComparison.Ordinal) >= 0
                   || clause.IndexOf("缺口", StringComparison.Ordinal) >= 0
                   || clause.IndexOf("院墙", StringComparison.Ordinal) >= 0
                   || clause.IndexOf("围栏", StringComparison.Ordinal) >= 0
                   || clause.IndexOf("边界", StringComparison.Ordinal) >= 0;
        }

        private static string ExtractName(string clause, out string rest)
        {
            rest = clause;
            int cut = clause.IndexOf('：');
            if (cut < 0) cut = clause.IndexOf(':');
            string head;
            if (cut > 0 && cut <= 20)
            {
                head = clause.Substring(0, cut);
                rest = clause.Substring(cut + 1);
            }
            else
            {
                int wei = -1;
                for (int i = 1; i < clause.Length && i < 24; i++)
                {
                    if (clause[i] != '为' && clause[i] != '是') continue;
                    wei = i;
                    break;
                }
                if (wei > 1)
                {
                    head = clause.Substring(0, wei);
                    rest = clause.Substring(wei + 1);
                }
                else
                {
                    head = clause;
                    rest = clause;
                }
            }
            head = head.Trim(' ', '\u3000', '，', ',', '、', '：', ':');
            // "第X排占地…" 之类的子句取主名
            int firstPair = head.IndexOf('(');
            if (firstPair > 0) head = head.Substring(0, firstPair);
            head = head.Trim(' ', '\u3000', '，', ',', '、');
            if (head.Length > 16) head = head.Substring(0, 16);
            return head;
        }

        private static bool IsZone(string tag, string clause)
        {
            if (tag.IndexOf("地面", StringComparison.Ordinal) >= 0) return true;
            for (int i = 0; i < ZoneWords.Length; i++)
                if (clause.IndexOf(ZoneWords[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        private static string GuessKind(string name)
        {
            for (int i = 0; i < BuildingWords.Length; i++)
                if (name.IndexOf(BuildingWords[i], StringComparison.Ordinal) >= 0) return "Building";
            return "Prop";
        }

        private static void NormalizeRect(Entity e)
        {
            float x0 = Mathf.Min(e.minX, e.maxX), x1 = Mathf.Max(e.minX, e.maxX);
            float z0 = Mathf.Min(e.minZ, e.maxZ), z1 = Mathf.Max(e.minZ, e.maxZ);
            e.minX = x0; e.maxX = x1; e.minZ = z0; e.maxZ = z1;
            e.sizeX = x1 - x0;
            e.sizeZ = z1 - z0;
            e.sizeFromText = true;
            if (!e.hasPoint) { e.point = new Vector2((x0 + x1) * 0.5f, (z0 + z1) * 0.5f); e.hasPoint = true; }
        }

        private static void ApplyTextSize(Entity e, string clause)
        {
            if (e.sizeFromText) return;
            Match diameter = DiameterRegex.Match(clause);
            if (diameter.Success)
            {
                e.sizeX = e.sizeZ = F(diameter.Groups[1].Value);
                e.sizeFromText = true;
                return;
            }
            Match cross = CrossRegex.Match(clause);
            if (cross.Success)
            {
                e.sizeX = F(cross.Groups[1].Value);
                e.sizeZ = F(cross.Groups[2].Value);
                e.sizeFromText = true;
                return;
            }
            Match width = WidthRegex.Match(clause);
            if (width.Success)
            {
                e.sizeX = e.sizeZ = F(width.Groups[1].Value);
                e.sizeFromText = true;
            }
        }

        /// <summary>通路是否被 Spec/Plan 的 paths 收到（按几何距离判断，不靠命名）。</summary>
        private static bool PathCovered(Entity e, LayoutSpec spec, GrayboxPlan plan)
        {
            if (spec != null && spec.paths != null)
            {
                for (int i = 0; i < spec.paths.Count; i++)
                {
                    LayoutPathSpec p = spec.paths[i];
                    if (p == null || p.nodes == null || p.nodes.Count == 0) continue;
                    if (NodesNear(e, p.nodes)) return true;
                }
            }
            if (plan != null && plan.paths != null)
            {
                for (int i = 0; i < plan.paths.Count; i++)
                {
                    GrayboxPath p = plan.paths[i];
                    if (p == null || p.nodes == null || p.nodes.Count == 0) continue;
                    if (NodesNear(e, p.nodes)) return true;
                }
            }
            // 完全没有坐标的"道路"描述：只要施工图里确实有道路，就认为收到了
            return !e.hasPoint && !e.hasRect && spec != null && spec.paths != null && spec.paths.Count > 0;
        }

        private static bool NodesNear(Entity e, List<Vector2> nodes)
        {
            if (!e.hasPoint) return false;
            const float tol = 8f;
            for (int i = 0; i < nodes.Count; i++)
            {
                float dx = Mathf.Abs(nodes[i].x - e.point.x);
                float dz = Mathf.Abs(nodes[i].y - e.point.y);
                if (dx <= tol && dz <= tol) return true;
                if (i + 1 < nodes.Count)
                {
                    Vector2 a = nodes[i], b = nodes[i + 1];
                    float minX = Mathf.Min(a.x, b.x) - tol, maxX = Mathf.Max(a.x, b.x) + tol;
                    float minZ = Mathf.Min(a.y, b.y) - tol, maxZ = Mathf.Max(a.y, b.y) + tol;
                    if (e.point.x >= minX && e.point.x <= maxX && e.point.y >= minZ && e.point.y <= maxZ)
                        return true;
                }
            }
            return false;
        }

        /// <summary>边界/围合句：只要施工图里有墙线（path.kind=wall）就算收到。</summary>
        private static bool BorderCovered(LayoutSpec spec, GrayboxPlan plan)
        {
            if (spec != null && spec.paths != null)
                for (int i = 0; i < spec.paths.Count; i++)
                    if (spec.paths[i] != null && IsWallKind(spec.paths[i].kind)) return true;
            if (plan != null && plan.paths != null)
                for (int i = 0; i < plan.paths.Count; i++)
                    if (plan.paths[i] != null && IsWallKind(plan.paths[i].kind)) return true;
            return false;
        }

        private static bool IsWallKind(string kind)
        {
            if (string.IsNullOrEmpty(kind)) return false;
            return kind.IndexOf("wall", StringComparison.OrdinalIgnoreCase) >= 0
                   || kind.IndexOf("fence", StringComparison.OrdinalIgnoreCase) >= 0
                   || kind.IndexOf("墙", StringComparison.Ordinal) >= 0;
        }

        // ─────────────────────────── 对账 ───────────────────────────

        private static bool ItemCovered(Entity e, LayoutSpec spec)
        {
            for (int i = 0; i < spec.items.Count; i++)
            {
                LayoutItemSpec item = spec.items[i];
                if (item == null) continue;
                if (NamesMatch(e.name, item.name, item.semantic)) return true;
                if (e.hasPoint && RectNear(e, item)) return true;
            }
            return false;
        }

        private static bool RectNear(Entity e, LayoutItemSpec item)
        {
            float cx = (e.minX + e.maxX) * 0.5f;
            float cz = (e.minZ + e.maxZ) * 0.5f;
            float dx = Mathf.Abs(cx - item.center.x);
            float dz = Mathf.Abs(cz - item.center.y);
            float tol = Mathf.Max(4f, Mathf.Max(e.sizeX, e.sizeZ) * 0.35f);
            if (dx > tol || dz > tol) return false;
            // 尺寸也在一个量级内，避免把"广场中央的喷泉"误判成"整个广场"
            return Mathf.Abs(e.sizeX - item.size.x) <= Mathf.Max(3f, e.sizeX * 0.6f)
                   || Mathf.Abs(e.sizeZ - item.size.y) <= Mathf.Max(3f, e.sizeZ * 0.6f);
        }

        private static bool NamesMatch(string a, string b, string semantic)
        {
            string x = Normalize(a);
            string y = Normalize(b);
            if (x.Length == 0 || y.Length == 0) return false;
            if (x == y) return true;
            if (x.Length >= 3 && y.Length >= 3 && (x.IndexOf(y, StringComparison.Ordinal) >= 0
                                                  || y.IndexOf(x, StringComparison.Ordinal) >= 0)) return true;
            // 名称被截断（"圣保罗教堂_石砌哥特式小" / "中央石板广场_0"）：只在 semantic 以实体名开头时认，
            // 不能用"包含"——否则"门楣上方的石砌钟楼"会因为主体 semantic 里提到它而被误判成"已覆盖"。
            string z = Normalize(semantic);
            string head = x.Length >= 4 ? x.Substring(0, 4) : x;
            return z.Length >= 4 && z.StartsWith(head, StringComparison.Ordinal);
        }

        private static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == ' ' || c == '\u3000' || c == '_' || c == '-' || c == '·' || c == '，' || c == ','
                    || c == ':' || c == '：' || c == '（' || c == '）' || c == '(' || c == ')' || c == '、')
                    continue;
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static LayoutZoneSpec FindZoneSpec(LayoutSpec spec, Entity e)
        {
            LayoutZoneSpec best = null;
            float bestIou = 0.55f;
            if (spec.zones == null) return null;
            for (int i = 0; i < spec.zones.Count; i++)
            {
                LayoutZoneSpec zone = spec.zones[i];
                if (zone == null || zone.polygon == null || zone.polygon.Count < 3) continue;
                if (NamesMatch(e.name, zone.description, zone.kind)) return zone;
                float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
                for (int p = 0; p < zone.polygon.Count; p++)
                {
                    x0 = Mathf.Min(x0, zone.polygon[p].x); x1 = Mathf.Max(x1, zone.polygon[p].x);
                    z0 = Mathf.Min(z0, zone.polygon[p].y); z1 = Mathf.Max(z1, zone.polygon[p].y);
                }
                float iou = IoU(e.minX, e.minZ, e.maxX, e.maxZ, x0, z0, x1, z1);
                if (iou > bestIou)
                {
                    bestIou = iou;
                    best = zone;
                    // 解析器常常只给 zone 一个"地面"的通用名，几何能对上就换成散文里的具体名
                    if (IsGenericZoneName(zone.description)) zone.description = e.name;
                }
            }
            return best;
        }

        private static bool IsGenericZoneName(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            string n = name.Trim();
            return n == "地面" || n == "分区" || n == "square" || n == "field" || n == "water"
                   || n == "场地" || n == "场景地面" || n == "未知";
        }

        /// <summary>方位/泛称：不能当成"剧情点名了东西"。</summary>
        private static bool IsDirectionalName(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            switch (name.Trim())
            {
                case "北侧": case "南侧": case "东侧": case "西侧":
                case "北面": case "南面": case "东面": case "西面":
                case "北": case "南": case "东": case "西":
                case "中央": case "中心": case "四周": case "外围":
                case "场地": case "场景": case "地面": case "街区": case "地块":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>"(0,96) 是圣安东门，石砌城门楼" → "圣安东门"。</summary>
        private static string RefineDirectionalName(string rest)
        {
            if (string.IsNullOrEmpty(rest)) return "";
            Match m = Regex.Match(rest, @"(?:是|为|有|立着|放着|矗立着)([\u4e00-\u9fa5A-Za-z0-9]{2,12})");
            if (!m.Success) return "";
            string name = m.Groups[1].Value.Trim();
            return IsDirectionalName(name) ? "" : name;
        }

        /// <summary>分区材质判错（例：石板广场被解析成 water）→ 按散文里的铺装词纠正（repair=false 时只报告）。</summary>
        private static void ReconcileZoneKind(LayoutZoneSpec zone, Entity e, Report report, bool repair = true)
        {
            string want = ZoneKindOf(e.clause);
            if (string.IsNullOrEmpty(want) || want == zone.kind) return;
            string before = zone.kind;
            if (!repair)
            {
                report.unresolved.Add(Truncate(e.name, 14) + " 分区材质 " + before + " 与剧情「" + want + "」不一致（未自动修改）");
                return;
            }
            zone.kind = want;
            report.approximate.Add(Truncate(e.name, 14) + " 分区材质校正 " + before + " → " + want);
        }

        private static string ZoneKindOf(string clause)
        {
            if (clause.IndexOf("水面", StringComparison.Ordinal) >= 0
                || clause.IndexOf("水域", StringComparison.Ordinal) >= 0
                || clause.IndexOf("河", StringComparison.Ordinal) >= 0)
                return "water";
            if (clause.IndexOf("夯土", StringComparison.Ordinal) >= 0
                || clause.IndexOf("泥土", StringComparison.Ordinal) >= 0
                || clause.IndexOf("碎石", StringComparison.Ordinal) >= 0
                || clause.IndexOf("泥地", StringComparison.Ordinal) >= 0)
                return "field";
            if (clause.IndexOf("石板", StringComparison.Ordinal) >= 0
                || clause.IndexOf("铺石", StringComparison.Ordinal) >= 0
                || clause.IndexOf("铺地", StringComparison.Ordinal) >= 0)
                return "square";
            return "";
        }

        private static LayoutItemSpec FindItem(LayoutSpec spec, string name)
        {
            for (int i = 0; i < spec.items.Count; i++)
            {
                LayoutItemSpec item = spec.items[i];
                if (item == null) continue;
                if (NamesMatch(name, item.name, item.semantic)) return item;
            }
            return null;
        }

        private static float IoU(float ax0, float az0, float ax1, float az1,
                                 float bx0, float bz0, float bx1, float bz1)
        {
            float ix0 = Mathf.Max(ax0, bx0), ix1 = Mathf.Min(ax1, bx1);
            float iz0 = Mathf.Max(az0, bz0), iz1 = Mathf.Min(az1, bz1);
            if (ix1 <= ix0 || iz1 <= iz0) return 0f;
            float inter = (ix1 - ix0) * (iz1 - iz0);
            float union = (ax1 - ax0) * (az1 - az0) + (bx1 - bx0) * (bz1 - bz0) - inter;
            return union <= 0f ? 0f : inter / union;
        }

        // ─────────────────────────── 补全 ───────────────────────────

        private static LayoutItemSpec AddItem(LayoutSpec spec, Entity e)
        {
            float w = e.sizeFromText ? Mathf.Clamp(e.sizeX, 0.6f, 70f) : (e.kindGuess == "Building" ? 8f : 2f);
            float d = e.sizeFromText ? Mathf.Clamp(e.sizeZ, 0.6f, 70f) : (e.kindGuess == "Building" ? 6f : 2f);
            var item = new LayoutItemSpec
            {
                id = "coverage_" + SafeId(e.name),
                name = e.name,
                semantic = Truncate(e.clause, 60),
                kind = e.kindGuess == "Building" ? SlotKind.Building
                    : (e.kindGuess == "Npc" ? SlotKind.Npc : SlotKind.Prop),
                center = e.hasPoint ? e.point : new Vector2((e.minX + e.maxX) * 0.5f, (e.minZ + e.maxZ) * 0.5f),
                size = new Vector2(w, d),
                rotation = 0f,
                required = true,
                source = "coverage",
                notes = "覆盖审计补全：" + Truncate(e.clause, 80),
            };
            spec.items.Add(item);
            return item;
        }

        /// <summary>把散文里的地面分区补进施工图（LayoutSpec.zones）——分区从此只有一个来源。</summary>
        private static LayoutZoneSpec AddZoneSpec(LayoutSpec spec, Entity e)
        {
            if (spec.zones == null) spec.zones = new List<LayoutZoneSpec>();
            string kind = ZoneKindOf(e.clause);
            if (string.IsNullOrEmpty(kind))
            {
                kind = "square";
                if (e.clause.IndexOf("夯土", StringComparison.Ordinal) >= 0
                    || e.clause.IndexOf("泥土", StringComparison.Ordinal) >= 0
                    || e.clause.IndexOf("碎石", StringComparison.Ordinal) >= 0) kind = "field";
            }
            var zone = new LayoutZoneSpec
            {
                zoneId = "zone_" + SafeId(e.name),
                kind = kind,
                description = e.name,
                polygon = RectPoly(e.minX, e.minZ, e.maxX, e.maxZ),
                groundHeight = kind == "water" ? -0.35f : 0f,
                required = true,
                source = "coverage",
            };
            spec.zones.Add(zone);
            return zone;
        }

        private static void MirrorZone(GrayboxPlan plan, LayoutZoneSpec zone)
        {
            if (plan == null || zone == null) return;
            if (plan.regions == null) plan.regions = new List<GrayboxRegion>();
            for (int i = 0; i < plan.regions.Count; i++)
            {
                GrayboxRegion r = plan.regions[i];
                if (r != null && NamesMatch(zone.description, r.description, r.kind)) return;
            }
            plan.regions.Add(new GrayboxRegion
            {
                regionId = string.IsNullOrEmpty(zone.zoneId) ? "region_coverage" : "region_" + zone.zoneId,
                kind = zone.kind,
                description = zone.description,
                style = zone.kind,
                polygon = zone.polygon != null && zone.polygon.Count >= 3
                    ? new List<Vector2>(zone.polygon)
                    : RectPoly(-1f, -1f, 1f, 1f),
                groundHeight = zone.groundHeight,
                areaWeight = 1f,
            });
        }

        private static List<Vector2> RectPoly(float minX, float minZ, float maxX, float maxZ)
        {
            return new List<Vector2>
            {
                new Vector2(minX, minZ),
                new Vector2(maxX, minZ),
                new Vector2(maxX, maxZ),
                new Vector2(minX, maxZ),
            };
        }

        private static void ReconcileSize(Entity e, LayoutSpec spec, Report report, bool repair = true)
        {
            if (!e.sizeFromText) return;
            for (int i = 0; i < spec.items.Count; i++)
            {
                LayoutItemSpec item = spec.items[i];
                if (item == null) continue;
                if (!NamesMatch(e.name, item.name, item.semantic)) continue;
                if (!string.IsNullOrEmpty(item.rowId)) return;   // 排屋已按开间拆成单元，不能用整排尺寸反算
                float dw = Mathf.Abs(item.size.x - e.sizeX);
                float dd = Mathf.Abs(item.size.y - e.sizeZ);
                if (dw <= Mathf.Max(1.5f, e.sizeX * 0.25f) && dd <= Mathf.Max(1.5f, e.sizeZ * 0.25f)) return;

                string before = item.size.x.ToString("0.#", CultureInfo.InvariantCulture) + "×"
                                + item.size.y.ToString("0.#", CultureInfo.InvariantCulture);
                if (!repair)
                {
                    report.unresolved.Add(e.name + " 施工图尺寸 " + before + " 与剧情 " + SizeText(e) + " 不一致（未自动修改）");
                    return;
                }
                // 剧情写了「直径6米」这类明确尺寸，正则没读出来 → 用剧情数值纠正施工图
                item.size = new Vector2(Mathf.Clamp(e.sizeX, 0.6f, 70f), Mathf.Clamp(e.sizeZ, 0.6f, 70f));
                item.notes = Truncate(item.notes + " · 覆盖审计校正尺寸 " + before + "→" + SizeText(e), 120);
                report.approximate.Add(e.name + " 尺寸校正 " + before + " → " + SizeText(e));
                return;
            }
        }

        private static string RectText(Entity e)
        {
            if (e.sizeFromText) return SizeText(e);
            return "无尺寸";
        }

        private static string SizeText(Entity e)
        {
            return e.sizeX.ToString("0.#", CultureInfo.InvariantCulture) + "×"
                   + e.sizeZ.ToString("0.#", CultureInfo.InvariantCulture);
        }

        private static string Truncate(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= n ? s : s.Substring(0, n);
        }

        private static string SafeId(string s)
        {
            if (string.IsNullOrEmpty(s)) return "x";
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length && sb.Length < 18; i++)
            {
                char c = s[i];
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            }
            return sb.Length == 0 ? "x" : sb.ToString();
        }

        private static void SafeLog(string message)
        {
            // 离线探针（不启 Unity）里 Debug.Log 会打到 native 上而失败，这里不能让它中断审计。
            try { Debug.Log(message); } catch { }
        }

        private static float F(string s)
        {
            float v;
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0f;
        }
    }
}
