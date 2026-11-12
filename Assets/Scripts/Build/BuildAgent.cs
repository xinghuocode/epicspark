// 搭建 Agent：当前正式路径只把已验收灰盒转换为 PrimitiveKit 简单几何体。
// 下方模型检索/资产生成代码为历史实现，强制素模阶段不会执行。
// 规则（设计文档「滑块」）：
//   sliderValue = 1 → 容忍度 0（最严格）→ 复用门槛相似度 1.0 → 正常永远不满足 → 总是现生成；
//   sliderValue = 0 → 容忍度 1（最宽松）→ 门槛 0 → 任何现有资产都接受。
// 资产生成 Agent 现生成的资产直接使用，不再回查容忍度（避免无限循环）。
// 生成失败 → 回退「最相似的现有资产」并写 warning，任何单点失败都不抛异常。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using EpicSpark.Core;
using UnityEngine;

namespace EpicSpark.Build
{
    /// <summary>IBuildAgent 实现：串行处理语义槽（受控并发），逐个检索或生成资产并落位。</summary>
    public class BuildAgent : IBuildAgent
    {
        private const int MaxWarnings = 64;

        /// <summary>本场景各资产已出现次数：同一栋房子重复 3 次会显得场景很"简单"。</summary>
        private readonly Dictionary<string, int> UsedModels = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly IModelCatalog catalogOverride;
        private ModelCatalog ownedCatalog;

        public BuildAgent()
        {
        }

        /// <summary>注入模型库；为 null 时使用 Services.Catalog，仍未装配则内部新建一个并自行加载。</summary>
        public BuildAgent(IModelCatalog catalog)
        {
            catalogOverride = catalog;
        }

