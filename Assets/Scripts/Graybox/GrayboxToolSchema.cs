// 灰盒编辑器的「有限工具 API」定义。
// 设计文档：LLM 面对的不是美术资产，而是一个受限的灰盒编辑器：
//   划定功能区域 / 划定道路墙壕 / 规划 NPC 动线 / 放置语义槽 / 给出出生点 / 结束。
// parametersJson 必须是合法的 JSON Schema（object 根），供 ILlmService.CompleteWithToolsAsync 转成 OpenAI tools 字段。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using EpicSpark.Core;
using UnityEngine;

namespace EpicSpark.Graybox
{
    /// <summary>灰盒工具集与提示词、以及 LLM 返回参数的解析辅助。</summary>
    public static class GrayboxToolSchema
    {
        public const string ToolDefineRegion = "define_region";
        public const string ToolDefinePath = "define_path";
        public const string ToolDefineNpcRoute = "define_npc_route";
        public const string ToolPlaceSlot = "place_slot";
        public const string ToolSetSpawn = "set_spawn";
        public const string ToolFinishGraybox = "finish_graybox";

        /// <summary>允许的区域功能类型（字符串自由扩展，但这里给出建议枚举）。</summary>
        public static readonly string[] RegionKinds =
        {
            "residential", "square", "church", "market", "water",
            "field", "harbor", "military", "cemetery", "palace",
        };

        /// <summary>允许的条状物类型。</summary>
        public static readonly string[] PathKinds = { "road", "wall", "fence", "trench" };

        /// <summary>允许的槽位种类（与 SlotKind 对应）。</summary>
        public static readonly string[] SlotKinds = { "building", "prop", "npc", "anchor", "decoration" };

        // ─────────────────────────── 工具定义 ───────────────────────────

        /// <summary>返回 6 个灰盒编辑工具的完整定义（每次调用返回新实例，便于调用方修改描述）。</summary>
        public static List<LlmToolDef> CreateToolDefs()
        {
            return new List<LlmToolDef>
            {
                new LlmToolDef
                {
                    name = ToolDefineRegion,
                    description = "划定一个功能区域（闭合多边形）。区域之间不得重叠，且必须完全位于场景边界内。"
                        + "适合表达居民区 / 广场 / 教堂区 / 集市区 / 水域 / 农田 / 港口 / 军事区 / 墓园 / 宫廷。",
                    parametersJson = RegionParameters,
                },
                new LlmToolDef
                {
                    name = ToolDefinePath,
                    description = "划定一条条状物：road 道路 / wall 石墙 / fence 栅栏 / trench 战壕。"
                        + "至少 2 个节点，可多段折线；道路应从出生点通向各功能区域。",
                    parametersJson = PathParameters,
                },
                new LlmToolDef
                {
                    name = ToolDefineNpcRoute,
                    description = "规划一名 NPC 的巡逻/行走动线。npcId 必须来自给定 NPC 列表；至少 2 个航点。",
                    parametersJson = NpcRouteParameters,
                },
                new LlmToolDef
                {
                    name = ToolPlaceSlot,
                    description = "放置一个语义槽：说明场景中某处具体是什么物件，等待搭建 Agent 检索或生成美术资产。"
                        + "semantic 必须是可直接用于美术检索的中文自然语言（材质+形制+风格），不要写抽象词。",
                    parametersJson = SlotParameters,
                },
                new LlmToolDef
                {
                    name = ToolSetSpawn,
                    description = "设置玩家出生点（平面坐标，原点在场景中心）。",
                    parametersJson = SpawnParameters,
                },
                new LlmToolDef
                {
                    name = ToolFinishGraybox,
                    description = "灰盒规划结束。调用前确认所有区域、道路、NPC 动线、语义槽、出生点都已给出。",
                    parametersJson = FinishParameters,
                },
            };
        }

        // ─────────────────────────── JSON Schema ───────────────────────────

