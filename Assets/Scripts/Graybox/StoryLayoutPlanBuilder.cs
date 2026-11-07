// 把「剧情布局解析结果」变成灰盒规划：区域、道路、语义槽全部按剧情给的坐标来。
// 这是"像人类做地编"的关键一步：剧情 Agent 已经把市政厅放在北侧、城门放在东边界、
// 街道中心线 Z=+2，这里必须照着摆，而不是按 archetype 重新随机铺一遍。
using System;
using System.Collections.Generic;
using System.Globalization;
using EpicSpark.Core;
using EpicSpark.Story;
using UnityEngine;

namespace EpicSpark.Graybox
{
    public static class StoryLayoutPlanBuilder
    {
        /// <summary>
        /// 尝试用剧情布局生成规划。解析不出足够信息时返回 false，调用方退回 archetype 规划。
        /// </summary>
        public static bool TryBuild(SceneSpec scene, GrayboxRequest request, GrayboxPlan plan, out string note)
        {
            note = "";
            if (scene == null || plan == null) return false;

            StoryLayout layout = StoryLayoutParser.Parse(scene.layoutDescription);
            if (!layout.Usable) return false;

            // ── 1) 场景尺寸：覆盖所有地标/分区，留 4 米边距，夹在 30..160 ──
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            for (int i = 0; i < layout.items.Count; i++)
            {
                LayoutItem item = layout.items[i];
                minX = Mathf.Min(minX, item.center.x - item.width * 0.5f);
                maxX = Mathf.Max(maxX, item.center.x + item.width * 0.5f);
                minZ = Mathf.Min(minZ, item.center.y - item.depth * 0.5f);
                maxZ = Mathf.Max(maxZ, item.center.y + item.depth * 0.5f);
            }
            for (int i = 0; i < layout.zones.Count; i++)
            {
                LayoutZone zone = layout.zones[i];
                minX = Mathf.Min(minX, zone.minX);
                maxX = Mathf.Max(maxX, zone.maxX);
                minZ = Mathf.Min(minZ, zone.minZ);
                maxZ = Mathf.Max(maxZ, zone.maxZ);
            }
            for (int i = 0; i < layout.paths.Count; i++)
            {
                LayoutPath path = layout.paths[i];
                for (int n = 0; n < path.nodes.Count; n++)
                {
                    minX = Mathf.Min(minX, path.nodes[n].x);
                    maxX = Mathf.Max(maxX, path.nodes[n].x);
                    minZ = Mathf.Min(minZ, path.nodes[n].y);
                    maxZ = Mathf.Max(maxZ, path.nodes[n].y);
                }
            }
            if (minX > maxX || minZ > maxZ)
            {
                minX = -30f; maxX = 30f; minZ = -30f; maxZ = 30f;
            }

            // 剧情&核心 Agent 现在可以自定义场地大小：优先用场景里给的 width/length。
            // 但如果布局坐标比它更大，就自动扩到装得下——不让建筑被夹在边界外。
            float needW = maxX - minX;
            float needL = maxZ - minZ;
            float storyW = scene.width;
            float storyL = scene.length;
            bool storySized = storyW > 20f && storyL > 20f;

            float width = Mathf.Clamp(storySized ? Mathf.Max(storyW, needW) : needW + 8f, 30f, 400f);
            float length = Mathf.Clamp(storySized ? Mathf.Max(storyL, needL) : needL + 8f, 30f, 400f);
            float centerX = (minX + maxX) * 0.5f;
            float centerZ = (minZ + maxZ) * 0.5f;

            plan.width = width;
            plan.length = length;
            float halfW = width * 0.5f;
            float halfL = length * 0.5f;

            // 布局坐标以原点为基准；若整体偏出场景框，统一平移回来
            Vector2 shift = Vector2.zero;
            if (Mathf.Abs(centerX) > halfW * 0.5f || Mathf.Abs(centerZ) > halfL * 0.5f)
                shift = new Vector2(-centerX * 0.5f, -centerZ * 0.5f);

            // ── 2) 出生点 ──
            Vector2 spawn = new Vector2(scene.spawnPoint.x, scene.spawnPoint.z);
            if (spawn == Vector2.zero) spawn = new Vector2(shift.x, -halfL + 5f);
            plan.spawnPoint = ClampToBounds(spawn, halfW, halfL);

            // ── 3) 地面分区：解析出来的优先，否则铺一块覆盖全场的广场 ──
            plan.regions = new List<GrayboxRegion>();
            for (int i = 0; i < layout.zones.Count; i++)
            {
                LayoutZone zone = layout.zones[i];
                plan.regions.Add(new GrayboxRegion
                {
                    regionId = "region_" + zone.kind + "_" + i.ToString(CultureInfo.InvariantCulture),
                    kind = zone.kind,
                    description = string.IsNullOrEmpty(zone.name) ? zone.kind : zone.name,
                    style = zone.kind,
                    polygon = RectPolygon(zone.minX + shift.x, zone.minZ + shift.y,
                        zone.maxX + shift.x, zone.maxZ + shift.y),
                    groundHeight = zone.kind == "water" ? -0.35f : 0f,
                    areaWeight = 1f,
                });
            }
            if (plan.regions.Count == 0)
            {
                plan.regions.Add(new GrayboxRegion
                {
                    regionId = "region_square_0",
                    kind = "square",
                    description = string.IsNullOrEmpty(scene.sceneName) ? "场景地面" : scene.sceneName,
                    style = "square",
                    polygon = RectPolygon(-halfW * 0.92f, -halfL * 0.92f, halfW * 0.92f, halfL * 0.92f),
                    groundHeight = 0f,
                    areaWeight = 1f,
                });
            }

            // ── 4) 道路 ──
            plan.paths = new List<GrayboxPath>();
            for (int i = 0; i < layout.paths.Count; i++)
            {
                LayoutPath path = layout.paths[i];
                var grayboxPath = new GrayboxPath
                {
                    pathId = "path_" + i.ToString(CultureInfo.InvariantCulture),
                    kind = "road",
                    width = path.width,
                };
                for (int n = 0; n < path.nodes.Count; n++)
                    grayboxPath.nodes.Add(ClampToBounds(path.nodes[n] + shift, halfW, halfL));
                plan.paths.Add(grayboxPath);
            }

            // 补齐缺失的轴向主干：只有一条南北街时，补一条东西街，让空间骨架是"十字"而不是"一条胡同"
            bool hasEastWest = false;
            bool hasNorthSouth = false;
            for (int i = 0; i < plan.paths.Count; i++)
            {
                GrayboxPath existingPath = plan.paths[i];
                if (existingPath == null || existingPath.nodes == null || existingPath.nodes.Count < 2) continue;
                Vector2 first = existingPath.nodes[0];
                Vector2 last = existingPath.nodes[existingPath.nodes.Count - 1];
                if (Mathf.Abs(last.x - first.x) >= Mathf.Abs(last.y - first.y)) hasEastWest = true;
                else hasNorthSouth = true;
            }
            if (hasEastWest && !hasNorthSouth)
            {
                plan.paths.Add(new GrayboxPath
                {
                    pathId = "path_main_ns",
                    kind = "road",
                    width = 6f,
                    nodes = { new Vector2(0f, -halfL * 0.82f), new Vector2(0f, halfL * 0.82f) },
                });
            }
            else if (hasNorthSouth && !hasEastWest)
            {
                plan.paths.Add(new GrayboxPath
                {
                    pathId = "path_main_ew",
                    kind = "road",
                    width = 7f,
                    nodes = { new Vector2(-halfW * 0.82f, 0f), new Vector2(halfW * 0.82f, 0f) },
                });
            }

            if (plan.paths.Count == 0)
            {
                plan.paths.Add(new GrayboxPath
                {
                    pathId = "path_main_ew",
                    kind = "road",
                    width = 7f,
                    nodes = { new Vector2(-halfW * 0.82f, 0f), new Vector2(halfW * 0.82f, 0f) },
                });
                plan.paths.Add(new GrayboxPath
                {
                    pathId = "path_main_ns",
                    kind = "road",
                    width = 6f,
                    nodes = { new Vector2(0f, -halfL * 0.82f), new Vector2(0f, halfL * 0.82f) },
                });
            }

            // ── 5) 语义槽：先放地标，再绑剧情单位 ──
            plan.slots = new List<GrayboxSlot>();
            var placedNames = new List<string>();
            for (int i = 0; i < layout.items.Count; i++)
            {
                LayoutItem item = layout.items[i];
                Vector2 pos = ClampToBounds(item.center + shift, halfW, halfL);
                string semantic = BuildSemantic(item, scene);
                var slot = new GrayboxSlot
                {
                    slotId = "slot_landmark_" + i.ToString(CultureInfo.InvariantCulture),
                    regionId = FindRegionId(plan.regions, pos),
                    semantic = semantic,
                    position = pos,
                    rotation = item.rotation,
                    sizeX = Mathf.Clamp(item.width, 0.8f, 60f),
                    sizeY = Mathf.Clamp(item.depth, 0.8f, 60f),
                    kind = ToSlotKind(item.kind),
                    expectedModelHint = item.name,
                };
                plan.slots.Add(slot);
                placedNames.Add(item.name);
            }

            AddStoryUnits(plan, scene, halfW, halfL, shift, placedNames, layout);

            // ── 6) NPC 动线 ──
            plan.npcPaths = new List<NpcPath>();
            if (scene.npcs != null)
            {
                for (int i = 0; i < scene.npcs.Count; i++)
                {
                    NpcSpec npc = scene.npcs[i];
                    if (npc == null) continue;
                    Vector2 target = new Vector2(npc.localPosition.x, npc.localPosition.z);
                    if (target == Vector2.zero) target = new Vector2(plan.spawnPoint.x + 4f + i * 2f, plan.spawnPoint.y + 5f);
                    target = ClampToBounds(target + shift, halfW, halfL);
                    var npcPath = new NpcPath
                    {
                        npcId = npc.npcId,
                        loop = true,
                        speed = 1.1f,
                    };
                    npcPath.waypoints.Add(ClampToBounds(plan.spawnPoint, halfW, halfL));
                    npcPath.waypoints.Add(target);
                    Vector2 mid = Vector2.Lerp(plan.spawnPoint, target, 0.6f);
                    npcPath.waypoints.Insert(1, ClampToBounds(mid + new Vector2(1.5f, 0f), halfW, halfL));
                    plan.npcPaths.Add(npcPath);
                }
            }

            note = string.Format(CultureInfo.InvariantCulture,
                "剧情布局地编：{0}，地标槽 {1} 个、分区 {2} 个、道路 {3} 条、剧情单位 {4} 个，场景 {5:0}×{6:0} 米。",
                layout.note, layout.items.Count, plan.regions.Count, plan.paths.Count,
                plan.slots.Count, plan.width, plan.length);
            return true;
        }

