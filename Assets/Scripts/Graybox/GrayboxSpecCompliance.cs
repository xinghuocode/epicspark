// LayoutSpec 合规校验 / 规格回填。
//
// 解决的问题：
//   远端灰盒返回的是「它认为合理的规划」，不是「按剧情施工图施工」。
//   这里做两件事：
//     1) MergeRequired：把规格要求的槽位补齐/恢复（远端移动/缩放/删除都会被纠正，并记录 deviation）；
//     2) Validate：检查缺项、数量、位移、缩放、排屋连续性、街面净宽、建筑重叠。
//   校验不通过时 GrayboxAgent 回退到 GrayboxSpecExecutor 的确定性计划，保证最终场景仍按剧情。
using System;
using System.Collections.Generic;
using System.Globalization;
using EpicSpark.Core;
using UnityEngine;

namespace EpicSpark.Graybox
{
    public static class GrayboxSpecCompliance
    {
        /// <summary>60 米只适合 60×60 小场景；大场景允许按场景尺寸放宽。</summary>
        private static float MaxSpecSize(GrayboxPlan plan)
        {
            if (plan == null) return 60f;
            return Mathf.Clamp(Mathf.Max(60f, Mathf.Max(plan.width, plan.length) * 0.9f), 60f, 180f);
        }

        /// <summary>把规格要求的槽位恢复/补齐到 plan 里；返回相对远端的偏离记录。</summary>
        public static List<string> MergeRequired(GrayboxPlan plan, LayoutSpec spec)
        {
            var deviations = new List<string>();
            if (plan == null || spec == null || spec.items == null) return deviations;
            if (plan.slots == null) plan.slots = new List<GrayboxSlot>();
            plan.specDriven = true;
            plan.layoutSpec = spec;
            if (plan.deviations == null) plan.deviations = new List<string>();
            DedupeRoads(plan, deviations);

            // 分区与 NPC 动线也属于「剧情指令」：远端灰盒可以用工具重定义它们，
            // 但最终必须回到施工图（spec.zones / spec.npcs），否则分区会被并成一块、
            // 动线全部从出生点出发——这正是 08:57 记录里"不听指挥"的表现。
            RestoreRegions(plan, spec, deviations);
            RestoreNpcPaths(plan, spec, deviations);

            var bySpec = new Dictionary<string, GrayboxSlot>(StringComparer.Ordinal);
            var duplicates = new HashSet<GrayboxSlot>();
            for (int i = 0; i < plan.slots.Count; i++)
            {
                GrayboxSlot slot = plan.slots[i];
                if (slot == null || string.IsNullOrEmpty(slot.specItemId)) continue;
                if (!bySpec.ContainsKey(slot.specItemId))
                {
                    bySpec[slot.specItemId] = slot;
                }
                else
                {
                    duplicates.Add(slot);
                    deviations.Add("重复槽位 " + slot.slotId + "（specItemId=" + slot.specItemId + "），已删除多余项");
                }
            }
            if (duplicates.Count > 0)
            {
                var kept = new List<GrayboxSlot>(plan.slots.Count);
                for (int i = 0; i < plan.slots.Count; i++)
                    if (plan.slots[i] != null && !duplicates.Contains(plan.slots[i])) kept.Add(plan.slots[i]);
                plan.slots = kept;
            }

            float halfW = Mathf.Max(2f, plan.width * 0.5f);
            float halfL = Mathf.Max(2f, plan.length * 0.5f);
            Vector2 shift = plan.layoutShift;
            var usedIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < plan.slots.Count; i++)
                if (plan.slots[i] != null && !string.IsNullOrEmpty(plan.slots[i].slotId)) usedIds.Add(plan.slots[i].slotId);

