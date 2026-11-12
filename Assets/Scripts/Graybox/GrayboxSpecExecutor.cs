// LayoutSpec 执行器：把结构化布局规格「照图施工」成 GrayboxPlan。
//
// 与旧的 GrayboxPlanner 的区别：
//   旧：看到 layoutDescription 就按一个地标一个槽放（50×20 街屋 = 1 个 40×20 大盒子）；
//   新：LayoutSpec 已经把街屋拆成单元、把锚点/NPC 坐标固定下来，这里只负责逐项落槽，
//       不再判断「这个区域主要放几个物件」。它是远端灰盒的种子，也是远端不合规时的回退。
//
// 保留回退：任何异常/空规格由调用方捕获并走旧的 GrayboxPlanner.Plan。
using System;
using System.Collections.Generic;
using System.Globalization;
using EpicSpark.Core;
using EpicSpark.SceneGen;
using EpicSpark.Story;
using UnityEngine;

namespace EpicSpark.Graybox
{
    public static class GrayboxSpecExecutor
    {
        /// <summary>按 LayoutSpec 生成确定性灰盒规划；spec 为空时返回 null。</summary>
        public static GrayboxPlan Build(SceneSpec scene, LayoutSpec spec, int seed, out string note)
        {
            note = "";
            if (scene == null) return null;

            // ── SceneGen 求解器：剧情只给语义骨架，地块/临街/围合/道具由确定性算法生成 ──
            // 这一段是"既不能太稀也不能混乱"的落地：密度由地块面积决定，秩序由几何不变量保证。
            if (EpicSparkFeatureFlags.UseSceneGenSolver)
            {
                try
                {
                    GrayboxPlan generated = SceneGenPipeline.Plan(scene, spec, seed);
                    if (generated != null && generated.slots != null && generated.slots.Count > 0)
                    {
                        note = generated.plannerNote;
                        return generated;
                    }
                    Debug.LogWarning("[灰盒/SceneGen] 求解结果为空，回退旧规格执行器。");
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[灰盒/SceneGen] 求解异常，回退旧规格执行器: " + e.Message);
                }
            }

            if (spec == null || !spec.Usable) return null;

            float width = scene.width > 1f ? scene.width : 60f;
            float length = scene.length > 1f ? scene.length : 60f;
            var plan = new GrayboxPlan
            {
                sceneId = string.IsNullOrEmpty(scene.sceneId) ? "scene_spec" : scene.sceneId,
                width = width,
                length = length,
                seed = seed,
                specDriven = true,
                layoutSpec = spec,
                finished = true,
            };

            string layoutNote = "";
            bool built = false;
            try
            {
                var request = new GrayboxRequest { scene = scene, seed = seed };
                // 传 spec：regions ← spec.zones、paths ← spec.paths、npcPaths ← spec.npcs，
                // 灰盒不再二次解析散文（这是"听指挥"的关键）。
                built = StoryLayoutPlanBuilder.TryBuild(scene, request, plan, out layoutNote, spec);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[灰盒执行器] 剧情布局建规划异常，改用基础场景骨架: " + e.Message);
                built = false;
            }

            if (!built)
            {
                BuildBasePlan(scene, spec, plan);
                layoutNote = "LayoutSpec 执行器（基础骨架）";
            }

            ApplySpecPaths(plan, spec);

            plan.slots = BuildSlots(spec, plan);
            AssignSlotRegions(plan);
            plan.specDriven = true;
            plan.layoutSpec = spec;
            plan.finished = true;
            // 有剧情直给施工图时，日志必须能一眼看出"这次没有再解析散文"。
            bool storyGiven = spec.source != null
                              && spec.source.StartsWith("storyLayoutSpec", StringComparison.Ordinal);
            plan.plannerNote = "LayoutSpec 执行器：" + (storyGiven ? "剧情施工图直给（未二次解析散文）" : layoutNote)
                + " · 规格项 " + spec.items.Count
                + " 个 · 槽位 " + plan.slots.Count + " 个"
                + " · 分区 " + (plan.regions == null ? 0 : plan.regions.Count)
                + " · 动线 " + (plan.npcPaths == null ? 0 : plan.npcPaths.Count)
                + " · 排屋单元 " + CountRowUnits(spec)
                + (string.IsNullOrEmpty(spec.diagnostics) ? "" : " · 覆盖审计 " + spec.diagnostics);
            note = plan.plannerNote;
            GrayboxPlanner.AssignStreamingSegments(plan);
            return plan;
        }