        public async Task<BuildResult> BuildAsync(BuildRequest request, CancellationToken ct)
        {
            var result = new BuildResult();
            if (request == null || request.scene == null)
            {
                AddWarning(result, "BuildRequest 或 SceneSpec 为空，跳过搭建。");
                return result;
            }

            SceneSpec scene = request.scene;
            result.sceneId = scene.sceneId ?? string.Empty;
            GrayboxPlan plan = request.plan;
            if (plan == null || plan.slots == null || plan.slots.Count == 0)
            {
                AddWarning(result, "灰盒规划为空（没有语义槽），无资产可落位。");
                return result;
            }

            // ── 素模阶段：完全不用美术资产库 ──
            // 只把槽位翻译成程序化几何体（长方体/圆柱/圆锥/楔形），先验证"密度/秩序/落位"。
            // 这一步以前要经过模型库检索 + 资产生成 + 容忍度判断，结果是缺资产就整块消失
            // （实测 30 个槽位只落了 12 个，城堡/城门/教堂/菜市全缺）。
            if (EpicSparkFeatureFlags.UsePrimitiveScene)
            {
                // 当前阶段为强制素模：即使结果为空也返回明确错误，禁止回退 GLB 检索/资产生成。
                BuildResult primitive = EpicSpark.SceneGen.SceneGenPipeline.PlaceAll(scene, plan);
                Debug.Log("[搭建/素模] 场景 " + result.sceneId + " 构件 " + primitive.placed.Count
                          + " 个（美术资产库与资产生成已禁用）");
                return primitive;
            }

            float slider = SliderConfig.Clamp01(request.sliderValue);
            float required = SliderConfig.RequiredSimilarity(slider);
            Debug.Log("[搭建] " + SliderConfig.Describe(slider) + " | 语义槽 " + plan.slots.Count + " 个 | 场景 "
                + (string.IsNullOrEmpty(result.sceneId) ? "(未命名)" : result.sceneId));

            IModelCatalog catalog = ResolveCatalog();
            try
            {
                await catalog.LoadAsync(ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                AddWarning(result, "模型库加载失败，检索将退化为“无命中”：" + e.Message);
            }

            // 串行处理：每次只打一个检索/生成请求，避免一次打出几十个生成请求。
            for (int i = 0; i < plan.slots.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                GrayboxSlot slot = plan.slots[i];
                if (slot == null) continue;
                try
                {
                    PlacedAsset placed = await BuildSlotAsync(slot, i, plan, catalog, slider, required, result, ct);
                    if (placed != null)
                {
                    result.placed.Add(placed);
                    if (!string.IsNullOrEmpty(placed.modelId))
                    {
                        int used;
                        UsedModels.TryGetValue(placed.modelId, out used);
                        UsedModels[placed.modelId] = used + 1;
                    }
                    if (slot.specRequired && slot.kind != SlotKind.Npc && slot.kind != SlotKind.Anchor)
                    {
                        if (string.IsNullOrEmpty(placed.modelId))
                            AddWarning(result, "剧情规格槽位 " + slot.slotId + " 没有可用模型，已落占位。");
                        else if (placed.matchScore > 0f && placed.matchScore < 0.5f)
                            AddWarning(result, "剧情规格槽位 " + slot.slotId + " 资产相似度偏低（"
                                               + placed.matchScore.ToString("0.00") + "），可能不像剧情描述。");
                    }
                }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    AddWarning(result, "槽位 " + (string.IsNullOrEmpty(slot.slotId) ? i.ToString(CultureInfo.InvariantCulture) : slot.slotId)
                        + " 落位异常：" + e.Message);
                }
            }

            EnsureStoryUnits(scene, plan, result);

            Debug.Log("[搭建] 完成：落位 " + result.placed.Count + " 个，现生成 " + result.generationCount
                + " 个，warning " + result.warnings.Count + " 条。");
            return result;
        }

        // ─────────────────────────── 单个槽位 ───────────────────────────

        private async Task<PlacedAsset> BuildSlotAsync(
            GrayboxSlot slot,
            int slotIndex,
            GrayboxPlan plan,
            IModelCatalog catalog,
            float slider,
            float required,
            BuildResult result,
            CancellationToken ct)
        {
            SanitizeSlot(slot, plan, slotIndex);

            // 人物 / 锚点：不查模型库，但必须产出带名字与位置的 PlacedAsset，避免剧情单位漏实例化。
            if (slot.kind == SlotKind.Npc || slot.kind == SlotKind.Anchor)
                return AssetBinding.CreateUnitAsset(slot, null);

            string query = AssetBinding.BuildQuery(slot);
            if (string.IsNullOrEmpty(query))
            {
                AddWarning(result, "槽位 " + slot.slotId + " 没有语义描述，落一个无模型占位，请检查灰盒输出。");
                return AssetBinding.CreateUnitAsset(slot, null);
            }

            ModelMatch match = new ModelMatch();
            try
            {
                match = await catalog.FindBestAsync(query, PreferredCategories(slot.kind), ct);
                // 同一场景内已经用过两次的资产会视觉雷同，优先换一个：换不到就交给生成器
                if (match.HasEntry && UsedModels.Count >= 2)
                {
                    string dupId = match.entry.id;
                    if (UsedModels.TryGetValue(dupId, out int usedCount) && usedCount >= 2)
                    {
                        ModelMatch alternative = await catalog.FindBestAsync(
                            query + "，" + slot.slotId, PreferredCategories(slot.kind), ct);
                        if (alternative.HasEntry && alternative.entry.id != dupId) match = alternative;
                        else AddWarning(result, "场景内 " + dupId + " 已用 " + usedCount
                                            + " 次，同类候选不足，只能重复（建议开启资产生成或补充建筑类资产）。");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                AddWarning(result, "槽位 " + slot.slotId + " 检索失败：" + e.Message);
            }

            // 命中缓存资产（相似度达到复用门槛；滑块 1 时恒不复用 → 总是现生成）。
            if (match.HasEntry && SliderConfig.Accepts(match.similarity, slider))
            {
                return AssetBinding.CreatePlacedAsset(slot, match.entry, false, match.similarity);
            }

            // 未达门槛 → 现生成。生成出来的资产直接使用，不再做容忍度判断。
            string category = AssetBinding.InferCategory(slot);
            IAssetGenerator generator = Services.AssetGenerator;
            ModelEntry generated = null;
            if (generator != null)
            {
                try
                {
                    generated = await generator.GenerateAsync(query, category, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    AddWarning(result, "槽位 " + slot.slotId + " 资产生成异常，回退现有资产：" + e.Message);
                }
            }
            else
            {
                AddWarning(result, "Services.AssetGenerator 未装配，槽位 " + slot.slotId + " 只能回退到最相似现有资产。");
            }

            if (generated != null)
            {
                result.generationCount++;
                Debug.Log("[搭建] 现生成资产 " + generated.id + "（" + generated.name + "）→ 槽位 " + slot.slotId);
                return AssetBinding.CreatePlacedAsset(slot, generated, true, 1f);
            }

            // 生成失败兜底：最相似的现有资产。
            if (match.HasEntry)
            {
                AddWarning(result, string.Format(CultureInfo.InvariantCulture,
                    "槽位 {0} 生成失败，回退最相似资产 {1}（相似度 {2:0.000} < 门槛 {3:0.000}）。",
                    slot.slotId, match.entry.id, match.similarity, required));
                return AssetBinding.CreatePlacedAsset(slot, match.entry, false, match.similarity);
            }

            AddWarning(result, "槽位 " + slot.slotId + " 无任何可用资产（模型库为空且生成失败），先落无模型占位。");
            return AssetBinding.CreateUnitAsset(slot, null);
        }

        /// <summary>不同语义槽期望的资产类别：建筑槽不该拿石阶/水井，道具槽也不该拿整栋楼。</summary>
        private static string[] PreferredCategories(SlotKind kind)
        {
            switch (kind)
            {
                case SlotKind.Building:
                    return new[] { "building" };
                case SlotKind.Prop:
                    return new[] { "prop", "container", "lighting", "weapon" };
                default:
                    return new[] { "prop", "lighting", "structure" };
            }
        }

        private static void SanitizeSlot(GrayboxSlot slot, GrayboxPlan plan, int slotIndex)
        {
            float halfW = Mathf.Max(2f, plan.width * 0.5f);
            float halfL = Mathf.Max(2f, plan.length * 0.5f);

            float x = slot.position.x;
            float y = slot.position.y;
            if (float.IsNaN(x) || float.IsInfinity(x)) x = 0f;
            if (float.IsNaN(y) || float.IsInfinity(y)) y = 0f;
            slot.position = new Vector2(
                Mathf.Clamp(x, -halfW + 0.5f, halfW - 0.5f),
                Mathf.Clamp(y, -halfL + 0.5f, halfL - 0.5f));

            if (float.IsNaN(slot.rotation) || float.IsInfinity(slot.rotation)) slot.rotation = 0f;
            slot.rotation = Mathf.DeltaAngle(0f, slot.rotation);

            // 60 米上限是给 60×60 小场景兜底的；200×200 场景里 70 米长的集市棚、
            // 长街排屋不能被削成 60 米，否则会凭空出现缺口。按场景尺寸放宽到 90%。
            float maxSize = plan.specDriven
                ? Mathf.Clamp(Mathf.Max(60f, Mathf.Max(plan.width, plan.length) * 0.9f), 60f, 180f)
                : 40f;
            slot.sizeX = ClampSize(slot.sizeX, maxSize);
            slot.sizeY = ClampSize(slot.sizeY, maxSize);
            if (string.IsNullOrEmpty(slot.slotId))
                slot.slotId = "slot_auto_" + slotIndex.ToString(CultureInfo.InvariantCulture);
        }

        private static float ClampSize(float value, float max)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value <= 0f) return 2f;
            return Mathf.Clamp(value, 0.2f, max);
        }

        // ─────────────────────────── 剧情单位兜底 ───────────────────────────

        /// <summary>
        /// 灰盒规划（尤其远端工具调用版本）可能漏掉 NPC / 锚点 / 道具槽位；
        /// 这里按 SceneSpec 的剧情位置补齐，保证上层拿到的剧情单位一个不漏。
        /// </summary>
        private static void EnsureStoryUnits(SceneSpec scene, GrayboxPlan plan, BuildResult result)
        {
            if (scene == null || plan == null) return;
            // 规格模式下剧情单位已由 LayoutSpec 执行器/合规合并精确落槽；
            // 再用 localPosition 扇形兜底会覆盖正确坐标，必须禁用。
            if (plan.specDriven) return;

            var npcBound = new HashSet<string>(StringComparer.Ordinal);
            var anchorBound = new HashSet<string>(StringComparer.Ordinal);
            var propBound = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < result.placed.Count; i++)
            {
                PlacedAsset asset = result.placed[i];
                if (asset == null || string.IsNullOrEmpty(asset.bindId)) continue;
                if (asset.kind == SlotKind.Npc) npcBound.Add(asset.bindId);
                else if (asset.kind == SlotKind.Anchor) anchorBound.Add(asset.bindId);
                else if (asset.kind == SlotKind.Prop) propBound.Add(asset.bindId);
            }

            if (scene.npcs != null)
            {
                for (int i = 0; i < scene.npcs.Count; i++)
                {
                    NpcSpec npc = scene.npcs[i];
                    if (npc == null || string.IsNullOrEmpty(npc.npcId) || npcBound.Contains(npc.npcId)) continue;
                    result.placed.Add(CreateFallbackUnit(npc.npcId, npc.displayName, npc.localPosition, plan, SlotKind.Npc));
                    Debug.Log("[搭建] 灰盒未给出 NPC 槽位，按剧情位置补位：" + npc.npcId);
                }
            }

            if (scene.anchors != null)
            {
                for (int i = 0; i < scene.anchors.Count; i++)
                {
                    SceneAnchorSpec anchor = scene.anchors[i];
                    if (anchor == null || string.IsNullOrEmpty(anchor.anchorId) || anchorBound.Contains(anchor.anchorId)) continue;
                    result.placed.Add(CreateFallbackUnit(anchor.anchorId, anchor.displayName, anchor.localPosition, plan, SlotKind.Anchor));
                    Debug.Log("[搭建] 灰盒未给出锚点槽位，按剧情位置补位：" + anchor.anchorId);
                }
            }

            if (scene.props != null)
            {
                for (int i = 0; i < scene.props.Count; i++)
                {
                    PropInteraction prop = scene.props[i];
                    if (prop == null || string.IsNullOrEmpty(prop.propId) || propBound.Contains(prop.propId)) continue;
                    result.placed.Add(CreateFallbackUnit(prop.propId, prop.displayName, Vector3.zero, plan, SlotKind.Prop));
                    AddWarning(result, "灰盒未给出道具槽位 " + prop.propId + "，已放在场景中心附近。");
                }
            }
        }

        private static PlacedAsset CreateFallbackUnit(string bindId, string displayName, Vector3 localPosition, GrayboxPlan plan, SlotKind kind)
        {
            float halfW = Mathf.Max(2f, plan.width * 0.5f);
            float halfL = Mathf.Max(2f, plan.length * 0.5f);
            float x = Mathf.Clamp(localPosition.x, -halfW + 0.5f, halfW - 0.5f);
            float z = Mathf.Clamp(localPosition.z, -halfL + 0.5f, halfL - 0.5f);
            float size = kind == SlotKind.Anchor ? 2f : (kind == SlotKind.Npc ? 0.6f : 1f);
            return new PlacedAsset
            {
                slotId = "slot_fallback_" + bindId,
                modelId = string.Empty,
                instanceName = string.IsNullOrEmpty(displayName) ? bindId : displayName + "_" + bindId,
                position = new Vector3(x, 0f, z),
                rotationY = 0f,
                sizeX = size,
                sizeY = size,
                generated = false,
                matchScore = 0f,
                kind = kind,
                bindId = bindId,
            };
        }

        // ─────────────────────────── 依赖装配 ───────────────────────────

        private IModelCatalog ResolveCatalog()
        {
            if (catalogOverride != null) return catalogOverride;
            if (Services.Catalog != null) return Services.Catalog;
            if (ownedCatalog == null)
            {
                ownedCatalog = new ModelCatalog();
                Debug.Log("[搭建] 未装配 Services.Catalog，搭建 Agent 自建只读模型库（离线兜底）。");
            }
            return ownedCatalog;
        }

        private static void AddWarning(BuildResult result, string message)
        {
            if (result == null || string.IsNullOrEmpty(message)) return;
            if (result.warnings.Count >= MaxWarnings) return;
            result.warnings.Add(message);
            Debug.LogWarning("[搭建] " + message);
        }
    }
}