            for (int i = 0; i < spec.items.Count; i++)
            {
                LayoutItemSpec item = spec.items[i];
                if (item == null || !item.required) continue;

                Vector2 expectedPos = Clamp(new Vector2(item.center.x + shift.x, item.center.y + shift.y), halfW, halfL);
                float maxSpecSize = MaxSpecSize(plan);
                Vector2 expectedSize = new Vector2(
                    Mathf.Clamp(item.size.x, 0.2f, maxSpecSize),
                    Mathf.Clamp(item.size.y, 0.2f, maxSpecSize));

                GrayboxSlot slot;
                if (bySpec.TryGetValue(item.id, out slot) && slot != null)
                {
                    bool moved = Vector2.Distance(slot.position, expectedPos) > Mathf.Max(0.5f, spec.positionTolerance * 0.5f);
                    bool resized = Mathf.Abs(slot.sizeX - expectedSize.x) > Mathf.Max(0.5f, spec.sizeTolerance)
                                   || Mathf.Abs(slot.sizeY - expectedSize.y) > Mathf.Max(0.5f, spec.sizeTolerance);
                    bool rotated = Mathf.Abs(Mathf.DeltaAngle(slot.rotation, item.rotation)) > 5f;
                    bool kindChanged = slot.kind != item.kind;
                    bool heightChanged = item.height > 0.5f && Mathf.Abs(slot.height - item.height) > 1f;
                    if (moved || resized || rotated || kindChanged || heightChanged)
                    {
                        deviations.Add(string.Format(CultureInfo.InvariantCulture,
                            "{0} 被远端修改（位移={1:0.0}m，尺寸={2:0.0}×{3:0.0}，kind={4}），已按规格恢复",
                            slot.slotId, Vector2.Distance(slot.position, expectedPos), slot.sizeX, slot.sizeY, slot.kind));
                    }
                    slot.position = expectedPos;
                    slot.sizeX = expectedSize.x;
                    slot.sizeY = expectedSize.y;
                    slot.rotation = item.rotation;
                    slot.kind = item.kind;
                    slot.bindId = item.bindId ?? string.Empty;
                    slot.height = Mathf.Clamp(item.height, 0f, 200f);
                }
                else
                {
                    slot = new GrayboxSlot
                    {
                        slotId = UniqueId("slot_spec_" + SafeId(item.id), usedIds),
                        position = expectedPos,
                        sizeX = expectedSize.x,
                        sizeY = expectedSize.y,
                        height = Mathf.Clamp(item.height, 0f, 200f),
                        rotation = item.rotation,
                        kind = item.kind,
                        bindId = item.bindId ?? string.Empty,
                        semantic = string.IsNullOrEmpty(item.semantic) ? item.name : item.semantic,
                        expectedModelHint = item.expectedModelHint ?? string.Empty,
                        specItemId = item.id ?? string.Empty,
                        rowId = item.rowId ?? string.Empty,
                        rowIndex = item.rowIndex,
                        rowCount = Mathf.Max(1, item.rowCount),
                        specRequired = true,
                    };
                    plan.slots.Add(slot);
                    bySpec[item.id] = slot;
                    deviations.Add("规格槽位 " + item.id + " 缺失，已按坐标 (" + expectedPos.x.ToString("0.#")
                        + ", " + expectedPos.y.ToString("0.#") + ") 补回");
                }

                // 语义/模型提示也以规格为准；远端可以补充，但不能把剧情语义换掉。
                if (!string.IsNullOrEmpty(item.semantic)) slot.semantic = item.semantic;
                if (!string.IsNullOrEmpty(item.expectedModelHint)) slot.expectedModelHint = item.expectedModelHint;
                slot.specItemId = item.id ?? string.Empty;
                slot.rowId = item.rowId ?? string.Empty;
                slot.rowIndex = item.rowIndex;
                slot.rowCount = Mathf.Max(1, item.rowCount);
                slot.specRequired = true;
            }

            if (deviations.Count > 0)
            {
                plan.deviations.AddRange(deviations);
                Debug.LogWarning("[灰盒合规] " + deviations.Count + " 处远端偏离已按 LayoutSpec 恢复。");
            }
            return deviations;
        }

