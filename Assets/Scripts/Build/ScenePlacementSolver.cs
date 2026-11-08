// 场景落位约束求解器：
//   1) 被摆放的模型之间不允许穿模（平面有向包围盒 OBB 的 SAT 分离 + 迭代推开 + 兜底重定位/缩放）；
//   2) NPC / 传送锚点不允许落在模型或墙体内部；
//   3) NPC 巡逻路点、玩家出生点必须落在可行走区域。
// 所有计算都是确定性的：同一份灰盒与搭建结果永远得到同一套布局，便于场景复用与调试复现。
// 坐标约定与其余模块一致：平面 (x, y) → 世界 (x, height, z = y)。
using System;
using System.Collections.Generic;
using UnityEngine;
using EpicSpark.Core;

namespace EpicSpark.Build
{
    /// <summary>
    /// 平面有向包围盒（OBB）。center / 半尺寸都在世界平面上，rotationY 与 Unity 的 transform.eulerAngles.y 一致：
    /// 局部 x 轴在世界平面上的方向为 (cos, -sin)，局部 z 轴为 (sin, cos)。
    /// </summary>
    public sealed class PlacementFootprint
    {
        public string id = "";
        public Vector2 center;
        public float halfX = 0.5f;
        public float halfY = 0.5f;
        public float rotationY;
        /// <summary>false 表示固定障碍（墙体等），求解时不会被推动。</summary>
        public bool movable = true;
        /// <summary>越大越容易被推开（小道具 1，长条结构 0.55，建筑 0.18）。</summary>
        public float mobility = 1f;
        /// <summary>产生该包围盒的落位资产；求解结束后据此回写位置与尺寸。</summary>
        public PlacedAsset owner;

        public Vector2 AxisX
        {
            get
            {
                float rad = rotationY * Mathf.Deg2Rad;
                return new Vector2(Mathf.Cos(rad), -Mathf.Sin(rad));
            }
        }

        public Vector2 AxisY
        {
            get
            {
                float rad = rotationY * Mathf.Deg2Rad;
                return new Vector2(Mathf.Sin(rad), Mathf.Cos(rad));
            }
        }

        /// <summary>旋转后轴对齐包围盒在 x 方向的半尺寸。</summary>
        public float AabbHalfX
        {
            get
            {
                float rad = rotationY * Mathf.Deg2Rad;
                float c = Mathf.Abs(Mathf.Cos(rad));
                float s = Mathf.Abs(Mathf.Sin(rad));
                return halfX * c + halfY * s;
            }
        }

        /// <summary>旋转后轴对齐包围盒在 z（平面 y）方向的半尺寸。</summary>
        public float AabbHalfY
        {
            get
            {
                float rad = rotationY * Mathf.Deg2Rad;
                float c = Mathf.Abs(Mathf.Cos(rad));
                float s = Mathf.Abs(Mathf.Sin(rad));
                return halfX * s + halfY * c;
            }
        }

        public float Area
        {
            get { return 4f * Mathf.Max(0.0001f, halfX) * Mathf.Max(0.0001f, halfY); }
        }

        public float Radius
        {
            get { return Mathf.Max(halfX, halfY); }
        }

        /// <summary>该包围盒在给定轴上的投影半径。</summary>
        public float ProjectRadius(Vector2 axis)
        {
            Vector2 u = AxisX;
            Vector2 v = AxisY;
            return halfX * Mathf.Abs(Vector2.Dot(u, axis)) + halfY * Mathf.Abs(Vector2.Dot(v, axis));
        }

        public PlacementFootprint CloneAt(Vector2 newCenter)
        {
            return new PlacementFootprint
            {
                id = id,
                center = newCenter,
                halfX = halfX,
                halfY = halfY,
                rotationY = rotationY,
                movable = movable,
                mobility = mobility,
                owner = null,
            };
        }
    }

    /// <summary>一次约束求解的统计与告警，供搭建 Agent 汇总进 BuildResult.warnings 与日志。</summary>
    public sealed class PlacementReport
    {
        public int modelCount;
        public int wallCount;
        public int movedModels;
        public int resolvedOverlapPairs;
        public int unresolvedOverlaps;
        public int movedActors;
        public int movedWaypoints;
        public bool spawnAdjusted;
        public readonly List<string> warnings = new List<string>();

        public void AppendTo(List<string> target)
        {
            if (target == null || warnings.Count == 0) return;
            for (int i = 0; i < warnings.Count; i++) target.Add(warnings[i]);
        }
    }

