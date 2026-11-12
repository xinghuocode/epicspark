// 把剧情布局解析结果提升成「结构化布局规格 LayoutSpec」。
//
// 为什么要多这一层：
//   剧情 Agent 的 layoutDescription 是散文，灰盒 LLM 看到的是「主要物件」，
//   于是 50×20 的连续街屋会被它理解成两个翼、每翼一栋 20 米大房子。
//   LayoutSpec 把「一条街屋 = 若干 8–9 米单元」写成机器可校验的施工图：
//     - 先按道路把占地切翼（保证 12 米街面净宽）
//     - 再按开间宽度拆单元（保证连续街屋的 count / rowId）
//     - 锚点/NPC 优先使用布局文本里的坐标，文本没有才回退 localPosition
//   LayoutSpec 是灰盒的输入约束，不是最终资产；旧数据/解析失败时返回空 spec，调用方回退旧路径。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using EpicSpark.Core;
using UnityEngine;

namespace EpicSpark.Story
{
    public static class StoryLayoutSpecBuilder
    {
        /// <summary>从 scene.layoutDescription 构建规格；解析不出内容时返回空 spec（Usable=false）。</summary>
        public static LayoutSpec Build(SceneSpec scene)
        {
            if (scene == null) return new LayoutSpec();
            StoryLayout layout = StoryLayoutParser.Parse(scene.layoutDescription);
            return Build(scene, layout);
        }

        /// <summary>用已解析好的 StoryLayout 构建规格，避免重复解析。</summary>
        public static LayoutSpec Build(SceneSpec scene, StoryLayout layout)
        {
            var spec = new LayoutSpec
            {
                sceneId = scene != null ? (scene.sceneId ?? string.Empty) : string.Empty,
                source = "layoutDescription",
            };
            if (scene == null || layout == null) return spec;

            // ── SceneGen 模式：剧情只交语义骨架，施工图交给确定性求解器 ──
            // 这一步刻意跳过下面全部"审计/补齐"逻辑。原因：
            //   · 覆盖审计会用散文正则重新推导锚点坐标（实测把 2/4 个锚点挪到建筑中心）；
            //   · AddMissingStoryUnits 会给同名地物补 2.4m 小方块，制造尺寸冲突与假缺口；
            //   · 行重排/夹取在旧链路里改了尺寸却没同步世界 AABB，导致排屋互相插入却"合规"。
            if (EpicSparkFeatureFlags.UseSceneGenSolver && scene.layoutSpec != null && scene.layoutSpec.Usable)
            {
                LayoutSpec semantic = CloneSpec(scene.layoutSpec);
                if (string.IsNullOrEmpty(semantic.sceneId)) semantic.sceneId = scene.sceneId ?? string.Empty;
                if (semantic.paths == null) semantic.paths = new List<LayoutPathSpec>();
                if (semantic.items == null) semantic.items = new List<LayoutItemSpec>();
                if (semantic.zones == null) semantic.zones = new List<LayoutZoneSpec>();
                if (semantic.npcs == null) semantic.npcs = new List<LayoutNpcSpec>();
                if (semantic.streets == null) semantic.streets = new List<LayoutStreetSpec>();
                if (semantic.landmarks == null) semantic.landmarks = new List<LayoutLandmarkSpec>();
                if (semantic.gaps == null) semantic.gaps = new List<string>();
                semantic.source = "storyLayoutSpec";
                return semantic;
            }

            // 剧情 Agent 已经直接给了结构化规格：以它为准，只补剧情单位与动线。
            // 注意：这里必须深拷贝——补单位会改动 spec，如果直接引用 scene.layoutSpec，
            // 后续重试/调试记录里会看到被就地改写的剧情输出，排查时无法区分"模型给的"和"我们补的"。
            if (scene.layoutSpec != null && scene.layoutSpec.Usable)
            {
                spec = CloneSpec(scene.layoutSpec);
                if (string.IsNullOrEmpty(spec.sceneId)) spec.sceneId = scene.sceneId ?? string.Empty;
                if (spec.paths == null) spec.paths = new List<LayoutPathSpec>();
                if (spec.items == null) spec.items = new List<LayoutItemSpec>();
                if (spec.zones == null) spec.zones = new List<LayoutZoneSpec>();
                if (spec.npcs == null) spec.npcs = new List<LayoutNpcSpec>();
                if (spec.gaps == null) spec.gaps = new List<string>();
                AddMissingStoryUnits(scene, layout, spec);
                ClampItemsToScene(spec, scene);
                SortByRow(spec);
                // 施工图由剧情直接给定：覆盖审计只做校验（报缺口），不再替模型补项/改尺寸。
                FinalizeSpec(scene, layout, spec, true);
                spec.source = "storyLayoutSpec+audit";
                return spec;
            }

            CopyPaths(layout, spec);
            AddBoundaryWalls(scene, spec);

            // 同一个 rowId 出现多次 = parser 已经按 9 米一间拆好了（浅进深排屋），不要再二次拆分。
            var splitRowIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < layout.items.Count; i++)
            {
                LayoutItem item = layout.items[i];
                if (item == null || string.IsNullOrEmpty(item.rowId)) continue;
                splitRowIds.Add(item.rowId);
            }
            var rowCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < layout.items.Count; i++)
            {
                LayoutItem item = layout.items[i];
                if (item == null || string.IsNullOrEmpty(item.rowId)) continue;
                int n;
                rowCounts.TryGetValue(item.rowId, out n);
                rowCounts[item.rowId] = n + 1;
            }

            var usedIds = new HashSet<string>(StringComparer.Ordinal);
            var carrierAt = new Dictionary<string, string>(StringComparer.Ordinal);
            int anchorCarrierCount = 0;