        /// <summary>校验规划是否按 LayoutSpec 执行；errors 非空即视为不合规。</summary>
        public static bool Validate(
            GrayboxPlan plan, LayoutSpec spec, out List<string> errors, out List<string> warnings,
            bool geometryAsErrors = true)
        {
            errors = new List<string>();
            warnings = new List<string>();
            if (plan == null)
            {
                errors.Add("plan 为空");
                return false;
            }
            if (spec == null || spec.items == null || spec.items.Count == 0)
            {
                warnings.Add("没有 LayoutSpec，跳过合规校验（旧模式）");
                return true;
            }
            if (!plan.finished)
                errors.Add("远端未调用 finish_graybox，计划不完整");

            float halfW = Mathf.Max(2f, plan.width * 0.5f);
            float halfL = Mathf.Max(2f, plan.length * 0.5f);
            Vector2 shift = plan.layoutShift;

            var bySpec = new Dictionary<string, GrayboxSlot>(StringComparer.Ordinal);
            var slotCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < plan.slots.Count; i++)
            {
                GrayboxSlot slot = plan.slots[i];
                if (slot == null || string.IsNullOrEmpty(slot.specItemId)) continue;
                int n;
                slotCounts.TryGetValue(slot.specItemId, out n);
                slotCounts[slot.specItemId] = n + 1;
                if (!bySpec.ContainsKey(slot.specItemId)) bySpec[slot.specItemId] = slot;
            }

            // 1) 规格项必须存在，位置/尺寸/类型/绑定必须匹配
            var requiredRows = new Dictionary<string, List<LayoutItemSpec>>(StringComparer.Ordinal);
            for (int i = 0; i < spec.items.Count; i++)
            {
                LayoutItemSpec item = spec.items[i];
                if (item == null) continue;
                if (!string.IsNullOrEmpty(item.rowId))
                {
                    List<LayoutItemSpec> row;
                    if (!requiredRows.TryGetValue(item.rowId, out row))
                    {
                        row = new List<LayoutItemSpec>();
                        requiredRows[item.rowId] = row;
                    }
                    row.Add(item);
                }
                if (!item.required) continue;

                GrayboxSlot slot;
                if (!bySpec.TryGetValue(item.id, out slot) || slot == null)
                {
                    errors.Add("缺少规格槽位 " + item.id + "（" + item.name + "）");
                    continue;
                }
                int count;
                if (slotCounts.TryGetValue(item.id, out count) && count > 1)
                    errors.Add("规格槽位 " + item.id + " 出现 " + count + " 次");

                Vector2 expectedPos = Clamp(new Vector2(item.center.x + shift.x, item.center.y + shift.y), halfW, halfL);
                float maxSpecSize = MaxSpecSize(plan);
                Vector2 expectedSize = new Vector2(
                    Mathf.Clamp(item.size.x, 0.2f, maxSpecSize),
                    Mathf.Clamp(item.size.y, 0.2f, maxSpecSize));
                float moved = Vector2.Distance(slot.position, expectedPos);
                if (moved > spec.positionTolerance)
                    errors.Add(string.Format(CultureInfo.InvariantCulture,
                        "规格槽位 {0} 位移 {1:0.0}m（允许 {2:0.0}m）", item.id, moved, spec.positionTolerance));
                if (Mathf.Abs(slot.sizeX - expectedSize.x) > Mathf.Max(0.5f, spec.sizeTolerance)
                    || Mathf.Abs(slot.sizeY - expectedSize.y) > Mathf.Max(0.5f, spec.sizeTolerance))
                    errors.Add(string.Format(CultureInfo.InvariantCulture,
                        "规格槽位 {0} 尺寸 {1:0.0}×{2:0.0}，规格 {3:0.0}×{4:0.0}",
                        item.id, slot.sizeX, slot.sizeY, expectedSize.x, expectedSize.y));
                if (slot.kind != item.kind)
                    errors.Add("规格槽位 " + item.id + " 类型 " + slot.kind + "，规格 " + item.kind);
                if (!string.IsNullOrEmpty(item.bindId) && slot.bindId != item.bindId)
                    errors.Add("规格槽位 " + item.id + " 绑定 " + slot.bindId + "，规格 " + item.bindId);
            }