        /// <summary>把槽位归属到分区：regionId 一直是空串，导致构建/绑定层拿不到"这块地在哪个区"。</summary>
        private static void AssignSlotRegions(GrayboxPlan plan)
        {
            if (plan == null || plan.slots == null || plan.regions == null) return;
            for (int i = 0; i < plan.slots.Count; i++)
            {
                GrayboxSlot slot = plan.slots[i];
                if (slot == null || !string.IsNullOrEmpty(slot.regionId)) continue;
                GrayboxRegion best = null;
                float bestArea = float.MaxValue;
                for (int r = 0; r < plan.regions.Count; r++)
                {
                    GrayboxRegion region = plan.regions[r];
                    if (region == null || region.polygon == null || region.polygon.Count < 3) continue;
                    if (!PointInPolygon(slot.position, region.polygon)) continue;
                    float area = RegionArea(region);
                    if (area < bestArea) { bestArea = area; best = region; }
                }
                if (best != null) slot.regionId = best.regionId;
            }
        }

        private static float RegionArea(GrayboxRegion region)
        {
            if (region == null || region.polygon == null || region.polygon.Count < 3) return float.MaxValue;
            float area = 0f;
            for (int i = 0; i < region.polygon.Count; i++)
            {
                Vector2 a = region.polygon[i];
                Vector2 b = region.polygon[(i + 1) % region.polygon.Count];
                area += a.x * b.y - b.x * a.y;
            }
            return Mathf.Abs(area) * 0.5f;
        }

        private static bool PointInPolygon(Vector2 p, List<Vector2> poly)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                if ((poly[i].y > p.y) == (poly[j].y > p.y)) continue;
                float x = (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x;
                if (p.x < x) inside = !inside;
            }
            return inside;
        }

        private static void BuildBasePlan(SceneSpec scene, LayoutSpec spec, GrayboxPlan plan)
        {
            float halfW = Mathf.Max(2f, plan.width * 0.5f);
            float halfL = Mathf.Max(2f, plan.length * 0.5f);
            plan.spawnPoint = new Vector2(scene.spawnPoint.x, scene.spawnPoint.z);
            if (plan.spawnPoint == Vector2.zero)
                plan.spawnPoint = new Vector2(0f, -halfL + 5f);
            plan.spawnPoint = new Vector2(
                Mathf.Clamp(plan.spawnPoint.x, -halfW + 1f, halfW - 1f),
                Mathf.Clamp(plan.spawnPoint.y, -halfL + 1f, halfL - 1f));
            plan.regions = new List<GrayboxRegion>
            {
                new GrayboxRegion
                {
                    regionId = "region_spec_ground",
                    kind = "square",
                    description = string.IsNullOrEmpty(scene.sceneName) ? "场景地面" : scene.sceneName,
                    style = "square",
                    polygon = GrayboxPlanner.RectRing(-halfW * 0.94f, -halfL * 0.94f, halfW * 0.94f, halfL * 0.94f),
                },
            };
            plan.paths = new List<GrayboxPath>();
            if (spec.paths != null)
            {
                for (int i = 0; i < spec.paths.Count; i++)
                {
                    LayoutPathSpec path = spec.paths[i];
                    if (path == null || path.nodes == null || path.nodes.Count < 2) continue;
                    var pb = new GrayboxPath
                    {
                        pathId = string.IsNullOrEmpty(path.pathId) ? "path_spec_" + i.ToString(CultureInfo.InvariantCulture) : path.pathId,
                        kind = GrayboxPlanner.NormalizePathKind(path.kind),
                        width = path.width,
                    };
                    for (int n = 0; n < path.nodes.Count; n++)
                    {
                        Vector2 p = path.nodes[n];
                        pb.nodes.Add(new Vector2(Mathf.Clamp(p.x, -halfW, halfW), Mathf.Clamp(p.y, -halfL, halfL)));
                    }
                    plan.paths.Add(pb);
                }
            }
            plan.npcPaths = new List<NpcPath>();
        }

