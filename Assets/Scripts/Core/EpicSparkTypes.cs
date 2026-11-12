// EpicSpark 核心数据契约
// 本文件是全部 Agent 与运行时模块之间唯一的数据交换定义，任何模块都不得自行新增跨模块类型。
// 设计依据：用户《新建 文本文档.txt》——四层 Agent 流水线。
using System;
using System.Collections.Generic;
using UnityEngine;

namespace EpicSpark.Core
{
    // ─────────────────────────── 枚举 ───────────────────────────

    /// <summary>主观能动者可携带的唯一工具类型（设计文档第二节）。</summary>
    public enum ToolKind
    {
        None = 0,
        Gun = 1,
        Satchel = 2,
    }

    /// <summary>传送锚点触发方式：摆放在场景内供玩家走入，或由事件触发。</summary>
    public enum AnchorTriggerKind
    {
        Placed = 0,
        Event = 1,
    }

    /// <summary>语义槽最终绑定的对象种类。</summary>
    public enum SlotKind
    {
        Building = 0,
        Prop = 1,
        Npc = 2,
        Anchor = 3,
        Decoration = 4,
    }

    /// <summary>一次场景生命周期的阶段，供加载动画与 HUD 显示。</summary>
    public enum ScenePhase
    {
        Idle = 0,
        Preparing = 1,   // 剧情&核心 Agent 推理中
        Graybox = 2,     // 灰盒阶段：灰线区域边界
        Build = 3,       // 搭建阶段：蓝绿棱线
        Reveal = 4,      // 贴图由近及远显现
        Playing = 5,
        Ending = 6,
        Finished = 7,
    }

    // ─────────────────────────── 剧情&核心 Agent 产物 ───────────────────────────

    /// <summary>传送锚点描述。剧情&核心 Agent 生成，搭建 Agent 负责摆放。</summary>
    [Serializable]
    public class SceneAnchorSpec
    {
        public string anchorId = "";
        public string displayName = "";
        public string description = "";
        /// <summary>载体要求，例如「必须是马车」「必须是床」。</summary>
        public string requirement = "";
        /// <summary>检索美术资产用的关键词提示。</summary>
        public string modelHint = "";
        /// <summary>时间跳转量：y/d/h/min 四种单位，例如 10d、2h、5min。</summary>
        public string timeDelta = "";
        /// <summary>剧情&核心 Agent 预测的下一场景 id；为空表示仍在本场景内。</summary>
        public string targetSceneId = "";
        public string targetSceneName = "";
        public AnchorTriggerKind triggerKind = AnchorTriggerKind.Placed;
        /// <summary>当 triggerKind 为 Event 时的触发条件描述。</summary>
        public string triggerEvent = "";
        public Vector3 localPosition = Vector3.zero;
    }

    /// <summary>NPC 描述。tag 用于 NPC 对话 LLM 的身份与上下文。</summary>
    [Serializable]
    public class NpcSpec
    {
        public string npcId = "";
        public string displayName = "";
        public string tag = "";
        public string persona = "";
        public string startingRegionId = "";
        public ToolKind heldTool = ToolKind.None;
        public float maxHp = 100f;
        public Vector3 localPosition = Vector3.zero;
        public string storyRole = "";
    }

    /// <summary>场景内可互动道具，例如可点火的火炮、可登乘的船。</summary>
    [Serializable]
    public class PropInteraction
    {
        public string propId = "";
        public string displayName = "";
        public string description = "";
        /// <summary>ignite / board / collapse / take / examine 五种之一。</summary>
        public string interactKind = "examine";
        public string effect = "";
        /// <summary>剧情给出的世界坐标（米）；剧情没给时为 0，由布局散文/规格兜底定位。</summary>
        public Vector3 localPosition = Vector3.zero;
    }