        // ─────────────────────────── 剧情单位 ───────────────────────────

        private static void AddStoryUnits(
            GrayboxPlan plan, SceneSpec scene, float halfW, float halfL, Vector2 shift,
            List<string> placedNames, StoryLayout layout)
        {
            if (scene.npcs != null)
            {
                for (int i = 0; i < scene.npcs.Count; i++)
                {
                    NpcSpec npc = scene.npcs[i];
                    if (npc == null || string.IsNullOrEmpty(npc.npcId)) continue;
                    Vector2 pos = new Vector2(npc.localPosition.x, npc.localPosition.z);
                    if (pos == Vector2.zero) pos = MatchItem(layout, npc.displayName);
                    if (pos == Vector2.zero) pos = new Vector2(plan.spawnPoint.x + 3f + i * 1.5f, plan.spawnPoint.y + 4f);
                    pos = ClampToBounds(pos + shift, halfW, halfL);
                    plan.slots.Add(new GrayboxSlot
                    {
                        slotId = "slot_npc_" + i.ToString(CultureInfo.InvariantCulture),
                        regionId = FindRegionId(plan.regions, pos),
                        semantic = BuildUnitSemantic(npc.displayName, npc.persona, npc.storyRole),
                        position = pos,
                        sizeX = 0.6f,
                        sizeY = 0.6f,
                        kind = SlotKind.Npc,
                        bindId = npc.npcId,
                    });
                }
            }

            if (scene.anchors != null)
            {
                for (int i = 0; i < scene.anchors.Count; i++)
                {
                    SceneAnchorSpec anchor = scene.anchors[i];
                    if (anchor == null || string.IsNullOrEmpty(anchor.anchorId)) continue;
                    Vector2 pos = new Vector2(anchor.localPosition.x, anchor.localPosition.z);
                    if (pos == Vector2.zero && StoryLayoutParser.TryFindPoint(anchor.description, out Vector2 anchorPoint))
                        pos = anchorPoint;
                    if (pos == Vector2.zero && StoryLayoutParser.TryFindPoint(anchor.requirement, out Vector2 anchorPoint2))
                        pos = anchorPoint2;
                    if (pos == Vector2.zero) pos = MatchItem(layout, anchor.displayName);
                    if (pos == Vector2.zero) pos = plan.spawnPoint;
                    pos = ClampToBounds(pos + shift, halfW, halfL);
                    plan.slots.Add(new GrayboxSlot
                    {
                        slotId = "slot_anchor_" + i.ToString(CultureInfo.InvariantCulture),
                        regionId = FindRegionId(plan.regions, pos),
                        semantic = BuildUnitSemantic(anchor.displayName, anchor.description, anchor.requirement),
                        position = pos,
                        sizeX = 2.4f,
                        sizeY = 2.4f,
                        kind = SlotKind.Anchor,
                        bindId = anchor.anchorId,
                    });
                }
            }

            if (scene.props != null)
            {
                for (int i = 0; i < scene.props.Count; i++)
                {
                    PropInteraction prop = scene.props[i];
                    if (prop == null || string.IsNullOrEmpty(prop.propId)) continue;
                    Vector2 pos = Vector2.zero;
                    if (StoryLayoutParser.TryFindPoint(prop.description, out Vector2 propPoint)) pos = propPoint;
                    if (pos == Vector2.zero) pos = MatchItem(layout, prop.displayName);
                    if (pos == Vector2.zero) pos = new Vector2(plan.spawnPoint.x + 2f, plan.spawnPoint.y + 3f + i);
                    pos = ClampToBounds(pos + shift, halfW, halfL);
                    plan.slots.Add(new GrayboxSlot
                    {
                        slotId = "slot_prop_" + i.ToString(CultureInfo.InvariantCulture),
                        regionId = FindRegionId(plan.regions, pos),
                        semantic = BuildUnitSemantic(prop.displayName, prop.description, prop.effect),
                        position = pos,
                        sizeX = 2.2f,
                        sizeY = 2.2f,
                        kind = SlotKind.Prop,
                        bindId = prop.propId,
                    });
                }
            }
        }