        /// <summary>
        /// StoryLayoutPlanBuilder 只解析 layoutDescription 里的道路；规格里补的边界墙
        /// 必须覆盖回 plan，否则广场仍然没有围合、看起来是一堆散落的房子。
        /// </summary>
        private static void ApplySpecPaths(GrayboxPlan plan, LayoutSpec spec)
        {
            if (plan == null || spec == null || spec.paths == null || spec.paths.Count == 0) return;

            var paths = new List<GrayboxPath>(spec.paths.Count);
            for (int i = 0; i < spec.paths.Count; i++)
            {
                LayoutPathSpec source = spec.paths[i];
                if (source == null || source.nodes == null || source.nodes.Count < 2) continue;
                var path = new GrayboxPath
                {
                    pathId = string.IsNullOrEmpty(source.pathId)
                        ? "spec_path_" + i.ToString(CultureInfo.InvariantCulture) : source.pathId,
                    kind = string.IsNullOrEmpty(source.kind) ? "road" : source.kind,
                    width = Mathf.Clamp(source.width, 1f, 30f),
                };
                for (int n = 0; n < source.nodes.Count; n++)
                {
                    Vector2 point = source.nodes[n] + plan.layoutShift;
                    point.x = Mathf.Clamp(point.x, -plan.width * 0.5f, plan.width * 0.5f);
                    point.y = Mathf.Clamp(point.y, -plan.length * 0.5f, plan.length * 0.5f);
                    path.nodes.Add(point);
                }
                paths.Add(path);
            }
            if (paths.Count > 0) plan.paths = paths;
        }

        private static List<GrayboxSlot> BuildSlots(LayoutSpec spec, GrayboxPlan plan)
        {
            var slots = new List<GrayboxSlot>();
            var used = new HashSet<string>(StringComparer.Ordinal);
            float halfW = Mathf.Max(2f, plan.width * 0.5f);
            float halfL = Mathf.Max(2f, plan.length * 0.5f);
            Vector2 shift = plan.layoutShift;
            // 60 米只适合小场景；200×200 的集市棚/长建筑不能被砍成 60 米。
            float maxSlotSize = Mathf.Clamp(
                Mathf.Max(60f, Mathf.Max(plan.width, plan.length) * 0.9f), 60f, 180f);
            for (int i = 0; i < spec.items.Count; i++)
            {
                LayoutItemSpec item = spec.items[i];
                if (item == null) continue;

                Vector2 pos = new Vector2(item.center.x + shift.x, item.center.y + shift.y);
                pos = new Vector2(
                    Mathf.Clamp(pos.x, -halfW + 0.5f, halfW - 0.5f),
                    Mathf.Clamp(pos.y, -halfL + 0.5f, halfL - 0.5f));

                string slotId = "slot_spec_" + (string.IsNullOrEmpty(item.id) ? i.ToString(CultureInfo.InvariantCulture) : item.id);
                if (!used.Add(slotId)) slotId = slotId + "_" + i.ToString(CultureInfo.InvariantCulture);

                slots.Add(new GrayboxSlot
                {
                    slotId = slotId,
                    semantic = string.IsNullOrEmpty(item.semantic) ? item.name : item.semantic,
                    position = pos,
                    rotation = item.rotation,
                    sizeX = Mathf.Clamp(item.size.x, 0.2f, maxSlotSize),
                    sizeY = Mathf.Clamp(item.size.y, 0.2f, maxSlotSize),
                    height = Mathf.Clamp(item.height, 0f, 200f),
                    kind = item.kind,
                    bindId = item.bindId ?? string.Empty,
                    expectedModelHint = item.expectedModelHint ?? string.Empty,
                    specItemId = item.id ?? string.Empty,
                    rowId = item.rowId ?? string.Empty,
                    rowIndex = item.rowIndex,
                    rowCount = Mathf.Max(1, item.rowCount),
                    specRequired = item.required,
                });
            }
            return slots;
        }

        private static int CountRowUnits(LayoutSpec spec)
        {
            int count = 0;
            for (int i = 0; i < spec.items.Count; i++)
                if (spec.items[i] != null && !string.IsNullOrEmpty(spec.items[i].rowId)) count++;
            return count;
        }
    }
}