            for (int i = 0; i < layout.items.Count; i++)
            {
                LayoutItem item = layout.items[i];
                if (item == null) continue;
                string name = string.IsNullOrEmpty(item.name) ? "布局物件" : item.name;

                // 锚点载体（马车/台阶/门廊）：先落一个可视载体；锚点 trigger 在后面统一加。
                if (string.Equals(item.kind, "Anchor", StringComparison.Ordinal))
                {
                    string carrierKey = name + "|" + Mathf.RoundToInt(item.center.x * 10f) + "|" + Mathf.RoundToInt(item.center.y * 10f);
                    if (!carrierAt.ContainsKey(carrierKey))
                    {
                        string carrierId = UniqueId("carrier_" + SafeId(name) + "_" + anchorCarrierCount.ToString(CultureInfo.InvariantCulture), usedIds);
                        anchorCarrierCount++;
                        spec.items.Add(new LayoutItemSpec
                        {
                            id = carrierId,
                            name = name,
                            semantic = BuildSemantic(name, item.raw, scene, "锚点载体，可交互"),
                            kind = SlotKind.Prop,
                            center = item.center,
                            size = new Vector2(Mathf.Max(1.6f, item.width), Mathf.Max(1.6f, item.depth)),
                            rotation = item.rotation,
                            expectedModelHint = name,
                            required = true,
                            source = "layout-carrier",
                            notes = "anchor carrier",
                        });
                        carrierAt[carrierKey] = carrierId;
                    }
                    continue;
                }

                bool isRow = !string.IsNullOrEmpty(item.rowId) && item.rowCount > 1;
                bool alreadySplit = isRow && splitRowIds.Contains(item.rowId) && rowCounts[item.rowId] > 1;

                if (isRow && !alreadySplit)
                {
                    // 深进深长条：先按道路切翼，再按开间拆单元。
                    BuildRowUnits(item, layout, scene, spec, usedIds);
                    continue;
                }

                if (alreadySplit)
                {
                    AddUnit(spec, usedIds, new LayoutItemSpec
                    {
                        id = UniqueId("rowunit_" + SafeId(item.rowId) + "_" + (item.rowIndex + 1).ToString(CultureInfo.InvariantCulture), usedIds),
                        name = name,
                        semantic = BuildSemantic(name, item.raw, scene, "连续街屋单元"),
                        kind = ToSlotKind(item.kind),
                        center = item.center,
                        size = new Vector2(Mathf.Max(0.8f, item.width), Mathf.Max(0.8f, item.depth)),
                        rotation = item.rotation,
                        expectedModelHint = name,
                        rowId = item.rowId,
                        rowIndex = item.rowIndex,
                        rowCount = Mathf.Max(1, item.rowCount),
                        required = true,
                        source = "layout-row-unit",
                    });
                    continue;
                }

                AddUnit(spec, usedIds, new LayoutItemSpec
                {
                    id = UniqueId("item_" + SafeId(name) + "_" + i.ToString(CultureInfo.InvariantCulture), usedIds),
                    name = name,
                    semantic = BuildSemantic(name, item.raw, scene, ""),
                    kind = ToSlotKind(item.kind),
                    center = item.center,
                    size = new Vector2(Mathf.Max(0.8f, item.width), Mathf.Max(0.8f, item.depth)),
                    rotation = item.rotation,
                    expectedModelHint = name,
                    required = true,
                    source = "layout",
                });
            }