    /// <summary>把场景里的模型 / 墙体 / 剧情单位统一成平面 OBB 后求解空间约束。</summary>
    public static class ScenePlacementSolver
    {
        /// <summary>模型与模型之间必须留出的最小缝隙（米），避免贴面时因浮点误差判定为穿模。</summary>
        public const float ModelGap = 0.12f;
        /// <summary>玩家出生点需要的最小净空（约等于玩家胶囊半径 + 余量）。</summary>
        public const float PlayerClearance = 0.55f;
        /// <summary>NPC 与模型 / 墙体之间的最小净空（NPC 胶囊半径 0.35 + 余量）。</summary>
        public const float NpcClearance = 0.75f;
        /// <summary>锚点与模型 / 墙体之间的最小净空（锚点可视化柱体半径 + 可交互余量）。</summary>
        public const float AnchorClearance = 1.2f;

        private const float Relax = 0.9f;
        private const int MaxWarnings = 16;

        // ─────────────────────────── 主入口 ───────────────────────────

        /// <summary>
        /// 对搭建结果做一次完整的空间约束求解：
        /// 模型互相分离 → 回写位置/尺寸 → NPC 与锚点避让 → 巡逻路点与出生点校验。
        /// 不抛异常；无法完全满足时写入 report.warnings。
        /// </summary>
        public static void ApplyPlacementConstraints(GrayboxPlan plan, BuildResult build, PlacementReport report)
        {
            if (report == null) report = new PlacementReport();
            if (build == null || build.placed == null || build.placed.Count == 0) return;

            float halfW = plan != null && plan.width > 1f ? plan.width * 0.5f : 30f;
            float halfL = plan != null && plan.length > 1f ? plan.length * 0.5f : 30f;

            // 1) 模型间不穿模。
            List<PlacedAsset> modelAssets = CollectModelAssets(build.placed);
            List<PlacementFootprint> walls = BuildWallFootprints(plan);
            List<PlacementFootprint> models = BuildModelFootprints(modelAssets);
            report.modelCount = models.Count;
            report.wallCount = walls.Count;

            var originals = new List<Vector2>(models.Count);
            for (int i = 0; i < models.Count; i++) originals.Add(models[i].center);

            ResolveModelOverlaps(models, walls, halfW, halfL, report);
            ApplyFootprintsToAssets(models);

            for (int i = 0; i < models.Count; i++)
            {
                if ((models[i].center - originals[i]).sqrMagnitude > 0.0001f) report.movedModels++;
            }

            // 2) 剧情单位（NPC / 锚点）不落进模型与墙体。
            var obstacles = new List<PlacementFootprint>(walls.Count + models.Count);
            for (int i = 0; i < walls.Count; i++) obstacles.Add(walls[i]);
            for (int i = 0; i < models.Count; i++) obstacles.Add(models[i]);

            ResolveActors(build.placed, obstacles, halfW, halfL, report);
            ResolveNpcPaths(plan, obstacles, halfW, halfL, report);
            ResolveSpawn(plan, obstacles, halfW, halfL, report);

            Debug.Log("[约束] 模型 " + report.modelCount + " 个 / 墙体障碍 " + report.wallCount + " 段；"
                + "推开模型 " + report.movedModels + " 次，调整 NPC 与锚点 " + report.movedActors + " 个，"
                + "调整巡逻路点 " + report.movedWaypoints + " 个"
                + (report.spawnAdjusted ? "，出生点已挪到空地" : "")
                + (report.unresolvedOverlaps > 0 ? "，残留重叠 " + report.unresolvedOverlaps + " 对（见告警）" : "。"));

            if (report.unresolvedOverlaps > 0)
            {
                Warn(report, "场景过密：仍有 " + report.unresolvedOverlaps + " 对模型无法完全分离，已尽力推开与缩放，请检查灰盒槽位分布。");
            }
        }

        // ─────────────────────────── 模型之间不穿模 ───────────────────────────