    /// <summary>结构化剧情步骤：灰盒 Agent 据此规划事件发生位置与 NPC 动线。</summary>
    [Serializable]
    public class FlowStep
    {
        public string stepId = "";
        public string actorId = "";
        public string regionId = "";
        public string description = "";
        public string trigger = "";
        public string effect = "";
        public float delaySeconds;
    }

    /// <summary>
    /// 结构化布局规格：剧情布局的「施工图」。
    /// 每个 item 对应灰盒里的一个单元（一行房屋会被拆成多个 item），灰盒只能执行/微调，不能重新规划。
    /// 旧数据没有这个字段时为 null，所有调用方都按原有路径回退。
    /// </summary>
    [Serializable]
    public class LayoutItemSpec
    {
        public string id = "";
        public string name = "";
        public string semantic = "";
        public SlotKind kind = SlotKind.Prop;
        public Vector2 center;
        public Vector2 size = new Vector2(2f, 2f);
        public float rotation;
        public string expectedModelHint = "";
        public string bindId = "";
        /// <summary>连续街屋的分组 id；非排屋为空。</summary>
        public string rowId = "";
        public int rowIndex;
        public int rowCount = 1;
        public bool required = true;
        /// <summary>layout / anchor / npc / prop / fallback / coverage。</summary>
        public string source = "layout";
        public string notes = "";
        /// <summary>剧情给出的高度（米）；0 表示剧情没写，由构建层按类别推定。</summary>
        public float height;
        /// <summary>立面法线（世界方向，单位向量）。由 SceneGen 求解器按临街关系推导，不再从 rotation 反推。</summary>
        public Vector2 faceDir;
        /// <summary>SceneGen 求解器给出的几何形态（box/cylinder/cone/wedge/...）；空 = 由搭建层按 kind 推定。</summary>
        public string primitive = "";
        /// <summary>true = 由 SceneGen 求解器生成（而非剧情点名）；用于审计与 KPI。</summary>
        public bool solverGenerated;
    }

    /// <summary>
    /// 结构化地面分区：施工图的一部分，灰盒直接照此分区，不再二次解析散文。
    /// 没有这个字段时，「中央石板广场 / 南侧夯土 / 东侧泥土小巷 / 西侧铺石地」这类信息
    /// 会在 LayoutSpec 里整段丢失（08:57 记录：散文 4 分区 → 灰盒 2 分区）。
    /// </summary>
    [Serializable]
    public class LayoutZoneSpec
    {
        public string zoneId = "";
        public string kind = "square";
        public string description = "";
        public List<Vector2> polygon = new List<Vector2>();
        public float groundHeight;
        public bool required = true;
        public string source = "layout";
        /// <summary>开敞度 0..1：1 = 允许完全空旷（广场/菜市主场地），0 = 必须被建筑围合。</summary>
        public float openness;
        /// <summary>表面材质词（石板/夯土/草地/水面…），供地面与灰盒渲染用。</summary>
        public string material = "";
    }

    /// <summary>结构化 NPC 动线：剧情给的走位，灰盒照此生成 npcPaths（不再从出生点合成）。</summary>
    [Serializable]
    public class LayoutNpcSpec
    {
        public string npcId = "";
        public string name = "";
        public List<Vector2> waypoints = new List<Vector2>();
        public bool loop = true;
        public bool required = true;
    }

    /// <summary>结构化道路/墙体约束，供合规校验检查街面净宽。</summary>
    [Serializable]
    public class LayoutPathSpec
    {
        public string pathId = "";
        public string kind = "road";
        public List<Vector2> nodes = new List<Vector2>();
        public float width = 3f;
        public bool required = true;
    }