            // 2) 排屋连续性：数量、相邻单元缝隙
            foreach (KeyValuePair<string, List<LayoutItemSpec>> pair in requiredRows)
            {
                List<LayoutItemSpec> units = pair.Value;
                if (units.Count <= 1) continue;
                var slots = new List<GrayboxSlot>();
                for (int i = 0; i < units.Count; i++)
                {
                    GrayboxSlot slot;
                    if (bySpec.TryGetValue(units[i].id, out slot) && slot != null) slots.Add(slot);
                }
                if (slots.Count != units.Count)
                {
                    errors.Add("排屋 " + pair.Key + " 应有 " + units.Count + " 个单元，实际 " + slots.Count + " 个");
                    continue;
                }
                slots.Sort((a, b) =>
                {
                    LayoutItemSpec ia = FindById(units, a.specItemId);
                    LayoutItemSpec ib = FindById(units, b.specItemId);
                    return (ia != null ? ia.rowIndex : 0).CompareTo(ib != null ? ib.rowIndex : 0);
                });
                bool alongX = Mathf.Abs(slots[0].position.x - slots[slots.Count - 1].position.x)
                              >= Mathf.Abs(slots[0].position.y - slots[slots.Count - 1].position.y);
                for (int i = 0; i + 1 < slots.Count; i++)
                {
                    GrayboxSlot a = slots[i];
                    GrayboxSlot b = slots[i + 1];
                    float centerDistance = alongX
                        ? Mathf.Abs(b.position.x - a.position.x)
                        : Mathf.Abs(b.position.y - a.position.y);
                    float halfA = alongX ? a.sizeX * 0.5f : a.sizeY * 0.5f;
                    float halfB = alongX ? b.sizeX * 0.5f : b.sizeY * 0.5f;
                    float gap = centerDistance - halfA - halfB;
                    if (gap > spec.rowGapTolerance)
                        errors.Add(string.Format(CultureInfo.InvariantCulture,
                            "排屋 {0} 第 {1}/{2} 单元之间缝隙 {3:0.0}m（允许 {4:0.0}m）",
                            pair.Key, i + 1, i + 2, gap, spec.rowGapTolerance));
                }
            }

            // 3) 街面净宽：建筑矩形不能压进 road 走廊
            if (plan.paths != null)
            {
                for (int p = 0; p < plan.paths.Count; p++)
                {
                    GrayboxPath path = plan.paths[p];
                    if (path == null || path.nodes == null || path.nodes.Count < 2) continue;
                    if (!string.Equals(path.kind, "road", StringComparison.OrdinalIgnoreCase)) continue;
                    float halfRoad = Mathf.Max(0.1f, path.width) * 0.5f;
                    for (int s = 0; s < plan.slots.Count; s++)
                    {
                        GrayboxSlot slot = plan.slots[s];
                        if (slot == null || slot.kind != SlotKind.Building) continue;
                        if (RectTouchesRoad(slot, path.nodes, halfRoad + spec.streetClearance))
                            AddGeometryIssue(errors, warnings, geometryAsErrors,
                                "建筑 " + slot.slotId + " 侵入道路 " + path.pathId + " 的净宽（" + path.width.ToString("0.#") + "m）");
                    }
                }
            }

