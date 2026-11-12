// SceneGen 求解器：把「剧情语义骨架」确定性编译成「完整施工图」。
//
// 为什么要有它：
//   旧链路让剧情 Agent 直接写 22 件地物的精确坐标，灰盒再"理解"一遍散文。
//   实测（2026-11-11 录制 s_3b3e2f12_0_f）后果：
//     · 密度：总占地 10.7%，9 块分区里 4 块（21.6% 地面）一件地物都没有；
//     · 秩序：排屋 width=22/depth=12/rotation=±90 与实际世界 AABB 冲突，
//             相邻单元实际互压 7m，而合规层用未旋转尺寸算出"缝隙 3.0m"并报 fail(6)；
//             2/4 个锚点被散文正则重新推导到主体建筑中心。
//   本求解器把这些交给确定性的几何算法：
//     · 模型只说"这里是一条主街、两侧连续街屋、北端是要塞"；
//     · 地块、开间、进深、立面朝向、围合、道具散布由这里算；
//     · 一切尺寸都是世界轴对齐 AABB，rotation 仅表示立面朝向 → 校验与渲染不会打架；
//     · 同一输入 + 同一 seed 必定产出同一张图（可回放、可离线验收）。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using EpicSpark.Core;
using UnityEngine;

namespace EpicSpark.SceneGen
{
    /// <summary>一次求解的 KPI 报告：把"稀/乱"变成可测数字。</summary>
    public class SceneGenReport
    {
        public int Items;
        public int Pinned;
        public int FillUnits;
        public int Props;
        public float FootprintRatio;
        public float FrontageContinuity;
        public float EnclosureRatio;
        public int Overlaps;
        public int OutOfZone;
        public int EmptyZones;
        public int Dropped;
        public int AnchorsBound;
        public int AnchorsUnbound;
        public readonly List<string> Notes = new List<string>();

        public string Compact()
        {
            var sb = new StringBuilder(220);
            sb.Append("密度 ").Append(Pct(FootprintRatio));
            sb.Append(" · 临街连续 ").Append(Pct(FrontageContinuity));
            sb.Append(" · 围合 ").Append(Pct(EnclosureRatio));
            sb.Append(" · 冲突 ").Append(Overlaps);
            sb.Append(" · 空地分区 ").Append(EmptyZones);
            sb.Append(" · 槽位 ").Append(Items);
            sb.Append("（点名 ").Append(Pinned).Append(" / 填充 ").Append(FillUnits)
              .Append(" / 道具 ").Append(Props).Append("）");
            if (Dropped > 0) sb.Append(" · 剔除 ").Append(Dropped);
            if (AnchorsUnbound > 0) sb.Append(" · 未绑定锚点 ").Append(AnchorsUnbound);
            return sb.ToString();
        }

        private static string Pct(float value)
        {
            return (value * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";
        }
    }

    public static class SceneGenSolver
    {
        private const float DefaultFloors = 3f;
        private const float FloorHeight = 3.1f;
        private const int MaxItems = 420;

        public static LayoutSpec Solve(SceneSpec scene, LayoutSpec input, out SceneGenReport report)
        {
            report = new SceneGenReport();
            var spec = input ?? new LayoutSpec();
            if (scene == null) return spec;

            float halfW = Mathf.Max(10f, scene.width > 1f ? scene.width * 0.5f : 30f);
            float halfL = Mathf.Max(10f, scene.length > 1f ? scene.length * 0.5f : 30f);
            var site = new Rect(-halfW, -halfL, halfW * 2f, halfL * 2f);
            var rng = new SceneGenRng(StableHash(scene.sceneId + "|" + scene.sceneName));

            spec.version = "2";
            spec.sceneId = scene.sceneId ?? string.Empty;
            spec.source = "sceneGen";
            if (spec.unitWidth < 3f) spec.unitWidth = 8.5f;
            if (spec.unitDepth < 3f) spec.unitDepth = 10f;
            if (spec.rowGap < 0f) spec.rowGap = 0.15f;
            if (spec.setback < 0f) spec.setback = 1.2f;

            NormalizeZones(spec, site);
            CollectStreets(spec);
            var zoneRects = ZoneRects(spec, site);
            var openRects = OpenZoneRects(spec, site);

            var items = new List<LayoutItemSpec>(MaxItems);
            var occupied = new List<Rect>();
            var roadRects = RoadCorridors(spec);

            // ① 剧情点名物（含排屋重排）：它们的位置优先，占位最先登记。
            AddPinnedItems(spec, items, occupied, site, zoneRects, rng, report);
            // ② 语义地标（模型只说"北端要塞"时在这里生成）。
            AddSemanticLandmarks(spec, items, occupied, site, zoneRects, roadRects, report);
            // ③ 临街立面填充：密度与连续性的主要来源。
            FillFrontage(spec, items, occupied, site, openRects, roadRects, rng, report);
            // ④ 空分区补齐：旧链路 4 个分区零地物，这里按分区类型补足。
            FillEmptyZones(spec, items, occupied, site, zoneRects, openRects, roadRects, rng, report);
            // ⑤ 边界围合（只在模型没给墙时生成；缺口只留在街口）。
            EnsureEnclosure(spec, site, report);
            // ⑥ 开敞区的道具散布：路灯 / 树 / 人群 / 摊位。
            ScatterZoneProps(spec, items, occupied, site, openRects, roadRects, rng, report);
            // ⑦ 剧情单位落位：锚点对齐载体、NPC/道具避免落进建筑。
            AddStoryUnits(scene, spec, items, occupied, site, zoneRects, roadRects, rng, report);

            spec.items = items;

            // ⑧ KPI + 门禁（剔除严重重叠的填充单元，不静默）。
            var geo = new SceneGenGeometryState(spec, site);
            DropBadSolverUnits(spec, geo, report);
            NormalizeAnchors(spec, geo, report);
            Measure(scene, spec, geo, report);
            spec.metrics = report.Compact();
            spec.diagnostics = report.Compact();
            if (spec.gaps == null) spec.gaps = new List<string>();
            return spec;
        }

        // ─────────────────────────── 分区 ───────────────────────────

        private static void NormalizeZones(LayoutSpec spec, Rect site)
        {
            if (spec.zones == null) spec.zones = new List<LayoutZoneSpec>();
            var cleaned = new List<LayoutZoneSpec>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < spec.zones.Count; i++)
            {
                LayoutZoneSpec zone = spec.zones[i];
                if (zone == null || zone.polygon == null || zone.polygon.Count < 3) continue;
                Rect rect = SceneGenRect.Bounds(zone.polygon);
                rect = SceneGenRect.ClipTo(rect, site);
                if (rect.width < 2f || rect.height < 2f) continue;
                zone.polygon = SceneGenRect.ToPolygon(rect);
                if (string.IsNullOrEmpty(zone.kind)) zone.kind = "field";
                if (zone.openness <= 0f) zone.openness = DefaultOpenness(zone.kind);
                if (string.IsNullOrEmpty(zone.material) && zone.polygon.Count > 0)
                    zone.material = MaterialForKind(zone.kind);
                string key = zone.zoneId + "|" + Mathf.RoundToInt(rect.xMin) + "|" + Mathf.RoundToInt(rect.yMin)
                             + "|" + Mathf.RoundToInt(rect.xMax) + "|" + Mathf.RoundToInt(rect.yMax);
                if (!seen.Add(key)) continue;
                cleaned.Add(zone);
            }
            spec.zones = cleaned;

            // 场地必须处处有地面：把未覆盖区域按条带补成"基底"分区，避免地物落在无分区处。
            var uncovered = new List<Rect> { site };
            for (int i = 0; i < cleaned.Count; i++)
            {
                Rect zr = SceneGenRect.Bounds(cleaned[i].polygon);
                var next = new List<Rect>();
                for (int u = 0; u < uncovered.Count; u++) next.AddRange(SceneGenRect.Subtract(uncovered[u], zr));
                uncovered = next;
            }
            for (int i = 0; i < uncovered.Count; i++)
            {
                Rect r = uncovered[i];
                if (r.width < 1.5f || r.height < 1.5f) continue;
                spec.zones.Add(new LayoutZoneSpec
                {
                    zoneId = "zone_fill_" + i.ToString(CultureInfo.InvariantCulture),
                    kind = "field",
                    description = "场地基底",
                    polygon = SceneGenRect.ToPolygon(r),
                    groundHeight = 0f,
                    required = false,
                    source = "sceneGen",
                    openness = 0.9f,
                    material = "泥土",
                });
            }
        }

        private static float DefaultOpenness(string kind)
        {
            switch ((kind ?? "").ToLowerInvariant())
            {
                case "square": return 0.85f;
                case "market": return 0.6f;
                case "church": return 0.5f;
                case "harbor": return 0.7f;
                case "water": return 1f;
                case "field": return 0.9f;
                case "residential": return 0.15f;
                default: return 0.5f;
            }
        }

        private static string MaterialForKind(string kind)
        {
            switch ((kind ?? "").ToLowerInvariant())
            {
                case "square": return "石板";
                case "market": return "夯实泥土";
                case "church": return "方石铺地";
                case "water": return "水面";
                case "field": return "泥土";
                case "residential": return "夯土";
                default: return "碎石";
            }
        }

        private static List<Rect> ZoneRects(LayoutSpec spec, Rect site)
        {
            var list = new List<Rect>();
            for (int i = 0; i < spec.zones.Count; i++)
            {
                LayoutZoneSpec zone = spec.zones[i];
                if (zone == null || zone.polygon == null || zone.polygon.Count < 3) continue;
                list.Add(SceneGenRect.ClipTo(SceneGenRect.Bounds(zone.polygon), site));
            }
            return list;
        }