        /// <summary>
        /// 迭代把互相重叠的模型推开（以及把模型从墙体里推开），并对剩余重叠做「重定位 + 轻微缩放」兜底。
        /// models 与 fixedObstacles 都会被原地修改 center；固定障碍不会被移动。
        /// </summary>
        public static void ResolveModelOverlaps(
            List<PlacementFootprint> models,
            IList<PlacementFootprint> fixedObstacles,
            float halfW,
            float halfL,
            PlacementReport report)
        {
            if (report == null) report = new PlacementReport();
            if (models == null || models.Count == 0) return;

            int iterations = Mathf.Clamp(models.Count * 4, 16, 72);
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                bool moved = false;
                for (int i = 0; i < models.Count; i++)
                {
                    for (int j = i + 1; j < models.Count; j++)
                        moved |= TryPushApart(models[i], models[j], halfW, halfL);
                }
                if (fixedObstacles != null)
                {
                    for (int i = 0; i < models.Count; i++)
                    {
                        for (int j = 0; j < fixedObstacles.Count; j++)
                        {
                            PlacementFootprint fixedObstacle = fixedObstacles[j];
                            if (fixedObstacle == null) continue;
                            moved |= TryPushApart(models[i], fixedObstacle, halfW, halfL);
                        }
                    }
                }
                if (!moved) break;
            }

            int before = CountModelOverlapsInternal(models, fixedObstacles, ModelGap);
            if (before > 0) ResolveByRelocation(models, fixedObstacles, halfW, halfL, report);
            int after = CountModelOverlapsInternal(models, fixedObstacles, ModelGap);

            report.resolvedOverlapPairs += Mathf.Max(0, before - after);
            report.unresolvedOverlaps += after;
        }

        /// <summary>两个包围盒重叠时沿最小穿透轴把双方按「可推动权重」分开。</summary>
        private static bool TryPushApart(PlacementFootprint a, PlacementFootprint b, float halfW, float halfL)
        {
            if (a == null || b == null) return false;
            Vector2 mtv;
            if (!Overlap(a, b, ModelGap, out mtv)) return false;

            float weightA = a.movable ? Mathf.Max(0.001f, a.mobility) : 0f;
            float weightB = b.movable ? Mathf.Max(0.001f, b.mobility) : 0f;
            float sum = weightA + weightB;
            if (sum <= 0.0001f) return false;

            if (weightA > 0f)
            {
                a.center += mtv * (weightA / sum) * Relax;
                ClampToScene(a, halfW, halfL);
            }
            if (weightB > 0f)
            {
                b.center -= mtv * (weightB / sum) * Relax;
                ClampToScene(b, halfW, halfL);
            }
            return weightA > 0f || weightB > 0f;
        }

        /// <summary>迭代后仍有重叠时的兜底：把较小的一方挪到最近的空地；实在不行再轻微缩小。</summary>
        private static void ResolveByRelocation(
            List<PlacementFootprint> models,
            IList<PlacementFootprint> fixedObstacles,
            float halfW,
            float halfL,
            PlacementReport report)
        {
            for (int pass = 0; pass < 10; pass++)
            {
                bool changed = false;

                for (int i = 0; i < models.Count; i++)
                {
                    for (int j = i + 1; j < models.Count; j++)
                    {
                        Vector2 mtv;
                        if (!Overlap(models[i], models[j], ModelGap, out mtv)) continue;

                        PlacementFootprint smaller = models[i].Area <= models[j].Area ? models[i] : models[j];
                        List<PlacementFootprint> obstacles = CollectObstacles(models, fixedObstacles, smaller);
                        Vector2 free;
                        if (FindFreePointForFootprint(smaller, smaller.center, obstacles, halfW, halfL, out free))
                        {
                            smaller.center = free;
                            changed = true;
                        }
                        else
                        {
                            Shrink(smaller);
                            changed = true;
                        }
                    }
                }

                if (fixedObstacles != null)
                {
                    for (int i = 0; i < models.Count; i++)
                    {
                        for (int j = 0; j < fixedObstacles.Count; j++)
                        {
                            PlacementFootprint wall = fixedObstacles[j];
                            if (wall == null) continue;
                            Vector2 mtv;
                            if (!Overlap(models[i], wall, ModelGap, out mtv)) continue;
                            List<PlacementFootprint> obstacles = CollectObstacles(models, fixedObstacles, models[i]);
                            Vector2 free;
                            if (FindFreePointForFootprint(models[i], models[i].center, obstacles, halfW, halfL, out free))
                            {
                                models[i].center = free;
                                changed = true;
                            }
                            else
                            {
                                Shrink(models[i]);
                                changed = true;
                            }
                        }
                    }
                }

                if (!changed) break;
            }
        }