            // 4) 建筑重叠（排屋相邻共用山墙算正常，重叠面积阈值放宽）
            for (int i = 0; i < plan.slots.Count; i++)
            {
                GrayboxSlot a = plan.slots[i];
                if (a == null || a.kind != SlotKind.Building) continue;
                for (int k = i + 1; k < plan.slots.Count; k++)
                {
                    GrayboxSlot b = plan.slots[k];
                    if (b == null || b.kind != SlotKind.Building) continue;
                    float overlap = RectOverlapArea(a, b);
                    if (overlap > 1.0f)
                        AddGeometryIssue(errors, warnings, geometryAsErrors,
                            "建筑 " + a.slotId + " 与 " + b.slotId + " 重叠 " + overlap.ToString("0.0") + " 平方米");
                }
            }

            // 4b) 高度：剧情给了高度就必须落地（height 曾经在 LayoutSpec → slot → 装配 三段里都被丢掉）
            if (plan.slots != null)
            {
                for (int i = 0; i < plan.slots.Count; i++)
                {
                    GrayboxSlot slot = plan.slots[i];
                    if (slot == null || string.IsNullOrEmpty(slot.specItemId)) continue;
                    LayoutItemSpec item = FindById(spec.items, slot.specItemId);
                    if (item == null || item.height <= 0.5f) continue;
                    if (Mathf.Abs(slot.height - item.height) > 1f)
                        warnings.Add("槽位 " + slot.slotId + " 高度 " + slot.height.ToString("0.#")
                                     + "m 与剧情 " + item.height.ToString("0.#") + "m 不一致");
                }
            }

            // 5) 非致命提醒
            int extras = 0;
            for (int i = 0; i < plan.slots.Count; i++)
                if (plan.slots[i] != null && string.IsNullOrEmpty(plan.slots[i].specItemId)) extras++;
            if (extras > 0) warnings.Add("灰盒额外添加了 " + extras + " 个规格外槽位（允许，但会被检查是否压路/重叠）");

            if (plan.regions != null)
            {
                for (int i = 0; i < plan.regions.Count; i++)
                {
                    GrayboxRegion a = plan.regions[i];
                    if (a == null) continue;
                    for (int k = i + 1; k < plan.regions.Count; k++)
                    {
                        GrayboxRegion b = plan.regions[k];
                        if (b == null) continue;
                        if (RegionOverlapsWithoutContainment(a, b))
                            warnings.Add("区域 " + a.regionId + " 与 " + b.regionId + " 有重叠（灰盒硬规则应避免）");
                    }
                }
            }

