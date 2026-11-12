// 语义槽 → 落位资产的几何 / 尺寸 / 命名规则。
// 全部是纯函数，搭建 Agent 只负责调度检索与生成，方便上层（场景装配、LOD、地形贴地）复算同一套数值。
using System;
using EpicSpark.Core;
using UnityEngine;

namespace EpicSpark.Build
{
    /// <summary>平面尺寸（x = 场景 x 方向，y = 场景 depth/z 方向）。</summary>
    public struct SlotSize
    {
        public float x;
        public float y;
    }

    /// <summary>
    /// 搭建 Agent 的落位规则：
    /// 平面坐标 (x, y) → 世界 (x, 0, y)；类别决定尺寸区间；旋转以槽位朝向为基准加类别小抖动。
    /// </summary>
    public static class AssetBinding
    {
        // ─────────────────────────── 稳定哈希 ───────────────────────────

        /// <summary>FNV-1a 32 位稳定哈希。不要用 string.GetHashCode：它在不同运行时可能被随机化。</summary>
        public static uint StableHash(string text)
        {
            uint hash = 2166136261u;
            if (string.IsNullOrEmpty(text)) return hash;
            for (int i = 0; i < text.Length; i++)
            {
                hash ^= text[i];
                hash *= 16777619u;
            }
            return hash;
        }

        /// <summary>把字符串确定性地映射到 [0,1]。</summary>
        public static float Deterministic01(string text)
        {
            return (StableHash(text) & 0xFFFFFFu) / 16777215f;
        }

        // ─────────────────────────── 类别推断 ───────────────────────────

        private static readonly string[] LightingKeywords =
        {
            "火把", "路灯", "街灯", "蜡烛", "烛台", "灯笼", "篝火", "火盆", "灯柱", "灯",
            "torch", "lantern", "candle", "lamp", "light", "campfire",
        };

        private static readonly string[] ContainerKeywords =
        {
            "木桶", "水桶", "橡木桶", "木箱", "货箱", "木箱堆", "麻袋", "布袋", "袋子", "箱", "桶",
            "barrel", "crate", "sack", "bucket", "chest", "container",
        };

        private static readonly string[] FurnitureKeywords =
        {
            "长凳", "木凳", "书架", "桌椅", "木桌", "桌子", "木椅", "椅子", "木床", "床", "柜子", "家具",
            "chair", "table", "bench", "bed", "bookshelf", "shelf", "furniture",
        };

        private static readonly string[] WeaponKeywords =
        {
            "火炮", "大炮", "火炮阵地", "断头台", "火枪", "步枪", "长枪", "长剑", "炮位", "炮", "枪", "剑",
            "cannon", "musket", "rifle", "sword", "guillotine", "weapon",
        };

        private static readonly string[] VehicleKeywords =
        {
            "马车", "手推车", "推车", "小船", "木船", "帆船", "船", "车厢",
            "carriage", "cart", "boat", "ship", "wagon", "vehicle",
        };

        private static readonly string[] BuildingKeywords =
        {
            "民居", "民房", "房屋", "房子", "教堂", "礼拜堂", "钟楼", "酒馆", "铁匠铺", "谷仓",
            "风车磨坊", "水车磨坊", "磨坊", "风车", "水车", "瞭望塔", "城堡", "主楼", "卫兵室",
            "仓库", "宫殿", "市场大棚", "棚屋", "屋", "楼", "塔", "建筑", "house", "church",
            "tavern", "barn", "windmill", "watermill", "castle", "tower", "warehouse", "palace", "building",
        };

        private static readonly string[] StructureKeywords =
        {
            "石砌城墙", "城墙", "矮墙", "石墙", "木墙", "围墙", "栅栏", "围栏", "篱笆", "拒马", "土垒",
            "水井", "石井", "拱门", "台阶", "石阶", "石桥", "栈桥", "驳岸", "路缘", "墙", "井",
            "wall", "fence", "well", "arch", "stairs", "bridge", "gate", "trench",
        };