        private static List<PlacementFootprint> CollectObstacles(
            List<PlacementFootprint> models,
            IList<PlacementFootprint> fixedObstacles,
            PlacementFootprint exclude)
        {
            var result = new List<PlacementFootprint>((models != null ? models.Count : 0) + (fixedObstacles != null ? fixedObstacles.Count : 0));
            if (models != null)
            {
                for (int i = 0; i < models.Count; i++)
                    if (models[i] != null && !ReferenceEquals(models[i], exclude)) result.Add(models[i]);
            }
            if (fixedObstacles != null)
            {
                for (int i = 0; i < fixedObstacles.Count; i++)
                    if (fixedObstacles[i] != null) result.Add(fixedObstacles[i]);
            }
            return result;
        }

        private static void Shrink(PlacementFootprint footprint)
        {
            if (footprint == null) return;
            footprint.halfX = Mathf.Max(0.08f, footprint.halfX * 0.92f);
            footprint.halfY = Mathf.Max(0.08f, footprint.halfY * 0.92f);
        }

        private static void ClampToScene(PlacementFootprint footprint, float halfW, float halfL)
        {
            if (footprint == null) return;
            float limitX = Mathf.Max(0f, halfW - footprint.AabbHalfX - 0.02f);
            float limitZ = Mathf.Max(0f, halfL - footprint.AabbHalfY - 0.02f);
            footprint.center = new Vector2(
                Mathf.Clamp(footprint.center.x, -limitX, limitX),
                Mathf.Clamp(footprint.center.y, -limitZ, limitZ));
        }

        // ─────────────────────────── 剧情单位避让 ───────────────────────────

        /// <summary>把落在模型 / 墙体内部的 NPC 与锚点挪到最近的空地。</summary>
        public static void ResolveActors(
            List<PlacedAsset> placed,
            IList<PlacementFootprint> obstacles,
            float halfW,
            float halfL,
            PlacementReport report)
        {
            if (report == null) report = new PlacementReport();
            if (placed == null) return;
            for (int i = 0; i < placed.Count; i++)
            {
                PlacedAsset asset = placed[i];
                if (asset == null) continue;
                if (asset.kind != SlotKind.Npc && asset.kind != SlotKind.Anchor) continue;

                float clearance = asset.kind == SlotKind.Anchor ? AnchorClearance : NpcClearance;
                Vector2 desired = new Vector2(asset.position.x, asset.position.z);
                if (IsPointFree(desired, clearance, obstacles, halfW, halfL)) continue;

                Vector2 free;
                if (TryFindFreePoint(desired, clearance, obstacles, halfW, halfL, out free))
                {
                    asset.position = new Vector3(free.x, asset.position.y, free.y);
                    report.movedActors++;
                }
                else
                {
                    Warn(report, "剧情单位 " + Describe(asset) + " 找不到不在模型内的空地，已保留原位置。");
                }
            }
        }

        /// <summary>NPC 巡逻路点同样不允许落在模型 / 墙体内部（否则 NPC 会往墙里走）。</summary>
        public static void ResolveNpcPaths(
            GrayboxPlan plan,
            IList<PlacementFootprint> obstacles,
            float halfW,
            float halfL,
            PlacementReport report)
        {
            if (plan == null || plan.npcPaths == null) return;
            if (report == null) report = new PlacementReport();
            for (int i = 0; i < plan.npcPaths.Count; i++)
            {
                NpcPath path = plan.npcPaths[i];
                if (path == null || path.waypoints == null) continue;
                for (int k = 0; k < path.waypoints.Count; k++)
                {
                    Vector2 waypoint = path.waypoints[k];
                    if (IsPointFree(waypoint, NpcClearance, obstacles, halfW, halfL)) continue;
                    Vector2 free;
                    if (TryFindFreePoint(waypoint, NpcClearance, obstacles, halfW, halfL, out free))
                    {
                        path.waypoints[k] = free;
                        report.movedWaypoints++;
                    }
                }
            }
        }

        /// <summary>玩家出生点不允许在模型 / 墙体内部。</summary>
        public static void ResolveSpawn(
            GrayboxPlan plan,
            IList<PlacementFootprint> obstacles,
            float halfW,
            float halfL,
            PlacementReport report)
        {
            if (plan == null) return;
            if (report == null) report = new PlacementReport();
            if (IsPointFree(plan.spawnPoint, PlayerClearance, obstacles, halfW, halfL)) return;

            Vector2 free;
            if (TryFindFreePoint(plan.spawnPoint, PlayerClearance, obstacles, halfW, halfL, out free))
            {
                plan.spawnPoint = free;
                report.spawnAdjusted = true;
            }
            else
            {
                Warn(report, "玩家出生点落在模型 / 墙体内，且找不到可用空地。");
            }
        }