    /// <summary>
    /// 街道语义（SceneGen 输入）：剧情只说「这是一条主街、两侧是连续街屋」，
    /// 具体地块切分、开间、进深、邻街立面全部由 SceneGen 求解器确定性生成。
    /// </summary>
    [Serializable]
    public class LayoutStreetSpec
    {
        public string pathId = "";
        public string name = "";
        public List<Vector2> nodes = new List<Vector2>();
        public float width = 8f;
        /// <summary>true = 两侧都要临街立面；false = 单侧或纯通路。</summary>
        public bool frontageBoth = true;
        /// <summary>立面风格：classical_row / shop_row / arcade / none。</summary>
        public string style = "classical_row";
        /// <summary>临街建筑进深（米）。0 = 按风格默认。</summary>
        public float depth;
        /// <summary>临街建筑层数；0 = 按风格默认。</summary>
        public int floors;
        /// <summary>true = 连续街墙（不留空隙）；false = 允许独立建筑。</summary>
        public bool continuous = true;
    }

    /// <summary>
    /// 地标语义（SceneGen 输入）：剧情点名「这里必须有一座要塞/教堂/城门」，
    /// 位置用 on（zone 名 / 街道名 / 端点）表达，具体坐标由求解器吸附得出。
    /// </summary>
    [Serializable]
    public class LayoutLandmarkSpec
    {
        public string name = "";
        /// <summary>building / fortress / church / gate / hall / steps / stall / prop / decoration</summary>
        public string kind = "building";
        /// <summary>绑定目标：分区名、街道名，或空（= 自动选最合适的临街位）。</summary>
        public string on = "";
        /// <summary>north / south / east / west / center / 空（= 自动）。朝向 = 立面法线。</summary>
        public string facing = "";
        public float width;
        public float depth;
        public float height;
        /// <summary>剧情给的粗坐标（可空）；求解器优先尊重它，被占用时才微调。</summary>
        public bool pinned;
        public Vector2 pin;
        public string hint = "";
        public string semantic = "";
        /// <summary>锚点载体：绑定到该地标的锚点 displayName 列表（逗号分隔）。</summary>
        public string anchorBinding = "";
    }

    /// <summary>剧情布局的完整结构化规格。</summary>
    [Serializable]
    public class LayoutSpec
    {
        public string version = "1";
        public string sceneId = "";
        public string source = "layoutDescription";
        public List<LayoutItemSpec> items = new List<LayoutItemSpec>();
        public List<LayoutPathSpec> paths = new List<LayoutPathSpec>();
        /// <summary>地面分区（石板广场/夯土/小巷…），灰盒 regions 的唯一来源。</summary>
        public List<LayoutZoneSpec> zones = new List<LayoutZoneSpec>();
        /// <summary>街道语义（SceneGen 求解器的临街立面来源）；为空时从 paths(kind=road) 推导。</summary>
        public List<LayoutStreetSpec> streets = new List<LayoutStreetSpec>();
        /// <summary>地标语义（必须存在的建筑/城门/要塞）；为空时从 items 推导。</summary>
        public List<LayoutLandmarkSpec> landmarks = new List<LayoutLandmarkSpec>();
        /// <summary>NPC 动线，灰盒 npcPaths 的唯一来源。</summary>
        public List<LayoutNpcSpec> npcs = new List<LayoutNpcSpec>();
        /// <summary>散文里写了、但没有任何几何可以落图的条目（诊断用，不允许静默）。</summary>
        public List<string> gaps = new List<string>();
        /// <summary>覆盖审计一行摘要（写日志/调试记录用）。</summary>
        public string diagnostics = "";
        /// <summary>SceneGen 求解器的一行 KPI 摘要（密度/围合/连续度/冲突）。</summary>
        public string metrics = "";
        /// <summary>排屋/街屋的默认开间宽度（米）。</summary>
        public float unitWidth = 8.5f;
        /// <summary>临街建筑默认进深（米）。</summary>
        public float unitDepth = 10f;
        /// <summary>相邻单元之间的目标缝隙（米）；0.15 表示几乎共墙。</summary>
        public float rowGap = 0.15f;
        /// <summary>临街立面到路缘的退让（米）。</summary>
        public float setback = 1.2f;
        public float positionTolerance = 1.5f;
        public float sizeTolerance = 0.75f;
        public float rowGapTolerance = 1.5f;
        public float streetClearance = 0.35f;
        public bool preserveCounts = true;