        private static readonly string[] NatureKeywords =
        {
            "枯树", "大树", "树木", "灌木", "草丛", "花丛", "巨石", "岩石", "石头", "树", "山",
            "tree", "rock", "bush", "stone", "plant", "nature",
        };

        private static readonly string[] PropKeywords =
        {
            "路牌", "告示板", "告示", "海报", "旗帜", "布旗", "旗帜杆", "铁砧", "雕像", "喷泉",
            "晾衣杆", "杂物", "旗帜布", "画框", "油画", "旗", "牌", "flag", "sign", "poster",
            "painting", "anvil", "statue", "fountain", "prop",
        };

        /// <summary>
        /// 按语义文本推断模型库类别（lighting/container/furniture/weapon/vehicle/building/structure/nature/prop）。
        /// 采用「最长关键词命中」，避免「石砌教堂」被「石」抢走。推断不出时按槽位种类兜底。
        /// </summary>
        public static string InferCategory(GrayboxSlot slot)
        {
            if (slot == null) return "prop";
            string text = LocalEmbedding.Normalize((slot.semantic ?? string.Empty) + " " + (slot.expectedModelHint ?? string.Empty));
            string bestCategory = string.Empty;
            int bestLength = 0;
            Match(text, LightingKeywords, "lighting", ref bestCategory, ref bestLength);
            Match(text, ContainerKeywords, "container", ref bestCategory, ref bestLength);
            Match(text, FurnitureKeywords, "furniture", ref bestCategory, ref bestLength);
            Match(text, WeaponKeywords, "weapon", ref bestCategory, ref bestLength);
            Match(text, VehicleKeywords, "vehicle", ref bestCategory, ref bestLength);
            Match(text, BuildingKeywords, "building", ref bestCategory, ref bestLength);
            Match(text, StructureKeywords, "structure", ref bestCategory, ref bestLength);
            Match(text, NatureKeywords, "nature", ref bestCategory, ref bestLength);
            Match(text, PropKeywords, "prop", ref bestCategory, ref bestLength);
            if (bestLength > 0) return bestCategory;

            switch (slot.kind)
            {
                case SlotKind.Building: return "building";
                case SlotKind.Decoration: return "prop";
                case SlotKind.Npc: return "prop";
                case SlotKind.Anchor: return "prop";
                default: return "prop";
            }
        }

        private static void Match(string text, string[] keywords, string category, ref string bestCategory, ref int bestLength)
        {
            for (int i = 0; i < keywords.Length; i++)
            {
                string keyword = keywords[i];
                if (keyword.Length <= bestLength) continue;
                if (text.IndexOf(keyword, StringComparison.Ordinal) >= 0)
                {
                    bestCategory = category;
                    bestLength = keyword.Length;
                }
            }
        }

        // ─────────────────────────── 尺寸 ───────────────────────────