        private static List<Rect> OpenZoneRects(LayoutSpec spec, Rect site)
        {
            var list = new List<Rect>();
            for (int i = 0; i < spec.zones.Count; i++)
            {
                LayoutZoneSpec zone = spec.zones[i];
                if (zone == null || zone.polygon == null || zone.polygon.Count < 3) continue;
                if (zone.openness < 0.75f) continue;
                list.Add(SceneGenRect.ClipTo(SceneGenRect.Bounds(zone.polygon), site));
            }
            return list;
        }

        // ─────────────────────────── 街道 ───────────────────────────

        /// <summary>把 paths(kind=road) 提升成街道语义；模型没给 streets 时也能跑。</summary>
        private static void CollectStreets(LayoutSpec spec)
        {
            if (spec.streets == null) spec.streets = new List<LayoutStreetSpec>();
            if (spec.streets.Count > 0) return;
            if (spec.paths == null) return;
            for (int i = 0; i < spec.paths.Count; i++)
            {
                LayoutPathSpec path = spec.paths[i];
                if (path == null || path.nodes == null || path.nodes.Count < 2) continue;
                if (!string.Equals(path.kind, "road", StringComparison.OrdinalIgnoreCase)) continue;
                spec.streets.Add(new LayoutStreetSpec
                {
                    pathId = path.pathId,
                    name = "街道" + i.ToString(CultureInfo.InvariantCulture),
                    nodes = new List<Vector2>(path.nodes),
                    width = Mathf.Clamp(path.width, 3f, 30f),
                    frontageBoth = true,
                    style = "classical_row",
                    continuous = true,
                });
            }
        }

        private static List<Rect> RoadCorridors(LayoutSpec spec)
        {
            var list = new List<Rect>();
            if (spec.streets == null) return list;
            for (int i = 0; i < spec.streets.Count; i++)
            {
                LayoutStreetSpec street = spec.streets[i];
                if (street == null || street.nodes == null) continue;
                List<SceneGenSegments.Segment> segs = SceneGenSegments.Split(street.nodes);
                for (int s = 0; s < segs.Count; s++)
                {
                    list.Add(SceneGenSegments.Corridor(segs[s], street.width * 0.5f));
                }
            }
            // 非街道的路径（墙/栅栏）不占临街地块，但要挡住建筑，用极窄走廊近似。
            if (spec.paths != null)
            {
                for (int i = 0; i < spec.paths.Count; i++)
                {
                    LayoutPathSpec path = spec.paths[i];
                    if (path == null || path.nodes == null) continue;
                    if (string.Equals(path.kind, "road", StringComparison.OrdinalIgnoreCase)) continue;
                    List<SceneGenSegments.Segment> segs = SceneGenSegments.Split(path.nodes);
                    for (int s = 0; s < segs.Count; s++)
                        list.Add(SceneGenSegments.Corridor(segs[s], Mathf.Max(0.6f, path.width * 0.5f) + 0.4f));
                }
            }
            return list;
        }

        // ─────────────────────────── 剧情点名物 ───────────────────────────

        private static void AddPinnedItems(
            LayoutSpec spec, List<LayoutItemSpec> items, List<Rect> occupied, Rect site,
            List<Rect> zoneRects, SceneGenRng rng, SceneGenReport report)
        {
            var existing = spec.items ?? new List<LayoutItemSpec>();
            var rowGroups = new Dictionary<string, List<LayoutItemSpec>>(StringComparer.Ordinal);
            var singles = new List<LayoutItemSpec>();

            for (int i = 0; i < existing.Count; i++)
            {
                LayoutItemSpec item = existing[i];
                if (item == null) continue;
                if (item.kind == SlotKind.Npc || item.kind == SlotKind.Anchor) continue; // 由 AddStoryUnits 统一处理
                if (!string.IsNullOrEmpty(item.rowId) && item.rowCount > 1)
                {
                    List<LayoutItemSpec> list;
                    if (!rowGroups.TryGetValue(item.rowId, out list))
                    {
                        list = new List<LayoutItemSpec>();
                        rowGroups[item.rowId] = list;
                    }
                    list.Add(item);
                }
                else
                {
                    singles.Add(item);
                }
            }

            for (int i = 0; i < singles.Count; i++)
            {
                LayoutItemSpec item = singles[i];
                ClampPinned(item, site);
                if (string.IsNullOrEmpty(item.primitive)) item.primitive = PrimitiveForKind(item.kind, item.name, item.height);
                if (item.faceDir.sqrMagnitude < 0.01f)
                    item.faceDir = SceneGenFacing.NormalFromAngle(item.rotation);
                items.Add(item);
                occupied.Add(SceneGenRect.FromCenter(item.center, item.size.x, item.size.y));
                report.Pinned++;
            }

            // 排屋重排：旧数据把 width 当"沿街宽"、rotation 当朝向，但校验/渲染用世界 AABB，
            // 于是一排房子互相插入。这里统一成世界 AABB：沿街 = 间距 - 缝隙，进深 = 较小的那个尺寸。
            foreach (KeyValuePair<string, List<LayoutItemSpec>> pair in rowGroups)
            {
                List<LayoutItemSpec> units = pair.Value;
                units.Sort((a, b) => a.rowIndex.CompareTo(b.rowIndex));
                ReflowRow(units, spec, site, occupied, items, zoneRects, rng, report);
            }
        }

        private static void ReflowRow(
            List<LayoutItemSpec> units, LayoutSpec spec, Rect site, List<Rect> occupied,
            List<LayoutItemSpec> items, List<Rect> zoneRects, SceneGenRng rng, SceneGenReport report)
        {
            int n = units.Count;
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                minX = Mathf.Min(minX, units[i].center.x); maxX = Mathf.Max(maxX, units[i].center.x);
                minZ = Mathf.Min(minZ, units[i].center.y); maxZ = Mathf.Max(maxZ, units[i].center.y);
            }
            bool alongZ = (maxZ - minZ) >= (maxX - minX);
            float span = alongZ ? (maxZ - minZ) : (maxX - minX);
            float pitch = n > 1 ? span / (n - 1) : Mathf.Max(6f, alongZ ? units[0].size.y : units[0].size.x);
            if (pitch < 3f)
            {
                float fallback = Mathf.Max(6f, spec.unitWidth);
                pitch = fallback;
            }
            float frontage = Mathf.Max(2.5f, pitch - Mathf.Max(0f, spec.rowGap));
            float depth = Mathf.Clamp(Mathf.Min(units[0].size.x, units[0].size.y), 6f, 18f);
            if (depth <= 0f) depth = 12f;

            Vector2 rowCenter = new Vector2((minX + maxX) * 0.5f, (minZ + maxZ) * 0.5f);
            Vector2 face = NearestStreetNormal(spec, rowCenter);
            if (face.sqrMagnitude < 0.01f) face = SceneGenFacing.SnapToAxis(new Vector2(-rowCenter.x, -rowCenter.y));
            face = SceneGenFacing.SnapToAxis(face);

            for (int i = 0; i < n; i++)
            {
                LayoutItemSpec unit = units[i];
                float t = (i - (n - 1) * 0.5f);
                Vector2 center;
                if (alongZ)
                {
                    center = new Vector2(minZ * 0f + (minX + maxX) * 0.5f, rowCenter.y + t * pitch);
                    unit.size = new Vector2(depth, frontage);
                }
                else
                {
                    center = new Vector2(rowCenter.x + t * pitch, rowCenter.y);
                    unit.size = new Vector2(frontage, depth);
                }
                // 面向街道：把建筑整体退到街道对侧，保持立面朝向 street。
                center += face * 0f;
                unit.center = center;
                unit.faceDir = face;
                unit.rotation = SceneGenFacing.AngleFromNormal(face);
                unit.rowCount = n;
                unit.rowId = string.IsNullOrEmpty(unit.rowId) ? "row" : unit.rowId;
                if (unit.height <= 0.5f) unit.height = DefaultFloors * FloorHeight;
                if (string.IsNullOrEmpty(unit.primitive)) unit.primitive = "row_house";
                unit.kind = SlotKind.Building;
                unit.required = true;
                unit.source = string.IsNullOrEmpty(unit.source) ? "storyLayoutSpec" : unit.source;
                unit.solverGenerated = false;
                ClampItemToSite(unit, site);

                items.Add(unit);
                occupied.Add(SceneGenRect.FromCenter(unit.center, unit.size.x, unit.size.y));
                report.Pinned++;
            }
            if (n > 1)
            {
                report.Notes.Add("排屋 " + units[0].rowId + " 重排为 " + n + " 间，沿街 "
                                 + (alongZ ? "Z" : "X") + " 轴，间距 " + pitch.ToString("0.0", CultureInfo.InvariantCulture)
                                 + "m，进深 " + depth.ToString("0.0", CultureInfo.InvariantCulture) + "m");
            }
        }

        private static Vector2 NearestStreetNormal(LayoutSpec spec, Vector2 point)
        {
            if (spec.streets == null) return Vector2.zero;
            float best = float.MaxValue;
            Vector2 normal = Vector2.zero;
            for (int i = 0; i < spec.streets.Count; i++)
            {
                LayoutStreetSpec street = spec.streets[i];
                if (street == null || street.nodes == null) continue;
                List<SceneGenSegments.Segment> segs = SceneGenSegments.Split(street.nodes);
                for (int s = 0; s < segs.Count; s++)
                {
                    SceneGenSegments.Segment seg = segs[s];
                    if (!seg.AxisAligned) continue;
                    Vector2 closest = ClosestOnSegment(point, seg.a, seg.b);
                    float distance = Vector2.Distance(point, closest);
                    if (distance >= best) continue;
                    best = distance;
                    normal = seg.vertical
                        ? new Vector2(point.x >= seg.a.x ? -1f : 1f, 0f)
                        : new Vector2(0f, point.y >= seg.a.y ? -1f : 1f);
                }
            }
            return normal;
        }

