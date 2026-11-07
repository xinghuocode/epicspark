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
        public Vector3 spawnPoint = new Vector3(0f, 1.2f, 0f);
        /// <summary>该场景是否来自既有场景复用（设计文档第五节）。</summary>
        public bool isReused;
        public List<SceneAnchorSpec> anchors = new List<SceneAnchorSpec>();
        public List<NpcSpec> npcs = new List<NpcSpec>();
        public List<PropInteraction> props = new List<PropInteraction>();
        public List<FlowStep> flow = new List<FlowStep>();
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
        public SlotKind kind = SlotKind.Prop;
        /// <summary>绑定 NpcSpec.npcId / SceneAnchorSpec.anchorId / PropInteraction.propId。</summary>
        public string bindId = "";
        public string expectedModelHint = "";
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
        /// <summary>true = 由资产生成 Agent 现生成，不再做容忍度判断。</summary>
        public bool generated;
        public float matchScore;
        public SlotKind kind = SlotKind.Prop;
        public string bindId = "";
    }

    [Serializable]
    public class BuildResult
    {
        public string sceneId = "";
        public List<PlacedAsset> placed = new List<PlacedAsset>();
        public List<string> warnings = new List<string>();
        public int generationCount;
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
        public string version = "1";
        public string userEvent = "";
        public string currentSceneId = "";
        public long gameMinutes;
        public float playerHp = 100f;
        public ToolKind heldTool = ToolKind.None;
        public List<string> visitedSceneIds = new List<string>();
        /// <summary>已生成过的场景定义，用于复用时不重跑灰盒/搭建。</summary>
        public List<SceneSpec> knownScenes = new List<SceneSpec>();
        public List<PlayerActionRecord> actionLog = new List<PlayerActionRecord>();
    }
}
