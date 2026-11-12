// SceneGen 投影器：把求解后的 LayoutSpec 投影成 GrayboxPlan。
//
// 与旧 GrayboxSpecExecutor.BuildSlots 的区别：
//   1) 尺寸语义唯一：size 就是世界轴对齐 AABB，rotation 只表示立面朝向（faceDir）；
//   2) 不再按"离出生点 60 米"切流式段：素模阶段全部一次落地，避免首帧只有 1/3 场景；
//   3) 槽位携带 primitive（box/cylinder/cone/...），搭建层不需要查美术库；
//   4) 分区/街道/锚点/NPC 全部来自求解结果，灰盒不再"理解散文"。
using System;
using System.Collections.Generic;
using System.Globalization;
using EpicSpark.Core;
using UnityEngine;

namespace EpicSpark.SceneGen
{
    public static class SceneGenProjector
    {
        public static GrayboxPlan Project(SceneSpec scene, LayoutSpec spec, int seed)
        {
            float width = scene != null ? SceneSize.SideOrDefault(scene.width) : 60f;
            float length = scene != null ? SceneSize.SideOrDefault(scene.length) : 60f;
            var plan = new GrayboxPlan
            {
                sceneId = scene != null ? (scene.sceneId ?? string.Empty) : string.Empty,
                width = width,
                length = length,
                seed = seed,
                spawnPoint = scene != null && (scene.spawnPoint.x != 0f || scene.spawnPoint.z != 0f)
                    ? new Vector2(scene.spawnPoint.x, scene.spawnPoint.z)
                    : new Vector2(0f, -length * 0.5f + 8f),
                specDriven = true,
                finished = true,
                layoutSpec = spec,
                layoutShift = Vector2.zero,
                segmentCount = 1,
            };

            plan.regions = BuildRegions(spec);
            plan.paths = BuildPaths(spec, plan);
            plan.npcPaths = BuildNpcPaths(spec);
            plan.slots = BuildSlots(spec, plan);
            plan.sceneGenReport = spec != null ? spec.metrics : string.Empty;
            plan.plannerNote = "SceneGen 求解器：语义骨架 → 确定性施工图"
                               + " · 分区 " + plan.regions.Count
                               + " · 街道 " + (spec != null ? spec.streets.Count : 0)
                               + " · 槽位 " + plan.slots.Count
                               + (spec != null && !string.IsNullOrEmpty(spec.metrics) ? " · " + spec.metrics : "");
            return plan;
        }

        private static List<GrayboxRegion> BuildRegions(LayoutSpec spec)
        {
            var regions = new List<GrayboxRegion>();
            if (spec == null || spec.zones == null) return regions;
            for (int i = 0; i < spec.zones.Count; i++)
            {
                LayoutZoneSpec zone = spec.zones[i];
                if (zone == null || zone.polygon == null || zone.polygon.Count < 3) continue;
                regions.Add(new GrayboxRegion
                {
                    regionId = string.IsNullOrEmpty(zone.zoneId)
                        ? "region_zone_" + i.ToString(CultureInfo.InvariantCulture) : zone.zoneId,
                    kind = NormalizeRegionKind(zone.kind),
                    description = string.IsNullOrEmpty(zone.description) ? zone.kind : zone.description,
                    style = string.IsNullOrEmpty(zone.material) ? zone.kind : zone.material,
                    polygon = new List<Vector2>(zone.polygon),
                    groundHeight = zone.groundHeight,
                    areaWeight = 1f,
                });
            }
            return regions;
        }

        private static string NormalizeRegionKind(string kind)
        {
            switch ((kind ?? "").ToLowerInvariant())
            {
                case "square": return "square";
                case "market": return "market";
                case "church": return "church";
                case "residential": return "residential";
                case "water": return "water";
                case "harbor": return "harbor";
                case "field": return "field";
                default: return "residential";
            }
        }