        /// <summary>剧情单位常常没有坐标，就用名字去布局里找同名/近义地标的坐标。</summary>
        private static Vector2 MatchItem(StoryLayout layout, string displayName)
        {
            if (layout == null || string.IsNullOrEmpty(displayName)) return Vector2.zero;
            string name = displayName.Trim();
            Vector2 best = Vector2.zero;
            int bestScore = 0;
            for (int i = 0; i < layout.items.Count; i++)
            {
                LayoutItem item = layout.items[i];
                if (string.IsNullOrEmpty(item.name)) continue;
                int score = 0;
                if (item.name.StartsWith(name, StringComparison.Ordinal) || name.StartsWith(item.name, StringComparison.Ordinal))
                    score = name.Length + item.name.Length;
                else if (item.name.IndexOf(name, StringComparison.Ordinal) >= 0 || name.IndexOf(item.name, StringComparison.Ordinal) >= 0)
                    score = name.Length;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = item.center;
                }
            }
            return best;
        }

        private static string BuildSemantic(LayoutItem item, SceneSpec scene)
        {
            string style = string.IsNullOrEmpty(scene.era) ? "" : scene.era + "，";
            string detail = item.raw ?? "";
            // 去掉开头的类别前缀，避免检索时被"建筑:"这类词带偏
            int colon = detail.IndexOf(':');
            if (colon > 0 && colon <= 6) detail = detail.Substring(colon + 1);
            string semantic = item.name + "，" + style + detail;
            return semantic.Length <= 120 ? semantic : semantic.Substring(0, 120);
        }