            plan.specCompliance = errors.Count == 0
                ? "pass · 规格项 " + spec.items.Count + " · 槽位 " + plan.slots.Count
                    + CoverageGapText(spec)
                : "fail(" + errors.Count + ") · " + errors[0];
            return errors.Count == 0;
        }

        /// <summary>
        /// 本校验只比「施工图 ↔ 槽位」，两侧同源，天然看不见剧情散文被正则丢掉的东西。
        /// 施工图现在自带覆盖审计结果（spec.diagnostics / spec.gaps），这里挂到合规串上，
        /// 避免出现「合规通过」而喷泉/钟楼/分区实际缺失的误导。
        /// </summary>
        private static string CoverageGapText(LayoutSpec spec)
        {
            if (spec == null) return "";
            int gaps = spec.gaps == null ? 0 : spec.gaps.Count;
            string diag = spec.diagnostics ?? "";
            if (gaps > 0)
            {
                string first = spec.gaps[0] ?? "";
                return " · 覆盖缺口 " + gaps + "（" + Shorten(first, 40) + "）";
            }
            if (!string.IsNullOrEmpty(diag)) return " · 覆盖审计[" + Shorten(diag, 60) + "]";
            return "";
        }

        private static string Shorten(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }

        // ─────────────────────────── 分区 / 动线还原 ───────────────────────────

        /// <summary>远端重定义分区后，按施工图重建 regions（分区是剧情指令，不是灰盒的自由度）。</summary>
        private static void RestoreRegions(GrayboxPlan plan, LayoutSpec spec, List<string> deviations)
        {
            if (plan == null || spec == null || spec.zones == null || spec.zones.Count == 0) return;
            float halfW = Mathf.Max(2f, plan.width * 0.5f);
            float halfL = Mathf.Max(2f, plan.length * 0.5f);
            Vector2 shift = plan.layoutShift;

            int before = plan.regions == null ? 0 : plan.regions.Count;
            var regions = new List<GrayboxRegion>();
            for (int i = 0; i < spec.zones.Count; i++)
            {
                LayoutZoneSpec zone = spec.zones[i];
                if (zone == null || zone.polygon == null || zone.polygon.Count < 3) continue;
                var poly = new List<Vector2>(zone.polygon.Count);
                for (int p = 0; p < zone.polygon.Count; p++)
                {
                    Vector2 v = zone.polygon[p] + shift;
                    poly.Add(new Vector2(Mathf.Clamp(v.x, -halfW, halfW), Mathf.Clamp(v.y, -halfL, halfL)));
                }
                regions.Add(new GrayboxRegion
                {
                    regionId = string.IsNullOrEmpty(zone.zoneId) ? "region_spec_" + i : "region_" + zone.zoneId,
                    kind = string.IsNullOrEmpty(zone.kind) ? "square" : zone.kind,
                    description = string.IsNullOrEmpty(zone.description) ? zone.kind : zone.description,
                    style = string.IsNullOrEmpty(zone.kind) ? "square" : zone.kind,
                    polygon = poly,
                    groundHeight = zone.groundHeight,
                    areaWeight = 1f,
                });
            }
            if (regions.Count == 0) return;
            if (before != regions.Count)
                deviations.Add("分区被远端改成 " + before + " 块，已按施工图恢复为 " + regions.Count + " 块");
            plan.regions = regions;
        }

        /// <summary>远端改写/丢失 NPC 动线后，按施工图重建 npcPaths。</summary>
        private static void RestoreNpcPaths(GrayboxPlan plan, LayoutSpec spec, List<string> deviations)
        {
            if (plan == null || spec == null || spec.npcs == null || spec.npcs.Count == 0) return;
            float halfW = Mathf.Max(2f, plan.width * 0.5f);
            float halfL = Mathf.Max(2f, plan.length * 0.5f);
            Vector2 shift = plan.layoutShift;

            var paths = new List<NpcPath>();
            for (int i = 0; i < spec.npcs.Count; i++)
            {
                LayoutNpcSpec npc = spec.npcs[i];
                if (npc == null || npc.waypoints == null || npc.waypoints.Count == 0) continue;
                var np = new NpcPath { npcId = npc.npcId ?? string.Empty, loop = npc.loop, speed = 1.1f };
                for (int w = 0; w < npc.waypoints.Count; w++)
                {
                    Vector2 v = npc.waypoints[w] + shift;
                    np.waypoints.Add(new Vector2(Mathf.Clamp(v.x, -halfW, halfW), Mathf.Clamp(v.y, -halfL, halfL)));
                }
                paths.Add(np);
            }
            if (paths.Count == 0) return;
            int before = plan.npcPaths == null ? 0 : plan.npcPaths.Count;
            if (before != paths.Count || NpcPathsMovedToSpawn(plan))
                deviations.Add("NPC 动线被远端改写（" + before + " 条），已按施工图恢复为 " + paths.Count + " 条");
            plan.npcPaths = paths;
        }

        /// <summary>远端常见表现：所有 npcPath 第一点都等于出生点。</summary>
        private static bool NpcPathsMovedToSpawn(GrayboxPlan plan)
        {
            if (plan.npcPaths == null) return false;
            int same = 0;
            for (int i = 0; i < plan.npcPaths.Count; i++)
            {
                NpcPath np = plan.npcPaths[i];
                if (np == null || np.waypoints == null || np.waypoints.Count == 0) continue;
                if (Vector2.Distance(np.waypoints[0], plan.spawnPoint) < 0.5f) same++;
            }
            return same > 0 && same == plan.npcPaths.Count;
        }

        // ─────────────────────────── 几何 ───────────────────────────

        /// <summary>
        /// 远端常把 seed 里的道路又 define_path 一次，生成两条几乎重合的 road；
        /// 这会让街面净宽校验对同一堵建筑报两次，也让道路渲染重叠。保留第一条（seed 顺序在前）。
        /// </summary>
        private static void DedupeRoads(GrayboxPlan plan, List<string> deviations)
        {
            if (plan == null || plan.paths == null || plan.paths.Count < 2) return;
            var kept = new List<GrayboxPath>(plan.paths.Count);
            for (int i = 0; i < plan.paths.Count; i++)
            {
                GrayboxPath path = plan.paths[i];
                if (path == null) continue;
                bool duplicate = false;
                for (int k = 0; k < kept.Count; k++)
                {
                    if (SameRoad(kept[k], path)) { duplicate = true; break; }
                }
                if (duplicate)
                {
                    deviations.Add("远端重复定义的 road " + path.pathId + " 已删除（与 seed 道路重合）");
                }
                else
                {
                    kept.Add(path);
                }
            }
            if (kept.Count != plan.paths.Count)
            {
                plan.paths = kept;
                Debug.LogWarning("[灰盒合规] 已清理 " + (deviations.Count) + " 条远端重复道路。");
            }
        }

        private static bool SameRoad(GrayboxPath a, GrayboxPath b)
        {
            if (a == null || b == null) return false;
            if (!string.Equals(a.kind, b.kind, StringComparison.OrdinalIgnoreCase)) return false;
            if (Mathf.Abs(a.width - b.width) > 0.8f) return false;
            if (a.nodes == null || b.nodes == null || a.nodes.Count < 2 || b.nodes.Count < 2) return false;
            Vector2 a0 = a.nodes[0];
            Vector2 a1 = a.nodes[a.nodes.Count - 1];
            Vector2 b0 = b.nodes[0];
            Vector2 b1 = b.nodes[b.nodes.Count - 1];
            bool sameDirection = Vector2.Distance(a0, b0) <= 3f && Vector2.Distance(a1, b1) <= 3f;
            bool reverseDirection = Vector2.Distance(a0, b1) <= 3f && Vector2.Distance(a1, b0) <= 3f;
            return sameDirection || reverseDirection;
        }

        private static void AddGeometryIssue(
            List<string> errors, List<string> warnings, bool asError, string message)
        {
            if (asError) errors.Add(message);
            else warnings.Add(message + "（剧情规格自身冲突，作为提醒）");
        }

        private static LayoutItemSpec FindById(List<LayoutItemSpec> items, string id)
        {
            for (int i = 0; i < items.Count; i++)
                if (items[i] != null && items[i].id == id) return items[i];
            return null;
        }

        private static Vector2 Clamp(Vector2 p, float halfW, float halfL)
        {
            return new Vector2(
                Mathf.Clamp(p.x, -halfW + 0.5f, halfW - 0.5f),
                Mathf.Clamp(p.y, -halfL + 0.5f, halfL - 0.5f));
        }

        private static bool RectTouchesRoad(GrayboxSlot slot, List<Vector2> nodes, float halfWidth)
        {
            float sx = Mathf.Abs(slot.sizeX);
            float sy = Mathf.Abs(slot.sizeY);
            bool swap = Mathf.Abs(Mathf.DeltaAngle(0f, slot.rotation)) > 45f
                        && Mathf.Abs(Mathf.DeltaAngle(0f, slot.rotation)) < 135f;
            if (swap) { float t = sx; sx = sy; sy = t; }
            float hx = sx * 0.5f;
            float hy = sy * 0.5f;
            Vector2 center = slot.position;
            for (int n = 0; n + 1 < nodes.Count; n++)
            {
                Vector2 a = nodes[n];
                Vector2 b = nodes[n + 1];
                // 四个角 + 四边中点，足够抓出压路。边界上恰好贴净宽不算侵入（留 5cm 数值容差）。
                float limit = halfWidth - 0.05f;
                for (int c = 0; c < 8; c++)
                {
                    float px = center.x + ((c == 0 || c == 3 || c == 5) ? -hx : (c == 2 || c == 4 || c == 7) ? hx : 0f);
                    float py = center.y + ((c == 0 || c == 1 || c == 2) ? -hy : (c == 5 || c == 6 || c == 7) ? hy : 0f);
                    if (PointSegmentDistance(new Vector2(px, py), a, b) < limit) return true;
                }
            }
            return false;
        }

        private static float PointSegmentDistance(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float len2 = ab.sqrMagnitude;
            if (len2 <= 0.0001f) return Vector2.Distance(p, a);
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
            return Vector2.Distance(p, a + ab * t);
        }

        private static float RectOverlapArea(GrayboxSlot a, GrayboxSlot b)
        {
            GetHalf(a, out float ahx, out float ahy);
            GetHalf(b, out float bhx, out float bhy);
            float overlapX = Mathf.Min(a.position.x + ahx, b.position.x + bhx)
                             - Mathf.Max(a.position.x - ahx, b.position.x - bhx);
            float overlapY = Mathf.Min(a.position.y + ahy, b.position.y + bhy)
                             - Mathf.Max(a.position.y - ahy, b.position.y - bhy);
            if (overlapX <= 0f || overlapY <= 0f) return 0f;
            return overlapX * overlapY;
        }

        private static void GetHalf(GrayboxSlot slot, out float hx, out float hy)
        {
            float sx = Mathf.Abs(slot.sizeX);
            float sy = Mathf.Abs(slot.sizeY);
            bool swap = Mathf.Abs(Mathf.DeltaAngle(0f, slot.rotation)) > 45f
                        && Mathf.Abs(Mathf.DeltaAngle(0f, slot.rotation)) < 135f;
            hx = (swap ? sy : sx) * 0.5f;
            hy = (swap ? sx : sy) * 0.5f;
        }

        private static bool RegionOverlapsWithoutContainment(GrayboxRegion a, GrayboxRegion b)
        {
            if (a.polygon == null || b.polygon == null || a.polygon.Count < 3 || b.polygon.Count < 3) return false;
            GetBounds(a.polygon, out float ax0, out float ax1, out float ay0, out float ay1);
            GetBounds(b.polygon, out float bx0, out float bx1, out float by0, out float by1);
            bool separate = ax1 <= bx0 || bx1 <= ax0 || ay1 <= by0 || by1 <= ay0;
            if (separate) return false;
            bool aContainsB = ax0 <= bx0 && ax1 >= bx1 && ay0 <= by0 && ay1 >= by1;
            bool bContainsA = bx0 <= ax0 && bx1 >= ax1 && by0 <= ay0 && by1 >= ay1;
            return !aContainsB && !bContainsA;
        }

        private static void GetBounds(List<Vector2> polygon, out float x0, out float x1, out float y0, out float y1)
        {
            x0 = float.MaxValue; x1 = float.MinValue; y0 = float.MaxValue; y1 = float.MinValue;
            for (int i = 0; i < polygon.Count; i++)
            {
                x0 = Mathf.Min(x0, polygon[i].x);
                x1 = Mathf.Max(x1, polygon[i].x);
                y0 = Mathf.Min(y0, polygon[i].y);
                y1 = Mathf.Max(y1, polygon[i].y);
            }
        }

        private static string SafeId(string text)
        {
            if (string.IsNullOrEmpty(text)) return "x";
            var sb = new System.Text.StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
            }
            return sb.ToString().Trim('_');
        }

        private static string UniqueId(string baseId, HashSet<string> used)
        {
            string id = baseId;
            int n = 1;
            while (used != null && used.Contains(id))
            {
                n++;
                id = baseId + "_" + n.ToString(CultureInfo.InvariantCulture);
            }
            if (used != null) used.Add(id);
            return id;
        }
    }
}
