// 工具驱动的灰盒工作区。
// 剧情 SceneSpec / 语义 LayoutSpec 永远是只读输入；这里持有一份独立的可施工状态。
// 所有尺寸均为世界 XZ 轴对齐包围盒，rotation/faceDir 只表达立面朝向。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using EpicSpark.Core;
using UnityEngine;

namespace EpicSpark.SceneGen
{
    /// <summary>工具执行记录。Errors 中任何一项都必须阻止场景进入搭建。</summary>
    public sealed class GrayboxToolReport
    {
        public readonly List<string> Operations = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public readonly List<string> Errors = new List<string>();
        public int Accepted;
        public int Rejected;

        public void Accept(string operation, string detail)
        {
            Accepted++;
            Operations.Add(operation + " ✓ " + detail);
        }

        public void Warn(string operation, string detail)
        {
            Warnings.Add(operation + "： " + detail);
        }

        public void Reject(string operation, string detail)
        {
            Rejected++;
            Errors.Add(operation + "： " + detail);
        }

        public string Compact()
        {
            return "工具 " + Accepted.ToString(CultureInfo.InvariantCulture)
                   + " 成功 / " + Rejected.ToString(CultureInfo.InvariantCulture) + " 拒绝"
                   + " · 警告 " + Warnings.Count.ToString(CultureInfo.InvariantCulture)
                   + " · 错误 " + Errors.Count.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// 灰盒 Agent 的受限工具集。每个公开操作都是一次可记录的灰盒编辑；
    /// 不接受“有冲突也先放下”的静默降级。
    /// </summary>
    public sealed class GrayboxToolWorkspace
    {
        private readonly SceneSpec scene;
        private readonly LayoutSpec source;
        private readonly int seed;
        private readonly Rect site;
        private readonly LayoutSpec state;
        private SceneGenRng rng;

        public GrayboxToolReport Report { get; private set; }
        public LayoutSpec State { get { return state; } }
        public Rect Site { get { return site; } }

        public GrayboxToolWorkspace(SceneSpec storyScene, LayoutSpec semantic, int layoutSeed)
        {
            scene = storyScene ?? new SceneSpec();
            source = CloneSpec(semantic) ?? new LayoutSpec();
            seed = layoutSeed;
            Report = new GrayboxToolReport();
            float width = SceneSize.SideOrDefault(scene.width);
            float length = SceneSize.SideOrDefault(scene.length);
            site = SceneGenRect.FromCenter(Vector2.zero, width, length);
            rng = new SceneGenRng(seed);
            state = new LayoutSpec
            {
                version = "3",
                sceneId = scene.sceneId ?? string.Empty,
                source = "graybox-tools",
                unitWidth = Mathf.Clamp(source.unitWidth > 0f ? source.unitWidth : 8.5f, 5f, 16f),
                unitDepth = Mathf.Clamp(source.unitDepth > 0f ? source.unitDepth : 10f, 5f, 18f),
                rowGap = 0f,
                setback = Mathf.Clamp(source.setback >= 0f ? source.setback : 1.2f, 0f, 4f),
            };
        }

        /// <summary>把剧情/解析器给出的语义逐项翻译成工具调用，不直接接管其对象引用。</summary>
        public void ImportStoryRequirements()
        {
            if (source.zones != null)
                for (int i = 0; i < source.zones.Count; i++) DefineZone(source.zones[i]);
            if (source.paths != null)
                for (int i = 0; i < source.paths.Count; i++) DefinePath(source.paths[i]);
            if (source.streets != null)
                for (int i = 0; i < source.streets.Count; i++) DefineStreet(source.streets[i]);
            EnsureStreetSemantics();
            if (source.npcs != null)
                for (int i = 0; i < source.npcs.Count; i++) DefineNpcRoute(source.npcs[i]);

            // 旧的“逐栋排屋坐标”不再作为硬施工图；由 CreateStreetRows 按道路重新切分连续地块。
            if (source.items != null)
            {
                for (int i = 0; i < source.items.Count; i++)
                {
                    LayoutItemSpec item = source.items[i];
                    if (item == null || item.kind == SlotKind.Npc) continue;
                    if (IsNonEntityItem(item))
                    {
                        Report.Accept("interpret_boundary", item.name + "（方位/边界说明，不作为独立物体）");
                        continue;
                    }
                    if (item.kind == SlotKind.Anchor)
                    {
                        // 旧解析器常把「市政厅正门/教堂门廊」只保留为 Anchor，导致真实载体消失。
                        // 带有明确大体量的 Anchor 先恢复成可视建筑；交互锚点在 BindStoryUnits 单独生成。
                        if (Mathf.Max(item.size.x, item.size.y) < 4f) continue;
                        LayoutItemSpec detailed;
                        if (TryRecoverDetailedCarrier(item, out detailed)) PlaceStoryItem(detailed);
                        else
                        {
                            item = CopyItem(item);
                            item.kind = SlotKind.Building;
                            item.source = "layout-carrier";
                            PlaceStoryItem(item);
                        }
                        continue;
                    }
                    if (!string.IsNullOrEmpty(item.rowId)) continue;
                    if (IsAggregateBuilding(item))
                    {
                        Report.Accept("interpret_street_row", item.name + "（聚合住宅交由沿街连续地块重建）");
                        continue;
                    }
                    if (IsRegionScaleItem(item))
                    {
                        Report.Accept("interpret_region", item.name + "（区域级描述，不作为单栋建筑）");
                        continue;
                    }
                    PlaceStoryItem(item);
                }
            }
            if (source.landmarks != null)
                for (int i = 0; i < source.landmarks.Count; i++) PlaceLandmark(source.landmarks[i]);
        }

        public bool DefineZone(LayoutZoneSpec input)
        {
            const string op = "define_zone";
            if (input == null || input.polygon == null || input.polygon.Count < 3)
                return RejectOptional(op, "分区缺少有效多边形", input != null && input.required);
            Rect rect = SceneGenRect.ClipTo(SceneGenRect.Bounds(input.polygon), site);
            if (rect.width < 3f || rect.height < 3f)
                return RejectOptional(op, "分区超出场地或面积过小", input.required);
            string id = UniqueId(string.IsNullOrEmpty(input.zoneId) ? "zone_" + SafeId(input.description) : input.zoneId,
                ZoneIds());
            string kind = NormalizeKind(input.kind);
            string description = string.IsNullOrEmpty(input.description) ? kind : input.description.Trim();
            float openness = input.openness > 0.01f ? Mathf.Clamp01(input.openness) : DefaultOpenness(kind);
            state.zones.Add(new LayoutZoneSpec
            {
                zoneId = id,
                kind = kind,
                description = description,
                polygon = SceneGenRect.ToPolygon(rect),
                groundHeight = input.groundHeight,
                required = input.required,
                source = "graybox-tool",
                openness = openness,
                material = string.IsNullOrEmpty(input.material) ? MaterialForKind(kind) : input.material,
            });
            Report.Accept(op, description + " " + F(rect.width) + "×" + F(rect.height));
            return true;
        }

        public bool DefinePath(LayoutPathSpec input)
        {
            const string op = "define_path";
            if (input == null || input.nodes == null || input.nodes.Count < 2)
                return RejectOptional(op, "路径至少需要两个节点", input != null && input.required);
            string kind = (input.kind ?? "road").Trim().ToLowerInvariant();
            if (kind != "road" && kind != "wall" && kind != "fence" && kind != "trench") kind = "road";
            var path = new LayoutPathSpec
            {
                pathId = UniqueId(string.IsNullOrEmpty(input.pathId) ? "path_" + state.paths.Count : input.pathId,
                    PathIds()),
                kind = kind,
                width = ClampPathWidth(input.width, kind),
                required = input.required,
            };
            for (int i = 0; i < input.nodes.Count; i++) path.nodes.Add(ClampPoint(input.nodes[i], 0.2f));
            state.paths.Add(path);
            Report.Accept(op, path.pathId + " / " + kind);
            return true;
        }

        public bool DefineStreet(LayoutStreetSpec input)
        {
            const string op = "define_street";
            if (input == null || input.nodes == null || input.nodes.Count < 2)
                return RejectOptional(op, "街道至少需要两个节点", true);
            string id = string.IsNullOrEmpty(input.pathId) ? "street_" + SafeId(input.name) : input.pathId;
            if (FindStreet(id) != null) return true;
            var street = new LayoutStreetSpec
            {
                pathId = UniqueId(id, StreetIds()),
                name = string.IsNullOrEmpty(input.name) ? id : input.name,
                width = Mathf.Clamp(input.width > 0f ? input.width : 8f, 4f, 24f),
                frontageBoth = input.frontageBoth,
                style = string.IsNullOrEmpty(input.style) ? "classical_row" : input.style,
                depth = Mathf.Clamp(input.depth > 0f ? input.depth : state.unitDepth, 5f, 18f),
                floors = Mathf.Clamp(input.floors > 0 ? input.floors : 3, 1, 6),
                continuous = input.continuous,
            };
            for (int i = 0; i < input.nodes.Count; i++) street.nodes.Add(ClampPoint(input.nodes[i], 0.2f));
            state.streets.Add(street);
            if (FindPath(street.pathId) == null)
            {
                state.paths.Add(new LayoutPathSpec
                {
                    pathId = street.pathId,
                    kind = "road",
                    width = street.width,
                    required = true,
                    nodes = new List<Vector2>(street.nodes),
                });
            }
            Report.Accept(op, street.name + " / " + street.style);
            return true;
        }

        public bool DefineNpcRoute(LayoutNpcSpec input)
        {
            if (input == null || string.IsNullOrEmpty(input.name) && string.IsNullOrEmpty(input.npcId)) return false;
            var route = new LayoutNpcSpec
            {
                npcId = input.npcId ?? string.Empty,
                name = input.name ?? string.Empty,
                loop = input.loop,
                required = input.required,
                waypoints = new List<Vector2>(),
            };
            if (input.waypoints != null)
                for (int i = 0; i < input.waypoints.Count; i++) route.waypoints.Add(ClampPoint(input.waypoints[i], 1f));
            state.npcs.Add(route);
            Report.Accept("define_npc_route", route.name + " / " + route.npcId);
            return true;
        }

        private void EnsureStreetSemantics()
        {
            var paths = new List<LayoutPathSpec>(state.paths);
            for (int i = 0; i < paths.Count; i++)
            {
                LayoutPathSpec path = paths[i];
                if (path == null || path.kind != "road" || FindStreet(path.pathId) != null) continue;
                DefineStreet(new LayoutStreetSpec
                {
                    pathId = path.pathId,
                    name = path.pathId,
                    nodes = new List<Vector2>(path.nodes),
                    width = path.width,
                    frontageBoth = true,
                    style = "classical_row",
                    continuous = true,
                });
            }
        }

        /// <summary>确保空输入有地面、住宅区有可组织的街道；不擅自给广场/市场围一圈房子。</summary>
        public void EnsureDistrictStructure()
        {
            if (state.zones.Count == 0)
            {
                DefineZone(new LayoutZoneSpec
                {
                    zoneId = "zone_ground",
                    kind = InferFallbackZoneKind(),
                    description = "场景基底",
                    polygon = SceneGenRect.ToPolygon(site),
                    required = true,
                });
            }

            var zones = new List<LayoutZoneSpec>(state.zones);
            for (int i = 0; i < zones.Count; i++)
            {
                LayoutZoneSpec zone = zones[i];
                if (zone.kind != "residential" || ZoneHasContent(zone)) continue;
                Rect zr = SceneGenRect.Bounds(zone.polygon);
                if (zr.width < 18f || zr.height < 18f) continue;
                bool vertical = zr.height >= zr.width;
                Vector2 a = vertical
                    ? new Vector2(SceneGenRect.Center(zr).x, zr.yMin + 5f)
                    : new Vector2(zr.xMin + 5f, SceneGenRect.Center(zr).y);
                Vector2 b = vertical
                    ? new Vector2(SceneGenRect.Center(zr).x, zr.yMax - 5f)
                    : new Vector2(zr.xMax - 5f, SceneGenRect.Center(zr).y);
                DefineStreet(new LayoutStreetSpec
                {
                    pathId = "district_street_" + SafeId(zone.zoneId),
                    name = zone.description + "内部街",
                    nodes = new List<Vector2> { a, b },
                    width = 6f,
                    frontageBoth = true,
                    style = "classical_row",
                    depth = Mathf.Min(9f, Mathf.Min(zr.width, zr.height) * 0.22f),
                    floors = 3,
                    continuous = true,
                });
            }
        }

        private static bool RequiresStreetRow(string style)
        {
            string value = (style ?? "classical_row").Trim().ToLowerInvariant();
            return value != "none" && value != "dirt_path" && value != "trodden_path"
                   && value != "path" && value != "trail" && value != "footpath";
        }

        /// <summary>工具：沿街道切分真实连续地块；轴对齐与斜向街道分别使用对应的保朝向工具。</summary>
        public void CreateStreetRows()
        {
            var streets = new List<LayoutStreetSpec>(state.streets);
            for (int si = 0; si < streets.Count; si++)
            {
                LayoutStreetSpec street = streets[si];
                if (street == null) continue;
                if (!RequiresStreetRow(street.style))
                {
                    Report.Accept("define_path_only", street.name + " / " + street.style + "（纯道路，不生成街屋）");
                    continue;
                }
                List<SceneGenSegments.Segment> segments = SceneGenSegments.Split(street.nodes);
                int streetMade = 0;
                for (int s = 0; s < segments.Count; s++)
                {
                    SceneGenSegments.Segment seg = segments[s];
                    if (!seg.AxisAligned)
                    {
                        streetMade += CreateDiagonalRows(street, seg, s);
                        continue;
                    }
                    if (seg.Length < 12f) continue;
                    int sides = street.frontageBoth ? 2 : 1;
                    for (int side = 0; side < sides; side++)
                    {
                        int sign = side == 0 ? 1 : -1;
                        Rect strip = FrontageStrip(seg, street.width, street.depth, state.setback, sign);
                        strip = SceneGenRect.ClipTo(strip, site);
                        if (strip.width < 5f || strip.height < 5f) continue;
                        var pieces = new List<Rect> { strip };
                        pieces = SubtractAll(pieces, RoadRects());
                        pieces = SubtractAll(pieces, ReservedOpenRects());
                        pieces = SubtractAll(pieces, OccupiedRects());
                        for (int p = 0; p < pieces.Count; p++) streetMade += CreateRowPiece(street, seg, sign, pieces[p], s, side, p);
                    }
                }
                if (streetMade == 0)
                {
                    if (IsExplicitStreet(street.pathId))
                        Report.Reject("create_street_row", street.name + " 要求临街建筑，但没有任何合法地块");
                    else
                        Report.Warn("create_street_row", street.name + " 没有可用临街地块，保留为纯通路");
                }
                else
                    Report.Accept("create_street_row", street.name + " → " + streetMade + " 个连续单元");
            }
        }

        /// <summary>工具：按分区语义布置绿化、路灯、人群和市场摊位，始终保留广场中央与道路。</summary>
        public void DecorateZones()
        {
            var zones = new List<LayoutZoneSpec>(state.zones);
            for (int z = 0; z < zones.Count; z++)
            {
                LayoutZoneSpec zone = zones[z];
                Rect rect = SceneGenRect.Bounds(zone.polygon);
                string text = ZoneRequirementText(zone);
                bool hasGreen = zone.kind == "field" || ContainsAny(text, "绿化", "植被", "树", "草");
                bool hasCrowd = ContainsAny(text, "抗议", "人群", "聚集", "集会", "市民");
                bool hasLamps = zone.kind != "water" && zone.kind != "field"
                                && (zone.kind == "square" || zone.kind == "market" || zone.kind == "residential"
                                    || ContainsAny(text, "路灯", "街灯"));

                if (hasGreen) CreateGreen(zone, rect);
                if (hasLamps) CreateLamps(zone, rect);
                if (hasCrowd && (zone.kind == "square" || zone.kind == "market")) CreateCrowds(zone, rect);
                if (zone.kind == "market") CreateMarketStalls(zone, rect);
            }
        }

        /// <summary>工具：绑定剧情锚点/NPC/道具。有效的剧情坐标优先；只有埋入建筑时才改用载体入口。</summary>
        public void BindStoryUnits()
        {
            BindAnchors();
            BindNpcs();
            BindProps();
        }

        /// <summary>生成最终灰盒计划。任何硬错误都返回 null，由调用方明确终止生成。</summary>
        public GrayboxPlan Complete()
        {
            Validate();
            if (Report.Errors.Count > 0)
            {
                try { Debug.LogWarning("[灰盒工具] 校验阻断：" + string.Join("；", Report.Errors.ToArray())); }
                catch (SecurityException) { /* 纯 .NET 离线探针没有 Unity 日志模块。 */ }
                return null;
            }
            state.metrics = Report.Compact() + " · 分区 " + state.zones.Count + " · 构件 " + state.items.Count;
            GrayboxPlan plan = SceneGenProjector.Project(scene, state, seed);
            plan.plannerNote = "灰盒 Agent 工具工作区（剧情输入只读） · " + state.metrics;
            plan.sceneGenReport = state.metrics;
            plan.specDriven = true;
            plan.finished = true;
            plan.segmentCount = 1;
            Report.Accept("finish_graybox", plan.plannerNote);
            return plan;
        }

        // ─────────────────────────── 地标与点名单元 ───────────────────────────

        private bool TryRecoverDetailedCarrier(LayoutItemSpec anchorItem, out LayoutItemSpec recovered)
        {
            recovered = null;
            string description = scene.layoutDescription ?? string.Empty;
            if (description.Length == 0) return false;
            const string number = @"([+-]?\d+(?:\.\d+)?)";
            MatchCollection matches = Regex.Matches(description,
                @"(?<name>[\u4e00-\u9fffA-Za-z0-9_]{2,24})\s*[:：]\s*中心\s*\(\s*(?<x>[+-]?\d+(?:\.\d+)?)"
                + @"\s*,\s*(?<z>[+-]?\d+(?:\.\d+)?)\s*\)(?<tail>[^。\n]*)");
            string key = Normalize((anchorItem.name ?? string.Empty) + " " + (anchorItem.semantic ?? string.Empty));
            Match best = null;
            int bestScore = 0;
            for (int i = 0; i < matches.Count; i++)
            {
                string candidateName = matches[i].Groups["name"].Value;
                string normalized = Normalize(candidateName);
                if (normalized.Length < 2 || !key.Contains(normalized)) continue;
                if (normalized.Length > bestScore) { best = matches[i]; bestScore = normalized.Length; }
            }
            if (best == null)
            {
                Report.Warn("recover_detail", "未匹配 " + anchorItem.name + "，候选数=" + matches.Count);
                return false;
            }

            float x, z;
            if (!float.TryParse(best.Groups["x"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                || !float.TryParse(best.Groups["z"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out z)) return false;
            string name = best.Groups["name"].Value;
            string tail = best.Groups["tail"].Value;
            Vector2 size = anchorItem.size;
            Match dimensions = Regex.Match(tail, number + @"\s*[×xX]\s*" + number + @"\s*米");
            if (dimensions.Success)
            {
                float w, d;
                if (float.TryParse(dimensions.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out w)
                    && float.TryParse(dimensions.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                    size = new Vector2(w, d);
            }
            float height = anchorItem.height;
            Match h = Regex.Match(tail, @"高\s*" + number + @"\s*米");
            if (h.Success) float.TryParse(h.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out height);
            float rotation = 0f;
            if (tail.Contains("朝南")) rotation = 180f;
            else if (tail.Contains("朝东")) rotation = 90f;
            else if (tail.Contains("朝西")) rotation = -90f;
            recovered = new LayoutItemSpec
            {
                id = "detail_" + SafeId(name),
                name = name,
                semantic = "剧情详细地标：" + tail,
                kind = SlotKind.Building,
                center = new Vector2(x, z),
                size = size,
                height = height,
                rotation = rotation,
                faceDir = SceneGenFacing.NormalFromAngle(rotation),
                primitive = PrimitiveForName(name),
                required = true,
                source = "layout-detail",
            };
            return true;
        }

        private static bool IsNonEntityItem(LayoutItemSpec item)
        {
            if (item == null) return true;
            string name = Normalize(item.name);
            return name == "两侧" || name == "南北两侧" || name == "东西两侧"
                   || name == "周边" || name == "周围" || name == "四周" || name == "南北两侧建筑";
        }

        private static bool IsAggregateBuilding(LayoutItemSpec item)
        {
            if (item == null || item.kind != SlotKind.Building) return false;
            string text = (item.name ?? string.Empty) + " " + (item.semantic ?? string.Empty);
            return ContainsAny(text, "连排", "联排", "排屋", "住宅_", "民宅_", "民居群", "房屋群");
        }

        private bool IsRegionScaleItem(LayoutItemSpec item)
        {
            if (item == null || item.kind != SlotKind.Building && item.kind != SlotKind.Prop && item.kind != SlotKind.Decoration)
                return false;
            string text = (item.name ?? string.Empty) + " " + (item.semantic ?? string.Empty);
            if (!ContainsAny(text, "居民区", "住区", "街区", "广场", "市场", "集市")) return false;
            string itemName = Normalize(item.name);
            for (int i = 0; i < source.zones.Count; i++)
            {
                LayoutZoneSpec zone = source.zones[i];
                if (zone == null) continue;
                string zoneName = Normalize(zone.description);
                if (itemName.Length < 2 || !zoneName.Contains(itemName) && !itemName.Contains(zoneName)) continue;
                float zoneArea = Mathf.Max(1f, SceneGenRect.Bounds(zone.polygon).width * SceneGenRect.Bounds(zone.polygon).height);
                float itemArea = item.size.x * item.size.y;
                if (itemArea >= zoneArea * 0.2f) return true;
            }
            return false;
        }

        private bool PlaceStoryItem(LayoutItemSpec input)
        {
            LayoutItemSpec item = CopyItem(input);
            NormalizeItem(item);
            if (item.kind == SlotKind.Prop && item.source == "layout-carrier"
                && Mathf.Max(item.size.x, item.size.y) >= 4f && IsBuildingPrimitive(item.primitive))
                item.kind = SlotKind.Building;
            if (item.kind == SlotKind.Prop || item.kind == SlotKind.Decoration)
            {
                Vector2 point = FindNearestFree(item.center, item.size, 24f);
                item.center = point;
                state.items.Add(item);
                Report.Accept("place_story_item", item.name);
                return true;
            }
            if (!TryPlaceBuilding(item, item.center, false))
            {
                Vector2 repaired;
                if (TryFindBuildingSpot(item, item.center, 30f, out repaired))
                {
                    Report.Warn("place_story_item", item.name + " 原位置冲突，已由工具在附近重新落位");
                    TryPlaceBuilding(item, repaired, false);
                }
                else
                {
                    Report.Reject("place_story_item", item.name + " 找不到不压道路/建筑的合法占地");
                    return false;
                }
            }
            Report.Accept("place_story_item", item.name);
            return true;
        }

        private bool PlaceLandmark(LayoutLandmarkSpec input)
        {
            if (input == null || string.IsNullOrEmpty(input.name)) return false;
            if (FindItemByName(input.name) != null) return true;
            var item = new LayoutItemSpec
            {
                id = "landmark_" + SafeId(input.name),
                name = input.name.Trim(),
                semantic = string.IsNullOrEmpty(input.semantic) ? input.name : input.semantic,
                kind = LandmarkSlotKind(input.kind),
                size = new Vector2(
                    input.width > 1f ? input.width : DefaultWidth(input.kind),
                    input.depth > 1f ? input.depth : DefaultDepth(input.kind)),
                height = input.height > 0.5f ? input.height : DefaultHeight(input.kind),
                primitive = PrimitiveForLandmark(input.kind),
                expectedModelHint = input.hint ?? string.Empty,
                required = true,
                source = "graybox-landmark",
                solverGenerated = false,
            };
            Vector2 face = Facing(input.facing);
            item.faceDir = face;
            item.rotation = SceneGenFacing.AngleFromNormal(face);

            Vector2 preferred;
            if (input.pinned) preferred = input.pin;
            else if (!TryLandmarkPoint(input, item.size, face, out preferred)) preferred = SceneGenRect.Center(site);

            if (item.kind != SlotKind.Building)
            {
                item.center = FindNearestFree(preferred, item.size, 30f);
                state.items.Add(item);
                Report.Accept("place_landmark", item.name);
                return true;
            }

            Vector2 chosen;
            if (!TryFindBuildingSpot(item, preferred, input.pinned ? 0f : 60f, out chosen))
            {
                Report.Reject("place_landmark", item.name + " 在目标分区附近没有合法位置，本次不放置冲突地标");
                return false;
            }
            TryPlaceBuilding(item, chosen, false);
            Report.Accept("place_landmark", item.name + " @ " + F(chosen.x) + "," + F(chosen.y));
            return true;
        }

        private bool TryLandmarkPoint(LayoutLandmarkSpec landmark, Vector2 size, Vector2 face, out Vector2 point)
        {
            point = Vector2.zero;
            LayoutZoneSpec zone = FindZone(landmark.on, landmark.kind);
            if (zone == null) return false;
            Rect rect = SceneGenRect.Bounds(zone.polygon);
            string on = landmark.on ?? string.Empty;
            if (ContainsAny(on, "中央", "中心", "center"))
            {
                point = SceneGenRect.Center(rect);
                return true;
            }
            if (ContainsAny(on, "北", "north"))
                point = new Vector2(SceneGenRect.Center(rect).x, rect.yMax - size.y * 0.5f - 2f);
            else if (ContainsAny(on, "南", "south"))
                point = new Vector2(SceneGenRect.Center(rect).x, rect.yMin + size.y * 0.5f + 2f);
            else if (ContainsAny(on, "东", "east"))
                point = new Vector2(rect.xMax - size.x * 0.5f - 2f, SceneGenRect.Center(rect).y);
            else if (ContainsAny(on, "西", "west"))
                point = new Vector2(rect.xMin + size.x * 0.5f + 2f, SceneGenRect.Center(rect).y);
            else if (Mathf.Abs(face.y) >= Mathf.Abs(face.x))
                point = new Vector2(SceneGenRect.Center(rect).x,
                    face.y >= 0f ? rect.yMin + size.y * 0.5f + 2f : rect.yMax - size.y * 0.5f - 2f);
            else
                point = new Vector2(face.x >= 0f ? rect.xMin + size.x * 0.5f + 2f : rect.xMax - size.x * 0.5f - 2f,
                    SceneGenRect.Center(rect).y);
            return true;
        }

        // ─────────────────────────── 街屋与装饰 ───────────────────────────

        private int CreateDiagonalRows(LayoutStreetSpec street, SceneGenSegments.Segment seg, int segmentIndex)
        {
            if (seg.Length < 8f) return 0;
            Vector2 direction = (seg.b - seg.a).normalized;
            Vector2 normal = new Vector2(-direction.y, direction.x);
            float module = Mathf.Clamp(state.unitWidth, 6f, 11f);
            int total = 0;
            int sides = street.frontageBoth ? 2 : 1;
            for (int side = 0; side < sides; side++)
            {
                int sign = side == 0 ? 1 : -1;
                Vector2 face = -normal * sign;
                float yaw = SceneGenFacing.AngleFromNormal(face);
                float radians = yaw * Mathf.Deg2Rad;
                // 斜向素模使用方形本地平面，确保任意角度都能精确保持世界 AABB 与连续间距。
                float worldSide = module * (Mathf.Abs(Mathf.Cos(radians)) + Mathf.Abs(Mathf.Sin(radians)));
                if (worldSide < 2f) continue;
                int capacity = Mathf.FloorToInt(seg.Length / module);
                if (capacity < 1) continue;
                float offset = street.width * 0.5f + state.setback + module * 0.5f;
                string rowId = "diag_" + SafeId(street.pathId) + "_" + segmentIndex + "_" + side;
                string primitive = street.style == "shop_row" ? "shop_house" : street.style == "arcade" ? "row_arch" : "row_house";
                int made = 0;
                for (int i = 0; i < capacity; i++)
                {
                    Vector2 center = seg.a + direction * (module * (i + 0.5f)) + normal * sign * offset;
                    var item = new LayoutItemSpec
                    {
                        id = rowId + "_" + i,
                        name = street.name + "斜向街屋 " + (i + 1),
                        semantic = street.style + "，面向" + street.name + "，斜向连续临街",
                        kind = SlotKind.Building,
                        center = center,
                        size = new Vector2(worldSide, worldSide),
                        height = Mathf.Max(3.2f, street.floors * 3.1f),
                        faceDir = face,
                        rotation = yaw,
                        primitive = primitive,
                        rowId = rowId,
                        rowIndex = i,
                        rowCount = capacity,
                        required = true,
                        source = "graybox-tool-diagonal-row",
                        solverGenerated = true,
                    };
                    if (!CanPlaceBuilding(item, center))
                    {
                        Rect rejected = SceneGenRect.FromCenter(center, item.size.x, item.size.y);
                        Report.Warn("create_diagonal_row", rowId + " 首冲突点 " + F(center.x) + "," + F(center.y)
                            + " · 边界=" + !ContainsRect(site, rejected)
                            + " · 道路=" + RoadConflict(item, rejected)
                            + " · 路距=" + SceneGenOBB.SegmentDistance(item, seg.a, seg.b)
                            + " · 开敞区=" + OverlapAny(rejected, ReservedOpenRects(), 0.05f)
                            + " · 已占=" + OverlapAny(rejected, OccupiedRects(), 0.05f));
                        break;
                    }
                    state.items.Add(item);
                    made++;
                }
                for (int i = 0; i < state.items.Count; i++)
                    if (state.items[i].rowId == rowId) state.items[i].rowCount = made;
                total += made;
            }
            return total;
        }

        private int CreateRowPiece(LayoutStreetSpec street, SceneGenSegments.Segment seg, int sign,
            Rect piece, int segmentIndex, int side, int pieceIndex)
        {
            if (piece.width < 4f || piece.height < 4f) return 0;
            bool alongZ = piece.height >= piece.width;
            float length = alongZ ? piece.height : piece.width;
            float depth = alongZ ? piece.width : piece.height;
            if (length < 6f || depth < 4f) return 0;
            float gap = street.continuous ? 0f : Mathf.Max(0.8f, source.rowGap);
            float targetWidth = Mathf.Max(5f, state.unitWidth);
            int count = Mathf.Clamp(Mathf.FloorToInt((length + gap) / (targetWidth + gap)), 1, 30);
            float frontage = (length - (count - 1) * gap) / count;
            if (frontage < 4f) return 0;
            string rowId = "row_" + SafeId(street.pathId) + "_" + segmentIndex + "_" + side + "_" + pieceIndex;
            Vector2 center = SceneGenRect.Center(piece);
            int made = 0;
            for (int i = 0; i < count; i++)
            {
                float along = (alongZ ? piece.yMin : piece.xMin) + frontage * 0.5f + i * (frontage + gap);
                Vector2 position = alongZ ? new Vector2(center.x, along) : new Vector2(along, center.y);
                Vector2 size = alongZ ? new Vector2(depth, frontage) : new Vector2(frontage, depth);
                Vector2 face = alongZ ? new Vector2(-sign, 0f) : new Vector2(0f, -sign);
                string primitive = street.style == "shop_row" ? "shop_house" : street.style == "arcade" ? "row_arch" : "row_house";
                state.items.Add(new LayoutItemSpec
                {
                    id = rowId + "_" + i,
                    name = street.name + "街屋 " + (i + 1) + "/" + count,
                    semantic = street.style + "，面向" + street.name + "，连续临街",
                    kind = SlotKind.Building,
                    center = position,
                    size = size,
                    height = Mathf.Max(3.2f, street.floors * 3.1f),
                    faceDir = face,
                    rotation = SceneGenFacing.AngleFromNormal(face),
                    primitive = primitive,
                    rowId = rowId,
                    rowIndex = i,
                    rowCount = count,
                    required = true,
                    source = "graybox-tool-row",
                    solverGenerated = true,
                });
                made++;
            }
            return made;
        }

        private void CreateGreen(LayoutZoneSpec zone, Rect rect)
        {
            if (zone.kind == "field")
            {
                int count = Mathf.Clamp(Mathf.RoundToInt(rect.width * rect.height / 500f), 3, 18);
                for (int i = 0; i < count; i++)
                {
                    Vector2 p = new Vector2(rng.Range(rect.xMin + 3f, rect.xMax - 3f), rng.Range(rect.yMin + 3f, rect.yMax - 3f));
                    TryDecoration(zone, "树", "tree", p, new Vector2(2.2f, 2.2f), rng.Range(4.5f, 7f));
                }
                return;
            }
            float inset = Mathf.Min(7f, Mathf.Min(rect.width, rect.height) * 0.18f);
            Vector2[] points =
            {
                new Vector2(rect.xMin + inset, rect.yMin + inset), new Vector2(rect.xMax - inset, rect.yMin + inset),
                new Vector2(rect.xMax - inset, rect.yMax - inset), new Vector2(rect.xMin + inset, rect.yMax - inset),
            };
            for (int i = 0; i < points.Length; i++) TryDecoration(zone, "绿化树", "tree", points[i], new Vector2(2.2f, 2.2f), 5.5f);
        }

        private void CreateLamps(LayoutZoneSpec zone, Rect rect)
        {
            float inset = 2f;
            Vector2 a = new Vector2(rect.xMin + inset, rect.yMin + inset);
            Vector2 b = new Vector2(rect.xMax - inset, rect.yMin + inset);
            Vector2 c = new Vector2(rect.xMax - inset, rect.yMax - inset);
            Vector2 d = new Vector2(rect.xMin + inset, rect.yMax - inset);
            PlaceLampsOnEdge(zone, a, b);
            PlaceLampsOnEdge(zone, c, d);
        }

        private void PlaceLampsOnEdge(LayoutZoneSpec zone, Vector2 a, Vector2 b)
        {
            int count = Mathf.Clamp(Mathf.FloorToInt(Vector2.Distance(a, b) / 20f), 1, 10);
            for (int i = 0; i < count; i++)
            {
                Vector2 p = Vector2.Lerp(a, b, (i + 0.5f) / count);
                TryDecoration(zone, "路灯", "lamp", p, new Vector2(0.7f, 0.7f), 4.2f);
            }
        }

        private void CreateCrowds(LayoutZoneSpec zone, Rect rect)
        {
            Vector2 center = SceneGenRect.Center(rect);
            int count = Mathf.Clamp(Mathf.RoundToInt(rect.width * rect.height / 1800f), 3, 8);
            float radius = Mathf.Min(14f, Mathf.Min(rect.width, rect.height) * 0.18f);
            for (int i = 0; i < count; i++)
            {
                float angle = (Mathf.PI * 2f * i) / count;
                Vector2 p = center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
                TryDecoration(zone, "聚集人群", "crowd", p, new Vector2(2.2f, 2.2f), 1.75f);
            }
        }

        private void CreateMarketStalls(LayoutZoneSpec zone, Rect rect)
        {
            int count = Mathf.Clamp(Mathf.FloorToInt(rect.height / 18f), 2, 6);
            float x = rect.xMax - 6f;
            for (int i = 0; i < count; i++)
            {
                float z = Mathf.Lerp(rect.yMin + 6f, rect.yMax - 6f, count == 1 ? 0.5f : (float)i / (count - 1));
                TryDecoration(zone, "集市摊位", "stall", new Vector2(x, z), new Vector2(6f, 4f), 2.6f);
            }
        }

        private bool TryDecoration(LayoutZoneSpec zone, string name, string primitive, Vector2 point, Vector2 size, float height)
        {
            Rect rect = SceneGenRect.FromCenter(point, size.x, size.y);
            if (!ContainsRect(site, rect) || OverlapAny(rect, OccupiedRects(), 0.01f) || OverlapAny(rect, RoadRects(), 0.01f)) return false;
            state.items.Add(new LayoutItemSpec
            {
                id = "decor_" + SafeId(zone.zoneId) + "_" + state.items.Count,
                name = name,
                semantic = zone.description + " / " + name,
                kind = SlotKind.Decoration,
                center = point,
                size = size,
                height = height,
                primitive = primitive,
                required = false,
                source = "graybox-tool-decor",
                solverGenerated = true,
            });
            return true;
        }

        // ─────────────────────────── 剧情单位 ───────────────────────────

        private void BindAnchors()
        {
            if (scene.anchors == null) return;
            for (int i = 0; i < scene.anchors.Count; i++)
            {
                SceneAnchorSpec anchor = scene.anchors[i];
                if (anchor == null) continue;
                Vector2 preferred = new Vector2(anchor.localPosition.x, anchor.localPosition.z);
                Vector2 position = preferred;
                bool explicitValid = preferred.sqrMagnitude > 0.01f && IsFreePoint(preferred, 0.3f);
                string carrierNote = "剧情明确坐标";
                if (!explicitValid)
                {
                    LayoutItemSpec carrier = FindCarrier(anchor);
                    if (carrier != null)
                    {
                        Vector2 face = carrier.faceDir.sqrMagnitude > 0.01f ? carrier.faceDir : Vector2.up;
                        face = SceneGenFacing.SnapToAxis(face);
                        float front = Mathf.Abs(face.x) * carrier.size.x * 0.5f + Mathf.Abs(face.y) * carrier.size.y * 0.5f;
                        Vector2 entrance = carrier.center + face * (front + 1.6f);
                        Vector2 inward = carrier.center - face * (front + 1.6f);
                        if (IsFreePoint(entrance, 0.3f)) position = entrance;
                        else if (IsFreePoint(inward, 0.3f)) position = inward; // 城门位于边界时，从内侧进入。
                        else position = FindNearestFree(entrance, new Vector2(2.4f, 2.4f), 18f);
                        carrierNote = "载体入口=" + carrier.name;
                    }
                    else
                    {
                        position = FindNearestFree(preferred.sqrMagnitude > 0.01f ? preferred : planSpawn(), new Vector2(2.4f, 2.4f), 60f);
                        carrierNote = "独立锚点（无载体）";
                        Report.Warn("bind_anchor", anchor.displayName + " 未找到载体，保留为独立可达锚点");
                    }
                }
                state.items.Add(new LayoutItemSpec
                {
                    id = UniqueId("anchor_" + SafeId(anchor.anchorId), ItemIds()),
                    name = anchor.displayName,
                    semantic = anchor.description,
                    kind = SlotKind.Anchor,
                    center = position,
                    size = new Vector2(2.4f, 2.4f),
                    height = 2.4f,
                    bindId = anchor.anchorId,
                    required = true,
                    source = "graybox-tool-anchor",
                    notes = carrierNote,
                });
                Report.Accept("bind_anchor", anchor.displayName + " @ " + F(position.x) + "," + F(position.y));
            }
        }

        private void BindNpcs()
        {
            if (scene.npcs == null) return;
            for (int i = 0; i < scene.npcs.Count; i++)
            {
                NpcSpec npc = scene.npcs[i];
                if (npc == null) continue;
                LayoutNpcSpec route = FindRoute(npc);
                Vector2 preferred = route != null && route.waypoints.Count > 0
                    ? route.waypoints[0] : new Vector2(npc.localPosition.x, npc.localPosition.z);
                Vector2 position = IsFreePoint(preferred, 0.4f) ? preferred : FindNearestFree(preferred, new Vector2(1f, 1f), 40f);
                state.items.Add(new LayoutItemSpec
                {
                    id = "npc_" + SafeId(npc.npcId),
                    name = npc.displayName,
                    semantic = npc.persona + " / " + npc.storyRole,
                    kind = SlotKind.Npc,
                    center = position,
                    size = new Vector2(0.8f, 0.8f),
                    height = 1.75f,
                    bindId = npc.npcId,
                    required = true,
                    source = "graybox-tool-npc",
                });
                if (route != null)
                {
                    route.npcId = npc.npcId;
                    if (route.waypoints.Count > 0) route.waypoints[0] = position;
                    for (int w = 0; w < route.waypoints.Count; w++)
                    {
                        Vector2 waypoint = route.waypoints[w];
                        if (!IsFreePoint(waypoint, 0.2f))
                            route.waypoints[w] = FindNearestFree(waypoint, new Vector2(1f, 1f), 30f);
                    }
                }
                Report.Accept("bind_npc", npc.displayName);
            }
        }

        private void BindProps()
        {
            if (scene.props == null) return;
            for (int i = 0; i < scene.props.Count; i++)
            {
                PropInteraction prop = scene.props[i];
                if (prop == null) continue;
                LayoutItemSpec existing = FindBoundItem(prop.propId, SlotKind.Prop);
                if (existing != null)
                {
                    Report.Accept("bind_prop", prop.displayName + "（复用已有点名物）");
                    continue;
                }
                LayoutItemSpec authored = FindBoundSourceItem(prop.propId, prop.displayName);
                Vector2 size = authored != null ? authored.size : new Vector2(2f, 2f);
                Vector2 preferred = new Vector2(prop.localPosition.x, prop.localPosition.z);
                if (authored != null && preferred.sqrMagnitude < 0.01f) preferred = authored.center;
                Vector2 position = IsFreePoint(preferred, 0.2f) ? preferred : FindNearestFree(preferred, size, 30f);
                state.items.Add(new LayoutItemSpec
                {
                    id = "prop_" + SafeId(prop.propId),
                    name = prop.displayName,
                    semantic = prop.description + " / " + prop.effect,
                    kind = SlotKind.Prop,
                    center = position,
                    size = size,
                    height = authored != null ? authored.height : 1.4f,
                    primitive = authored != null && !string.IsNullOrEmpty(authored.primitive)
                        ? authored.primitive : PrimitiveForName(prop.displayName),
                    bindId = prop.propId,
                    required = true,
                    source = "graybox-tool-prop",
                });
                Report.Accept("bind_prop", prop.displayName);
            }
        }

        // ─────────────────────────── 校验 ───────────────────────────

        private void Validate()
        {
            if (state.zones.Count == 0) Report.Reject("validate_graybox", "没有功能分区");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < state.items.Count; i++)
            {
                LayoutItemSpec item = state.items[i];
                if (item == null) continue;
                if (!ids.Add(item.id)) Report.Reject("validate_graybox", "重复构件 id=" + item.id);
                Rect rect = SceneGenRect.FromCenter(item.center, item.size.x, item.size.y);
                if (!ContainsRect(site, rect)) Report.Reject("validate_graybox", item.name + " 超出场景边界");
                if (item.kind == SlotKind.Anchor && OverlapBuilding(rect, 0.05f))
                    Report.Reject("validate_graybox", "锚点「" + item.name + "」仍埋入建筑");
            }
            for (int i = 0; i < state.items.Count; i++)
            {
                LayoutItemSpec a = state.items[i];
                if (a == null || a.kind != SlotKind.Building) continue;
                Rect ra = SceneGenRect.FromCenter(a.center, a.size.x, a.size.y);
                if (RoadConflict(a, ra)) Report.Reject("validate_graybox", "建筑「" + a.name + "」侵入道路");
                for (int j = i + 1; j < state.items.Count; j++)
                {
                    LayoutItemSpec b = state.items[j];
                    if (b == null || b.kind != SlotKind.Building) continue;
                    if (SceneGenOBB.Overlaps(a, b, 0.05f))
                        Report.Reject("validate_graybox", "建筑重叠：" + a.name + " × " + b.name);
                }
            }
            ValidateRows();
            ValidateStoryCoverage();
            for (int r = 0; r < state.npcs.Count; r++)
            {
                LayoutNpcSpec route = state.npcs[r];
                if (route == null || route.waypoints == null) continue;
                for (int w = 0; w < route.waypoints.Count; w++)
                {
                    Rect point = SceneGenRect.FromCenter(route.waypoints[w], 1f, 1f);
                    if (OverlapBuilding(point, 0.01f))
                        Report.Reject("validate_npc_route", "NPC「" + route.name + "」的航点仍位于建筑内");
                }
            }
            for (int z = 0; z < state.zones.Count; z++)
            {
                LayoutZoneSpec zone = state.zones[z];
                if (zone.kind == "residential" && !ZoneHasContent(zone))
                    Report.Reject("validate_graybox", "住宅区「" + zone.description + "」没有任何建筑或街道内容");
            }
        }

        private void ValidateRows()
        {
            var rows = new Dictionary<string, List<LayoutItemSpec>>(StringComparer.Ordinal);
            for (int i = 0; i < state.items.Count; i++)
            {
                LayoutItemSpec item = state.items[i];
                if (item == null || string.IsNullOrEmpty(item.rowId)) continue;
                if (!rows.ContainsKey(item.rowId)) rows[item.rowId] = new List<LayoutItemSpec>();
                rows[item.rowId].Add(item);
            }
            foreach (KeyValuePair<string, List<LayoutItemSpec>> pair in rows)
            {
                var items = pair.Value;
                items.Sort((a, b) => a.rowIndex.CompareTo(b.rowIndex));
                for (int i = 0; i + 1 < items.Count; i++)
                {
                    LayoutItemSpec a = items[i], b = items[i + 1];
                    Vector2 delta = b.center - a.center;
                    float actual = delta.magnitude;
                    Vector2 direction = actual > 0.001f ? delta / actual : Vector2.right;
                    float expected = SceneGenOBB.ProjectedHalfWidth(a, direction)
                                     + SceneGenOBB.ProjectedHalfWidth(b, direction);
                    float gap = actual - expected;
                    if (gap < -0.05f || gap > 0.2f)
                        Report.Reject("validate_street_row", pair.Key + " 单元 " + i + "→" + (i + 1)
                            + " 间距/占地不连续，净隙 " + F(gap));
                }
            }
        }

        private void ValidateStoryCoverage()
        {
            if (source.landmarks != null)
            {
                for (int i = 0; i < source.landmarks.Count; i++)
                {
                    LayoutLandmarkSpec landmark = source.landmarks[i];
                    if (landmark != null && !string.IsNullOrEmpty(landmark.name) && FindItemByName(landmark.name) == null)
                        Report.Reject("validate_story_coverage", "剧情地标缺失：" + landmark.name);
                }
            }
            if (scene.anchors != null)
            {
                for (int i = 0; i < scene.anchors.Count; i++)
                    if (scene.anchors[i] != null && FindBoundItem(scene.anchors[i].anchorId, SlotKind.Anchor) == null)
                        Report.Reject("validate_story_coverage", "锚点缺失：" + scene.anchors[i].displayName);
            }
            if (scene.npcs != null)
            {
                for (int i = 0; i < scene.npcs.Count; i++)
                    if (scene.npcs[i] != null && FindBoundItem(scene.npcs[i].npcId, SlotKind.Npc) == null)
                        Report.Reject("validate_story_coverage", "NPC 缺失：" + scene.npcs[i].displayName);
            }
        }

        // ─────────────────────────── 几何与数据辅助 ───────────────────────────

        private bool TryFindBuildingSpot(LayoutItemSpec item, Vector2 preferred, float radius, out Vector2 chosen)
        {
            if (CanPlaceBuilding(item, preferred)) { chosen = preferred; return true; }
            if (radius <= 0f) { chosen = preferred; return false; }
            float step = Mathf.Max(3f, Mathf.Min(item.size.x, item.size.y) * 0.5f);
            for (float r = step; r <= radius; r += step)
            {
                for (int i = 0; i < 16; i++)
                {
                    float angle = i * Mathf.PI * 2f / 16f;
                    Vector2 candidate = preferred + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * r;
                    if (CanPlaceBuilding(item, candidate)) { chosen = candidate; return true; }
                }
            }
            chosen = preferred;
            return false;
        }

        private bool TryPlaceBuilding(LayoutItemSpec item, Vector2 position, bool allowConflict)
        {
            if (!allowConflict && !CanPlaceBuilding(item, position)) return false;
            item.center = position;
            item.id = UniqueId(item.id, ItemIds());
            state.items.Add(item);
            return true;
        }

        private bool CanPlaceBuilding(LayoutItemSpec item, Vector2 position)
        {
            Rect rect = SceneGenRect.FromCenter(position, item.size.x, item.size.y);
            return ContainsRect(site, rect) && !OverlapOccupied(item, position)
                   && !OverlapReserved(item, position) && !RoadConflict(item, rect);
        }

        private bool OverlapOccupied(LayoutItemSpec item, Vector2 position)
        {
            LayoutItemSpec candidate = CopyItem(item);
            candidate.center = position;
            for (int i = 0; i < state.items.Count; i++)
            {
                LayoutItemSpec other = state.items[i];
                if (other == null || other.kind == SlotKind.Anchor || other.kind == SlotKind.Npc) continue;
                if (SceneGenOBB.Overlaps(candidate, other)) return true;
            }
            return false;
        }

        private bool OverlapReserved(LayoutItemSpec item, Vector2 position)
        {
            LayoutItemSpec candidate = CopyItem(item);
            candidate.center = position;
            List<Rect> reserved = ReservedOpenRects();
            for (int i = 0; i < reserved.Count; i++) if (SceneGenOBB.Overlaps(candidate, reserved[i])) return true;
            return false;
        }

        private bool RoadConflict(LayoutItemSpec item, Rect rect)
        {
            LayoutItemSpec candidate = CopyItem(item);
            candidate.center = SceneGenRect.Center(rect);
            bool endpointCarrier = candidate.primitive == "gate" || candidate.primitive == "fortress";
            if (endpointCarrier) return RoadCrosses(rect) || DiagonalRoadConflict(candidate, true);
            List<Rect> roads = RoadRects();
            for (int i = 0; i < roads.Count; i++) if (SceneGenOBB.Overlaps(candidate, roads[i], 0.05f)) return true;
            return DiagonalRoadConflict(candidate, false);
        }

        private bool RoadCrosses(Rect rect)
        {
            List<Rect> roads = RoadRects();
            for (int i = 0; i < roads.Count; i++)
            {
                Rect road = roads[i];
                if (SceneGenRect.OverlapArea(rect, road) <= 0.5f) continue;
                bool vertical = road.height >= road.width;
                if (vertical && road.yMin < rect.yMin - 2f && road.yMax > rect.yMax + 2f) return true;
                if (!vertical && road.xMin < rect.xMin - 2f && road.xMax > rect.xMax + 2f) return true;
            }
            return false;
        }

        private bool DiagonalRoadConflict(LayoutItemSpec item, bool endpointCarrier)
        {
            Rect bounds = SceneGenRect.FromCenter(item.center, item.size.x, item.size.y);
            for (int p = 0; p < state.paths.Count; p++)
            {
                LayoutPathSpec path = state.paths[p];
                if (path == null || path.kind != "road" || path.nodes == null) continue;
                List<SceneGenSegments.Segment> segments = SceneGenSegments.Split(path.nodes);
                for (int i = 0; i < segments.Count; i++)
                {
                    SceneGenSegments.Segment seg = segments[i];
                    if (seg.AxisAligned) continue;
                    float distance = SceneGenOBB.SegmentDistance(item, seg.a, seg.b);
                    if (distance > path.width * 0.5f + 0.05f) continue;
                    if (!endpointCarrier) return true;
                    // 城门/要塞允许道路在端点接入；两端都在外且道路穿入占地则仍视为冲突。
                    bool aInside = SceneGenRect.Contains(bounds, seg.a, 0f);
                    bool bInside = SceneGenRect.Contains(bounds, seg.b, 0f);
                    if (!aInside && !bInside) return true;
                }
            }
            return false;
        }

        private static float SegmentRectDistance(Vector2 a, Vector2 b, Rect rect)
        {
            if (SceneGenRect.Contains(rect, a, 0f) || SceneGenRect.Contains(rect, b, 0f)) return 0f;
            Vector2[] corners =
            {
                new Vector2(rect.xMin, rect.yMin), new Vector2(rect.xMax, rect.yMin),
                new Vector2(rect.xMax, rect.yMax), new Vector2(rect.xMin, rect.yMax),
            };
            float best = float.MaxValue;
            for (int i = 0; i < 4; i++)
                best = Mathf.Min(best, SegmentSegmentDistance(a, b, corners[i], corners[(i + 1) % 4]));
            return best;
        }

        private static float SegmentSegmentDistance(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            if (SegmentsIntersect(a, b, c, d)) return 0f;
            return Mathf.Min(
                Mathf.Min(PointSegmentDistance(a, c, d), PointSegmentDistance(b, c, d)),
                Mathf.Min(PointSegmentDistance(c, a, b), PointSegmentDistance(d, a, b)));
        }

        private static float PointSegmentDistance(Vector2 point, Vector2 a, Vector2 b)
        {
            Vector2 segment = b - a;
            float length2 = segment.sqrMagnitude;
            if (length2 < 0.00001f) return Vector2.Distance(point, a);
            float t = Mathf.Clamp01(Vector2.Dot(point - a, segment) / length2);
            return Vector2.Distance(point, a + segment * t);
        }

        private static bool SegmentsIntersect(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            float abC = Cross(b - a, c - a);
            float abD = Cross(b - a, d - a);
            float cdA = Cross(d - c, a - c);
            float cdB = Cross(d - c, b - c);
            const float epsilon = 0.0001f;
            return abC * abD <= epsilon && cdA * cdB <= epsilon
                   && Mathf.Sign(abC) != Mathf.Sign(abD) && Mathf.Sign(cdA) != Mathf.Sign(cdB);
        }

        private static float Cross(Vector2 a, Vector2 b)
        {
            return a.x * b.y - a.y * b.x;
        }

        private Vector2 FindNearestFree(Vector2 preferred, Vector2 size, float radius)
        {
            if (IsFreeFootprint(preferred, size)) return preferred;
            if (preferred.sqrMagnitude < 0.01f) preferred = planSpawn();
            for (float r = 2f; r <= radius; r += 2f)
            {
                for (int i = 0; i < 16; i++)
                {
                    float angle = i * Mathf.PI * 2f / 16f;
                    Vector2 p = preferred + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * r;
                    if (IsFreeFootprint(p, size)) return p;
                }
            }
            Vector2 clamped = ClampPoint(preferred, 2f);
            Report.Warn("find_free_point", "未找到完全无冲突的位置，使用最近的边界内点 " + F(clamped.x) + "," + F(clamped.y));
            return clamped;
        }

        private bool IsFreePoint(Vector2 point, float margin)
        {
            return SceneGenRect.Contains(site, point, -0.5f)
                   && !OverlapBuilding(SceneGenRect.FromCenter(point, 2.4f, 2.4f), Mathf.Max(0.01f, margin * 0.1f));
        }

        private bool IsFreeFootprint(Vector2 point, Vector2 size)
        {
            Rect rect = SceneGenRect.FromCenter(point, Mathf.Max(0.8f, size.x), Mathf.Max(0.8f, size.y));
            return ContainsRect(site, rect) && !OverlapAny(rect, OccupiedRects(), 0.05f);
        }

        private bool OverlapBuilding(Rect rect, float minArea)
        {
            for (int i = 0; i < state.items.Count; i++)
            {
                LayoutItemSpec item = state.items[i];
                if (item == null || item.kind != SlotKind.Building) continue;
                if (SceneGenRect.OverlapArea(rect, SceneGenRect.FromCenter(item.center, item.size.x, item.size.y)) > minArea) return true;
            }
            return false;
        }

        private List<Rect> OccupiedRects()
        {
            var list = new List<Rect>();
            for (int i = 0; i < state.items.Count; i++)
            {
                LayoutItemSpec item = state.items[i];
                if (item == null || item.kind == SlotKind.Anchor || item.kind == SlotKind.Npc) continue;
                list.Add(SceneGenRect.FromCenter(item.center, item.size.x, item.size.y));
            }
            return list;
        }

        private List<Rect> ReservedOpenRects()
        {
            var list = new List<Rect>();
            for (int i = 0; i < state.zones.Count; i++)
            {
                LayoutZoneSpec zone = state.zones[i];
                if (zone.kind != "square" && zone.kind != "market") continue;
                string text = ZoneRequirementText(zone);
                if (!ContainsAny(text, "抗议", "人群", "聚集", "集会", "市民")) continue;
                Rect zr = SceneGenRect.Bounds(zone.polygon);
                float width = Mathf.Clamp(zr.width * 0.45f, 16f, 64f);
                float depth = Mathf.Clamp(zr.height * 0.45f, 16f, 64f);
                list.Add(SceneGenRect.FromCenter(SceneGenRect.Center(zr), width, depth));
            }
            return list;
        }

        private List<Rect> RoadRects()
        {
            var list = new List<Rect>();
            for (int p = 0; p < state.paths.Count; p++)
            {
                LayoutPathSpec path = state.paths[p];
                if (path == null || path.kind != "road" || path.nodes == null) continue;
                List<SceneGenSegments.Segment> segments = SceneGenSegments.Split(path.nodes);
                for (int i = 0; i < segments.Count; i++)
                {
                    SceneGenSegments.Segment seg = segments[i];
                    if (seg.AxisAligned) list.Add(SceneGenSegments.Corridor(seg, path.width * 0.5f));
                }
            }
            return list;
        }

        private static List<Rect> SubtractAll(List<Rect> pieces, List<Rect> cutters)
        {
            var current = pieces;
            for (int i = 0; i < cutters.Count && current.Count > 0; i++)
            {
                var next = new List<Rect>();
                for (int p = 0; p < current.Count; p++) next.AddRange(SceneGenRect.Subtract(current[p], cutters[i]));
                current = next;
                if (current.Count > 96) break;
            }
            return current;
        }

        private static Rect FrontageStrip(SceneGenSegments.Segment seg, float roadWidth, float depth, float setback, int sign)
        {
            float inner = roadWidth * 0.5f + Mathf.Max(0f, setback);
            if (seg.vertical)
            {
                float x0 = sign > 0 ? seg.a.x + inner : seg.a.x - inner - depth;
                return SceneGenRect.FromMinMax(x0, Mathf.Min(seg.a.y, seg.b.y), x0 + depth, Mathf.Max(seg.a.y, seg.b.y));
            }
            float z0 = sign > 0 ? seg.a.y + inner : seg.a.y - inner - depth;
            return SceneGenRect.FromMinMax(Mathf.Min(seg.a.x, seg.b.x), z0, Mathf.Max(seg.a.x, seg.b.x), z0 + depth);
        }

        private LayoutItemSpec FindCarrier(SceneAnchorSpec anchor)
        {
            if (source.landmarks != null)
            {
                for (int i = 0; i < source.landmarks.Count; i++)
                {
                    LayoutLandmarkSpec landmark = source.landmarks[i];
                    if (landmark == null || string.IsNullOrEmpty(landmark.anchorBinding)) continue;
                    if (landmark.anchorBinding.IndexOf(anchor.displayName ?? string.Empty, StringComparison.Ordinal) >= 0)
                    {
                        LayoutItemSpec bound = FindItemByName(landmark.name);
                        if (bound != null) return bound;
                    }
                }
            }
            string key = Normalize((anchor.displayName ?? "") + " " + (anchor.requirement ?? ""));
            LayoutItemSpec best = null;
            int bestScore = 0;
            for (int i = 0; i < state.items.Count; i++)
            {
                LayoutItemSpec item = state.items[i];
                if (item == null || item.kind != SlotKind.Building && item.kind != SlotKind.Prop) continue;
                string name = Normalize(item.name);
                if (name.Length == 0) continue;
                int score = key.Contains(name) ? name.Length * 4 : 0;
                if (score > bestScore) { best = item; bestScore = score; }
            }
            return best;
        }

        private LayoutItemSpec FindBoundSourceItem(string id, string name)
        {
            if (source.items == null) return null;
            for (int i = 0; i < source.items.Count; i++)
            {
                LayoutItemSpec item = source.items[i];
                if (item == null) continue;
                if (!string.IsNullOrEmpty(id) && item.bindId == id) return item;
                if (!string.IsNullOrEmpty(name) && Normalize(item.name) == Normalize(name)) return item;
            }
            return null;
        }

        private LayoutNpcSpec FindRoute(NpcSpec npc)
        {
            for (int i = 0; i < state.npcs.Count; i++)
            {
                LayoutNpcSpec route = state.npcs[i];
                if (route == null) continue;
                if (!string.IsNullOrEmpty(route.npcId) && route.npcId == npc.npcId) return route;
                if (!string.IsNullOrEmpty(route.name) && (Normalize(route.name) == Normalize(npc.displayName)
                    || Normalize(route.name).Contains(Normalize(npc.displayName)) || Normalize(npc.displayName).Contains(Normalize(route.name)))) return route;
            }
            return null;
        }

        private LayoutZoneSpec FindZone(string text, string landmarkKind)
        {
            string key = Normalize(text);
            LayoutZoneSpec best = null;
            int bestLength = 0;
            for (int i = 0; i < state.zones.Count; i++)
            {
                LayoutZoneSpec zone = state.zones[i];
                string name = Normalize(zone.description);
                if (name.Length > 0 && key.Contains(name) && name.Length > bestLength) { best = zone; bestLength = name.Length; }
            }
            if (best != null) return best;
            string wanted = landmarkKind == "church" ? "church" : landmarkKind == "stall" ? "market" : "square";
            for (int i = 0; i < state.zones.Count; i++) if (state.zones[i].kind == wanted) return state.zones[i];
            return state.zones.Count > 0 ? state.zones[0] : null;
        }

        private bool ZoneHasContent(LayoutZoneSpec zone)
        {
            Rect rect = SceneGenRect.Bounds(zone.polygon);
            for (int i = 0; i < state.items.Count; i++)
                if (state.items[i] != null && SceneGenRect.Contains(rect, state.items[i].center, 0f)) return true;
            for (int i = 0; i < state.streets.Count; i++)
            {
                List<SceneGenSegments.Segment> segments = SceneGenSegments.Split(state.streets[i].nodes);
                for (int s = 0; s < segments.Count; s++)
                    if (SceneGenRect.Overlaps(rect, SceneGenSegments.Corridor(segments[s], 0.1f))) return true;
            }
            return false;
        }

        private string ZoneRequirementText(LayoutZoneSpec zone)
        {
            string local = (zone.description ?? string.Empty) + " " + (zone.material ?? string.Empty);
            string full = (scene.layoutDescription ?? string.Empty) + "\n" + (scene.buildRequirement ?? string.Empty);
            if (state.zones.Count == 1) return local + " " + full;
            if (string.IsNullOrEmpty(zone.description)) return local;
            int at = full.IndexOf(zone.description, StringComparison.Ordinal);
            if (at < 0) return local;
            int end = full.IndexOfAny(new[] { '。', '\n', '；' }, at);
            if (end < 0 || end - at > 240) end = Mathf.Min(full.Length, at + 240);
            return local + " " + full.Substring(at, end - at);
        }

        private string InferFallbackZoneKind()
        {
            string text = (scene.layoutDescription ?? "") + (scene.buildRequirement ?? "") + (scene.sceneName ?? "");
            if (ContainsAny(text, "居民", "住宅", "城区", "街区")) return "residential";
            if (ContainsAny(text, "市场", "集市")) return "market";
            if (ContainsAny(text, "教堂")) return "church";
            if (ContainsAny(text, "广场", "集会", "要塞", "战场")) return "square";
            return "field";
        }

        private Vector2 planSpawn()
        {
            return new Vector2(scene.spawnPoint.x, scene.spawnPoint.z);
        }

        private Vector2 ClampPoint(Vector2 p, float margin)
        {
            return new Vector2(Mathf.Clamp(p.x, site.xMin + margin, site.xMax - margin),
                Mathf.Clamp(p.y, site.yMin + margin, site.yMax - margin));
        }

        private void NormalizeItem(LayoutItemSpec item)
        {
            item.size = new Vector2(Mathf.Clamp(item.size.x, 0.4f, 400f), Mathf.Clamp(item.size.y, 0.4f, 400f));
            item.height = Mathf.Clamp(item.height, 0f, 300f);
            item.faceDir = SceneGenFacing.SnapToAxis(item.faceDir.sqrMagnitude > 0.01f
                ? item.faceDir : SceneGenFacing.NormalFromAngle(item.rotation));
            item.rotation = SceneGenFacing.AngleFromNormal(item.faceDir);
            if (string.IsNullOrEmpty(item.primitive)) item.primitive = PrimitiveForName(item.name);
            if (item.kind == SlotKind.Building && item.primitive == "prop") item.primitive = "row_house";
            item.id = string.IsNullOrEmpty(item.id) ? "story_" + SafeId(item.name) : item.id;
        }

        private static bool IsBuildingPrimitive(string primitive)
        {
            return primitive == "fortress" || primitive == "church" || primitive == "gate"
                   || primitive == "hall" || primitive == "row_house" || primitive == "shop_house";
        }

        private static string PrimitiveForName(string name)
        {
            string n = name ?? string.Empty;
            if (n.Contains("要塞") || n.Contains("城堡") || n.Contains("主堡")) return "fortress";
            if (n.Contains("教堂")) return "church";
            if (n.Contains("城门") || n.Contains("门楼")) return "gate";
            if (n.Contains("市政厅") || n.Contains("官署")) return "hall";
            if (n.Contains("台阶") || n.Contains("阶梯")) return "steps";
            if (n.Contains("摊") || n.Contains("棚")) return "stall";
            if (n.Contains("灯")) return "lamp";
            if (n.Contains("树") || n.Contains("绿化")) return "tree";
            if (n.Contains("人群") || n.Contains("聚集")) return "crowd";
            if (n.Contains("鼓")) return "drum";
            if (n.Contains("草")) return "grass";
            return "prop";
        }

        private static SlotKind LandmarkSlotKind(string kind)
        {
            if (kind == "steps" || kind == "stall") return SlotKind.Prop;
            if (kind == "decoration") return SlotKind.Decoration;
            return SlotKind.Building;
        }

        private static string PrimitiveForLandmark(string kind)
        {
            switch ((kind ?? "building").ToLowerInvariant())
            {
                case "fortress": return "fortress";
                case "church": return "church";
                case "gate": return "gate";
                case "hall": return "hall";
                case "steps": return "steps";
                case "stall": return "stall";
                case "decoration": return "prop";
                default: return "row_house";
            }
        }

        private static float DefaultWidth(string kind)
        {
            switch (kind) { case "fortress": return 44f; case "church": return 26f; case "gate": return 16f; case "hall": return 28f; case "steps": return 20f; case "stall": return 8f; default: return 14f; }
        }
        private static float DefaultDepth(string kind)
        {
            switch (kind) { case "fortress": return 22f; case "church": return 20f; case "gate": return 8f; case "hall": return 14f; case "steps": return 4f; case "stall": return 5f; default: return 12f; }
        }
        private static float DefaultHeight(string kind)
        {
            switch (kind) { case "fortress": return 26f; case "church": return 22f; case "gate": return 14f; case "hall": return 16f; case "steps": return 1.5f; case "stall": return 2.6f; default: return 9.3f; }
        }

        private static Vector2 Facing(string word)
        {
            return SceneGenFacing.SnapToAxis(SceneGenFacing.NormalFromAngle(SceneGenFacing.AngleFromWord(word)));
        }

        private static string NormalizeKind(string kind)
        {
            switch ((kind ?? "square").Trim().ToLowerInvariant())
            {
                case "residential": return "residential";
                case "field": return "field";
                case "water": return "water";
                case "church": return "church";
                case "market": return "market";
                case "harbor": return "harbor";
                case "military": return "square";
                default: return "square";
            }
        }

        private static float DefaultOpenness(string kind)
        {
            switch (kind) { case "residential": return 0.15f; case "market": return 0.6f; case "church": return 0.5f; case "field": return 0.9f; case "water": return 1f; case "harbor": return 0.7f; default: return 0.85f; }
        }

        private static string MaterialForKind(string kind)
        {
            switch (kind) { case "residential": return "夯土"; case "market": return "夯实泥土"; case "church": return "方石"; case "field": return "草地"; case "water": return "水面"; case "harbor": return "木板"; default: return "石板"; }
        }

        private static float ClampPathWidth(float width, string kind)
        {
            if (kind == "road") return Mathf.Clamp(width > 0f ? width : 8f, 3f, 30f);
            return Mathf.Clamp(width > 0f ? width : 1.2f, 0.4f, 5f);
        }

        private static bool ContainsRect(Rect outer, Rect inner)
        {
            return inner.xMin >= outer.xMin - 0.01f && inner.xMax <= outer.xMax + 0.01f
                   && inner.yMin >= outer.yMin - 0.01f && inner.yMax <= outer.yMax + 0.01f;
        }

        private static bool OverlapAny(Rect rect, List<Rect> others, float minArea)
        {
            for (int i = 0; i < others.Count; i++) if (SceneGenRect.OverlapArea(rect, others[i]) > minArea) return true;
            return false;
        }

        private static bool ContainsAny(string text, params string[] words)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < words.Length; i++) if (text.IndexOf(words[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private LayoutItemSpec FindItemByName(string name)
        {
            string key = Normalize(name);
            for (int i = 0; i < state.items.Count; i++) if (Normalize(state.items[i].name) == key) return state.items[i];
            return null;
        }

        private LayoutItemSpec FindBoundItem(string id, SlotKind kind)
        {
            for (int i = 0; i < state.items.Count; i++) if (state.items[i].kind == kind && state.items[i].bindId == id) return state.items[i];
            return null;
        }

        private LayoutPathSpec FindPath(string id)
        {
            for (int i = 0; i < state.paths.Count; i++) if (state.paths[i].pathId == id) return state.paths[i];
            return null;
        }

        private LayoutStreetSpec FindStreet(string id)
        {
            for (int i = 0; i < state.streets.Count; i++) if (state.streets[i].pathId == id) return state.streets[i];
            return null;
        }

        private bool IsExplicitStreet(string id)
        {
            if (source.streets == null) return false;
            for (int i = 0; i < source.streets.Count; i++)
            {
                LayoutStreetSpec street = source.streets[i];
                if (street == null) continue;
                string sourceId = string.IsNullOrEmpty(street.pathId) ? "street_" + SafeId(street.name) : street.pathId;
                if (sourceId == id) return true;
            }
            return false;
        }

        private HashSet<string> ItemIds()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < state.items.Count; i++) set.Add(state.items[i].id);
            return set;
        }
        private HashSet<string> ZoneIds()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < state.zones.Count; i++) set.Add(state.zones[i].zoneId);
            return set;
        }
        private HashSet<string> PathIds()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < state.paths.Count; i++) set.Add(state.paths[i].pathId);
            return set;
        }
        private HashSet<string> StreetIds()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < state.streets.Count; i++) set.Add(state.streets[i].pathId);
            return set;
        }