        // ─────────────────────────── 包围盒构造 ───────────────────────────

        /// <summary>由 PlacedAsset 的落位尺寸与朝向构造平面 OBB；空模型（不渲染）不参与碰撞。</summary>
        public static PlacementFootprint FootprintFromPlaced(PlacedAsset asset)
        {
            if (asset == null) return null;
            float halfX = Mathf.Max(0.05f, asset.sizeX * 0.5f);
            float halfY = Mathf.Max(0.05f, asset.sizeY * 0.5f);
            return new PlacementFootprint
            {
                id = asset.slotId ?? "",
                center = new Vector2(asset.position.x, asset.position.z),
                halfX = halfX,
                halfY = halfY,
                rotationY = asset.rotationY,
                movable = true,
                mobility = MobilityFor(asset.kind, halfX, halfY),
                owner = asset,
            };
        }

        /// <summary>只收集会真正生成模型的落位（NPC / 锚点与空模型不参与模型间碰撞）。</summary>
        public static List<PlacementFootprint> BuildModelFootprints(IEnumerable<PlacedAsset> placed)
        {
            var result = new List<PlacementFootprint>();
            if (placed == null) return result;
            foreach (PlacedAsset asset in placed)
            {
                if (asset == null) continue;
                if (asset.kind == SlotKind.Npc || asset.kind == SlotKind.Anchor) continue;
                if (string.IsNullOrEmpty(asset.modelId)) continue;
                PlacementFootprint footprint = FootprintFromPlaced(asset);
                if (footprint != null) result.Add(footprint);
            }
            return result;
        }

        /// <summary>灰盒规划里的墙 / 栅栏会生成实体碰撞体，这里同步成固定障碍。</summary>
        public static List<PlacementFootprint> BuildWallFootprints(GrayboxPlan plan)
        {
            var result = new List<PlacementFootprint>();
            if (plan == null || plan.paths == null) return result;
            for (int i = 0; i < plan.paths.Count; i++)
            {
                GrayboxPath path = plan.paths[i];
                if (path == null || path.nodes == null || path.nodes.Count < 2) continue;
                if (path.kind != "wall" && path.kind != "fence") continue;
                for (int n = 0; n < path.nodes.Count - 1; n++)
                {
                    PlacementFootprint segment = CreateWallFootprint(path.nodes[n], path.nodes[n + 1], path.pathId + "_" + n);
                    if (segment != null) result.Add(segment);
                }
            }
            return result;
        }

        /// <summary>
        /// 单段墙体 OBB：与 SceneAssembler.BuildWallSegments 的几何一致（长 = 段长，厚 = 0.6，绕 Y 旋转）。
        /// </summary>
        public static PlacementFootprint CreateWallFootprint(Vector2 a, Vector2 b, string id)
        {
            Vector2 delta = b - a;
            float length = delta.magnitude;
            if (length < 0.5f) return null;
            return new PlacementFootprint
            {
                id = id ?? "wall",
                center = (a + b) * 0.5f,
                halfX = length * 0.5f,
                halfY = 0.3f,
                rotationY = -Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg,
                movable = false,
                mobility = 0f,
                owner = null,
            };
        }

        /// <summary>把求解后的包围盒写回落位资产（位置与尺寸）。</summary>
        public static void ApplyFootprintsToAssets(IList<PlacementFootprint> footprints)
        {
            if (footprints == null) return;
            for (int i = 0; i < footprints.Count; i++)
            {
                PlacementFootprint footprint = footprints[i];
                if (footprint == null || footprint.owner == null) continue;
                PlacedAsset asset = footprint.owner;
                asset.position = new Vector3(footprint.center.x, asset.position.y, footprint.center.y);
                asset.sizeX = Mathf.Max(0.1f, footprint.halfX * 2f);
                asset.sizeY = Mathf.Max(0.1f, footprint.halfY * 2f);
            }
        }

        private static List<PlacedAsset> CollectModelAssets(List<PlacedAsset> placed)
        {
            var result = new List<PlacedAsset>();
            if (placed == null) return result;
            for (int i = 0; i < placed.Count; i++)
            {
                PlacedAsset asset = placed[i];
                if (asset == null) continue;
                if (asset.kind == SlotKind.Npc || asset.kind == SlotKind.Anchor) continue;
                if (string.IsNullOrEmpty(asset.modelId)) continue;
                result.Add(asset);
            }
            return result;
        }