            AddMissingStoryUnits(scene, layout, spec);
            ClampItemsToScene(spec, scene);
            SortByRow(spec);
            FinalizeSpec(scene, layout, spec);
            return spec;
        }

        // ─────────────────────────── 收口：分区 / NPC 动线 / 覆盖审计 ───────────────────────────

        /// <summary>
        /// 把「散文 → 施工图」这一步产出的分区与 NPC 动线一并塞进 LayoutSpec，并跑覆盖审计。
        /// 灰盒此后只读 LayoutSpec（regions ← spec.zones，npcPaths ← spec.npcs），
        /// 不再自己二次解析散文——这是"灰盒听指挥"的结构前提。
        /// </summary>
        private static void FinalizeSpec(SceneSpec scene, StoryLayout layout, LayoutSpec spec, bool auditOnly = false)
        {
            // 施工图由剧情直给时，不再让散文解析器往里加分区（那会变成"两套真相"）；
            // 但 NPC 动线仍然要落到真实 npcId 上，否则 SceneAssembler 找不到对应 NPC。
            if (!auditOnly) ComposeZones(layout, spec);
            ComposeNpcs(scene, layout, spec);

            if (LayoutCoverage.Enabled)
            {
                LayoutCoverage.Report report = LayoutCoverage.Apply(scene, spec, null, !auditOnly);
                if (report != null) spec.diagnostics = report.Compact();
            }
            EnsureBaseZone(scene, spec);
            DropGroundItems(spec);
            if (string.IsNullOrEmpty(spec.diagnostics))
                spec.diagnostics = "未做覆盖审计";
            if (!auditOnly) spec.source = "layoutDescription+coverage";
        }

        /// <summary>深拷贝剧情直给的施工图，避免后续补全把模型的原始输出改写掉。</summary>
        private static LayoutSpec CloneSpec(LayoutSpec source)
        {
            var copy = new LayoutSpec
            {
                version = source.version,
                sceneId = source.sceneId,
                source = source.source,
                unitWidth = source.unitWidth,
                unitDepth = source.unitDepth,
                rowGap = source.rowGap,
                setback = source.setback,
                positionTolerance = source.positionTolerance,
                sizeTolerance = source.sizeTolerance,
                rowGapTolerance = source.rowGapTolerance,
                streetClearance = source.streetClearance,
                preserveCounts = source.preserveCounts,
                diagnostics = source.diagnostics,
                metrics = source.metrics,
            };
            if (source.items != null)
                for (int i = 0; i < source.items.Count; i++)
                {
                    LayoutItemSpec it = source.items[i];
                    if (it == null) continue;
                    copy.items.Add(new LayoutItemSpec
                    {
                        id = it.id, name = it.name, semantic = it.semantic, kind = it.kind,
                        center = it.center, size = it.size, rotation = it.rotation,
                        expectedModelHint = it.expectedModelHint, bindId = it.bindId,
                        rowId = it.rowId, rowIndex = it.rowIndex, rowCount = it.rowCount,
                        required = it.required, source = it.source, notes = it.notes, height = it.height,
                        faceDir = it.faceDir, primitive = it.primitive, solverGenerated = it.solverGenerated,
                    });
                }
            if (source.paths != null)
                for (int i = 0; i < source.paths.Count; i++)
                {
                    LayoutPathSpec p = source.paths[i];
                    if (p == null) continue;
                    var np = new LayoutPathSpec
                    {
                        pathId = p.pathId, kind = p.kind, width = p.width, required = p.required,
                    };
                    if (p.nodes != null) np.nodes = new List<Vector2>(p.nodes);
                    copy.paths.Add(np);
                }
            if (source.zones != null)
                for (int i = 0; i < source.zones.Count; i++)
                {
                    LayoutZoneSpec z = source.zones[i];
                    if (z == null) continue;
                    var nz = new LayoutZoneSpec
                    {
                        zoneId = z.zoneId, kind = z.kind, description = z.description,
                        groundHeight = z.groundHeight, required = z.required, source = z.source,
                        openness = z.openness, material = z.material,
                    };
                    if (z.polygon != null) nz.polygon = new List<Vector2>(z.polygon);
                    copy.zones.Add(nz);
                }
            if (source.streets != null)
                for (int i = 0; i < source.streets.Count; i++)
                {
                    LayoutStreetSpec s = source.streets[i];
                    if (s == null) continue;
                    var ns = new LayoutStreetSpec
                    {
                        pathId = s.pathId, name = s.name, width = s.width,
                        frontageBoth = s.frontageBoth, style = s.style,
                        depth = s.depth, floors = s.floors, continuous = s.continuous,
                    };
                    if (s.nodes != null) ns.nodes = new List<Vector2>(s.nodes);
                    copy.streets.Add(ns);
                }
            if (source.landmarks != null)
                for (int i = 0; i < source.landmarks.Count; i++)
                {
                    LayoutLandmarkSpec l = source.landmarks[i];
                    if (l == null) continue;
                    copy.landmarks.Add(new LayoutLandmarkSpec
                    {
                        name = l.name, kind = l.kind, on = l.on, facing = l.facing,
                        width = l.width, depth = l.depth, height = l.height,
                        pinned = l.pinned, pin = l.pin, hint = l.hint, semantic = l.semantic,
                        anchorBinding = l.anchorBinding,
                    });
                }
            if (source.npcs != null)
                for (int i = 0; i < source.npcs.Count; i++)
                {
                    LayoutNpcSpec n = source.npcs[i];
                    if (n == null) continue;
                    var nn = new LayoutNpcSpec { npcId = n.npcId, name = n.name, loop = n.loop, required = n.required };
                    if (n.waypoints != null) nn.waypoints = new List<Vector2>(n.waypoints);
                    copy.npcs.Add(nn);
                }
            if (source.gaps != null) copy.gaps = new List<string>(source.gaps);
            return copy;
        }

        /// <summary>
        /// 地面被解析成地标时会出现「石板广场 = 70×70 的 Prop 槽位」，最终在广场中央立一块巨物。
        /// 分区已经由 spec.zones 承载，这里把与分区同名的、面积 ≥ 200㎡ 的 Prop/Decoration 项删掉。
        /// </summary>
        private static void DropGroundItems(LayoutSpec spec)
        {
            if (spec == null || spec.zones == null || spec.zones.Count == 0) return;
            for (int i = spec.items.Count - 1; i >= 0; i--)
            {
                LayoutItemSpec item = spec.items[i];
                if (item == null) continue;
                if (item.kind != SlotKind.Prop && item.kind != SlotKind.Decoration) continue;
                if (item.size.x * item.size.y < 200f) continue;
                for (int z = 0; z < spec.zones.Count; z++)
                {
                    LayoutZoneSpec zone = spec.zones[z];
                    if (zone == null || string.IsNullOrEmpty(zone.description)) continue;
                    if (!string.Equals((item.name ?? "").Trim(), zone.description.Trim(), StringComparison.Ordinal)) continue;
                    spec.items.RemoveAt(i);
                    spec.diagnostics = string.IsNullOrEmpty(spec.diagnostics)
                        ? "地面项 " + item.name + " 已由分区承载"
                        : spec.diagnostics + " · 地面项 " + item.name + " 已由分区承载";
                    break;
                }
            }
        }

        /// <summary>
        /// 场地内必须处处有地面：散文只写了广场周边的分区时，北侧街屋/城门/锚点会落在所有分区之外，
        /// regionId 为空、下游绑定拿不到归属。这里补一块覆盖整个场地的基底分区（排在最前，
        /// 具体分区叠在上面；槽位取"包含它的最小分区"）。
        /// </summary>
        private static void EnsureBaseZone(SceneSpec scene, LayoutSpec spec)
        {
            if (scene == null || spec == null) return;
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            for (int i = 0; i < spec.zones.Count; i++)
            {
                LayoutZoneSpec z = spec.zones[i];
                if (z == null || z.polygon == null) continue;
                for (int p = 0; p < z.polygon.Count; p++)
                {
                    minX = Mathf.Min(minX, z.polygon[p].x); maxX = Mathf.Max(maxX, z.polygon[p].x);
                    minZ = Mathf.Min(minZ, z.polygon[p].y); maxZ = Mathf.Max(maxZ, z.polygon[p].y);
                }
            }
            float halfW = scene.width > 20f ? scene.width * 0.5f : Mathf.Max(20f, Mathf.Max(Mathf.Abs(minX), Mathf.Abs(maxX)));
            float halfL = scene.length > 20f ? scene.length * 0.5f : Mathf.Max(20f, Mathf.Max(Mathf.Abs(minZ), Mathf.Abs(maxZ)));
            if (spec.zones.Count > 0
                && minX <= -halfW + 2f && maxX >= halfW - 2f
                && minZ <= -halfL + 2f && maxZ >= halfL - 2f)
                return;

            spec.zones.Insert(0, new LayoutZoneSpec
            {
                zoneId = "zone_base",
                kind = "field",
                description = string.IsNullOrEmpty(scene.sceneName) ? "场景基底地面" : scene.sceneName + "·基底地面",
                polygon = new List<Vector2>
                {
                    new Vector2(-halfW, -halfL),
                    new Vector2(halfW, -halfL),
                    new Vector2(halfW, halfL),
                    new Vector2(-halfW, halfL),
                },
                groundHeight = 0f,
                required = false,
                source = "composed-base",
            });
        }

        /// <summary>解析器认出来的地面分区先进施工图；覆盖审计再补它漏掉的（东侧小巷等）。</summary>
        private static void ComposeZones(StoryLayout layout, LayoutSpec spec)
        {
            if (spec.zones == null) spec.zones = new List<LayoutZoneSpec>();
            if (layout == null || layout.zones == null) return;
            for (int i = 0; i < layout.zones.Count; i++)
            {
                LayoutZone zone = layout.zones[i];
                if (zone == null) continue;
                string name = string.IsNullOrEmpty(zone.name) ? zone.kind : zone.name;
                bool exists = false;
                for (int k = 0; k < spec.zones.Count; k++)
                {
                    LayoutZoneSpec z = spec.zones[k];
                    if (z == null) continue;
                    float cx = 0f, cz = 0f;
                    for (int p = 0; p < z.polygon.Count; p++) { cx += z.polygon[p].x; cz += z.polygon[p].y; }
                    if (z.polygon.Count > 0) { cx /= z.polygon.Count; cz /= z.polygon.Count; }
                    float ecx = (zone.minX + zone.maxX) * 0.5f;
                    float ecz = (zone.minZ + zone.maxZ) * 0.5f;
                    if (Mathf.Abs(cx - ecx) < 2f && Mathf.Abs(cz - ecz) < 2f) { exists = true; break; }
                }
                if (exists) continue;

                spec.zones.Add(new LayoutZoneSpec
                {
                    zoneId = "zone_" + i.ToString(CultureInfo.InvariantCulture) + "_" + SafeId(name),
                    kind = string.IsNullOrEmpty(zone.kind) ? "square" : zone.kind,
                    description = name,
                    polygon = new List<Vector2>
                    {
                        new Vector2(zone.minX, zone.minZ),
                        new Vector2(zone.maxX, zone.minZ),
                        new Vector2(zone.maxX, zone.maxZ),
                        new Vector2(zone.minX, zone.maxZ),
                    },
                    groundHeight = zone.kind == "water" ? -0.35f : 0f,
                    required = true,
                    source = "layout",
                });
            }
        }

        /// <summary>
        /// NPC 动线：起点用剧情给的坐标（散文坐标 → 锚点 → localPosition），
        /// 不再让所有 npcPath 都从出生点出发（08:57 记录：4 条动线起点全是 spawn）。
        /// </summary>
        private static void ComposeNpcs(SceneSpec scene, StoryLayout layout, LayoutSpec spec)
        {
            if (spec.npcs == null) spec.npcs = new List<LayoutNpcSpec>();
            if (scene == null || scene.npcs == null) return;

            float halfW = Mathf.Max(4f, scene.width * 0.5f - 2f);
            float halfL = Mathf.Max(4f, scene.length * 0.5f - 2f);

            // 1) 剧情直给的动线先按名字对齐到真实 npcId。
            //    模型给的 npcId 是它自己编的（"npc_铁匠"），SceneAssembler 按 npcId 找 NpcSpec，
            //    对不上时整条走位会被静默丢弃——这是"剧情写了走位、NPC 却站着不动"的根因。
            for (int i = 0; i < spec.npcs.Count; i++)
            {
                LayoutNpcSpec placed = spec.npcs[i];
                if (placed == null) continue;
                if (placed.waypoints == null) placed.waypoints = new List<Vector2>();
                if (string.IsNullOrEmpty(placed.npcId)) placed.npcId = "npc_place_" + i.ToString(CultureInfo.InvariantCulture);
                for (int k = 0; k < scene.npcs.Count; k++)
                {
                    NpcSpec npc = scene.npcs[k];
                    if (npc == null) continue;
                    if (!string.IsNullOrEmpty(npc.npcId) && placed.npcId == npc.npcId) break;
                    if (string.IsNullOrEmpty(placed.name) || string.IsNullOrEmpty(npc.displayName)) continue;
                    if (!NamesMatch(placed.name, npc.displayName)) continue;
                    if (!string.IsNullOrEmpty(npc.npcId)) placed.npcId = npc.npcId;
                    break;
                }
            }

            // 2) 场景里还没有动线的 NPC：用剧情坐标合成一条环形走位。
            for (int i = 0; i < scene.npcs.Count; i++)
            {
                NpcSpec npc = scene.npcs[i];
                if (npc == null) continue;

                bool exists = false;
                for (int k = 0; k < spec.npcs.Count; k++)
                {
                    LayoutNpcSpec placed = spec.npcs[k];
                    if (placed != null && !string.IsNullOrEmpty(npc.npcId) && placed.npcId == npc.npcId) { exists = true; break; }
                }
                if (exists) continue;

                Vector2 pos = ResolveNpcPosition(npc, layout);
                if (pos == Vector2.zero)
                    pos = new Vector2(scene.spawnPoint.x, scene.spawnPoint.z);
                pos = new Vector2(Mathf.Clamp(pos.x, -halfW, halfW), Mathf.Clamp(pos.y, -halfL, halfL));

                float r = 3f;
                var waypoints = new List<Vector2>
                {
                    pos,
                    new Vector2(Mathf.Clamp(pos.x + r, -halfW, halfW), pos.y),
                    new Vector2(Mathf.Clamp(pos.x + r, -halfW, halfW), Mathf.Clamp(pos.y + r, -halfL, halfL)),
                    new Vector2(pos.x, Mathf.Clamp(pos.y + r, -halfL, halfL)),
                };

                spec.npcs.Add(new LayoutNpcSpec
                {
                    npcId = npc.npcId ?? string.Empty,
                    name = npc.displayName ?? string.Empty,
                    waypoints = waypoints,
                    loop = true,
                    required = true,
                });
            }
        }

        /// <summary>在施工图的 NPC 动线里找这个剧情 NPC（按 npcId 或名字对齐）。</summary>
        private static LayoutNpcSpec FindPlacedNpc(LayoutSpec spec, NpcSpec npc)
        {
            if (spec == null || spec.npcs == null || npc == null) return null;
            for (int i = 0; i < spec.npcs.Count; i++)
            {
                LayoutNpcSpec placed = spec.npcs[i];
                if (placed == null) continue;
                if (!string.IsNullOrEmpty(placed.npcId) && placed.npcId == npc.npcId) return placed;
                if (NamesMatch(placed.name, npc.displayName)) return placed;
            }
            return null;
        }

        /// <summary>名字对齐：忽略空白/标点后的互相包含（"铁匠" ↔ "铁匠·马修"）。</summary>
        private static bool NamesMatch(string a, string b)
        {
            string x = Normalize(a);
            string y = Normalize(b);
            if (x.Length == 0 || y.Length == 0) return false;
            if (string.Equals(x, y, StringComparison.Ordinal)) return true;
            if (x.Length >= 2 && y.IndexOf(x, StringComparison.Ordinal) >= 0) return true;
            if (y.Length >= 2 && x.IndexOf(y, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        private static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            }
            return sb.ToString();
        }

        // ─────────────────────────── 排屋 ───────────────────────────

        private static void BuildRowUnits(
            LayoutItem item, StoryLayout layout, SceneSpec scene, LayoutSpec spec, HashSet<string> usedIds)
        {
            string name = string.IsNullOrEmpty(item.name) ? "连续街屋" : item.name;
            float halfW = Mathf.Max(0.5f, item.width) * 0.5f;
            float halfD = Mathf.Max(0.5f, item.depth) * 0.5f;
            var pieces = new List<Rect>
            {
                new Rect(item.center.x - halfW, item.center.y - halfD, item.width, item.depth)
            };

            // 只处理轴对齐道路；斜路/墙不参与切分，避免把地块切碎。
            pieces = SubtractRoadBands(pieces, layout.paths, spec.streetClearance);

            // 排屋朝向由「原始地块」的长轴决定，而不是由道路切出来的每一翼决定。
            // 50×20 的南侧民居群被 12 米主路切成两个 18.65×20 的翼，如果按每翼重新判断，
            // 18.65 < 20 会误判成南北向，单元变成 18.65×10，不再沿街连续。
            bool rowAlongX = item.width >= item.depth;
            int pieceIndex = 0;
            for (int p = 0; p < pieces.Count; p++)
            {
                Rect piece = pieces[p];
                if (piece.width < 1f || piece.height < 1f) continue;

                bool alongX = rowAlongX;
                float longLen = alongX ? piece.width : piece.height;
                int count = Mathf.Clamp(Mathf.RoundToInt(longLen / Mathf.Max(2f, spec.unitWidth)), 1, 8);
                // 50 米长条按 8.5 米开间：5.88 → 6 间；18.5 米的一翼：2.18 → 2 间。
                if (count <= 1 && longLen >= 12f) count = 2;

                string rowId = "row_" + SafeId(name) + "_" + pieceIndex.ToString(CultureInfo.InvariantCulture);
                pieceIndex++;

                float slotW = alongX ? piece.width / count : piece.width;
                float slotD = alongX ? piece.height : piece.height / count;
                for (int k = 0; k < count; k++)
                {
                    float offset = k - (count - 1) * 0.5f;
                    float cx = piece.x + piece.width * 0.5f + (alongX ? offset * slotW : 0f);
                    float cz = piece.y + piece.height * 0.5f + (alongX ? 0f : offset * slotD);
                    string unitId = UniqueId(
                        "rowunit_" + SafeId(name) + "_" + pieceIndex.ToString(CultureInfo.InvariantCulture)
                        + "_" + (k + 1).ToString(CultureInfo.InvariantCulture), usedIds);

                    AddUnit(spec, usedIds, new LayoutItemSpec
                    {
                        id = unitId,
                        name = name + " 单元 " + (k + 1).ToString(CultureInfo.InvariantCulture) + "/" + count.ToString(CultureInfo.InvariantCulture),
                        semantic = BuildSemantic(name, item.raw, scene, "连续街屋单元，底层为店铺"),
                        kind = SlotKind.Building,
                        center = new Vector2(cx, cz),
                        size = new Vector2(Mathf.Max(1f, slotW), Mathf.Max(1f, slotD)),
                        // 槽位的 sizeX/sizeY 已经是世界轴对齐尺寸；rotation 只表示模型朝向。
                        // 如果这里再转 90°，合规/搭建会把尺寸交换，导致相邻单元互相压住。
                        rotation = 0f,
                        expectedModelHint = name + "，连续街屋，底层店铺",
                        rowId = rowId,
                        rowIndex = k,
                        rowCount = count,
                        required = true,
                        source = "layout-row",
                        notes = "row split by unitWidth " + spec.unitWidth.ToString("0.##", CultureInfo.InvariantCulture),
                    });
                }
            }
        }

        /// <summary>从一组矩形里减去轴对齐道路的带状走廊（含 streetClearance）。</summary>
        private static List<Rect> SubtractRoadBands(List<Rect> input, List<LayoutPath> paths, float clearance)
        {
            List<Rect> current = input;
            if (paths == null) return current;
            for (int pass = 0; pass < 3; pass++)
            {
                bool changed = false;
                for (int i = 0; i < paths.Count; i++)
                {
                    LayoutPath path = paths[i];
                    if (path == null || path.nodes == null || path.nodes.Count < 2) continue;
                    if (!string.Equals(path.kind, "road", StringComparison.OrdinalIgnoreCase)) continue;
                    float half = Mathf.Max(0.1f, path.width) * 0.5f + Mathf.Max(0f, clearance);
                    for (int n = 0; n + 1 < path.nodes.Count; n++)
                    {
                        Vector2 a = path.nodes[n];
                        Vector2 b = path.nodes[n + 1];
                        bool vertical = Mathf.Abs(a.x - b.x) <= 0.01f;
                        bool horizontal = Mathf.Abs(a.y - b.y) <= 0.01f;
                        if (!vertical && !horizontal) continue;
                        // 道路只在自身线段范围内切分地块。否则一条 z=20 的西街会把 z=20 的东侧排屋也切掉，
                        // 产生一排本不该存在的 9 米窄单元，并与真正切出来的单元重叠。
                        float segMinX = Mathf.Min(a.x, b.x);
                        float segMaxX = Mathf.Max(a.x, b.x);
                        float segMinY = Mathf.Min(a.y, b.y);
                        float segMaxY = Mathf.Max(a.y, b.y);
                        var next = new List<Rect>(current.Count + 2);
                        for (int r = 0; r < current.Count; r++)
                        {
                            Rect rect = current[r];
                            if (vertical)
                            {
                                if (segMaxY <= rect.yMin || segMinY >= rect.yMax) { next.Add(rect); continue; }
                                float bandMin = a.x - half;
                                float bandMax = a.x + half;
                                if (bandMax <= rect.xMin || bandMin >= rect.xMax) { next.Add(rect); continue; }
                                if (bandMin > rect.xMin) next.Add(new Rect(rect.xMin, rect.yMin, bandMin - rect.xMin, rect.height));
                                if (bandMax < rect.xMax) next.Add(new Rect(bandMax, rect.yMin, rect.xMax - bandMax, rect.height));
                                changed = true;
                            }
                            else
                            {
                                if (segMaxX <= rect.xMin || segMinX >= rect.xMax) { next.Add(rect); continue; }
                                float bandMin = a.y - half;
                                float bandMax = a.y + half;
                                if (bandMax <= rect.yMin || bandMin >= rect.yMax) { next.Add(rect); continue; }
                                if (bandMin > rect.yMin) next.Add(new Rect(rect.xMin, rect.yMin, rect.width, bandMin - rect.yMin));
                                if (bandMax < rect.yMax) next.Add(new Rect(rect.xMin, bandMax, rect.width, rect.yMax - bandMax));
                                changed = true;
                            }
                        }
                        current = next;
                    }
                }
                if (!changed) break;
            }
            // 去掉被切出来的细小碎片（墙缝/边角）
            var filtered = new List<Rect>();
            for (int i = 0; i < current.Count; i++)
                if (current[i].width >= 1f && current[i].height >= 1f) filtered.Add(current[i]);
            return filtered.Count > 0 ? filtered : input;
        }

        // ─────────────────────────── 锚点 / NPC / 道具 ───────────────────────────

        private static void AddMissingStoryUnits(SceneSpec scene, StoryLayout layout, LayoutSpec spec)
        {
            var boundAnchors = new HashSet<string>(StringComparer.Ordinal);
            var boundNpcs = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < spec.items.Count; i++)
            {
                LayoutItemSpec item = spec.items[i];
                if (item == null || string.IsNullOrEmpty(item.bindId)) continue;
                if (item.kind == SlotKind.Anchor) boundAnchors.Add(item.bindId);
                else if (item.kind == SlotKind.Npc) boundNpcs.Add(item.bindId);
            }

            var usedIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < spec.items.Count; i++)
                if (spec.items[i] != null && !string.IsNullOrEmpty(spec.items[i].id)) usedIds.Add(spec.items[i].id);

            if (scene.anchors != null)
            {
                for (int i = 0; i < scene.anchors.Count; i++)
                {
                    SceneAnchorSpec anchor = scene.anchors[i];
                    if (anchor == null || string.IsNullOrEmpty(anchor.anchorId) || boundAnchors.Contains(anchor.anchorId)) continue;
                    Vector2 pos = ResolveAnchorPosition(anchor, layout);
                    spec.items.Add(new LayoutItemSpec
                    {
                        id = UniqueId("anchor_" + SafeId(anchor.anchorId), usedIds),
                        name = string.IsNullOrEmpty(anchor.displayName) ? anchor.anchorId : anchor.displayName,
                        semantic = BuildUnitSemantic(anchor.displayName, anchor.description, anchor.requirement),
                        kind = SlotKind.Anchor,
                        center = pos,
                        size = new Vector2(2.4f, 2.4f),
                        rotation = 0f,
                        expectedModelHint = anchor.modelHint,
                        bindId = anchor.anchorId,
                        required = true,
                        source = "anchor",
                    });
                    boundAnchors.Add(anchor.anchorId);
                }
            }

            if (scene.npcs != null)
            {
                for (int i = 0; i < scene.npcs.Count; i++)
                {
                    NpcSpec npc = scene.npcs[i];
                    if (npc == null || string.IsNullOrEmpty(npc.npcId) || boundNpcs.Contains(npc.npcId)) continue;
                    // 剧情直给了走位：站位必须等于走位起点，否则 NPC 第一帧站在散文兜底位置、
                    // 随后又瞬移到路线上（"NPC 会瞬移"的观感来源）。
                    Vector2 pos = Vector2.zero;
                    LayoutNpcSpec placed = FindPlacedNpc(spec, npc);
                    if (placed != null && placed.waypoints != null && placed.waypoints.Count > 0)
                        pos = placed.waypoints[0];
                    if (pos == Vector2.zero) pos = ResolveNpcPosition(npc, layout);
                    spec.items.Add(new LayoutItemSpec
                    {
                        id = UniqueId("npc_" + SafeId(npc.npcId), usedIds),
                        name = string.IsNullOrEmpty(npc.displayName) ? npc.npcId : npc.displayName,
                        semantic = BuildUnitSemantic(npc.displayName, npc.persona, npc.storyRole),
                        kind = SlotKind.Npc,
                        center = pos,
                        size = new Vector2(0.6f, 0.6f),
                        rotation = 0f,
                        bindId = npc.npcId,
                        required = true,
                        source = "npc",
                    });
                    boundNpcs.Add(npc.npcId);
                }
            }

            if (scene.props != null)
            {
                var boundProps = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < spec.items.Count; i++)
                {
                    LayoutItemSpec item = spec.items[i];
                    if (item != null && item.kind == SlotKind.Prop && !string.IsNullOrEmpty(item.bindId))
                        boundProps.Add(item.bindId);
                }
                for (int i = 0; i < scene.props.Count; i++)
                {
                    PropInteraction prop = scene.props[i];
                    if (prop == null || string.IsNullOrEmpty(prop.propId) || boundProps.Contains(prop.propId)) continue;
                    Vector2 pos = ResolvePropPosition(prop, layout);

                    // 去重：散文已经把这件道具写成地标时，不再叠一层 Prop 槽位。
                    // 08:57 记录里「公告木柱」地标与 prop_p_0 落在同一点，最终场景立了两根柱子。
                    LayoutItemSpec existing = FindExistingItemForUnit(spec, prop.displayName, pos, 2.5f);
                    if (existing != null)
                    {
                        if (string.IsNullOrEmpty(existing.bindId)) existing.bindId = prop.propId;
                        boundProps.Add(prop.propId);
                        continue;
                    }

                    spec.items.Add(new LayoutItemSpec
                    {
                        id = UniqueId("prop_" + SafeId(prop.propId), usedIds),
                        name = string.IsNullOrEmpty(prop.displayName) ? prop.propId : prop.displayName,
                        semantic = BuildUnitSemantic(prop.displayName, prop.description, prop.effect),
                        kind = SlotKind.Prop,
                        center = pos,
                        size = new Vector2(2f, 2f),
                        rotation = 0f,
                        bindId = prop.propId,
                        required = true,
                        source = "prop",
                    });
                    boundProps.Add(prop.propId);
                }
            }
        }

        private static Vector2 ResolveAnchorPosition(SceneAnchorSpec anchor, StoryLayout layout)
        {
            // 1) 布局文本里同名地标（「出租马车停在 (0,-38)」）
            Vector2 matched = MatchItem(layout, anchor.displayName);
            if (matched != Vector2.zero) return matched;
            matched = MatchItem(layout, anchor.requirement);
            if (matched != Vector2.zero) return matched;

            // 2) 锚点描述/载体要求里直接写的坐标
            Vector2 point;
            if (StoryLayoutParser.TryFindPoint(anchor.description, out point)) return point;
            if (StoryLayoutParser.TryFindPoint(anchor.requirement, out point)) return point;

            // 3) 旧数据回退：Story 确定性构建器扇形填的 localPosition
            return new Vector2(anchor.localPosition.x, anchor.localPosition.z);
        }

        private static Vector2 ResolveNpcPosition(NpcSpec npc, StoryLayout layout)
        {
            Vector2 matched = MatchItem(layout, npc.displayName);
            if (matched != Vector2.zero) return matched;
            Vector2 point;
            if (StoryLayoutParser.TryFindPoint(npc.persona, out point)) return point;
            if (StoryLayoutParser.TryFindPoint(npc.storyRole, out point)) return point;
            return new Vector2(npc.localPosition.x, npc.localPosition.z);
        }

        private static Vector2 ResolvePropPosition(PropInteraction prop, StoryLayout layout)
        {
            Vector2 matched = MatchItem(layout, prop.displayName);
            if (matched != Vector2.zero) return matched;
            Vector2 point;
            if (StoryLayoutParser.TryFindPoint(prop.description, out point)) return point;
            if (prop.localPosition.x != 0f || prop.localPosition.z != 0f)
                return new Vector2(prop.localPosition.x, prop.localPosition.z);
            return Vector2.zero;
        }

        /// <summary>
        /// 占位单位（道具/NPC/锚点）是否已经由散文/施工图里的某件地物承载。
        /// 命中则把 bindId 挂上去复用，不再新增一个重叠的槽位。
        /// </summary>
        private static LayoutItemSpec FindExistingItemForUnit(LayoutSpec spec, string name, Vector2 pos, float tolerance)
        {
            if (spec == null || spec.items == null) return null;
            for (int i = 0; i < spec.items.Count; i++)
            {
                LayoutItemSpec item = spec.items[i];
                if (item == null || item.kind == SlotKind.Npc || item.kind == SlotKind.Anchor) continue;
                bool sameName = !string.IsNullOrEmpty(name) && NamesMatch(name, item.name);
                float distance = Vector2.Distance(pos, item.center);
                if (sameName && distance <= Mathf.Max(tolerance * 4f, 6f)) return item;
                bool reusableKind = item.kind == SlotKind.Prop || item.kind == SlotKind.Decoration;
                if (reusableKind && distance <= tolerance) return item;
            }
            return null;
        }

        private static Vector2 MatchItem(StoryLayout layout, string text)
        {
            if (layout == null || string.IsNullOrEmpty(text)) return Vector2.zero;
            string name = text.Trim();
            Vector2 best = Vector2.zero;
            int bestScore = 0;
            for (int i = 0; i < layout.items.Count; i++)
            {
                LayoutItem item = layout.items[i];
                if (item == null || string.IsNullOrEmpty(item.name)) continue;
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

        // ─────────────────────────── 辅助 ───────────────────────────

        /// <summary>
        /// 剧情常写「四周由建筑外墙围合……只有南侧和东北角两个缺口」，
        /// 但 parser 只识别道路，不识别边界；于是广场只剩几栋孤零零的房子。
        /// 这里按场景尺寸补四面围墙，并在文本给出的缺口处断开。
        /// </summary>
        private static void AddBoundaryWalls(SceneSpec scene, LayoutSpec spec)
        {
            string text = scene != null ? (scene.layoutDescription ?? "") : "";
            if (text.Length == 0) return;
            if (text.IndexOf("边界", StringComparison.Ordinal) < 0) return;
            if (text.IndexOf("围合", StringComparison.Ordinal) < 0
                && text.IndexOf("不可翻越", StringComparison.Ordinal) < 0)
                return;

            for (int i = 0; i < spec.paths.Count; i++)
                if (spec.paths[i] != null && (spec.paths[i].kind == "wall" || spec.paths[i].kind == "fence"))
                    return;

            int start = text.IndexOf("边界", StringComparison.Ordinal);
            int end = text.IndexOf("【", start + 2, StringComparison.Ordinal);
            string section = end > start ? text.Substring(start, end - start) : text.Substring(start);

            var points = new List<Vector2>();
            MatchCollection pm = Regex.Matches(section, @"\(\s*([+-]?\d+(?:\.\d+)?)\s*,\s*([+-]?\d+(?:\.\d+)?)\s*\)");
            for (int i = 0; i < pm.Count && points.Count < 2; i++)
            {
                float px, pz;
                if (float.TryParse(pm[i].Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out px)
                    && float.TryParse(pm[i].Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out pz))
                    points.Add(new Vector2(px, pz));
            }
            if (points.Count == 0) return;

            float halfW = Mathf.Max(4f, scene.width * 0.5f - 0.5f);
            float halfL = Mathf.Max(4f, scene.length * 0.5f - 0.5f);
            float gap = 6f;
            Match gapMatch = Regex.Match(section, @"宽\s*(\d+(?:\.\d+)?)\s*米");
            if (gapMatch.Success)
            {
                float g;
                if (float.TryParse(gapMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out g))
                    gap = Mathf.Clamp(g, 2f, 12f);
            }
            gap *= 0.5f;

            Vector2 southGap = points[0];
            Vector2 northEastGap = points.Count > 1 ? points[1] : new Vector2(halfW * 0.8f, halfL * 0.8f);
            float wallWidth = 6f;

            // 南墙
            AddWall(spec, "boundary_south_w", new Vector2(-halfW, -halfL), new Vector2(southGap.x - gap, -halfL), wallWidth);
            AddWall(spec, "boundary_south_e", new Vector2(southGap.x + gap, -halfL), new Vector2(halfW, -halfL), wallWidth);
            // 北墙（东北角巷口）
            AddWall(spec, "boundary_north_w", new Vector2(-halfW, halfL), new Vector2(northEastGap.x - gap, halfL), wallWidth);
            AddWall(spec, "boundary_north_e", new Vector2(northEastGap.x + gap, halfL), new Vector2(halfW, halfL), wallWidth);
            // 东墙（东北角巷口）
            AddWall(spec, "boundary_east_s", new Vector2(halfW, -halfL), new Vector2(halfW, northEastGap.y - gap), wallWidth);
            AddWall(spec, "boundary_east_n", new Vector2(halfW, northEastGap.y + gap), new Vector2(halfW, halfL), wallWidth);
            // 西墙
            AddWall(spec, "boundary_west", new Vector2(-halfW, -halfL), new Vector2(-halfW, halfL), wallWidth);
        }

        private static void AddWall(LayoutSpec spec, string id, Vector2 a, Vector2 b, float width)
        {
            if (Vector2.Distance(a, b) < 0.5f) return;
            var wall = new LayoutPathSpec { pathId = id, kind = "wall", width = width };
            wall.nodes.Add(a);
            wall.nodes.Add(b);
            spec.paths.Add(wall);
        }

        private static void CopyPaths(StoryLayout layout, LayoutSpec spec)
        {
            spec.paths = new List<LayoutPathSpec>();
            for (int i = 0; i < layout.paths.Count; i++)
            {
                LayoutPath path = layout.paths[i];
                if (path == null || path.nodes == null || path.nodes.Count < 2) continue;
                var copy = new LayoutPathSpec
                {
                    pathId = "spec_path_" + i.ToString(CultureInfo.InvariantCulture),
                    kind = path.kind,
                    width = path.width,
                };
                for (int n = 0; n < path.nodes.Count; n++) copy.nodes.Add(path.nodes[n]);
                spec.paths.Add(copy);
            }
        }

        private static void AddUnit(LayoutSpec spec, HashSet<string> usedIds, LayoutItemSpec item)
        {
            if (item == null) return;
            if (string.IsNullOrEmpty(item.id)) item.id = UniqueId("item", usedIds);
            if (!usedIds.Add(item.id))
                item.id = UniqueId(item.id, usedIds);
            spec.items.Add(item);
        }

        /// <summary>
        /// 「正立面在 Z=+21、进深 10 米」会把北侧建筑中心推到 26 米，后墙越过 60×60 场地。
        /// 只平移、不缩放，避免剧情明确写了尺寸的建筑被压扁；至少保证主体在场景内。
        /// </summary>
        private static void ClampItemsToScene(LayoutSpec spec, SceneSpec scene)
        {
            if (spec == null || spec.items == null || scene == null) return;
            float halfW = Mathf.Max(2f, scene.width * 0.5f);
            float halfL = Mathf.Max(2f, scene.length * 0.5f);
            for (int i = 0; i < spec.items.Count; i++)
            {
                LayoutItemSpec item = spec.items[i];
                if (item == null || item.size.x <= 0f || item.size.y <= 0f) continue;

                float hx = Mathf.Min(item.size.x * 0.5f, halfW - 0.5f);
                float hz = Mathf.Min(item.size.y * 0.5f, halfL - 0.5f);
                item.center = new Vector2(
                    Mathf.Clamp(item.center.x, -halfW + hx, halfW - hx),
                    Mathf.Clamp(item.center.y, -halfL + hz, halfL - hz));
            }
        }

        private static void SortByRow(LayoutSpec spec)
        {
            if (spec.items == null || spec.items.Count <= 1) return;
            spec.items.Sort((a, b) =>
            {
                if (a == null || b == null) return 0;
                int row = string.CompareOrdinal(a.rowId ?? "", b.rowId ?? "");
                if (row != 0) return row;
                return a.rowIndex.CompareTo(b.rowIndex);
            });
        }

        private static SlotKind ToSlotKind(string kind)
        {
            if (string.Equals(kind, "Building", StringComparison.OrdinalIgnoreCase)) return SlotKind.Building;
            if (string.Equals(kind, "Prop", StringComparison.OrdinalIgnoreCase)) return SlotKind.Prop;
            if (string.Equals(kind, "Anchor", StringComparison.OrdinalIgnoreCase)) return SlotKind.Anchor;
            return SlotKind.Decoration;
        }

        private static string BuildSemantic(string name, string raw, SceneSpec scene, string suffix)
        {
            string era = scene != null ? (scene.era ?? "") : "";
            string text = name + "，" + (string.IsNullOrEmpty(era) ? "" : era + "，") + (raw ?? "");
            if (!string.IsNullOrEmpty(suffix)) text += "，" + suffix;
            return Trim(text, 160);
        }

        private static string BuildUnitSemantic(string displayName, string a, string b)
        {
            string text = (displayName ?? "") + "，" + (a ?? "");
            if (!string.IsNullOrEmpty(b)) text += "，" + b;
            return Trim(text, 160);
        }

        private static string Trim(string text, int max)
        {
            text = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
            return text.Length <= max ? text : text.Substring(0, max);
        }

        private static string SafeId(string text)
        {
            if (string.IsNullOrEmpty(text)) return "x";
            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-') sb.Append(c);
                else sb.Append('_');
            }
            string result = sb.ToString().Trim('_');
            return result.Length == 0 ? "x" : result;
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
            return id;
        }
    }
}