        private static List<GrayboxPath> BuildPaths(LayoutSpec spec, GrayboxPlan plan)
        {
            var paths = new List<GrayboxPath>();
            if (spec == null || spec.paths == null) return paths;
            float halfW = plan.width * 0.5f;
            float halfL = plan.length * 0.5f;
            for (int i = 0; i < spec.paths.Count; i++)
            {
                LayoutPathSpec source = spec.paths[i];
                if (source == null || source.nodes == null || source.nodes.Count < 2) continue;
                var path = new GrayboxPath
                {
                    pathId = string.IsNullOrEmpty(source.pathId)
                        ? "path_spec_" + i.ToString(CultureInfo.InvariantCulture) : source.pathId,
                    kind = string.IsNullOrEmpty(source.kind) ? "road" : source.kind,
                    width = Mathf.Clamp(source.width, 0.6f, 30f),
                };
                for (int n = 0; n < source.nodes.Count; n++)
                {
                    Vector2 p = source.nodes[n];
                    path.nodes.Add(new Vector2(Mathf.Clamp(p.x, -halfW, halfW), Mathf.Clamp(p.y, -halfL, halfL)));
                }
                paths.Add(path);
            }
            return paths;
        }

        private static List<NpcPath> BuildNpcPaths(LayoutSpec spec)
        {
            var list = new List<NpcPath>();
            if (spec == null || spec.npcs == null) return list;
            for (int i = 0; i < spec.npcs.Count; i++)
            {
                LayoutNpcSpec npc = spec.npcs[i];
                if (npc == null || npc.waypoints == null || npc.waypoints.Count < 2) continue;
                var path = new NpcPath
                {
                    npcId = npc.npcId,
                    loop = npc.loop,
                    speed = 1.2f,
                };
                for (int w = 0; w < npc.waypoints.Count; w++) path.waypoints.Add(npc.waypoints[w]);
                list.Add(path);
            }
            return list;
        }

        private static List<GrayboxSlot> BuildSlots(LayoutSpec spec, GrayboxPlan plan)
        {
            var slots = new List<GrayboxSlot>();
            if (spec == null || spec.items == null) return slots;
            var used = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < spec.items.Count; i++)
            {
                LayoutItemSpec item = spec.items[i];
                if (item == null) continue;

                string slotId = "slot_" + (string.IsNullOrEmpty(item.id) ? i.ToString(CultureInfo.InvariantCulture) : item.id);
                if (!used.Add(slotId)) slotId = slotId + "_" + i.ToString(CultureInfo.InvariantCulture);

                Vector2 face = item.faceDir;
                if (face.sqrMagnitude < 0.01f) face = SceneGenFacing.NormalFromAngle(item.rotation);
                float rotation = item.faceDir.sqrMagnitude < 0.01f ? item.rotation : SceneGenFacing.AngleFromNormal(face);

                slots.Add(new GrayboxSlot
                {
                    slotId = slotId,
                    regionId = FindRegionId(plan.regions, item.center),
                    semantic = string.IsNullOrEmpty(item.semantic) ? item.name : item.semantic,
                    position = item.center,
                    rotation = rotation,
                    sizeX = Mathf.Clamp(item.size.x, 0.2f, 400f),
                    sizeY = Mathf.Clamp(item.size.y, 0.2f, 400f),
                    height = Mathf.Clamp(item.height, 0f, 300f),
                    kind = item.kind,
                    bindId = item.bindId ?? string.Empty,
                    expectedModelHint = item.expectedModelHint ?? string.Empty,
                    segment = 0,
                    specItemId = item.id ?? string.Empty,
                    rowId = item.rowId ?? string.Empty,
                    rowIndex = item.rowIndex,
                    rowCount = Mathf.Max(1, item.rowCount),
                    specRequired = item.required,
                    faceDir = face,
                    primitive = string.IsNullOrEmpty(item.primitive)
                        ? PrimitiveForSlot(item) : item.primitive,
                    solverGenerated = item.solverGenerated,
                });
            }
            return slots;
        }

        private static string PrimitiveForSlot(LayoutItemSpec item)
        {
            switch (item.kind)
            {
                case SlotKind.Building: return "building";
                case SlotKind.Anchor: return "anchor";
                case SlotKind.Npc: return "npc";
                default: return "prop";
            }
        }

        private static string FindRegionId(List<GrayboxRegion> regions, Vector2 point)
        {
            if (regions == null) return string.Empty;
            string best = string.Empty;
            float bestArea = float.MaxValue;
            for (int i = 0; i < regions.Count; i++)
            {
                GrayboxRegion region = regions[i];
                if (region == null || region.polygon == null || region.polygon.Count < 3) continue;
                if (!PointInPolygon(point, region.polygon)) continue;
                float area = SceneGenRect.Bounds(region.polygon).width * SceneGenRect.Bounds(region.polygon).height;
                if (area < bestArea) { bestArea = area; best = region.regionId; }
            }
            return best;
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
    }
}
