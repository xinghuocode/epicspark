// 场景生成兼容门面。
// 灰盒：GrayboxToolPipeline 通过受限工具建立唯一空间状态。
// 搭建：只把灰盒槽位翻译成 PrimitiveKit 可构建的几何体，不接触美术资产库。
using System;
using System.Security;
using EpicSpark.Core;
using UnityEngine;

namespace EpicSpark.SceneGen
{
    public static class SceneGenPipeline
    {
        /// <summary>灰盒入口：显式传入从剧情描述理解出的语义，禁止再从 scene.layoutSpec 偷偷换数据源。</summary>
        public static GrayboxPlan Plan(SceneSpec scene, LayoutSpec semantic, int seed)
        {
            GrayboxToolReport report;
            GrayboxPlan plan = GrayboxToolPipeline.Build(scene, semantic, seed, out report);
            if (plan == null)
            {
                SafeWarn("[灰盒工具] 生成被阻断：" + report.Compact());
                return null;
            }
            for (int i = 0; i < report.Warnings.Count; i++) SafeWarn("[灰盒工具] " + report.Warnings[i]);
            for (int i = 0; i < report.Operations.Count; i++) SafeLog("[灰盒工具] " + report.Operations[i]);
            SafeLog("[灰盒工具] " + plan.plannerNote);
            return plan;
        }

        /// <summary>旧调用兼容；新代码必须调用带 semantic 参数的重载。</summary>
        public static GrayboxPlan Plan(SceneSpec scene, int seed)
        {
            return Plan(scene, scene != null ? scene.layoutSpec : null, seed);
        }

        /// <summary>搭建入口：每个有效灰盒槽位对应一个简单几何体，锚点/NPC 由装配器专门生成。</summary>
        public static BuildResult PlaceAll(SceneSpec scene, GrayboxPlan plan)
        {
            var result = new BuildResult
            {
                sceneId = scene != null ? (scene.sceneId ?? string.Empty) : string.Empty,
                primitiveMode = true,
            };
            if (plan == null || plan.slots == null || plan.slots.Count == 0)
            {
                result.warnings.Add("灰盒规划为空（没有槽位），素模搭建跳过。");
                return result;
            }

            for (int i = 0; i < plan.slots.Count; i++)
            {
                GrayboxSlot slot = plan.slots[i];
                if (slot == null) continue;
                if (slot.kind == SlotKind.Anchor || slot.kind == SlotKind.Npc) continue;

                string primitive = string.IsNullOrEmpty(slot.primitive) ? PrimitiveForKind(slot) : slot.primitive;
                float height = slot.height > 0.05f ? slot.height : DefaultHeight(primitive, slot.sizeX, slot.sizeY);
                result.placed.Add(new PlacedAsset
                {
                    slotId = slot.slotId,
                    modelId = "prim:" + primitive,
                    instanceName = primitive + "_" + slot.slotId,
                    position = new Vector3(slot.position.x, 0f, slot.position.y),
                    rotationY = slot.faceDir.sqrMagnitude > 0.01f
                        ? SceneGenFacing.AngleFromNormal(slot.faceDir) : slot.rotation,
                    sizeX = slot.sizeX,
                    sizeY = slot.sizeY,
                    height = height,
                    generated = false,
                    matchScore = 1f,
                    kind = slot.kind,
                    bindId = slot.bindId ?? string.Empty,
                    primitive = primitive,
                    faceDir = slot.faceDir,
                });
            }

            if (result.placed.Count == 0)
                result.warnings.Add("素模搭建没有产出任何构件（槽位全部是锚点/NPC）。");
            SafeLog("[搭建/素模] 灰盒槽位 " + plan.slots.Count + " 个 → 几何构件 " + result.placed.Count
                      + " 个（美术库与资产生成均未参与）");
            return result;
        }

        private static void SafeLog(string message)
        {
            try { Debug.Log(message); } catch (SecurityException) { }
        }

        private static void SafeWarn(string message)
        {
            try { Debug.LogWarning(message); } catch (SecurityException) { }
        }

        private static string PrimitiveForKind(GrayboxSlot slot)
        {
            if (slot.kind == SlotKind.Building) return "row_house";
            if (slot.kind == SlotKind.Anchor) return "anchor";
            if (slot.kind == SlotKind.Npc) return "npc";
            return "prop";
        }

        private static float DefaultHeight(string primitive, float sx, float sz)
        {
            switch (primitive)
            {
                case "lamp": return 4.2f;
                case "tree": return 6f;
                case "crowd": return 1.75f;
                case "prop": return 1.4f;
                case "stall": return 2.6f;
                case "steps": return 1.5f;
                case "drum": return 1.2f;
                case "grass": return 0.12f;
                default: return Mathf.Clamp(Mathf.Min(sx, sz) * 0.75f, 3f, 26f);
            }
        }
    }
}