        private static Vector2 ClosestOnSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float len2 = ab.sqrMagnitude;
            if (len2 <= 0.0001f) return a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
            return a + ab * t;
        }

        // ─────────────────────────── 语义地标 ───────────────────────────

        private static void AddSemanticLandmarks(
            LayoutSpec spec, List<LayoutItemSpec> items, List<Rect> occupied, Rect site,
            List<Rect> zoneRects, List<Rect> roadRects, SceneGenReport report)
        {
            if (spec.landmarks == null) return;
            int index = 0;
            for (int i = 0; i < spec.landmarks.Count; i++)
            {
                LayoutLandmarkSpec landmark = spec.landmarks[i];
                if (landmark == null || string.IsNullOrEmpty(landmark.name)) continue;
                if (FindByName(items, landmark.name) != null) continue;

                float width = landmark.width > 1f ? landmark.width : DefaultLandmarkWidth(landmark.kind);
                float depth = landmark.depth > 1f ? landmark.depth : DefaultLandmarkDepth(landmark.kind);
                float height = landmark.height > 1f ? landmark.height : DefaultLandmarkHeight(landmark.kind);
                Vector2 facing = SceneGenFacing.SnapToAxis(SceneGenFacing.NormalFromAngle(
                    SceneGenFacing.AngleFromWord(landmark.facing)));

                Vector2 center;
                if (landmark.pinned)
                {
                    center = landmark.pin;
                }
                else
                {
                    // on 的写法是「分区名 + 方位」：先把分区名剥掉，剩下才是方位词，
                    // 否则「中央广场 北侧」会被"中央"抢先命中而落到广场正中（教堂和市政厅叠在一起）。
                    string placement = StripZoneNames(spec, landmark.on);
                    Rect anchorZone;
                    if (TryFindZoneRect(spec, landmark.on, zoneRects, out anchorZone))
                        center = ZoneEdgePoint(anchorZone, facing, width, depth, placement);
                    else
                        center = new Vector2(0f, site.yMax - depth * 0.5f - 6f);

                    // 地标不能压在街道上：沿"垂直立面"的方向左右挪，找最近的不占道落点。
                    Vector2 lateral = facing.sqrMagnitude < 0.01f ? new Vector2(0f, 1f) : new Vector2(-facing.y, facing.x);
                    float step = (width + depth) * 0.5f + 3f;
                    float[] offsets = { 0f, step, -step, step * 2f, -step * 2f };
                    bool placed = false;
                    for (int attempt = 0; attempt < offsets.Length && !placed; attempt++)
                    {
                        Vector2 candidate = ClampCenter(center + lateral * offsets[attempt], width, depth, site);
                        Rect rect = SceneGenRect.FromCenter(candidate, width, depth);
                        // 只有"穿过"地标的道路才算冲突：街道尽端 T 进要塞/城门是合理的城市形态。
                        if (RoadCrosses(rect, roadRects) || OverlapAny(rect, occupied, 1f)) continue;
                        center = candidate;
                        placed = true;
                    }
                }
                center = ClampCenter(center, width, depth, site);

                var item = new LayoutItemSpec
                {
                    id = UniqueId("lm_" + SafeId(landmark.name) + "_" + index.ToString(CultureInfo.InvariantCulture), items),
                    name = landmark.name,
                    semantic = string.IsNullOrEmpty(landmark.semantic) ? landmark.name : landmark.semantic,
                    kind = SlotKind.Building,
                    center = center,
                    size = new Vector2(width, depth),
                    rotation = SceneGenFacing.AngleFromNormal(facing),
                    faceDir = facing,
                    height = height,
                    primitive = PrimitiveForLandmark(landmark.kind),
                    expectedModelHint = string.IsNullOrEmpty(landmark.hint) ? landmark.name : landmark.hint,
                    required = true,
                    source = "sceneGen-landmark",
                    solverGenerated = false,
                };
                items.Add(item);
                occupied.Add(SceneGenRect.FromCenter(center, width, depth));
                index++;
                report.Pinned++;
            }
        }

        private static float DefaultLandmarkWidth(string kind)
        {
            switch ((kind ?? "").ToLowerInvariant())
            {
                case "fortress": return 44f;
                case "church": return 26f;
                case "gate": return 16f;
                case "hall": return 28f;
                case "steps": return 20f;
                case "stall": return 8f;
                default: return 14f;
            }
        }

        private static float DefaultLandmarkDepth(string kind)
        {
            switch ((kind ?? "").ToLowerInvariant())
            {
                case "fortress": return 22f;
                case "church": return 20f;
                case "gate": return 8f;
                case "hall": return 14f;
                case "steps": return 4f;
                case "stall": return 5f;
                default: return 12f;
            }
        }

        private static float DefaultLandmarkHeight(string kind)
        {
            switch ((kind ?? "").ToLowerInvariant())
            {
                case "fortress": return 26f;
                case "church": return 22f;
                case "gate": return 14f;
                case "hall": return 16f;
                case "steps": return 1.5f;
                case "stall": return 2.5f;
                default: return 12f;
            }
        }

        private static string PrimitiveForLandmark(string kind)
        {
            switch ((kind ?? "").ToLowerInvariant())
            {
                case "fortress": return "fortress";
                case "church": return "church";
                case "gate": return "gate";
                case "hall": return "hall";
                case "steps": return "steps";
                case "stall": return "stall";
                case "decoration": return "prop";
                default: return "building";
            }
        }

        private static string PrimitiveForKind(SlotKind kind, string name, float height)
        {
            if (kind == SlotKind.Decoration || kind == SlotKind.Prop)
            {
                string n = name ?? "";
                if (n.IndexOf("灯", StringComparison.Ordinal) >= 0) return "lamp";
                if (n.IndexOf("树", StringComparison.Ordinal) >= 0) return "tree";
                if (n.IndexOf("鼓", StringComparison.Ordinal) >= 0) return "drum";
                if (n.IndexOf("摊", StringComparison.Ordinal) >= 0) return "stall";
                if (height > 6f) return "pillar";
                return "prop";
            }
            return "building";
        }

        // ─────────────────────────── 临街填充 ───────────────────────────

        private static void FillFrontage(
            LayoutSpec spec, List<LayoutItemSpec> items, List<Rect> occupied, Rect site,
            List<Rect> openRects, List<Rect> roadRects, SceneGenRng rng, SceneGenReport report)
        {
            if (spec.streets == null) return;
            for (int si = 0; si < spec.streets.Count; si++)
            {
                LayoutStreetSpec street = spec.streets[si];
                if (street == null || street.nodes == null || street.nodes.Count < 2) continue;
                if (string.Equals(street.style, "none", StringComparison.OrdinalIgnoreCase)) continue;
                bool both = street.frontageBoth;
                float width = Mathf.Clamp(street.width, 3f, 30f);
                float depth = street.depth > 1f ? Mathf.Clamp(street.depth, 6f, 18f) : spec.unitDepth;
                int floors = street.floors > 0 ? Mathf.Clamp(street.floors, 1, 6) : 3;
                float height = floors * FloorHeight;

                List<SceneGenSegments.Segment> segs = SceneGenSegments.Split(street.nodes);
                int wantTotal = 0;
                int madeTotal = 0;
                for (int s = 0; s < segs.Count; s++)
                {
                    SceneGenSegments.Segment seg = segs[s];
                    if (!seg.AxisAligned) continue;
                    if (seg.Length < 6f) continue;

                    int sideCount = both ? 2 : 1;
                    for (int side = 0; side < sideCount; side++)
                    {
                        int sign = side == 0 ? 1 : -1;
                        Rect strip = FrontageStrip(seg, width, depth, spec.setback, sign);
                        strip = SceneGenRect.ClipTo(strip, site);
                        if (strip.width < 2f || strip.height < 2f) continue;

                        var pieces = new List<Rect> { strip };
                        pieces = SubtractAll(pieces, roadRects);
                        pieces = SubtractAll(pieces, openRects);
                        pieces = SubtractAll(pieces, occupied);

                        for (int p = 0; p < pieces.Count; p++)
                        {
                            Rect piece = pieces[p];
                            bool alongZ = piece.height >= piece.width;
                            float along = alongZ ? piece.height : piece.width;
                            float across = alongZ ? piece.width : piece.height;
                            if (along < 5f || across < 4f) continue;

                            float unitW = Mathf.Max(3f, spec.unitWidth);
                            float gap = Mathf.Max(0f, spec.rowGap);
                            int count = Mathf.Max(1, Mathf.FloorToInt((along + gap) / (unitW + gap)));
                            if (count == 1 && along >= 12f) count = 2;
                            if (count > 30) count = 30;
                            float unitAlong = (along - (count - 1) * gap) / count;
                            if (unitAlong < 2.5f) continue;
                            float unitDepth = Mathf.Min(across, depth);

                            string rowId = "row_" + SafeId(street.name) + "_" + (both ? (sign > 0 ? "A" : "B") : "A")
                                           + "_" + s.ToString(CultureInfo.InvariantCulture);
                            int addedHere = 0;
                            Vector2 pieceCenter = SceneGenRect.Center(piece);
                            for (int k = 0; k < count; k++)
                            {
                                if (items.Count >= MaxItems) return;
                                float alongPos = (alongZ ? piece.yMin : piece.xMin)
                                                 + unitAlong * 0.5f + k * (unitAlong + gap);
                                Vector2 center = alongZ
                                    ? new Vector2(pieceCenter.x, alongPos)
                                    : new Vector2(alongPos, pieceCenter.y);
                                Vector2 size = alongZ
                                    ? new Vector2(unitDepth, unitAlong)
                                    : new Vector2(unitAlong, unitDepth);
                                Vector2 face = alongZ ? new Vector2(-sign, 0f) : new Vector2(0f, -sign);
                                var unit = new LayoutItemSpec
                                {
                                    id = UniqueId("fill_" + rowId + "_" + k.ToString(CultureInfo.InvariantCulture), items),
                                    name = StreetBuildingName(street, k, count),
                                    semantic = "临街" + (StyleWord(street.style)) + "，" + floors + " 层，面向街道",
                                    kind = SlotKind.Building,
                                    center = center,
                                    size = size,
                                    rotation = SceneGenFacing.AngleFromNormal(face),
                                    faceDir = face,
                                    height = height,
                                    primitive = StylePrimitive(street.style),
                                    expectedModelHint = "18th century european street house, " + floors + " storeys",
                                    rowId = rowId,
                                    rowIndex = k,
                                    rowCount = count,
                                    required = true,
                                    source = "sceneGen-fill",
                                    solverGenerated = true,
                                };
                                ClampItemToSite(unit, site);
                                Rect r = SceneGenRect.FromCenter(unit.center, unit.size.x, unit.size.y);
                                if (OverlapAny(r, occupied, 0.35f)) continue;
                                items.Add(unit);
                                occupied.Add(r);
                                report.FillUnits++;
                                addedHere++;
                            }
                            wantTotal += count;
                            madeTotal += addedHere;
                        }
                    }
                }
                if (report.Notes.Count < 12)
                    report.Notes.Add("临街「" + street.name + "」应建 " + wantTotal + " 间 · 实建 " + madeTotal + " 间");
            }
        }

        private static Rect FrontageStrip(SceneGenSegments.Segment seg, float roadWidth, float depth, float setback, int sign)
        {
            float inner = roadWidth * 0.5f + Mathf.Max(0f, setback);
            if (seg.vertical)
            {
                float x0 = sign > 0 ? seg.a.x + inner : seg.a.x - inner - depth;
                return SceneGenRect.FromMinMax(x0, Mathf.Min(seg.a.y, seg.b.y),
                    x0 + depth, Mathf.Max(seg.a.y, seg.b.y));
            }
            float z0 = sign > 0 ? seg.a.y + inner : seg.a.y - inner - depth;
            return SceneGenRect.FromMinMax(Mathf.Min(seg.a.x, seg.b.x), z0,
                Mathf.Max(seg.a.x, seg.b.x), z0 + depth);
        }

        private static List<Rect> SubtractAll(List<Rect> pieces, List<Rect> cutters)
        {
            if (cutters == null || cutters.Count == 0) return pieces;
            List<Rect> current = pieces;
            for (int i = 0; i < cutters.Count && current.Count > 0; i++)
            {
                Rect cutter = cutters[i];
                var next = new List<Rect>(current.Count + 2);
                for (int p = 0; p < current.Count; p++) next.AddRange(SceneGenRect.Subtract(current[p], cutter));
                current = next;
                if (current.Count > 64) break;
            }
            return current;
        }

        private static string StreetBuildingName(LayoutStreetSpec street, int index, int count)
        {
            string baseName = string.IsNullOrEmpty(street.name) ? "临街建筑" : street.name;
            return baseName + "街屋" + (index + 1).ToString(CultureInfo.InvariantCulture)
                   + "/" + count.ToString(CultureInfo.InvariantCulture);
        }

        private static string StyleWord(string style)
        {
            if (string.Equals(style, "shop_row", StringComparison.OrdinalIgnoreCase)) return "店铺";
            if (string.Equals(style, "arcade", StringComparison.OrdinalIgnoreCase)) return "拱廊";
            return "联排住宅";
        }

        private static string StylePrimitive(string style)
        {
            if (string.Equals(style, "shop_row", StringComparison.OrdinalIgnoreCase)) return "shop_house";
            if (string.Equals(style, "arcade", StringComparison.OrdinalIgnoreCase)) return "row_arch";
            return "row_house";
        }

        // ─────────────────────────── 空分区补齐 ───────────────────────────

        private static void FillEmptyZones(
            LayoutSpec spec, List<LayoutItemSpec> items, List<Rect> occupied, Rect site,
            List<Rect> zoneRects, List<Rect> openRects, List<Rect> roadRects, SceneGenRng rng, SceneGenReport report)
        {
            for (int z = 0; z < spec.zones.Count; z++)
            {
                LayoutZoneSpec zone = spec.zones[z];
                if (zone == null || zone.polygon == null) continue;
                Rect zr = SceneGenRect.ClipTo(SceneGenRect.Bounds(zone.polygon), site);
                string kind = (zone.kind ?? "").ToLowerInvariant();
                if (kind == "water") continue;
                if (zone.openness >= 0.85f) continue; // 广场/荒地由道具承担，不建房子

                bool hasItem = false;
                for (int i = 0; i < items.Count && !hasItem; i++)
                    if (SceneGenRect.Contains(zr, items[i].center, 0.5f)) hasItem = true;

                if (kind == "residential" || kind == "church" || kind == "market")
                {
                    // 街区围合：沿分区内缘补一圈连续街屋（留出中心庭院/广场）。
                    // 注意这里不再"只有空分区才补"——只沿街一条线的旧结果是：
                    // 主街两侧有房子、街背后整片空白，俯视像"一条带子贴在空地上"。
                    // 现在街区四个边都尝试补，已被临街/点名地物占掉的位置由重叠检查跳过。
                    // 街区进深随分区尺度自适应：小屋群用浅进深（否则 60×60 的小村镇会被填到 58% 密不透风），
                    // 大城区用标准进深，保证"越小越疏、越大越密"的直觉成立。
                    float depth = Mathf.Clamp(
                        Mathf.Min(spec.unitDepth, Mathf.Min(zr.width, zr.height) * 0.12f), 4.5f, 12f);
                    float inset = 1.5f;
                    var strips = new List<Rect>
                    {
                        SceneGenRect.FromMinMax(zr.xMin + inset, zr.yMin + inset, zr.xMax - inset, zr.yMin + inset + depth),
                        SceneGenRect.FromMinMax(zr.xMin + inset, zr.yMax - inset - depth, zr.xMax - inset, zr.yMax - inset),
                        SceneGenRect.FromMinMax(zr.xMin + inset, zr.yMin + inset + depth, zr.xMin + inset + depth, zr.yMax - inset - depth),
                        SceneGenRect.FromMinMax(zr.xMax - inset - depth, zr.yMin + inset + depth, zr.xMax - inset, zr.yMax - inset - depth),
                    };
                    for (int s = 0; s < strips.Count; s++)
                    {
                        Rect strip = strips[s];
                        if (strip.width < 5f || strip.height < 5f) continue;
                        bool alongZ = strip.height >= strip.width;
                        float along = alongZ ? strip.height : strip.width;
                        float across = alongZ ? strip.width : strip.height;
                        float unitW = Mathf.Max(3f, spec.unitWidth);
                        int count = Mathf.Max(1, Mathf.FloorToInt((along + spec.rowGap) / (unitW + spec.rowGap)));
                        if (count > 26) count = 26;
                        float unitAlong = (along - (count - 1) * spec.rowGap) / count;
                        if (unitAlong < 2.5f) continue;
                        float unitDepth = Mathf.Min(across, depth);
                        string rowId = "row_zone_" + SafeId(zone.description ?? zone.zoneId) + "_" + s.ToString(CultureInfo.InvariantCulture);
                        Vector2 stripCenter = SceneGenRect.Center(strip);
                        for (int k = 0; k < count; k++)
                        {
                            if (items.Count >= MaxItems) return;
                            float alongPos = (alongZ ? strip.yMin : strip.xMin)
                                             + unitAlong * 0.5f + k * (unitAlong + spec.rowGap);
                            Vector2 center = alongZ
                                ? new Vector2(stripCenter.x, alongPos)
                                : new Vector2(alongPos, stripCenter.y);
                            Vector2 size = alongZ ? new Vector2(unitDepth, unitAlong) : new Vector2(unitAlong, unitDepth);
                            // 立面朝外（背离分区中心），保证围合立面都朝街区外侧的街道。
                            Vector2 outward = alongZ
                                ? new Vector2(stripCenter.x >= SceneGenRect.Center(zr).x ? 1f : -1f, 0f)
                                : new Vector2(0f, stripCenter.y >= SceneGenRect.Center(zr).y ? 1f : -1f);
                            var unit = new LayoutItemSpec
                            {
                                id = UniqueId("zone_" + rowId + "_" + k.ToString(CultureInfo.InvariantCulture), items),
                                name = (string.IsNullOrEmpty(zone.description) ? "街区" : zone.description)
                                       + "街屋" + (k + 1).ToString(CultureInfo.InvariantCulture),
                                semantic = "围合" + kind + "街区的连续街屋",
                                kind = SlotKind.Building,
                                center = center,
                                size = size,
                                rotation = SceneGenFacing.AngleFromNormal(outward),
                                faceDir = outward,
                                height = 3f * FloorHeight,
                                primitive = "row_house",
                                expectedModelHint = "18th century european row house",
                                rowId = rowId,
                                rowIndex = k,
                                rowCount = count,
                                required = false,
                                source = "sceneGen-zone",
                                solverGenerated = true,
                            };
                            ClampItemToSite(unit, site);
                            Rect r = SceneGenRect.FromCenter(unit.center, unit.size.x, unit.size.y);
                            if (OverlapAny(r, roadRects, 0.2f)) continue;
                            if (OverlapAny(r, occupied, 0.35f)) continue;
                            items.Add(unit);
                            occupied.Add(r);
                            report.FillUnits++;
                        }
                    }
                }
                else
                {
                    // 荒地/草场：用树/石/栅栏柱填充，保证不空但不像建筑。
                    int count = Mathf.Clamp(Mathf.RoundToInt(zr.width * zr.height / 420f), 3, 14);
                    for (int k = 0; k < count; k++)
                    {
                        Vector2 p = new Vector2(rng.Range(zr.xMin + 2f, zr.xMax - 2f), rng.Range(zr.yMin + 2f, zr.yMax - 2f));
                        if (OverlapAny(SceneGenRect.FromCenter(p, 2.5f, 2.5f), roadRects, 0.2f)) continue;
                        if (OverlapAny(SceneGenRect.FromCenter(p, 2.5f, 2.5f), occupied, 0.2f)) continue;
                        items.Add(new LayoutItemSpec
                        {
                            id = UniqueId("wild_" + SafeId(zone.zoneId) + "_" + k.ToString(CultureInfo.InvariantCulture), items),
                            name = (k % 3 == 0) ? "草丛" : "树",
                            semantic = "场地边缘的自然植被",
                            kind = SlotKind.Decoration,
                            center = p,
                            size = new Vector2(2f, 2f),
                            rotation = 0f,
                            faceDir = Vector2.up,
                            height = 4f,
                            primitive = (k % 3 == 0) ? "grass" : "tree",
                            source = "sceneGen-zone",
                            solverGenerated = true,
                            required = false,
                        });
                        occupied.Add(SceneGenRect.FromCenter(p, 2.5f, 2.5f));
                        report.Props++;
                    }
                }
            }
        }

        // ─────────────────────────── 围合 ───────────────────────────

        private static void EnsureEnclosure(LayoutSpec spec, Rect site, SceneGenReport report)
        {
            if (spec.paths == null) spec.paths = new List<LayoutPathSpec>();
            bool hasWall = false;
            for (int i = 0; i < spec.paths.Count; i++)
            {
                if (spec.paths[i] == null) continue;
                if (string.Equals(spec.paths[i].kind, "wall", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(spec.paths[i].kind, "fence", StringComparison.OrdinalIgnoreCase))
                {
                    hasWall = true;
                    break;
                }
            }
            if (hasWall) return;

            // 街口 = 街道端点贴边处，围墙在这里断开（缺口就是入口）。
            var gaps = new List<Vector2>();
            if (spec.streets != null)
            {
                for (int i = 0; i < spec.streets.Count; i++)
                {
                    LayoutStreetSpec street = spec.streets[i];
                    if (street == null || street.nodes == null || street.nodes.Count < 2) continue;
                    Vector2 a = street.nodes[0];
                    Vector2 b = street.nodes[street.nodes.Count - 1];
                    gaps.Add(a);
                    gaps.Add(b);
                }
            }

            // 街口 = 街道端点贴边处（10 米内视作穿出边界），围墙在这里断开 4.5 米作为门洞。
            AddEdgeWalls(spec, site, true, site.yMin, gaps, 4.5f, "south", report);
            AddEdgeWalls(spec, site, true, site.yMax, gaps, 4.5f, "north", report);
            AddEdgeWalls(spec, site, false, site.xMin, gaps, 4.5f, "west", report);
            AddEdgeWalls(spec, site, false, site.xMax, gaps, 4.5f, "east", report);
        }

        private static void AddEdgeWalls(LayoutSpec spec, Rect site, bool horizontal, float at, List<Vector2> gaps,
            float gapHalf, string tag, SceneGenReport report)
        {
            float min = horizontal ? site.xMin : site.yMin;
            float max = horizontal ? site.xMax : site.yMax;
            var cuts = new List<Vector2>();
            for (int i = 0; i < gaps.Count; i++)
            {
                Vector2 g = gaps[i];
                float value = horizontal ? g.y : g.x;
                if (Mathf.Abs(value - at) > 10f) continue;
                float along = horizontal ? g.x : g.y;
                cuts.Add(new Vector2(along, 0f));
            }
            cuts.Sort((a, b) => a.x.CompareTo(b.x));

            float cursor = min;
            int index = 0;
            var segments = new List<Vector2>();
            for (int i = 0; i < cuts.Count; i++)
            {
                float g0 = Mathf.Max(min, cuts[i].x - gapHalf);
                float g1 = Mathf.Min(max, cuts[i].x + gapHalf);
                if (g0 > cursor + 1f) segments.Add(new Vector2(cursor, g0));
                cursor = Mathf.Max(cursor, g1);
            }
            if (max > cursor + 1f) segments.Add(new Vector2(cursor, max));

            for (int i = 0; i < segments.Count; i++)
            {
                Vector2 a = horizontal ? new Vector2(segments[i].x, at) : new Vector2(at, segments[i].x);
                Vector2 b = horizontal ? new Vector2(segments[i].y, at) : new Vector2(at, segments[i].y);
                if (Vector2.Distance(a, b) < 2f) continue;
                var wall = new LayoutPathSpec
                {
                    pathId = "wall_" + tag + "_" + index.ToString(CultureInfo.InvariantCulture),
                    kind = "wall",
                    width = 1.6f,
                    required = false,
                };
                wall.nodes.Add(a);
                wall.nodes.Add(b);
                spec.paths.Add(wall);
                index++;
            }
            if (index > 0) report.Notes.Add("补充 " + tag + " 边界墙 " + index + " 段");
        }

        // ─────────────────────────── 道具散布 ───────────────────────────

        private static void ScatterZoneProps(
            LayoutSpec spec, List<LayoutItemSpec> items, List<Rect> occupied, Rect site,
            List<Rect> openRects, List<Rect> roadRects, SceneGenRng rng, SceneGenReport report)
        {
            for (int z = 0; z < spec.zones.Count; z++)
            {
                LayoutZoneSpec zone = spec.zones[z];
                if (zone == null || zone.polygon == null) continue;
                string kind = (zone.kind ?? "").ToLowerInvariant();
                Rect zr = SceneGenRect.ClipTo(SceneGenRect.Bounds(zone.polygon), site);
                if (zone.openness < 0.5f) continue;
                if (zr.width < 6f || zr.height < 6f) continue;

                // 路灯：沿分区内缘每 20m 一盏（广场/市场/教堂前）。
                if (kind == "square" || kind == "market" || kind == "church" || kind == "harbor")
                {
                    float inset = 3f;
                    var edge = new List<Vector2>
                    {
                        new Vector2(zr.xMin + inset, zr.yMin + inset),
                        new Vector2(zr.xMax - inset, zr.yMin + inset),
                        new Vector2(zr.xMax - inset, zr.yMax - inset),
                        new Vector2(zr.xMin + inset, zr.yMax - inset),
                    };
                    for (int e = 0; e < edge.Count; e++)
                    {
                        Vector2 a = edge[e];
                        Vector2 b = edge[(e + 1) % edge.Count];
                        float len = Vector2.Distance(a, b);
                        int n = Mathf.Max(1, Mathf.RoundToInt(len / 20f));
                        for (int k = 0; k < n; k++)
                        {
                            float f = (k + 0.5f) / n;
                            Vector2 p = Vector2.Lerp(a, b, f);
                            if (items.Count >= MaxItems) return;
                            if (OverlapAny(SceneGenRect.FromCenter(p, 1.2f, 1.2f), roadRects, 0.1f)) continue;
                            if (OverlapAny(SceneGenRect.FromCenter(p, 1.2f, 1.2f), occupied, 0.1f)) continue;
                            items.Add(MakeProp("路灯", "lamp", p, 0.5f, 4.2f, "18th century street lantern", rng));
                            occupied.Add(SceneGenRect.FromCenter(p, 1.2f, 1.2f));
                            report.Props++;
                        }
                    }
                }

                // 绿化：广场四角种树；草地/荒地成片。
                int treeCount = kind == "field" ? Mathf.Clamp(Mathf.RoundToInt(zr.width * zr.height / 300f), 3, 18)
                                               : Mathf.Clamp(Mathf.RoundToInt(zr.width * zr.height / 900f), 2, 10);
                for (int k = 0; k < treeCount; k++)
                {
                    if (items.Count >= MaxItems) return;
                    Vector2 p = new Vector2(rng.Range(zr.xMin + 2f, zr.xMax - 2f), rng.Range(zr.yMin + 2f, zr.yMax - 2f));
                    if (OverlapAny(SceneGenRect.FromCenter(p, 2.2f, 2.2f), roadRects, 0.1f)) continue;
                    if (OverlapAny(SceneGenRect.FromCenter(p, 2.2f, 2.2f), occupied, 0.1f)) continue;
                    items.Add(MakeProp("树", "tree", p, 2.2f, rng.Range(4.5f, 7.5f), "plane tree", rng));
                    occupied.Add(SceneGenRect.FromCenter(p, 2.2f, 2.2f));
                    report.Props++;
                }

                // 人群：广场/市场中心附近的聚集（体现"抗议人群/市集人流"）。
                if (kind == "square" || kind == "market")
                {
                    string semantic = (zone.description ?? "") + (zone.material ?? "");
                    int crowd = semantic.IndexOf("广场", StringComparison.Ordinal) >= 0 || semantic.IndexOf("市", StringComparison.Ordinal) >= 0
                        ? rng.RangeInt(4, 9) : 0;
                    for (int k = 0; k < crowd; k++)
                    {
                        float angle = rng.Range(0f, Mathf.PI * 2f);
                        float radius = rng.Range(6f, Mathf.Min(24f, Mathf.Min(zr.width, zr.height) * 0.35f));
                        Vector2 p = SceneGenRect.Center(zr) + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
                        if (!SceneGenRect.Contains(zr, p, -2f)) continue;
                        if (OverlapAny(SceneGenRect.FromCenter(p, 1.6f, 1.6f), occupied, 0.05f)) continue;
                        items.Add(MakeProp("人群", "crowd", p, 1.6f, 1.7f, "crowd of townspeople", rng));
                        occupied.Add(SceneGenRect.FromCenter(p, 1.6f, 1.6f));
                        report.Props++;
                    }
                }
            }
        }

        private static LayoutItemSpec MakeProp(string name, string primitive, Vector2 center, float size, float height,
            string hint, SceneGenRng rng)
        {
            return new LayoutItemSpec
            {
                id = "",
                name = name,
                semantic = name + "（场景填充）",
                kind = SlotKind.Decoration,
                center = center,
                size = new Vector2(size, size),
                rotation = rng.Range(0f, 360f),
                faceDir = new Vector2(0f, 1f),
                height = height,
                primitive = primitive,
                expectedModelHint = string.IsNullOrEmpty(hint) ? name : hint,
                required = false,
                solverGenerated = true,
                source = "sceneGen-prop",
            };
        }

        // ─────────────────────────── 剧情单位 ───────────────────────────

        private static void AddStoryUnits(
            SceneSpec scene, LayoutSpec spec, List<LayoutItemSpec> items, List<Rect> occupied, Rect site,
            List<Rect> zoneRects, List<Rect> roadRects, SceneGenRng rng, SceneGenReport report)
        {
            // 先登记模型给的 NPC 动线，保证同一 NPC 只落一次。
            if (spec.npcs == null) spec.npcs = new List<LayoutNpcSpec>();

            if (scene.anchors != null)
            {
                for (int i = 0; i < scene.anchors.Count; i++)
                {
                    SceneAnchorSpec anchor = scene.anchors[i];
                    if (anchor == null) continue;
                    Vector2 position;
                    string bound = FindAnchorBinding(spec, anchor.displayName);
                    LayoutItemSpec carrier = FindCarrier(items, anchor.displayName + " " + anchor.requirement, bound);
                    if (carrier != null)
                    {
                        // 立面朝向决定"正前方"的半深：朝北的教堂前方是 depth/2，不是 max(w,d)/2。
                        Vector2 face = carrier.faceDir.sqrMagnitude < 0.01f ? new Vector2(0f, -1f) : carrier.faceDir;
                        float front = Mathf.Abs(face.x) * carrier.size.x * 0.5f
                                      + Mathf.Abs(face.y) * carrier.size.y * 0.5f;
                        position = carrier.center + face * (front + 1.6f);
                        report.AnchorsBound++;
                    }
                    else
                    {
                        position = ResolveFallbackPoint(site, zoneRects, occupied, anchor.localPosition);
                        report.AnchorsUnbound++;
                        spec.gaps.Add("锚点「" + anchor.displayName + "」没有可绑定的载体地标，已落在场地空处");
                    }
                    position = ClampPoint(position, site, 1.2f);
                    anchor.localPosition = new Vector3(position.x, 0f, position.y);

                    items.Add(new LayoutItemSpec
                    {
                        id = "anchor_" + SafeId(anchor.anchorId),
                        name = string.IsNullOrEmpty(anchor.displayName) ? anchor.anchorId : anchor.displayName + "（锚点）",
                        semantic = "锚点：" + (anchor.description ?? ""),
                        kind = SlotKind.Anchor,
                        center = position,
                        size = new Vector2(2.4f, 2.4f),
                        rotation = 0f,
                        faceDir = new Vector2(0f, 1f),
                        height = 2.4f,
                        primitive = "anchor",
                        expectedModelHint = anchor.modelHint,
                        bindId = anchor.anchorId,
                        required = true,
                        source = "anchor",
                        solverGenerated = false,
                        notes = carrier != null ? "载体=" + carrier.name : "无载体",
                    });
                }
            }

            if (scene.npcs != null)
            {
                for (int i = 0; i < scene.npcs.Count; i++)
                {
                    NpcSpec npc = scene.npcs[i];
                    if (npc == null) continue;
                    LayoutNpcSpec placed = FindNpc(spec, npc);
                    Vector2 position;
                    if (placed != null && placed.waypoints != null && placed.waypoints.Count > 0)
                    {
                        position = placed.waypoints[0];
                    }
                    else
                    {
                        position = ResolveFallbackPoint(site, zoneRects, occupied, npc.localPosition);
                        if (placed == null)
                        {
                            placed = new LayoutNpcSpec { npcId = npc.npcId, name = npc.displayName };
                            spec.npcs.Add(placed);
                        }
                        placed.waypoints = new List<Vector2>
                        {
                            position,
                            position + new Vector2(2.5f, 0f),
                            position + new Vector2(2.5f, 2.5f),
                            position + new Vector2(0f, 2.5f),
                        };
                    }
                    npc.localPosition = new Vector3(position.x, 0f, position.y);
                    items.Add(new LayoutItemSpec
                    {
                        id = "npc_" + SafeId(npc.npcId),
                        name = string.IsNullOrEmpty(npc.displayName) ? npc.npcId : npc.displayName,
                        semantic = string.IsNullOrEmpty(npc.persona) ? npc.displayName : npc.displayName + "，" + npc.persona,
                        kind = SlotKind.Npc,
                        center = position,
                        size = new Vector2(0.6f, 0.6f),
                        rotation = 0f,
                        faceDir = new Vector2(0f, 1f),
                        height = 1.75f,
                        primitive = "npc",
                        expectedModelHint = "",
                        bindId = npc.npcId,
                        required = true,
                        source = "npc",
                    });
                }
            }

            if (scene.props != null)
            {
                for (int i = 0; i < scene.props.Count; i++)
                {
                    PropInteraction prop = scene.props[i];
                    if (prop == null) continue;
                    Vector2 position = ResolveFallbackPoint(site, zoneRects, occupied, prop.localPosition);
                    prop.localPosition = new Vector3(position.x, 0f, position.y);
                    items.Add(new LayoutItemSpec
                    {
                        id = "prop_" + SafeId(prop.propId),
                        name = string.IsNullOrEmpty(prop.displayName) ? prop.propId : prop.displayName,
                        semantic = string.IsNullOrEmpty(prop.description) ? prop.displayName : prop.displayName + "，" + prop.description,
                        kind = SlotKind.Prop,
                        center = position,
                        size = new Vector2(2f, 2f),
                        rotation = 0f,
                        faceDir = new Vector2(0f, 1f),
                        height = 1.4f,
                        primitive = "prop",
                        expectedModelHint = "",
                        bindId = prop.propId,
                        required = true,
                        source = "prop",
                    });
                }
            }
        }

        private static LayoutItemSpec FindCarrier(List<LayoutItemSpec> items, string text, string boundLandmark)
        {
            // ① 模型用 anchorFor 明确绑定过的地标，最可信。
            if (!string.IsNullOrEmpty(boundLandmark))
            {
                var byBinding = FindByName(items, boundLandmark);
                if (byBinding != null && byBinding.kind == SlotKind.Building) return byBinding;
            }

            string key = Normalize(text);
            if (key.Length == 0) return null;
            LayoutItemSpec best = null;
            int bestScore = 0;
            for (int i = 0; i < items.Count; i++)
            {
                LayoutItemSpec item = items[i];
                if (item == null || item.kind != SlotKind.Building) continue;
                string name = Normalize(item.name);
                if (name.Length == 0) continue;
                int score = 0;
                if (name == key) score = 1000 + name.Length;
                else if (key.StartsWith(name, StringComparison.Ordinal)) score = name.Length * 4;
                else if (key.IndexOf(name, StringComparison.Ordinal) >= 0) score = name.Length * 2;
                else
                {
                    // ② 别名匹配：「去教堂的马车 / 圣马丁教堂」共享「教堂」；
                    //    「市政厅侧门 / 市政厅」「出城的城门 / 南城门」同理。
                    //    取最长公共子串，≥2 字才算命中（中文地标名几乎都 ≥2 字）。
                    int common = LongestCommonSubstring(name, key);
                    if (common >= 2) score = common * 3;
                }
                if (score > bestScore) { bestScore = score; best = item; }
            }
            return best;
        }

        private static int LongestCommonSubstring(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;
            int best = 0;
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (int i = 1; i <= a.Length; i++)
            {
                for (int j = 1; j <= b.Length; j++)
                {
                    cur[j] = a[i - 1] == b[j - 1] ? prev[j - 1] + 1 : 0;
                    if (cur[j] > best) best = cur[j];
                }
                var swap = prev; prev = cur; cur = swap;
                for (int j = 0; j <= b.Length; j++) cur[j] = 0;
            }
            return best;
        }

        private static string FindAnchorBinding(LayoutSpec spec, string anchorName)
        {
            if (spec.landmarks == null || string.IsNullOrEmpty(anchorName)) return "";
            for (int i = 0; i < spec.landmarks.Count; i++)
            {
                LayoutLandmarkSpec landmark = spec.landmarks[i];
                if (landmark == null || string.IsNullOrEmpty(landmark.anchorBinding)) continue;
                if (landmark.anchorBinding.IndexOf(anchorName, StringComparison.Ordinal) >= 0) return landmark.name;
            }
            return "";
        }

        private static LayoutNpcSpec FindNpc(LayoutSpec spec, NpcSpec npc)
        {
            if (spec.npcs == null) return null;
            for (int i = 0; i < spec.npcs.Count; i++)
            {
                LayoutNpcSpec placed = spec.npcs[i];
                if (placed == null) continue;
                if (!string.IsNullOrEmpty(npc.npcId) && placed.npcId == npc.npcId) return placed;
                if (!string.IsNullOrEmpty(npc.displayName) && !string.IsNullOrEmpty(placed.name)
                    && (placed.name.IndexOf(npc.displayName, StringComparison.Ordinal) >= 0
                        || npc.displayName.IndexOf(placed.name, StringComparison.Ordinal) >= 0))
                    return placed;
            }
            return null;
        }

        private static Vector2 ResolveFallbackPoint(Rect site, List<Rect> zoneRects, List<Rect> occupied, Vector3 preferred)
        {
            var p = new Vector2(preferred.x, preferred.z);
            if (p.sqrMagnitude > 0.01f && SceneGenRect.Contains(site, p, -1f)
                && !OverlapAny(SceneGenRect.FromCenter(p, 1.6f, 1.6f), occupied, -0.2f))
                return p;

            // 网格扫描：找离场地中心最近的一块空地（不压建筑、不出场地）。
            // 旧链路这里会直接落到 (0,0)，于是所有无载体锚点/NPC 叠在广场正中——"混乱"的显性来源之一。
            Vector2 center = SceneGenRect.Center(site);
            Vector2 best = center;
            float bestScore = float.MaxValue;
            const float step = 4f;
            for (float x = site.xMin + 2f; x <= site.xMax - 2f; x += step)
            {
                for (float z = site.yMin + 2f; z <= site.yMax - 2f; z += step)
                {
                    var candidate = new Vector2(x, z);
                    var probe = SceneGenRect.FromCenter(candidate, 2.4f, 2.4f);
                    if (OverlapAny(probe, occupied, 0.05f)) continue;
                    float score = Vector2.Distance(candidate, center);
                    if (score < bestScore) { bestScore = score; best = candidate; }
                }
            }
            return best;
        }

        // ─────────────────────────── KPI / 门禁 ───────────────────────────

        private class SceneGenGeometryState
        {
            public Rect Site;
            public Rect[] RectOf;

            public SceneGenGeometryState(LayoutSpec spec, Rect site)
            {
                Site = site;
                var items = spec.items ?? new List<LayoutItemSpec>();
                RectOf = new Rect[items.Count];
                for (int i = 0; i < items.Count; i++)
                {
                    LayoutItemSpec item = items[i];
                    RectOf[i] = item == null
                        ? new Rect(0f, 0f, 0f, 0f)
                        : SceneGenRect.FromCenter(item.center, Mathf.Max(0.1f, item.size.x), Mathf.Max(0.1f, item.size.y));
                }
            }
        }

        private static void DropBadSolverUnits(LayoutSpec spec, SceneGenGeometryState geo, SceneGenReport report)
        {
            var items = spec.items;
            if (items == null) return;
            var keep = new bool[items.Count];
            for (int i = 0; i < items.Count; i++) keep[i] = true;

            for (int i = 0; i < items.Count; i++)
            {
                LayoutItemSpec a = items[i];
                if (a == null || !a.solverGenerated || a.kind != SlotKind.Building) continue;
                Rect ra = geo.RectOf[i];
                for (int j = 0; j < items.Count; j++)
                {
                    if (i == j) continue;
                    LayoutItemSpec b = items[j];
                    if (b == null || b.kind != SlotKind.Building || !keep[j]) continue;
                    Rect rb = geo.RectOf[j];
                    float overlap = SceneGenRect.OverlapArea(ra, rb);
                    float minArea = Mathf.Min(ra.width * ra.height, rb.width * rb.height);
                    if (minArea <= 0f || overlap / minArea < 0.25f) continue;
                    // 丢弃由求解器生成的那个；点名物永远保留。
                    if (a.solverGenerated && !b.solverGenerated) { keep[i] = false; report.Dropped++; break; }
                    if (b.solverGenerated) { keep[j] = false; report.Dropped++; }
                }
            }

            var filtered = new List<LayoutItemSpec>(items.Count);
            for (int i = 0; i < items.Count; i++) if (keep[i] && items[i] != null) filtered.Add(items[i]);
            spec.items = filtered;
        }

        private static void NormalizeAnchors(LayoutSpec spec, SceneGenGeometryState geo, SceneGenReport report)
        {
            if (spec.items == null) return;
            for (int i = 0; i < spec.items.Count; i++)
            {
                LayoutItemSpec item = spec.items[i];
                if (item == null || item.kind != SlotKind.Anchor) continue;
                // 锚点不能埋在建筑里：与建筑重叠时推到建筑外缘。
                for (int j = 0; j < spec.items.Count; j++)
                {
                    if (i == j) continue;
                    LayoutItemSpec b = spec.items[j];
                    if (b == null || b.kind != SlotKind.Building) continue;
                    Rect rb = SceneGenRect.FromCenter(b.center, b.size.x, b.size.y);
                    if (!SceneGenRect.Contains(rb, item.center, 0.2f)) continue;
                    Vector2 face = b.faceDir.sqrMagnitude < 0.01f ? new Vector2(0f, -1f) : b.faceDir;
                    item.center = b.center + face * (Mathf.Max(b.size.x, b.size.y) * 0.5f + 1.6f);
                    break;
                }
            }
        }

        private static void Measure(SceneSpec scene, LayoutSpec spec, SceneGenGeometryState geo, SceneGenReport report)
        {
            var items = spec.items ?? new List<LayoutItemSpec>();
            report.Items = items.Count;
            float siteArea = Mathf.Max(1f, geo.Site.width * geo.Site.height);

            float footprint = 0f;
            int buildingCount = 0;
            var zoneRects = new List<Rect>();
            if (spec.zones != null)
                for (int i = 0; i < spec.zones.Count; i++)
                    if (spec.zones[i] != null && spec.zones[i].polygon != null && spec.zones[i].polygon.Count >= 3)
                        zoneRects.Add(SceneGenRect.Bounds(spec.zones[i].polygon));

            for (int i = 0; i < items.Count; i++)
            {
                LayoutItemSpec item = items[i];
                if (item == null) continue;
                if (item.kind == SlotKind.Building) { footprint += item.size.x * item.size.y; buildingCount++; }
            }
            report.FootprintRatio = Mathf.Clamp01(footprint / siteArea);

            // 临街连续度：沿街每 2 米采样，只有"本该有立面"的区段才计入分母。
            // 广场/水面（openness ≥ 0.75）穿过的街段本来就不该砌房子，不能算缺口——
            // 旧口径把广场中央也算进"需要的街墙"，于是正常布局也会被判成连续度不足。
            var openRects = new List<Rect>();
            if (spec.zones != null)
                for (int i = 0; i < spec.zones.Count; i++)
                {
                    LayoutZoneSpec zone = spec.zones[i];
                    if (zone == null || zone.polygon == null || zone.polygon.Count < 3) continue;
                    if (zone.openness < 0.75f) continue;
                    openRects.Add(SceneGenRect.Bounds(zone.polygon));
                }

            float requiredLength = 0f;
            float coveredLength = 0f;
            if (spec.streets != null)
            {
                for (int s = 0; s < spec.streets.Count; s++)
                {
                    LayoutStreetSpec street = spec.streets[s];
                    if (street == null || street.nodes == null) continue;
                    if (string.Equals(street.style, "none", StringComparison.OrdinalIgnoreCase)) continue;
                    List<SceneGenSegments.Segment> segs = SceneGenSegments.Split(street.nodes);
                    for (int i = 0; i < segs.Count; i++)
                    {
                        SceneGenSegments.Segment seg = segs[i];
                        if (!seg.AxisAligned) continue;
                        int sides = street.frontageBoth ? 2 : 1;
                        for (int side = 0; side < sides; side++)
                        {
                            int sign = side == 0 ? 1 : -1;
                            float halfDepth = 24f;   // 允许的最大进深（含退线），超过就不算这条街的立面
                            int steps = Mathf.Max(1, Mathf.CeilToInt(seg.Length / 2f));
                            float stepLength = seg.Length / steps;
                            for (int step = 0; step < steps; step++)
                            {
                                float t = (step + 0.5f) / steps;
                                Vector2 point = Vector2.Lerp(seg.a, seg.b, t);
                                bool inOpenZone = false;
                                for (int o = 0; o < openRects.Count && !inOpenZone; o++)
                                    if (SceneGenRect.Contains(openRects[o], point, 0f)) inOpenZone = true;
                                if (inOpenZone) continue;

                                requiredLength += stepLength;
                                float alongPoint = seg.vertical ? point.y : point.x;
                                bool covered = false;
                                for (int k = 0; k < items.Count && !covered; k++)
                                {
                                    LayoutItemSpec item = items[k];
                                    if (item == null || item.kind != SlotKind.Building) continue;
                                    float offset = seg.vertical ? item.center.x - seg.a.x : item.center.y - seg.a.y;
                                    if (offset * sign <= 0f) continue; // 必须在采样点所在的这一侧
                                    if (Mathf.Abs(offset) > street.width * 0.5f + halfDepth) continue;
                                    float frontage = seg.vertical ? item.size.y : item.size.x;
                                    float alongCenter = seg.vertical ? item.center.y : item.center.x;
                                    if (Mathf.Abs(alongCenter - alongPoint) <= frontage * 0.5f + 0.01f) covered = true;
                                }
                                if (covered) coveredLength += stepLength;
                            }
                        }
                    }
                }
            }
            report.FrontageContinuity = requiredLength > 0.01f ? Mathf.Clamp01(coveredLength / requiredLength) : 1f;

            // 围合：边界被墙覆盖的比例。
            float perimeter = 2f * (geo.Site.width + geo.Site.height);
            float wallLength = 0f;
            if (spec.paths != null)
            {
                for (int i = 0; i < spec.paths.Count; i++)
                {
                    LayoutPathSpec path = spec.paths[i];
                    if (path == null || path.nodes == null || path.nodes.Count < 2) continue;
                    if (!string.Equals(path.kind, "wall", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(path.kind, "fence", StringComparison.OrdinalIgnoreCase)) continue;
                    bool onEdge = true;
                    for (int n = 0; n < path.nodes.Count; n++)
                    {
                        Vector2 p = path.nodes[n];
                        bool edge = Mathf.Abs(p.x - geo.Site.xMin) < 2f || Mathf.Abs(p.x - geo.Site.xMax) < 2f
                                    || Mathf.Abs(p.y - geo.Site.yMin) < 2f || Mathf.Abs(p.y - geo.Site.yMax) < 2f;
                        if (!edge) { onEdge = false; break; }
                    }
                    if (!onEdge) continue;
                    for (int n = 0; n + 1 < path.nodes.Count; n++)
                        wallLength += Vector2.Distance(path.nodes[n], path.nodes[n + 1]);
                }
            }
            report.EnclosureRatio = Mathf.Clamp01(wallLength / Mathf.Max(1f, perimeter));

            // 冲突：建筑互相重叠（含点名物之间的擦碰，逐个列出）。
            int overlaps = 0;
            for (int i = 0; i < items.Count; i++)
            {
                LayoutItemSpec a = items[i];
                if (a == null || a.kind != SlotKind.Building) continue;
                for (int j = i + 1; j < items.Count; j++)
                {
                    LayoutItemSpec b = items[j];
                    if (b == null || b.kind != SlotKind.Building) continue;
                    float overlap = SceneGenRect.OverlapArea(
                        SceneGenRect.FromCenter(a.center, a.size.x, a.size.y),
                        SceneGenRect.FromCenter(b.center, b.size.x, b.size.y));
                    if (overlap <= 1f) continue;
                    overlaps++;
                    if (report.Notes.Count < 24)
                        report.Notes.Add("重叠：" + a.name + " × " + b.name + " "
                                         + overlap.ToString("0", CultureInfo.InvariantCulture) + "㎡");
                }
            }
            report.Overlaps = overlaps;

            // 出界 / 空地分区
            int outOfZone = 0;
            for (int i = 0; i < items.Count; i++)
            {
                LayoutItemSpec item = items[i];
                if (item == null) continue;
                bool inside = false;
                for (int z = 0; z < zoneRects.Count && !inside; z++)
                    if (SceneGenRect.Contains(zoneRects[z], item.center, 0.2f)) inside = true;
                if (!inside) outOfZone++;
            }
            report.OutOfZone = outOfZone;

            int empty = 0;
            for (int z = 0; z < spec.zones.Count; z++)
            {
                LayoutZoneSpec zone = spec.zones[z];
                if (zone == null || zone.polygon == null) continue;
                if (zone.openness >= 0.85f) continue; // 广场/荒地本来就该空旷
                Rect zr = SceneGenRect.Bounds(zone.polygon);
                bool has = false;
                for (int i = 0; i < items.Count && !has; i++)
                    if (items[i] != null && SceneGenRect.Contains(zr, items[i].center, 0.5f)) has = true;
                if (!has) { empty++; if (report.Notes.Count < 24) report.Notes.Add("空地分区：" + zone.description); }
            }
            report.EmptyZones = empty;

            if (EpicSparkFeatureFlags.SceneGenGate)
            {
                if (report.FrontageContinuity < 0.5f)
                    report.Notes.Add("临街连续度偏低（" + report.FrontageContinuity.ToString("0%", CultureInfo.InvariantCulture)
                                     + "）：街道两侧缺少连续街墙");
                if (report.EmptyZones > 0)
                    report.Notes.Add("仍有 " + report.EmptyZones + " 个非开敞分区没有任何地物");
                if (report.Overlaps > 0)
                    report.Notes.Add("存在 " + report.Overlaps + " 组建筑重叠，需要检查点名物之间的占地");
            }
        }

        // ─────────────────────────── 小工具 ───────────────────────────

        private static void ClampPinned(LayoutItemSpec item, Rect site)
        {
            if (item.size.x < 0.2f) item.size.x = 2f;
            if (item.size.y < 0.2f) item.size.y = 2f;
            ClampItemToSite(item, site);
        }

        private static void ClampItemToSite(LayoutItemSpec item, Rect site)
        {
            float hx = Mathf.Min(item.size.x * 0.5f, site.width * 0.5f);
            float hz = Mathf.Min(item.size.y * 0.5f, site.height * 0.5f);
            item.center = new Vector2(
                Mathf.Clamp(item.center.x, site.xMin + hx, site.xMax - hx),
                Mathf.Clamp(item.center.y, site.yMin + hz, site.yMax - hz));
        }

        private static Vector2 ClampCenter(Vector2 center, float width, float depth, Rect site)
        {
            float hx = Mathf.Min(width * 0.5f, site.width * 0.5f);
            float hz = Mathf.Min(depth * 0.5f, site.height * 0.5f);
            return new Vector2(
                Mathf.Clamp(center.x, site.xMin + hx, site.xMax - hx),
                Mathf.Clamp(center.y, site.yMin + hz, site.yMax - hz));
        }

        private static Vector2 ClampPoint(Vector2 point, Rect site, float margin)
        {
            return new Vector2(
                Mathf.Clamp(point.x, site.xMin + margin, site.xMax - margin),
                Mathf.Clamp(point.y, site.yMin + margin, site.yMax - margin));
        }

        private static bool OverlapAny(Rect rect, List<Rect> others, float minArea)
        {
            if (others == null) return false;
            for (int i = 0; i < others.Count; i++)
            {
                if (SceneGenRect.OverlapArea(rect, others[i]) > minArea) return true;
            }
            return false;
        }

        private static bool TryFindZoneRect(LayoutSpec spec, string name, List<Rect> rects, out Rect rect)
        {
            rect = new Rect(0f, 0f, 0f, 0f);
            if (string.IsNullOrEmpty(name)) return false;
            // on 的写法是「分区名 + 方位」，用最长的分区名命中，避免「中央广场」被「广场」抢走。
            string key = Normalize(name);
            int bestLength = 0;
            for (int i = 0; i < spec.zones.Count && i < rects.Count; i++)
            {
                LayoutZoneSpec zone = spec.zones[i];
                if (zone == null) continue;
                string raw = zone.description ?? "";
                string zoneName = Normalize(raw);
                if (zoneName.Length == 0) continue;
                bool hit = key.IndexOf(zoneName, StringComparison.Ordinal) >= 0
                           || zoneName.IndexOf(key, StringComparison.Ordinal) >= 0;
                if (!hit) continue;
                if (zoneName.Length <= bestLength) continue;
                bestLength = zoneName.Length;
                rect = rects[i];
            }
            return bestLength > 0;
        }

        /// <summary>道路是否"穿过"这块占地（两端都伸出占地之外）；只在端头搭接的不算冲突。</summary>
        private static bool RoadCrosses(Rect rect, List<Rect> roads)
        {
            if (roads == null) return false;
            for (int i = 0; i < roads.Count; i++)
            {
                Rect road = roads[i];
                if (SceneGenRect.OverlapArea(rect, road) <= 0.5f) continue;
                bool vertical = road.height >= road.width;
                if (vertical)
                {
                    if (road.yMin < rect.yMin - 2f && road.yMax > rect.yMax + 2f) return true;
                }
                else
                {
                    if (road.xMin < rect.xMin - 2f && road.xMax > rect.xMax + 2f) return true;
                }
            }
            return false;
        }

        /// <summary>把 on 里的分区名剥掉，只留方位词（避免「中央广场 北侧」被"中央"误判）。</summary>
        private static string StripZoneNames(LayoutSpec spec, string on)
        {
            string rest = on ?? "";
            if (spec.zones == null) return rest;
            for (int i = 0; i < spec.zones.Count; i++)
            {
                LayoutZoneSpec zone = spec.zones[i];
                if (zone == null || string.IsNullOrEmpty(zone.description)) continue;
                rest = rest.Replace(zone.description, " ");
            }
            return rest;
        }

        /// <summary>把地标贴到分区某条边的内侧中点（on 支持 north/south/east/west 字样）。</summary>
        private static Vector2 ZoneEdgePoint(Rect zone, Vector2 facing, float width, float depth, string on)
        {
            string key = on ?? "";
            if (key.IndexOf("中央", StringComparison.Ordinal) >= 0 || key.IndexOf("中心", StringComparison.Ordinal) >= 0)
                return SceneGenRect.Center(zone);
            if (key.IndexOf("北", StringComparison.Ordinal) >= 0)
                return new Vector2(SceneGenRect.Center(zone).x, zone.yMax - depth * 0.5f - 2f);
            if (key.IndexOf("南", StringComparison.Ordinal) >= 0)
                return new Vector2(SceneGenRect.Center(zone).x, zone.yMin + depth * 0.5f + 2f);
            if (key.IndexOf("东", StringComparison.Ordinal) >= 0)
                return new Vector2(zone.xMax - width * 0.5f - 2f, SceneGenRect.Center(zone).y);
            if (key.IndexOf("西", StringComparison.Ordinal) >= 0)
                return new Vector2(zone.xMin + width * 0.5f + 2f, SceneGenRect.Center(zone).y);
            // 没写方位：按立面法线推到该方向的分区边。
            if (Mathf.Abs(facing.y) >= Mathf.Abs(facing.x))
                return new Vector2(SceneGenRect.Center(zone).x,
                    facing.y >= 0f ? zone.yMin + depth * 0.5f + 2f : zone.yMax - depth * 0.5f - 2f);
            return new Vector2(facing.x >= 0f ? zone.xMin + width * 0.5f + 2f : zone.xMax - width * 0.5f - 2f,
                SceneGenRect.Center(zone).y);
        }

        private static LayoutItemSpec FindByName(List<LayoutItemSpec> items, string name)
        {
            string key = Normalize(name);
            for (int i = 0; i < items.Count; i++)
            {
                LayoutItemSpec item = items[i];
                if (item == null) continue;
                if (Normalize(item.name) == key) return item;
            }
            return null;
        }

        private static string UniqueId(string baseId, List<LayoutItemSpec> items)
        {
            string id = string.IsNullOrEmpty(baseId) ? "item" : baseId;
            int n = 1;
            while (ContainsId(items, id))
            {
                n++;
                id = baseId + "_" + n.ToString(CultureInfo.InvariantCulture);
            }
            return id;
        }

        private static bool ContainsId(List<LayoutItemSpec> items, string id)
        {
            for (int i = 0; i < items.Count; i++)
                if (items[i] != null && items[i].id == id) return true;
            return false;
        }

        private static string SafeId(string text)
        {
            if (string.IsNullOrEmpty(text)) return "x";
            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length && sb.Length < 20; i++)
            {
                char c = text[i];
                if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
                else sb.Append('_');
            }
            return sb.Length == 0 ? "x" : sb.ToString();
        }

        private static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == ' ' || c == '\u3000' || c == '_' || c == '-' || c == '·' || c == '，' || c == ','
                    || c == ':' || c == '：' || c == '（' || c == '）' || c == '(' || c == ')' || c == '、'
                    || c == '。' || c == '；' || c == ';') continue;
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static int StableHash(string text)
        {
            if (string.IsNullOrEmpty(text)) return 1;
            unchecked
            {
                int hash = 17;
                for (int i = 0; i < text.Length; i++) hash = hash * 31 + text[i];
                return hash == 0 ? 1 : hash;
            }
        }
    }
}