        /// <summary>有任何一条可施工内容即可用：只有分区的规格（纯场地）也算施工图。</summary>
        public bool Usable
        {
            get
            {
                return (items != null && items.Count > 0)
                       || (zones != null && zones.Count > 0)
                       || (paths != null && paths.Count > 0)
                       || (streets != null && streets.Count > 0)
                       || (landmarks != null && landmarks.Count > 0);
            }
        }
    }

    /// <summary>剧情&核心 Agent 生成的单个场景。文字描述必须「可直接复现」。 </summary>
    [Serializable]
    public class SceneSpec
    {
        public string sceneId = "";
        public string sceneName = "";
        public string era = "";
        public string region = "";
        /// <summary>精确布局文字（东西放在哪）。</summary>
        public string layoutDescription = "";
        /// <summary>剧情流程文字（东西们怎么工作）。</summary>
        public string flowDescription = "";
        /// <summary>给灰盒/搭建的要求。</summary>
        public string buildRequirement = "";
        public float width = 60f;
        public float length = 60f;
        /// <summary>剧情为该场景指定的起始年份；公元前使用负数，0 表示年代未知。</summary>
        public int startYear;
        public Vector3 spawnPoint = new Vector3(0f, 1.2f, 0f);
        /// <summary>该场景是否来自既有场景复用（设计文档第五节）。</summary>
        public bool isReused;
        public List<SceneAnchorSpec> anchors = new List<SceneAnchorSpec>();
        public List<NpcSpec> npcs = new List<NpcSpec>();
        public List<PropInteraction> props = new List<PropInteraction>();
        public List<FlowStep> flow = new List<FlowStep>();
        /// <summary>可选的结构化布局规格；为空时由 StoryLayoutSpecBuilder 从 layoutDescription 构建。</summary>
        public LayoutSpec layoutSpec;
    }

    /// <summary>一届剧情回合：剧情&核心 Agent 每次推理的输出。</summary>
    [Serializable]
    public class StoryTurn
    {
        public string turnId = "";
        /// <summary>给玩家看的叙述文字。</summary>
        public string narrative = "";
        public List<SceneSpec> scenes = new List<SceneSpec>();
        public int activeSceneIndex;
        public bool gameOver;
        public string endingSummary = "";
        public string endingAchievement = "";
        /// <summary>RemoteAccepted / RemoteRepaired / LocalFallback；便于确认实际采用的内容来源。</summary>
        public string contentSource = "";
        /// <summary>本地兜底的具体原因；空表示没有发生远端失败。</summary>
        public string fallbackReason = "";
    }

    /// <summary>玩家走入锚点后唤醒剧情&核心 Agent 的输入。</summary>
    [Serializable]
    public class StoryContinueRequest
    {
        public string currentSceneId = "";
        public string currentSceneName = "";
        public string anchorId = "";
        public string anchorName = "";
        public string anchorDescription = "";
        public string anchorRequirement = "";
        public string anchorTimeDelta = "";
        public long gameMinutes;
        public List<PlayerActionRecord> recentActions = new List<PlayerActionRecord>();
    }

    // ─────────────────────────── 灰盒 Agent 产物 ───────────────────────────

    /// <summary>功能区域：灰盒阶段用灰色线条框出边界。</summary>
    [Serializable]
    public class GrayboxRegion
    {
        public string regionId = "";
        /// <summary>residential / square / church / market / water / field / harbor 等。</summary>
        public string kind = "residential";
        public string description = "";
        /// <summary>风格描述，供搭建 Agent 选型，例如「欧洲古典居民区，居民楼沿路而建」。</summary>
        public string style = "";
        public List<Vector2> polygon = new List<Vector2>();
        public float groundHeight;
        public float areaWeight = 1f;
    }