        /// <summary>
        /// 槽位尺寸 + 资产类别 → 占地尺寸。
        /// 小道具 0.5~1.5，家具 1~2，建筑 6~20；墙/栅栏这类长条物把 sizeX 拉长、sizeY 保持很薄。
        /// </summary>
        public static SlotSize SizeForCategory(string category, GrayboxSlot slot)
        {
            float slotX = slot != null ? slot.sizeX : 4f;
            float slotY = slot != null ? slot.sizeY : 4f;
            if (slotX <= 0f) slotX = 4f;
            if (slotY <= 0f) slotY = slotX;
            // 剧情规格槽位的建筑占地是施工图硬约束：必须原尺寸落地，
            // 不能按类别随机拉宽到 6~20 米，否则连续街屋会互相压住、侵入街道。
            if (slot != null && slot.specRequired && slot.kind == SlotKind.Building)
                return new SlotSize { x = slotX, y = slotY };
            float jitter = 0.9f + Deterministic01((slot != null ? slot.slotId : string.Empty) + "|size") * 0.2f;

            switch (category)
            {
                case "lighting":
                    return Uniform(Scaled(slotX, 0.5f, 1.5f, jitter));
                case "container":
                    return Uniform(Scaled(slotX, 0.7f, 1.5f, jitter));
                case "furniture":
                    return new SlotSize
                    {
                        x = Scaled(slotX, 1.0f, 2.0f, jitter),
                        y = Scaled(slotY, 0.8f, 2.0f, jitter),
                    };
                case "prop":
                    return Uniform(Scaled(slotX, 0.5f, 1.5f, jitter));
                case "weapon":
                    return new SlotSize
                    {
                        x = Scaled(slotX, 1.0f, 2.5f, jitter),
                        y = Scaled(slotY, 0.8f, 2.0f, jitter),
                    };
                case "vehicle":
                    return new SlotSize
                    {
                        x = Scaled(slotX, 2.5f, 6.0f, jitter),
                        y = Scaled(slotX, 1.5f, 3.5f, jitter),
                    };
                case "nature":
                    return Uniform(Scaled(slotX, 1.2f, 5.0f, jitter));
                case "structure":
                    if (IsLongStructure(slot))
                    {
                        return new SlotSize
                        {
                            x = Scaled(slotX, 3.0f, 16.0f, jitter),
                            y = Mathf.Clamp(0.6f * jitter, 0.4f, 1.0f),
                        };
                    }
                    return new SlotSize
                    {
                        x = Scaled(slotX, 2.0f, 6.0f, jitter),
                        y = Scaled(slotY, 1.5f, 5.0f, jitter),
                    };
                case "building":
                    {
                        float width = Scaled(slotX, 6.0f, 20.0f, jitter);
                        float depthRatio = 0.6f + Deterministic01((slot != null ? slot.slotId : string.Empty) + "|depth") * 0.35f;
                        float depth = Mathf.Clamp(width * depthRatio, 4.0f, 20.0f);
                        return new SlotSize { x = width, y = depth };
                    }
                default:
                    return new SlotSize
                    {
                        x = Scaled(slotX, 0.3f, 20f, jitter),
                        y = Scaled(slotY, 0.3f, 20f, jitter),
                    };
            }
        }

        /// <summary>先把槽位尺寸收进类别区间，再乘抖动，最后再收一次，保证结果始终落在类别区间内。</summary>
        private static float Scaled(float value, float min, float max, float jitter)
        {
            float clamped = Mathf.Clamp(value, min, max);
            return Mathf.Clamp(clamped * jitter, min, max);
        }