        private const string RegionParameters = @"{
  ""type"": ""object"",
  ""properties"": {
    ""regionId"": { ""type"": ""string"", ""description"": ""唯一区域 id，例如 region_market_0"" },
    ""kind"": { ""type"": ""string"", ""enum"": [""residential"", ""square"", ""church"", ""market"", ""water"", ""field"", ""harbor"", ""military"", ""cemetery"", ""palace""], ""description"": ""功能类型"" },
    ""description"": { ""type"": ""string"", ""description"": ""该区域承担的功能与氛围"" },
    ""style"": { ""type"": ""string"", ""description"": ""风格与材质描述，供搭建 Agent 选型"" },
    ""polygon"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 6, ""description"": ""平面顶点扁平数组 x0,y0,x1,y1,...，至少 3 个顶点；原点在场景中心，坐标范围见系统提示"" },
    ""groundHeight"": { ""type"": ""number"", ""description"": ""地面相对高度，默认 0"" },
    ""areaWeight"": { ""type"": ""number"", ""description"": ""区域权重，默认 1"" }
  },
  ""required"": [""regionId"", ""kind"", ""polygon""]
}";

        private const string PathParameters = @"{
  ""type"": ""object"",
  ""properties"": {
    ""pathId"": { ""type"": ""string"", ""description"": ""唯一路径 id，例如 path_main"" },
    ""kind"": { ""type"": ""string"", ""enum"": [""road"", ""wall"", ""fence"", ""trench""], ""description"": ""条状物类型"" },
    ""nodes"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 4, ""description"": ""平面节点扁平数组 x0,y0,x1,y1,...，至少 2 个节点"" },
    ""width"": { ""type"": ""number"", ""description"": ""宽度/厚度（米），道路 3~7，墙 0.6~2"" }
  },
  ""required"": [""pathId"", ""kind"", ""nodes""]
}";

        private const string NpcRouteParameters = @"{
  ""type"": ""object"",
  ""properties"": {
    ""npcId"": { ""type"": ""string"", ""description"": ""必须等于给定 NPC 列表中的 npcId"" },
    ""waypoints"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 4, ""description"": ""平面航点扁平数组 x0,y0,x1,y1,...，至少 2 个"" },
    ""loop"": { ""type"": ""boolean"", ""description"": ""是否循环巡逻，默认 true"" },
    ""speed"": { ""type"": ""number"", ""description"": ""移动速度（米/秒），0.4~4"" }
  },
  ""required"": [""npcId"", ""waypoints""]
}";

        private const string SlotParameters = @"{
  ""type"": ""object"",
  ""properties"": {
    ""slotId"": { ""type"": ""string"", ""description"": ""唯一槽位 id"" },
    ""regionId"": { ""type"": ""string"", ""description"": ""所属区域 id，可为空表示不在任何区域内"" },
    ""semantic"": { ""type"": ""string"", ""description"": ""可直接检索美术资产的中文描述，例如「石砌教堂，中世纪西欧风格，带尖顶」「橡木桶，陈旧，铁箍」"" },
    ""x"": { ""type"": ""number"", ""description"": ""平面 x 坐标"" },
    ""y"": { ""type"": ""number"", ""description"": ""平面 y 坐标（场景纵深方向）"" },
    ""rotation"": { ""type"": ""number"", ""description"": ""朝向（度），0 表示面向 +z"" },
    ""sizeX"": { ""type"": ""number"", ""description"": ""占地宽度（米）"" },
    ""sizeY"": { ""type"": ""number"", ""description"": ""占地纵深（米）"" },
    ""kind"": { ""type"": ""string"", ""enum"": [""building"", ""prop"", ""npc"", ""anchor"", ""decoration""], ""description"": ""槽位种类"" },
    ""bindId"": { ""type"": ""string"", ""description"": ""绑定 NpcSpec.npcId / SceneAnchorSpec.anchorId / PropInteraction.propId，可不填"" },
    ""expectedModelHint"": { ""type"": ""string"", ""description"": ""期望模型关键词补充，例如此刻期望「路灯」"" },
    ""specItemId"": { ""type"": ""string"", ""description"": ""执行模式下必须等于 seed 槽位的 specItemId，新增槽位不填"" },
    ""rowId"": { ""type"": ""string"", ""description"": ""连续街屋分组 id；执行模式下保留 seed 值"" },
    ""rowIndex"": { ""type"": ""integer"", ""description"": ""排屋单元序号，从 0 开始"" },
    ""rowCount"": { ""type"": ""integer"", ""description"": ""排屋单元总数"" }
  },
  ""required"": [""slotId"", ""semantic"", ""x"", ""y"", ""kind""]
}";

        private const string SpawnParameters = @"{
  ""type"": ""object"",
  ""properties"": {
    ""x"": { ""type"": ""number"", ""description"": ""出生点平面 x 坐标"" },
    ""y"": { ""type"": ""number"", ""description"": ""出生点平面 y 坐标（场景纵深方向）"" }
  },
  ""required"": [""x"", ""y""]
}";

        private const string FinishParameters = @"{
  ""type"": ""object"",
  ""properties"": {
    ""note"": { ""type"": ""string"", ""description"": ""一句话总结灰盒布局思路"" }
  },
  ""required"": [""note""]
}";

        // ─────────────────────────── 提示词 ───────────────────────────

        /// <summary>系统提示词：把 LLM 约束成「灰盒编辑器操作员」。</summary>
        public static string BuildSystemPrompt(SceneSpec scene, LayoutSpec spec = null)
        {
            float width = scene != null && scene.width > 1f ? scene.width : 60f;
            float length = scene != null && scene.length > 1f ? scene.length : 60f;
            var sb = new StringBuilder(2048);
            sb.AppendLine("你是 EpicSpark 的灰盒编辑器操作员。你面对的不是美术资产，而是一个有限的灰盒编辑 API。");
            sb.AppendLine("你只能用工具调用表达空间意图，禁止输出几何体、材质、贴图或美术资产名称清单。");
            sb.AppendLine();
            sb.AppendLine("坐标系：平面 (x, y)，原点在场景中心。");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "  场景尺寸 width={0:0.#}（x 方向）、length={1:0.#}（y 方向）：x ∈ [{2:0.#}, {3:0.#}]，y ∈ [{4:0.#}, {5:0.#}]。",
                width, length, -width * 0.5f, width * 0.5f, -length * 0.5f, length * 0.5f));
            sb.AppendLine("  所有坐标必须落在上述范围内，否则会被裁剪。");
            sb.AppendLine();
            sb.AppendLine("可用工具：");
            sb.AppendLine("  1) define_region —— 划定功能区域（多边形，顶点用扁平数组）。区域之间不得重叠，必须闭合。");
            sb.AppendLine("  2) define_path —— 划定 road / wall / fence / trench。道路必须从出生点连通到每个区域。");
            sb.AppendLine("  3) define_npc_route —— 为每个 NPC 给出动线航点（可用循环）。");
            sb.AppendLine("  4) place_slot —— 放置语义槽，semantic 用中文自然语言描述具体物件。");
            sb.AppendLine("  5) set_spawn —— 设置玩家出生点（一般放在场景边缘的入口）。");
            sb.AppendLine("  6) finish_graybox —— 规划完成时最后调用一次。");
            sb.AppendLine();
            sb.AppendLine("硬性规则：");
            sb.AppendLine("  · 区域多边形至少 3 个顶点，顶点按顺序排列；不得超出场景边界；区域面积加起来不要超过场景 85%。");
            sb.AppendLine("  · 语义槽要覆盖每个区域的主要物件（建筑/道具/装饰），总数 8~20 个，沿道路与区域内均匀分布。");
            sb.AppendLine("  · 每条 semantic 必须能直接拿去检索美术资产：写清材质、形制、风格，例如「石砌教堂，中世纪西欧风格，带尖顶」「木桶，橡木，陈旧，铁箍」。");
            sb.AppendLine("  · 不要写「建筑的占位物」「区域A的槽位」这类抽象词。");
            sb.AppendLine("  · 给定 NPC 必须每人一条 define_npc_route，并在其活动区域放一个 kind=npc、bindId=npcId 的槽位。");
            sb.AppendLine("  · 给定锚点必须放一个 kind=anchor、bindId=anchorId 的槽位（场景边缘或建筑旁），semantic 描述载体。");
            sb.AppendLine("  · 给定道具必须放一个 kind=prop、bindId=propId 的槽位。");
            if (spec != null && spec.Usable)
            {
                sb.AppendLine();
                sb.AppendLine("本次是【执行模式】：系统已经给出 LayoutSpec（剧情施工图），seed 槽位就是规格要求。");
                sb.AppendLine("  · seed 槽位带 specItemId/rowId/rowCount；必须原样保留，不能删除、不能合并、不能减少数量。");
                sb.AppendLine("  · 每个规格项对应一个槽位；连续街屋的每个单元都要保留，单元之间不得留缝、不得把多个单元合成一个大房子。");
                sb.AppendLine("  · 规格项的坐标/尺寸/朝向/kind/bindId 是硬约束；只有和道路/其他槽位冲突时才允许做不超过 1.5 米的微调，并保持 streetClearance。");
                sb.AppendLine("  · seed 里的区域/道路已由规格给出，不要用 define_region/define_path 重复定义；确需调整时只改已有条目的参数。");
                sb.AppendLine("  · 可以新增规格外的装饰/道具槽，但不能压进道路、不能与规格建筑重叠。");
                sb.AppendLine("  · 必须调用 finish_graybox；未调用视为计划未完成。");
            }
            else
            {
                sb.AppendLine("  · 全部完成后再调用 finish_graybox。");
            }
            return sb.ToString();
        }

        /// <summary>用户提示词：把 SceneSpec 的文字与剧情单位压成结构化输入。</summary>
        public static string BuildUserPrompt(GrayboxRequest request, LayoutSpec spec = null)
        {
            SceneSpec scene = request != null ? request.scene : null;
            var sb = new StringBuilder(1024);
            sb.AppendLine("请为下面这个场景做灰盒空间规划。");
            if (scene == null)
            {
                sb.AppendLine("（场景描述缺失，请按 60x60 的空旷城镇中心规划。）");
                return sb.ToString();
            }
            sb.AppendLine("sceneId: " + Safe(scene.sceneId));
            sb.AppendLine("场景名: " + Safe(scene.sceneName));
            sb.AppendLine("时代: " + Safe(scene.era));
            sb.AppendLine("地域: " + Safe(scene.region));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "尺寸: width={0:0.#} length={1:0.#}", scene.width, scene.length));
            sb.AppendLine("布局要求: " + Safe(scene.layoutDescription));
            sb.AppendLine("剧情流程: " + Safe(scene.flowDescription));
            sb.AppendLine("搭建要求: " + Safe(scene.buildRequirement));
            if (spec != null && spec.Usable)
            {
                sb.AppendLine();
                sb.AppendLine("【LayoutSpec：剧情施工图（硬约束，逐项执行）】");
                sb.AppendLine("单位：米；坐标原点在场景中心；rowId 相同的槽位是连续街屋，必须保持 rowCount 个单元和相邻关系。");
                for (int i = 0; i < spec.items.Count; i++)
                {
                    LayoutItemSpec item = spec.items[i];
                    if (item == null) continue;
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "- id={0} | kind={1} | center=({2:0.##},{3:0.##}) | size=({4:0.##}×{5:0.##}) | height={6:0.##}"
                        + " | rot={7:0.#} | rowId={8} | rowIndex={9}/{10} | bindId={11} | required={12} | {13}",
                        Safe(item.id), item.kind, item.center.x, item.center.y, item.size.x, item.size.y,
                        item.height, item.rotation, Safe(item.rowId), item.rowIndex + 1, item.rowCount,
                        Safe(item.bindId), item.required ? "true" : "false", Safe(item.name)));
                }
                if (spec.zones != null && spec.zones.Count > 0)
                {
                    sb.AppendLine("地面分区约束（regions 必须逐条照建，不能合并/不能新增）：");
                    for (int i = 0; i < spec.zones.Count; i++)
                    {
                        LayoutZoneSpec zone = spec.zones[i];
                        if (zone == null) continue;
                        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                            "- {0} | kind={1} | 范围=({2}…) | {3}",
                            Safe(zone.zoneId), Safe(zone.kind), PolygonEndpoints(zone.polygon), Safe(zone.description)));
                    }
                }
                if (spec.npcs != null && spec.npcs.Count > 0)
                {
                    sb.AppendLine("NPC 走位约束（npcPaths 必须逐条照建，起点不得改成出生点）：");
                    for (int i = 0; i < spec.npcs.Count; i++)
                    {
                        LayoutNpcSpec npc = spec.npcs[i];
                        if (npc == null) continue;
                        string start = "（无点）";
                        if (npc.waypoints != null && npc.waypoints.Count > 0)
                        {
                            Vector2 p = npc.waypoints[0];
                            start = string.Format(CultureInfo.InvariantCulture, "({0:0.##},{1:0.##})", p.x, p.y);
                        }
                        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                            "- npcId={0} | 名字={1} | 起点={2} | 点数={3}",
                            Safe(npc.npcId), Safe(npc.name), start, npc.waypoints != null ? npc.waypoints.Count : 0));
                    }
                }
                if (spec.paths != null && spec.paths.Count > 0)
                {
                    sb.AppendLine("道路/墙体约束：");
                    for (int i = 0; i < spec.paths.Count; i++)
                    {
                        LayoutPathSpec path = spec.paths[i];
                        if (path == null) continue;
                        string endpoints = "";
                        if (path.nodes != null && path.nodes.Count >= 2)
                        {
                            Vector2 a = path.nodes[0];
                            Vector2 b = path.nodes[path.nodes.Count - 1];
                            endpoints = string.Format(CultureInfo.InvariantCulture,
                                " | ({0:0.##},{1:0.##})→({2:0.##},{3:0.##})", a.x, a.y, b.x, b.y);
                        }
                        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                            "- {0} | kind={1} | width={2:0.##} | nodes={3}{4}", Safe(path.pathId), Safe(path.kind),
                            path.width, path.nodes != null ? path.nodes.Count : 0, endpoints));
                    }
                }
            }
            if (!string.IsNullOrEmpty(request.knowledgeContext))
            {
                sb.AppendLine();
                sb.AppendLine("【历史知识上下文】");
                sb.AppendLine(request.knowledgeContext.Trim());
            }

            sb.AppendLine();
            sb.AppendLine("【NPC（必须每人一条动线 + 一个 npc 槽位）】");
            if (scene.npcs == null || scene.npcs.Count == 0) sb.AppendLine("（无）");
            else
            {
                for (int i = 0; i < scene.npcs.Count; i++)
                {
                    NpcSpec npc = scene.npcs[i];
                    if (npc == null) continue;
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "- npcId={0} | 名字={1} | 身份={2} | 起始区域={3} | 人设={4} | 剧情作用={5}",
                        Safe(npc.npcId), Safe(npc.displayName), Safe(npc.tag), Safe(npc.startingRegionId),
                        Safe(npc.persona), Safe(npc.storyRole)));
                }
            }

            sb.AppendLine();
            sb.AppendLine("【传送锚点（必须每个一个 anchor 槽位）】");
            if (scene.anchors == null || scene.anchors.Count == 0) sb.AppendLine("（无）");
            else
            {
                for (int i = 0; i < scene.anchors.Count; i++)
                {
                    SceneAnchorSpec anchor = scene.anchors[i];
                    if (anchor == null) continue;
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "- anchorId={0} | 名字={1} | 载体要求={2} | 描述={3} | 模型提示={4} | 时间跳转={5}",
                        Safe(anchor.anchorId), Safe(anchor.displayName), Safe(anchor.requirement),
                        Safe(anchor.description), Safe(anchor.modelHint), Safe(anchor.timeDelta)));
                }
            }

            sb.AppendLine();
            sb.AppendLine("【可互动道具（必须每个一个 prop 槽位）】");
            if (scene.props == null || scene.props.Count == 0) sb.AppendLine("（无）");
            else
            {
                for (int i = 0; i < scene.props.Count; i++)
                {
                    PropInteraction prop = scene.props[i];
                    if (prop == null) continue;
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "- propId={0} | 名字={1} | 交互={2} | 描述={3}",
                        Safe(prop.propId), Safe(prop.displayName), Safe(prop.interactKind), Safe(prop.description)));
                }
            }

            sb.AppendLine();
            sb.AppendLine("现在开始逐个调用工具。全部完成后调用 finish_graybox。");
            return sb.ToString();
        }

        /// <summary>把当前已累积的规划压成简短文本，供多轮工具调用时做「已完成的编辑」上下文。</summary>
        public static string DescribePlan(GrayboxPlan plan)
        {
            if (plan == null) return "（还没有任何编辑）";
            var sb = new StringBuilder(512);
            sb.AppendLine("出生点: (" + Format(plan.spawnPoint.x) + ", " + Format(plan.spawnPoint.y) + ")");
            sb.AppendLine("区域 " + (plan.regions != null ? plan.regions.Count : 0) + " 个:");
            if (plan.regions != null)
            {
                for (int i = 0; i < plan.regions.Count; i++)
                {
                    GrayboxRegion region = plan.regions[i];
                    if (region == null) continue;
                    sb.AppendLine("  - " + Safe(region.regionId) + " [" + Safe(region.kind) + "] " + Safe(region.description)
                        + " 顶点数 " + (region.polygon != null ? region.polygon.Count : 0));
                }
            }
            sb.AppendLine("条状物 " + (plan.paths != null ? plan.paths.Count : 0) + " 条");
            if (plan.paths != null)
            {
                for (int i = 0; i < plan.paths.Count; i++)
                {
                    GrayboxPath path = plan.paths[i];
                    if (path == null) continue;
                    string endpoints = "";
                    if (path.nodes != null && path.nodes.Count >= 2)
                    {
                        Vector2 a = path.nodes[0];
                        Vector2 b = path.nodes[path.nodes.Count - 1];
                        endpoints = " (" + Format(a.x) + "," + Format(a.y) + ")→(" + Format(b.x) + "," + Format(b.y) + ")";
                    }
                    sb.AppendLine("  - " + Safe(path.pathId) + " [" + Safe(path.kind) + "] width="
                        + Format(path.width) + endpoints);
                }
            }
            sb.AppendLine("语义槽 " + (plan.slots != null ? plan.slots.Count : 0) + " 个");
            if (plan.slots != null)
            {
                for (int i = 0; i < plan.slots.Count; i++)
                {
                    GrayboxSlot slot = plan.slots[i];
                    if (slot == null) continue;
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  - {0} [{1}] ({2:0.##},{3:0.##}) size=({4:0.##}×{5:0.##}) h={6:0.##} rot={7:0.#}"
                        + " spec={8} row={9}/{10} bind={11} required={12} | {13}",
                        Safe(slot.slotId), slot.kind, slot.position.x, slot.position.y,
                        slot.sizeX, slot.sizeY, slot.height, slot.rotation, Safe(slot.specItemId),
                        slot.rowIndex + 1, slot.rowCount, Safe(slot.bindId),
                        slot.specRequired ? "true" : "false", Safe(slot.semantic)));
                }
            }
            sb.AppendLine("NPC 动线 " + (plan.npcPaths != null ? plan.npcPaths.Count : 0) + " 条");
            if (plan.npcPaths != null)
            {
                for (int i = 0; i < plan.npcPaths.Count; i++)
                {
                    NpcPath path = plan.npcPaths[i];
                    if (path == null) continue;
                    string start = "（无点）";
                    if (path.waypoints != null && path.waypoints.Count > 0)
                    {
                        Vector2 p = path.waypoints[0];
                        start = string.Format(CultureInfo.InvariantCulture, "({0:0.##},{1:0.##})", p.x, p.y);
                    }
                    sb.AppendLine("  - " + Safe(path.npcId) + " 起点=" + start + " 点数="
                        + (path.waypoints != null ? path.waypoints.Count : 0));
                }
            }
            if (plan.specDriven)
                sb.AppendLine("模式: LayoutSpec 执行（seed 槽位/分区/动线必须保留，finish_graybox 必须调用）");
            return sb.ToString();
        }

        private static string Safe(string value)
        {
            return string.IsNullOrEmpty(value) ? "（空）" : value.Trim();
        }

        private static string Format(float value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        /// <summary>多边形 → "(minX,minZ)→(maxX,maxZ)" 形式的包围盒文字（提示词用）。</summary>
        private static string PolygonEndpoints(List<Vector2> polygon)
        {
            if (polygon == null || polygon.Count == 0) return "空";
            float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
            for (int i = 0; i < polygon.Count; i++)
            {
                x0 = Mathf.Min(x0, polygon[i].x); x1 = Mathf.Max(x1, polygon[i].x);
                z0 = Mathf.Min(z0, polygon[i].y); z1 = Mathf.Max(z1, polygon[i].y);
            }
            return string.Format(CultureInfo.InvariantCulture, "({0:0.##},{1:0.##})→({2:0.##},{3:0.##})", x0, z0, x1, z1);
        }

        // ─────────────────────────── 参数解析辅助 ───────────────────────────

        /// <summary>
        /// 稳健地从 argumentsJson 里抽取「扁平数字数组」字段（polygon / nodes / waypoints）。
        /// JsonUtility 能直接反序列化 float[]，但当模型把坐标写成 [{x,y},...] 或夹带注释时会失败；
        /// 这里按字符扫描，遇到数字就收集，因此对象数组写法也能还原成 x0,y0,x1,y1...
        /// </summary>
        public static bool TryExtractFlatNumbers(string json, string fieldName, out float[] values)
        {
            values = null;
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(fieldName)) return false;

            string key = "\"" + fieldName + "\"";
            int keyIndex = json.IndexOf(key, StringComparison.Ordinal);
            if (keyIndex < 0) return false;
            int colon = json.IndexOf(':', keyIndex + key.Length);
            if (colon < 0) return false;
            int start = json.IndexOf('[', colon);
            if (start < 0) return false;

            int end = -1;
            int depth = 0;
            for (int i = start; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '[') depth++;
                else if (c == ']')
                {
                    depth--;
                    if (depth == 0) { end = i; break; }
                }
            }
            if (end <= start) return false;

            var numbers = new List<float>(16);
            var builder = new StringBuilder(24);
            for (int i = start + 1; i < end; i++)
            {
                char c = json[i];
                bool numeric = (c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E';
                if (numeric)
                {
                    builder.Append(c);
                    continue;
                }
                Flush(builder, numbers);
            }
            Flush(builder, numbers);
            if (numbers.Count == 0) return false;
            values = numbers.ToArray();
            return true;
        }

        private static void Flush(StringBuilder builder, List<float> numbers)
        {
            if (builder.Length == 0) return;
            string token = builder.ToString();
            builder.Length = 0;
            float value;
            if (float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                numbers.Add(value);
        }

        /// <summary>把扁平数组两两成对转成平面的点列表。</summary>
        public static List<Vector2> ToPoints(float[] flat, int maxPoints)
        {
            var points = new List<Vector2>();
            if (flat == null) return points;
            int count = flat.Length / 2;
            if (maxPoints > 0 && count > maxPoints) count = maxPoints;
            for (int i = 0; i < count; i++)
            {
                float x = flat[i * 2];
                float y = flat[i * 2 + 1];
                if (float.IsNaN(x) || float.IsInfinity(x) || float.IsNaN(y) || float.IsInfinity(y)) continue;
                points.Add(new Vector2(x, y));
            }
            return points;
        }

        // ─────────────────────────── 工具参数 DTO ───────────────────────────
        // JsonUtility 不支持 Dictionary / 顶层数组，因此参数一律用扁平数组 + 标量字段。

        [Serializable]
        public class DefineRegionArgs
        {
            public string regionId = "";
            public string kind = "";
            public string description = "";
            public string style = "";
            public float[] polygon;
            public float groundHeight;
            public float areaWeight = 1f;
        }

        [Serializable]
        public class DefinePathArgs
        {
            public string pathId = "";
            public string kind = "";
            public float[] nodes;
            public float width = 3f;
        }

        [Serializable]
        public class DefineNpcRouteArgs
        {
            public string npcId = "";
            public float[] waypoints;
            public bool loop = true;
            public float speed = 1.2f;
        }

        [Serializable]
        public class PlaceSlotArgs
        {
            public string slotId = "";
            public string regionId = "";
            public string semantic = "";
            public float x;
            public float y;
            public float rotation;
            public float sizeX = 2f;
            public float sizeY = 2f;
            public string kind = "prop";
            public string bindId = "";
            public string expectedModelHint = "";
            public string specItemId = "";
            public string rowId = "";
            public int rowIndex;
            public int rowCount = 1;
        }

        [Serializable]
        public class SetSpawnArgs
        {
            public float x;
            public float y;
        }

        [Serializable]
        public class FinishGrayboxArgs
        {
            public string note = "";
        }
    }
}