        private static string UniqueId(string value, HashSet<string> used)
        {
            string id = string.IsNullOrEmpty(value) ? "item" : value;
            string baseId = id;
            int n = 1;
            while (used.Contains(id)) { n++; id = baseId + "_" + n.ToString(CultureInfo.InvariantCulture); }
            return id;
        }

        private static string SafeId(string text)
        {
            if (string.IsNullOrEmpty(text)) return "x";
            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length && sb.Length < 40; i++)
            {
                char c = text[i];
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-') sb.Append(c); else sb.Append('_');
            }
            return sb.ToString().Trim('_');
        }

        private static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c) || c == '_' || c == '-' || c == '·' || c == '，' || c == ',' || c == '：' || c == ':') continue;
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static string F(float value) { return value.ToString("0.0", CultureInfo.InvariantCulture); }

        private bool RejectOptional(string operation, string detail, bool required)
        {
            if (required) Report.Reject(operation, detail); else Report.Warn(operation, detail);
            return false;
        }

        private static LayoutSpec CloneSpec(LayoutSpec input)
        {
            if (input == null) return null;
            var copy = new LayoutSpec
            {
                version = input.version, sceneId = input.sceneId, source = input.source,
                diagnostics = input.diagnostics, metrics = input.metrics,
                unitWidth = input.unitWidth, unitDepth = input.unitDepth, rowGap = input.rowGap,
                setback = input.setback, positionTolerance = input.positionTolerance,
                sizeTolerance = input.sizeTolerance, rowGapTolerance = input.rowGapTolerance,
                streetClearance = input.streetClearance, preserveCounts = input.preserveCounts,
                gaps = input.gaps != null ? new List<string>(input.gaps) : new List<string>(),
            };
            if (input.zones != null)
            {
                for (int i = 0; i < input.zones.Count; i++)
                {
                    LayoutZoneSpec z = input.zones[i];
                    if (z == null) continue;
                    copy.zones.Add(new LayoutZoneSpec
                    {
                        zoneId = z.zoneId, kind = z.kind, description = z.description,
                        polygon = z.polygon != null ? new List<Vector2>(z.polygon) : new List<Vector2>(),
                        groundHeight = z.groundHeight, required = z.required, source = z.source,
                        openness = z.openness, material = z.material,
                    });
                }
            }
            if (input.paths != null)
            {
                for (int i = 0; i < input.paths.Count; i++)
                {
                    LayoutPathSpec p = input.paths[i];
                    if (p == null) continue;
                    copy.paths.Add(new LayoutPathSpec
                    {
                        pathId = p.pathId, kind = p.kind, width = p.width, required = p.required,
                        nodes = p.nodes != null ? new List<Vector2>(p.nodes) : new List<Vector2>(),
                    });
                }
            }
            if (input.streets != null)
            {
                for (int i = 0; i < input.streets.Count; i++)
                {
                    LayoutStreetSpec s = input.streets[i];
                    if (s == null) continue;
                    copy.streets.Add(new LayoutStreetSpec
                    {
                        pathId = s.pathId, name = s.name, width = s.width,
                        nodes = s.nodes != null ? new List<Vector2>(s.nodes) : new List<Vector2>(),
                        frontageBoth = s.frontageBoth, style = s.style, depth = s.depth,
                        floors = s.floors, continuous = s.continuous,
                    });
                }
            }
            if (input.landmarks != null)
            {
                for (int i = 0; i < input.landmarks.Count; i++)
                {
                    LayoutLandmarkSpec l = input.landmarks[i];
                    if (l == null) continue;
                    copy.landmarks.Add(new LayoutLandmarkSpec
                    {
                        name = l.name, kind = l.kind, on = l.on, facing = l.facing,
                        width = l.width, depth = l.depth, height = l.height,
                        pinned = l.pinned, pin = l.pin, hint = l.hint, semantic = l.semantic,
                        anchorBinding = l.anchorBinding,
                    });
                }
            }
            if (input.npcs != null)
            {
                for (int i = 0; i < input.npcs.Count; i++)
                {
                    LayoutNpcSpec n = input.npcs[i];
                    if (n == null) continue;
                    copy.npcs.Add(new LayoutNpcSpec
                    {
                        npcId = n.npcId, name = n.name, loop = n.loop, required = n.required,
                        waypoints = n.waypoints != null ? new List<Vector2>(n.waypoints) : new List<Vector2>(),
                    });
                }
            }
            if (input.items != null)
                for (int i = 0; i < input.items.Count; i++) if (input.items[i] != null) copy.items.Add(CopyItem(input.items[i]));
            return copy;
        }

        private static LayoutItemSpec CopyItem(LayoutItemSpec input)
        {
            return new LayoutItemSpec
            {
                id = input.id, name = input.name, semantic = input.semantic, kind = input.kind,
                center = input.center, size = input.size, rotation = input.rotation,
                expectedModelHint = input.expectedModelHint, bindId = input.bindId,
                rowId = input.rowId, rowIndex = input.rowIndex, rowCount = input.rowCount,
                required = input.required, source = input.source, notes = input.notes,
                height = input.height, faceDir = input.faceDir, primitive = input.primitive,
                solverGenerated = input.solverGenerated,
            };
        }
    }
}