    /// <summary>条状物：道路、墙、栅栏、战壕。</summary>
    [Serializable]
    public class GrayboxPath
    {
        public string pathId = "";
        /// <summary>road / wall / fence / trench。</summary>
        public string kind = "road";
        public List<Vector2> nodes = new List<Vector2>();
        public float width = 3f;
    }

    /// <summary>语义槽：灰盒 Agent 对场景某一部分的详细说明，等待搭建 Agent 绑定美术资产。</summary>
    [Serializable]
    public class GrayboxSlot
    {
        public string slotId = "";
        public string regionId = "";
        /// <summary>语义描述，直接作为 Embedding 检索的查询文本。</summary>
        public string semantic = "";
        public Vector2 position;
        public float rotation;
        public float sizeX = 4f;
        public float sizeY = 4f;
        /// <summary>剧情给出的高度（米）；0 = 剧情没写，构建层按类别/资产比例推定。</summary>
        public float height;
        public SlotKind kind = SlotKind.Prop;
        /// <summary>绑定 NpcSpec.npcId / SceneAnchorSpec.anchorId / PropInteraction.propId。</summary>
        public string bindId = "";
        public string expectedModelHint = "";
        /// <summary>流式加载分段号：按到出生点距离每 60 米一段，0 为最近一段。</summary>
        public int segment;
        /// <summary>对应的 LayoutItemSpec.id；为空表示这是灰盒自行添加的槽位。</summary>
        public string specItemId = "";
        public string rowId = "";
        public int rowIndex;
        public int rowCount = 1;
        /// <summary>true = 剧情规格要求必须存在的槽位。</summary>
        public bool specRequired;
        /// <summary>立面法线（世界方向）；由 SceneGen 推导，装配层据此摆正朝向。</summary>
        public Vector2 faceDir;
        /// <summary>素模形态：box/cylinder/cone/wedge/steps/stall/lamp/tree/crowd/... 空 = 按 kind 推定。</summary>
        public string primitive = "";
        /// <summary>true = SceneGen 求解器生成的填充物（不是剧情点名）。</summary>
        public bool solverGenerated;
    }

    /// <summary>NPC 动线。</summary>
    [Serializable]
    public class NpcPath
    {
        public string npcId = "";
        public List<Vector2> waypoints = new List<Vector2>();
        public bool loop = true;
        public float speed = 1.2f;
    }

    /// <summary>灰盒规划结果：整个场景的空间骨架。</summary>
    [Serializable]
    public class GrayboxPlan
    {
        public string sceneId = "";
        public float width = 60f;
        public float length = 60f;
        public int seed;
        public Vector2 spawnPoint;
        public List<GrayboxRegion> regions = new List<GrayboxRegion>();
        public List<GrayboxPath> paths = new List<GrayboxPath>();
        public List<GrayboxSlot> slots = new List<GrayboxSlot>();
        public List<NpcPath> npcPaths = new List<NpcPath>();
        public string plannerNote = "";
        /// <summary>分段总数（结算/缓存前需要等所有段拼完）。</summary>
        public int segmentCount = 1;
        /// <summary>结构化布局规格；specDriven 时灰盒必须按它执行。</summary>
        public LayoutSpec layoutSpec;
        /// <summary>true = 规划由 LayoutSpec 驱动，ComplianceValidator 会检查。</summary>
        public bool specDriven;
        /// <summary>远端是否显式调用 finish_graybox。</summary>
        public bool finished;
        /// <summary>剧情布局坐标原点与场景坐标之间的平移（由 StoryLayoutPlanBuilder 给出）。</summary>
        public Vector2 layoutShift;
        /// <summary>远端相对规格的偏离记录；最终仍以规格为准。</summary>
        public List<string> deviations = new List<string>();
        /// <summary>合规校验摘要（通过/失败原因）。</summary>
        public string specCompliance = "";
        /// <summary>SceneGen 求解器 KPI 摘要（密度/围合/连续度/冲突）。</summary>
        public string sceneGenReport = "";
    }