        private static float MobilityFor(SlotKind kind, float halfX, float halfY)
        {
            if (kind == SlotKind.Building) return 0.18f;
            float min = Mathf.Max(0.001f, Mathf.Min(halfX, halfY));
            float aspect = Mathf.Max(halfX, halfY) / min;
            if (aspect >= 3.5f) return 0.55f; // 长条结构（城墙 / 栅栏 / 栈桥）折中
            return 1f;
        }

        /// <summary>按落位资产类别与占地比例给出可推动权重（装配阶段按真实包围盒复核时复用）。</summary>
        public static float MobilityFor(PlacedAsset asset)
        {
            if (asset == null) return 1f;
            float halfX = Mathf.Max(0.05f, asset.sizeX * 0.5f);
            float halfY = Mathf.Max(0.05f, asset.sizeY * 0.5f);
            return MobilityFor(asset.kind, halfX, halfY);
        }

        // ─────────────────────────── 几何判定 ───────────────────────────

        /// <summary>两个平面 OBB 是否重叠（SAT）；重叠时输出把 a 推离 b 的最小平移向量（已含 gap）。</summary>
        public static bool Overlap(PlacementFootprint a, PlacementFootprint b, float gap, out Vector2 mtv)
        {
            mtv = Vector2.zero;
            if (a == null || b == null) return false;

            Vector2 delta = a.center - b.center;
            Vector2[] axes = { a.AxisX, a.AxisY, b.AxisX, b.AxisY };
            float minPenetration = float.MaxValue;
            Vector2 minAxis = Vector2.zero;

            for (int i = 0; i < axes.Length; i++)
            {
                Vector2 axis = axes[i];
                if (axis.sqrMagnitude < 0.000001f) continue;
                float radiusA = a.ProjectRadius(axis);
                float radiusB = b.ProjectRadius(axis);
                float distance = Mathf.Abs(Vector2.Dot(delta, axis));
                float penetration = radiusA + radiusB + gap - distance;
                if (penetration <= 0f) return false; // 该轴已分离
                if (penetration < minPenetration)
                {
                    minPenetration = penetration;
                    minAxis = axis;
                }
            }

            if (minPenetration <= 0f || minAxis.sqrMagnitude < 0.000001f) return false;
            float sign = Vector2.Dot(delta, minAxis) >= 0f ? 1f : -1f;
            mtv = minAxis * (minPenetration * sign);
            return true;
        }

        /// <summary>点到有向包围盒的最短距离是否小于 clearance（点在盒内视为 0 距离）。</summary>
        public static bool CircleOverlaps(PlacementFootprint obstacle, Vector2 point, float clearance)
        {
            if (obstacle == null) return false;
            float radius = Mathf.Max(0f, clearance);
            Vector2 delta = point - obstacle.center;
            float localX = Vector2.Dot(delta, obstacle.AxisX);
            float localZ = Vector2.Dot(delta, obstacle.AxisY);
            float closestX = Mathf.Clamp(localX, -obstacle.halfX, obstacle.halfX);
            float closestZ = Mathf.Clamp(localZ, -obstacle.halfY, obstacle.halfY);
            float dx = localX - closestX;
            float dz = localZ - closestZ;
            return dx * dx + dz * dz < radius * radius;
        }

        /// <summary>平面点（带净空）是否可站：在场景内且不与任何障碍相交。</summary>
        public static bool IsPointFree(
            Vector2 point,
            float clearance,
            IList<PlacementFootprint> obstacles,
            float halfW,
            float halfL)
        {
            float limitX = Mathf.Max(0.1f, halfW - clearance - 0.2f);
            float limitZ = Mathf.Max(0.1f, halfL - clearance - 0.2f);
            if (Mathf.Abs(point.x) > limitX || Mathf.Abs(point.y) > limitZ) return false;
            if (obstacles == null) return true;
            for (int i = 0; i < obstacles.Count; i++)
            {
                if (CircleOverlaps(obstacles[i], point, clearance)) return false;
            }
            return true;
        }