        private static string BuildUnitSemantic(string displayName, string detailA, string detailB)
        {
            string text = (displayName ?? "") + "，" + (detailA ?? "");
            if (!string.IsNullOrEmpty(detailB)) text += "，" + detailB;
            return text.Length <= 120 ? text : text.Substring(0, 120);
        }

        private static SlotKind ToSlotKind(string kind)
        {
            if (kind == "Building") return SlotKind.Building;
            if (kind == "Prop") return SlotKind.Prop;
            return SlotKind.Decoration;
        }

        // ─────────────────────────── 几何工具 ───────────────────────────

        private static List<Vector2> RectPolygon(float x0, float z0, float x1, float z1)
        {
            return new List<Vector2>
            {
                new Vector2(x0, z0),
                new Vector2(x1, z0),
                new Vector2(x1, z1),
                new Vector2(x0, z1),
            };
        }

        private static Vector2 ClampToBounds(Vector2 pos, float halfW, float halfL)
        {
            return new Vector2(
                Mathf.Clamp(pos.x, -halfW + 1f, halfW - 1f),
                Mathf.Clamp(pos.y, -halfL + 1f, halfL - 1f));
        }

        private static string FindRegionId(List<GrayboxRegion> regions, Vector2 pos)
        {
            if (regions == null) return "";
            for (int i = 0; i < regions.Count; i++)
            {
                GrayboxRegion region = regions[i];
                if (region == null || region.polygon == null || region.polygon.Count < 3) continue;
                if (PointInPolygon(region.polygon, pos)) return region.regionId;
            }
            return regions.Count > 0 && regions[0] != null ? regions[0].regionId : "";
        }

        private static bool PointInPolygon(List<Vector2> polygon, Vector2 point)
        {
            bool inside = false;
            int count = polygon.Count;
            for (int i = 0, j = count - 1; i < count; j = i++)
            {
                Vector2 a = polygon[i];
                Vector2 b = polygon[j];
                if ((a.y > point.y) != (b.y > point.y) &&
                    point.x < (b.x - a.x) * (point.y - a.y) / Mathf.Max(0.0001f, b.y - a.y) + a.x)
                    inside = !inside;
            }
            return inside;
        }
    }
}