    [Serializable]
    public class GrayboxRequest
    {
        public SceneSpec scene;
        public string knowledgeContext = "";
        public int seed;
    }

    // ─────────────────────────── 搭建 Agent 产物 ───────────────────────────

    /// <summary>缓存模型库中的一条美术资产。vector 为 Qwen3-Embedding-8B 归一化向量。</summary>
    [Serializable]
    public class ModelEntry
    {
        public string id = "";
        public string name = "";
        public string prompt = "";
        public string category = "";
        /// <summary>bottom / center。决定摆放时模型原点对齐方式。</summary>
        public string pivot = "bottom";
        public string glb = "";
        public float[] vector;
    }

    [Serializable]
    public class ModelLibraryMeta
    {
        public int dim;
        public string embed_model = "";
        public int count;
        public bool normalized;
        public string updated_at = "";
    }

    [Serializable]
    public class ModelLibrary
    {
        public ModelLibraryMeta meta;
        public List<ModelEntry> entries = new List<ModelEntry>();
    }

    /// <summary>一个语义槽的最终落位结果。</summary>
    [Serializable]
    public class PlacedAsset
    {
        public string slotId = "";
        public string modelId = "";
        public string instanceName = "";
        public Vector3 position;
        public float rotationY;
        public float sizeX = 4f;
        public float sizeY = 4f;
        /// <summary>剧情给出的高度（米）；0 = 剧情没写，装配时按资产比例等比缩放。</summary>
        public float height;
        /// <summary>true = 由资产生成 Agent 现生成，不再做容忍度判断。</summary>
        public bool generated;
        public float matchScore;
        public SlotKind kind = SlotKind.Prop;
        public string bindId = "";
        /// <summary>素模形态（box/cylinder/cone/...）；非空 = 本资产用程序化几何体构建，不查美术库。</summary>
        public string primitive = "";
        /// <summary>立面法线（世界方向）；素模用它摆正朝向。</summary>
        public Vector2 faceDir;
    }

    [Serializable]
    public class BuildResult
    {
        public string sceneId = "";
        public List<PlacedAsset> placed = new List<PlacedAsset>();
        public List<string> warnings = new List<string>();
        public int generationCount;
        /// <summary>true = 本次搭建全部由程序化几何体完成（美术资产库不参与）。</summary>
        public bool primitiveMode;
    }

    [Serializable]
    public class BuildRequest
    {
        public SceneSpec scene;
        public GrayboxPlan plan;
        /// <summary>滑块值 0..1。1 = 容忍度为 0（最严格，倾向生成新资产）。</summary>
        public float sliderValue = 0.5f;
    }

    // ─────────────────────────── 运行时记录 ───────────────────────────

    /// <summary>玩家（或 NPC）的一次操作及其全部影响，逐条记录并打印。</summary>
    [Serializable]
    public class PlayerActionRecord
    {
        public string sceneId = "";
        public string actorId = "player";
        /// <summary>fire / talk / enter_anchor / ignite / collapse / take / damage / mount ...</summary>
        public string actionType = "";
        public string targetId = "";
        public string detail = "";
        public long gameMinutes;
        public Vector3 position;
        public string timestampUtc = "";
    }

    /// <summary>存档：同时承载「场景复用」所需的已访问场景列表。</summary>
    [Serializable]
    public class GameSaveData
    {
        public string version = "2";
        public string userEvent = "";
        public string currentSceneId = "";
        public long gameMinutes;
        /// <summary>当前剧情的起始年；0 = 未知，负数 = 公元前。</summary>
        public int baseYear;
        public float playerHp = 100f;
        public ToolKind heldTool = ToolKind.None;
        public List<string> visitedSceneIds = new List<string>();
        /// <summary>已生成过的场景定义，用于复用时不重跑灰盒/搭建。</summary>
        public List<SceneSpec> knownScenes = new List<SceneSpec>();
        public List<PlayerActionRecord> actionLog = new List<PlayerActionRecord>();
    }
}