        /// <summary>
        /// 从期望点开始找最近的可站点：先按逐渐扩大的同心环采样，再退回全场景网格扫描。
        /// 搜索完全确定（固定相位与步长），同一输入永远得到同一结果。
        /// </summary>
        public static bool TryFindFreePoint(
            Vector2 desired,
            float clearance,
            IList<PlacementFootprint> obstacles,
            float halfW,
            float halfL,
            out Vector2 result)
        {
            result = desired;
            if (IsPointFree(desired, clearance, obstacles, halfW, halfL)) return true;

            float step = Mathf.Max(0.75f, clearance * 1.2f);
            float maxRadius = Mathf.Max(halfW, halfL) * 1.4f;
            for (int ring = 1; ring <= 28; ring++)
            {
                float radius = step * ring;
                if (radius > maxRadius) break;
                int samples = Mathf.Clamp(12 + ring * 4, 12, 96);
                float phase = ring * 0.61803f * Mathf.PI;
                for (int s = 0; s < samples; s++)
                {
                    float angle = phase + s * (Mathf.PI * 2f / samples);
                    Vector2 candidate = desired + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
                    if (IsPointFree(candidate, clearance, obstacles, halfW, halfL))
                    {
                        result = candidate;
                        return true;
                    }
                }
            }

            // 网格兜底：取全场景内离期望点最近的可站点。
            float gridStep = Mathf.Max(1.5f, clearance * 2f);
            Vector2 best = Vector2.zero;
            float bestDistance = float.MaxValue;
            bool found = false;
            for (float x = -halfW; x <= halfW + 0.001f; x += gridStep)
            {
                for (float z = -halfL; z <= halfL + 0.001f; z += gridStep)
                {
                    Vector2 candidate = new Vector2(x, z);
                    if (!IsPointFree(candidate, clearance, obstacles, halfW, halfL)) continue;
                    float distance = (candidate - desired).sqrMagnitude;
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = candidate;
                        found = true;
                    }
                }
            }
            if (found)
            {
                result = best;
                return true;
            }
            return false;
        }

        /// <summary>为一个平面 OBB 找到最近的、不与障碍重叠的落点（用于重叠兜底重定位）。</summary>
        public static bool FindFreePointForFootprint(
            PlacementFootprint footprint,
            Vector2 desired,
            IList<PlacementFootprint> obstacles,
            float halfW,
            float halfL,
            out Vector2 result)
        {
            result = desired;
            if (footprint == null) return false;
            if (IsFootprintFree(footprint, desired, obstacles, halfW, halfL, true)) return true;

            float step = Mathf.Clamp(footprint.Radius * 0.5f, 0.8f, 5f);
            float maxRadius = Mathf.Max(halfW, halfL) * 1.4f;
            for (int ring = 1; ring <= 24; ring++)
            {
                float radius = step * ring;
                if (radius > maxRadius) break;
                int samples = Mathf.Clamp(10 + ring * 3, 10, 64);
                float phase = ring * 0.61803f * Mathf.PI;
                for (int s = 0; s < samples; s++)
                {
                    float angle = phase + s * (Mathf.PI * 2f / samples);
                    Vector2 candidate = desired + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
                    if (IsFootprintFree(footprint, candidate, obstacles, halfW, halfL, true))
                    {
                        result = candidate;
                        return true;
                    }
                }
            }

            float gridStep = Mathf.Clamp(footprint.Radius * 1.5f, 1.5f, 6f);
            Vector2 best = Vector2.zero;
            float bestDistance = float.MaxValue;
            bool found = false;
            for (float x = -halfW; x <= halfW + 0.001f; x += gridStep)
            {
                for (float z = -halfL; z <= halfL + 0.001f; z += gridStep)
                {
                    Vector2 candidate = new Vector2(x, z);
                    if (!IsFootprintFree(footprint, candidate, obstacles, halfW, halfL, false)) continue;
                    float distance = (candidate - desired).sqrMagnitude;
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = candidate;
                        found = true;
                    }
                }
            }
            if (found)
            {
                result = best;
                return true;
            }
            return false;
        }

        private static bool IsFootprintFree(
            PlacementFootprint footprint,
            Vector2 center,
            IList<PlacementFootprint> obstacles,
            float halfW,
            float halfL,
            bool checkBounds)
        {
            PlacementFootprint probe = footprint.CloneAt(center);
            if (checkBounds)
            {
                float limitX = halfW - probe.AabbHalfX - 0.05f;
                float limitZ = halfL - probe.AabbHalfY - 0.05f;
                if (limitX < 0f || limitZ < 0f) return false;
                if (Mathf.Abs(center.x) > limitX || Mathf.Abs(center.y) > limitZ) return false;
            }
            if (obstacles == null) return true;
            for (int i = 0; i < obstacles.Count; i++)
            {
                PlacementFootprint obstacle = obstacles[i];
                if (obstacle == null) continue;
                if (ReferenceEquals(obstacle, footprint)) continue;
                Vector2 mtv;
                if (Overlap(probe, obstacle, ModelGap, out mtv)) return false;
            }
            return true;
        }

        // ─────────────────────────── 审计用统计 ───────────────────────────

        /// <summary>统计 BuildResult 中会生成模型的落位之间（按请求尺寸）的重叠对数。</summary>
        public static int CountModelOverlaps(IEnumerable<PlacedAsset> placed, float gap)
        {
            List<PlacementFootprint> models = BuildModelFootprints(placed);
            return CountModelOverlapsInternal(models, null, gap);
        }

        /// <summary>统计 NPC / 锚点落在模型或墙体内部的数量（按请求尺寸，用于搭建阶段自检）。</summary>
        public static int CountUnitModelViolations(
            IEnumerable<PlacedAsset> placed,
            GrayboxPlan plan,
            out int npcViolations,
            out int anchorViolations)
        {
            npcViolations = 0;
            anchorViolations = 0;
            if (placed == null) return 0;

            var obstacles = BuildModelFootprints(placed);
            obstacles.AddRange(BuildWallFootprints(plan));

            foreach (PlacedAsset asset in placed)
            {
                if (asset == null) continue;
                if (asset.kind != SlotKind.Npc && asset.kind != SlotKind.Anchor) continue;
                float clearance = asset.kind == SlotKind.Anchor ? AnchorClearance : NpcClearance;
                Vector2 point = new Vector2(asset.position.x, asset.position.z);
                for (int i = 0; i < obstacles.Count; i++)
                {
                    if (!CircleOverlaps(obstacles[i], point, clearance)) continue;
                    if (asset.kind == SlotKind.Anchor) anchorViolations++;
                    else npcViolations++;
                    break;
                }
            }
            return npcViolations + anchorViolations;
        }

        /// <summary>统计两组包围盒之间的重叠对数；second 为空时只统计 first 内部的两两重叠。</summary>
        public static int CountFootprintOverlaps(
            IList<PlacementFootprint> first,
            IList<PlacementFootprint> second,
            float gap)
        {
            if (first == null || first.Count == 0) return 0;
            int count = 0;
            if (second == null || second.Count == 0)
            {
                for (int i = 0; i < first.Count; i++)
                {
                    for (int j = i + 1; j < first.Count; j++)
                    {
                        Vector2 mtv;
                        if (Overlap(first[i], first[j], gap, out mtv)) count++;
                    }
                }
                return count;
            }
            for (int i = 0; i < first.Count; i++)
            {
                for (int j = 0; j < second.Count; j++)
                {
                    Vector2 mtv;
                    if (Overlap(first[i], second[j], gap, out mtv)) count++;
                }
            }
            return count;
        }

        private static int CountModelOverlapsInternal(
            List<PlacementFootprint> models,
            IList<PlacementFootprint> fixedObstacles,
            float gap)
        {
            if (models == null || models.Count == 0) return 0;
            int count = 0;
            for (int i = 0; i < models.Count; i++)
            {
                for (int j = i + 1; j < models.Count; j++)
                {
                    Vector2 mtv;
                    if (Overlap(models[i], models[j], gap, out mtv)) count++;
                }
                if (fixedObstacles == null) continue;
                for (int j = 0; j < fixedObstacles.Count; j++)
                {
                    PlacementFootprint obstacle = fixedObstacles[j];
                    if (obstacle == null) continue;
                    Vector2 mtv;
                    if (Overlap(models[i], obstacle, gap, out mtv)) count++;
                }
            }
            return count;
        }

        // ─────────────────────────── 文本 / 工具 ───────────────────────────

        private static string Describe(PlacedAsset asset)
        {
            if (asset == null) return "(空)";
            if (!string.IsNullOrEmpty(asset.bindId)) return asset.bindId;
            if (!string.IsNullOrEmpty(asset.instanceName)) return asset.instanceName;
            return asset.slotId ?? "(未命名)";
        }

        private static void Warn(PlacementReport report, string message)
        {
            if (report == null || string.IsNullOrEmpty(message)) return;
            if (report.warnings.Count >= MaxWarnings) return;
            report.warnings.Add(message);
            Debug.LogWarning("[约束] " + message);
        }
    }
}