        private static bool IsLongStructure(GrayboxSlot slot)
        {
            if (slot == null) return false;
            string semantic = LocalEmbedding.Normalize((slot.semantic ?? string.Empty) + " " + (slot.expectedModelHint ?? string.Empty));
            string[] longKeywords = { "墙", "栅栏", "围栏", "篱笆", "战壕", "拒马", "土垒", "wall", "fence", "trench" };
            for (int i = 0; i < longKeywords.Length; i++)
                if (semantic.IndexOf(longKeywords[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        private static SlotSize Uniform(float size)
        {
            float value = Mathf.Max(0.2f, size);
            return new SlotSize { x = value, y = value };
        }

        // ─────────────────────────── 旋转与坐标 ───────────────────────────

        /// <summary>槽位朝向 + 类别抖动（道具 ±16°，建筑 ±6°，墙 ±4°），结果收敛到 -180..180。</summary>
        public static float RotationFor(GrayboxSlot slot, string category)
        {
            if (slot == null) return 0f;
            float range = 16f;
            if (category == "building") range = 6f;
            else if (category == "structure") range = 4f;
            else if (category == "furniture") range = 12f;
            // 连续街屋要的是「共用山墙」，任何角度抖动都会在单元之间制造 V 形缝或角部重叠。
            if (slot.specRequired) range = 0f;
            float jitter = (Deterministic01((slot.slotId ?? string.Empty) + "|rot|" + category) * 2f - 1f) * range;
            return Mathf.DeltaAngle(0f, slot.rotation + jitter);
        }

        /// <summary>平面 (x,y) → 世界 (x, groundHeight, y)。地面高度先给 0，上层按地形修正。</summary>
        public static Vector3 ToWorldPosition(Vector2 planar, float groundHeight)
        {
            return new Vector3(planar.x, groundHeight, planar.y);
        }

        // ─────────────────────────── 落位结果 ───────────────────────────

        /// <summary>检索命中 / 现生成资产 → PlacedAsset。textLength 由槽位决定，modelId 来自条目。</summary>
        public static PlacedAsset CreatePlacedAsset(GrayboxSlot slot, ModelEntry entry, bool generated, float matchScore)
        {
            if (slot == null) return null;
            string category = InferCategory(slot);
            SlotSize size = SizeForCategory(category, slot);
            return new PlacedAsset
            {
                slotId = slot.slotId ?? string.Empty,
                modelId = entry != null ? entry.id ?? string.Empty : string.Empty,
                instanceName = InstanceNameFor(slot, entry),
                position = ToWorldPosition(slot.position, 0f),
                rotationY = RotationFor(slot, category),
                sizeX = size.x,
                sizeY = size.y,
                height = slot.height,
                generated = generated,
                matchScore = Mathf.Clamp01(matchScore),
                kind = slot.kind,
                bindId = slot.bindId ?? string.Empty,
            };
        }

        /// <summary>
        /// NPC / 锚点这类「不查模型库」的槽位：仍然产出一条 PlacedAsset 占位，
        /// modelId 留空由上层按人物 / 锚点规则接管，但要给出 instanceName 与位置。
        /// </summary>
        public static PlacedAsset CreateUnitAsset(GrayboxSlot slot, string instanceName)
        {
            if (slot == null) return null;
            float size;
            if (slot.kind == SlotKind.Anchor) size = Mathf.Clamp(slot.sizeX <= 0f ? 2f : slot.sizeX, 1.5f, 6f);
            else size = Mathf.Clamp(slot.sizeX <= 0f ? 0.6f : slot.sizeX, 0.4f, 1.2f);
            return new PlacedAsset
            {
                slotId = slot.slotId ?? string.Empty,
                modelId = string.Empty,
                instanceName = string.IsNullOrEmpty(instanceName) ? InstanceNameFor(slot, null) : instanceName,
                position = ToWorldPosition(slot.position, 0f),
                rotationY = Mathf.DeltaAngle(0f, slot.rotation),
                sizeX = size,
                sizeY = size,
                height = slot.height,
                generated = false,
                matchScore = 0f,
                kind = slot.kind,
                bindId = slot.bindId ?? string.Empty,
            };
        }

        /// <summary>实例名：优先剧情绑定 id，其次资产名，最后槽位 id；追加槽位 id 保证场景内唯一。</summary>
        public static string InstanceNameFor(GrayboxSlot slot, ModelEntry entry)
        {
            if (slot == null) return "asset";
            string baseName = null;
            if (!string.IsNullOrEmpty(slot.bindId)) baseName = slot.bindId;
            else if (entry != null && !string.IsNullOrEmpty(entry.name)) baseName = entry.name;
            else if (entry != null && !string.IsNullOrEmpty(entry.id)) baseName = entry.id;
            else baseName = "asset";
            if (string.IsNullOrEmpty(slot.slotId)) return baseName;
            return baseName + "_" + slot.slotId;
        }

        /// <summary>检索查询文本：语义描述为主，expectedModelHint 作补充（已被语义包含时不重复拼接）。</summary>
        public static string BuildQuery(GrayboxSlot slot)
        {
            if (slot == null) return string.Empty;
            string semantic = (slot.semantic ?? string.Empty).Trim();
            string hint = (slot.expectedModelHint ?? string.Empty).Trim();
            string body;
            if (semantic.Length == 0) body = hint;
            else if (hint.Length == 0) body = semantic;
            else if (semantic.IndexOf(hint, StringComparison.Ordinal) >= 0) body = semantic;
            else body = semantic + " " + hint;

            // 美术库条目的 category 是英文（building / weapon / lighting ...），
            // 把推断出的类别并入 query，双语概念匹配才能拿到类别命中分。
            string category = InferCategory(slot);
            if (!string.IsNullOrEmpty(category) && body.IndexOf(category, StringComparison.OrdinalIgnoreCase) < 0)
                body = body.Length == 0 ? category : body + " " + category;
            return body;
        }
    }
}
