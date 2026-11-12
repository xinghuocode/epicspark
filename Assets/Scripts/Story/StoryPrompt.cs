// 剧情&核心 Agent 的提示词。
// 设计文档核心要求：场景描述必须精确到位置，流程必须精确到每一步如何推进。
using System;
using System.Collections.Generic;
using System.Text;
using EpicSpark.Core;

namespace EpicSpark.Story
{
    /// <summary>提示词与工具定义。纯静态，无状态。</summary>
    public static class StoryPrompt
    {
        public const string ToolName = "emit_story_turn";

        // ─────────────────────────── 工具定义 ───────────────────────────

        /// <summary>OpenAI function 工具定义：让模型把整回合 JSON 当作参数返回。</summary>
        public static LlmToolDef BuildToolDef()
        {
            return new LlmToolDef
            {
                name = ToolName,
                description = "提交一回合剧情：一段叙述 + 1~3 个可直接复现的场景（精确坐标布局与流程）。必须一次给出完整结构。",
                parametersJson = ToolParametersJson(),
            };
        }

        /// <summary>工具的 JSON Schema（object 根）。</summary>
        public static string ToolParametersJson()
        {
            const string layoutSpecSchema =
                "\"layoutSpec\":{\"type\":\"object\",\"description\":\"语义布局：只说分区/街道/地标是什么、在哪一带、朝哪；开间数/尺寸/坐标由确定性求解器生成\",\"properties\":{"
                + "\"zones\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{"
                + "\"name\":{\"type\":\"string\"},"
                + "\"kind\":{\"type\":\"string\",\"enum\":[\"square\",\"field\",\"water\",\"church\",\"market\",\"residential\",\"harbor\"]},"
                + "\"x1\":{\"type\":\"number\"},\"z1\":{\"type\":\"number\"},\"x2\":{\"type\":\"number\"},\"z2\":{\"type\":\"number\"},"
                + "\"openness\":{\"type\":\"number\",\"description\":\"开敞程度 0~1：广场/荒地 0.85~1，密排街区 0.1~0.3；决定这里该不该建房\"},"
                + "\"material\":{\"type\":\"string\",\"description\":\"地面材质，例如 石板/夯土/草地/水面\"}"
                + "},\"required\":[\"name\",\"kind\",\"x1\",\"z1\",\"x2\",\"z2\"]}},"
                + "\"streets\":{\"type\":\"array\",\"description\":\"街道：只给折线走向与街宽。两侧连续街屋由求解器按开间宽度自动生成，不要自己数房子\",\"items\":{\"type\":\"object\",\"properties\":{"
                + "\"name\":{\"type\":\"string\"},"
                + "\"nodes\":{\"type\":\"string\",\"description\":\"街道中心线，格式 x,z;x,z（尽量轴对齐）\"},"
                + "\"width\":{\"type\":\"number\",\"description\":\"街面净宽（米），6~14\"},"
                + "\"frontageBoth\":{\"type\":\"boolean\",\"description\":\"是否两侧都要连续临街立面\"},"
                + "\"style\":{\"type\":\"string\",\"enum\":[\"classical_row\",\"shop_row\",\"arcade\",\"dirt_path\",\"trodden_path\",\"none\"]},"
                + "\"depth\":{\"type\":\"number\",\"description\":\"单侧建筑进深（米），8~14，不填由系统给 10\"},"
                + "\"floors\":{\"type\":\"number\",\"description\":\"层数，2~5\"}"
                + "},\"required\":[\"name\",\"nodes\",\"width\"]}},"
                + "\"landmarks\":{\"type\":\"array\",\"description\":\"语义地标：独一无二、有叙事含义的东西（要塞/教堂/城门/官署/台阶/摊位）。不要给坐标和尺寸，只说在哪个分区、哪一面、朝哪\",\"items\":{\"type\":\"object\",\"properties\":{"
                + "\"name\":{\"type\":\"string\"},"
                + "\"kind\":{\"type\":\"string\",\"enum\":[\"fortress\",\"church\",\"gate\",\"hall\",\"steps\",\"stall\",\"decoration\"]},"
                + "\"on\":{\"type\":\"string\",\"description\":\"贴哪个分区的哪条边，例如「中央广场 北侧」「市场区 东侧」\"},"
                + "\"facing\":{\"type\":\"string\",\"enum\":[\"north\",\"south\",\"east\",\"west\"],\"description\":\"正立面朝向\"},"
                + "\"hint\":{\"type\":\"string\",\"description\":\"美术检索英文关键词\"},"
                + "\"semantic\":{\"type\":\"string\",\"description\":\"一句话说明它是什么、剧情上有什么用\"},"
                + "\"anchorFor\":{\"type\":\"string\",\"description\":\"若某个传送锚点的载体是它，填该锚点的 displayName\"}"
                + "},\"required\":[\"name\",\"kind\",\"on\",\"facing\"]}},"
                + "\"items\":{\"type\":\"array\",\"description\":\"可选：仅用于剧情明确要求「必须在原地」的唯一道具（例如某口井）。普通建筑、成排街屋一律不要写\",\"items\":{\"type\":\"object\",\"properties\":{"
                + "\"name\":{\"type\":\"string\"},"
                + "\"kind\":{\"type\":\"string\",\"enum\":[\"building\",\"prop\",\"decoration\"]},"
                + "\"x\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"},"
                + "\"width\":{\"type\":\"number\"},\"depth\":{\"type\":\"number\"},\"height\":{\"type\":\"number\"},"
                + "\"rotation\":{\"type\":\"number\"},"
                + "\"hint\":{\"type\":\"string\",\"description\":\"美术检索英文关键词\"}"
                + "},\"required\":[\"name\",\"x\",\"z\",\"width\",\"depth\"]}},"
                + "\"npcs\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{"
                + "\"name\":{\"type\":\"string\"},\"x\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"},"
                + "\"waypoints\":{\"type\":\"string\",\"description\":\"沿街道的 2~4 个大致走位点，格式 x,z;x,z;x,z\"}"
                + "},\"required\":[\"name\"]}}"
                + "}},";

            const string sceneProperties =
                "\"sceneName\":{\"type\":\"string\",\"description\":\"给玩家看的地名，例如 巴黎·市政广场\"},"
                + "\"era\":{\"type\":\"string\"},"
                + "\"region\":{\"type\":\"string\"},"
                + "\"layoutDescription\":{\"type\":\"string\",\"description\":\"给灰盒 Agent 的详细场景需求：每个区域的功能、绿化/路灯/人群等内容、道路连接、地标与入口的方位关系；不要逐栋写坐标\"},"
                + "\"flowDescription\":{\"type\":\"string\",\"description\":\"精确到每一步的流程：谁做什么、触发条件、结果\"},"
                + "\"buildRequirement\":{\"type\":\"string\"},"
                + "\"width\":{\"type\":\"number\",\"description\":\"剧情自定义的场景世界 X 宽度（米），与 length 独立，不套固定尺寸模板\"},"
                + "\"length\":{\"type\":\"number\",\"description\":\"剧情自定义的场景世界 Z 纵深（米），与 width 独立\"},"
                + "\"startYear\":{\"type\":\"integer\",\"description\":\"该幕剧情的起始年份：公元后为正数，公元前为负数，例如 1258 或 -2600；确实无法判断时才填 0\"},"
                + "\"anchors\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{"
                + "\"displayName\":{\"type\":\"string\"},"
                + "\"description\":{\"type\":\"string\"},"
                + "\"requirement\":{\"type\":\"string\",\"description\":\"载体要求，例如 必须是马车 / 必须是城门\"},"
                + "\"modelHint\":{\"type\":\"string\",\"description\":\"检索美术资产的英文关键词\"},"
                + "\"timeDelta\":{\"type\":\"string\",\"description\":\"时间跳转量，单位 y/d/h/min，例如 2h\"},"
                + "\"targetSceneId\":{\"type\":\"string\"},"
                + "\"targetSceneName\":{\"type\":\"string\"},"
                + "\"triggerKind\":{\"type\":\"string\",\"enum\":[\"Placed\",\"Event\"]},"
                + "\"triggerEvent\":{\"type\":\"string\"},"
                + "\"x\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"}"
                + "},\"required\":[\"displayName\",\"requirement\",\"timeDelta\"]}},"
                + "\"npcs\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{"
                + "\"displayName\":{\"type\":\"string\"},"
                + "\"tag\":{\"type\":\"string\",\"description\":\"身份标签，例如 信使/士兵/商人/平民/神职/贵族/工匠/告密者\"},"
                + "\"persona\":{\"type\":\"string\"},"
                + "\"storyRole\":{\"type\":\"string\"},"
                + "\"heldTool\":{\"type\":\"string\",\"enum\":[\"None\",\"Gun\",\"Satchel\"]},"
                + "\"x\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"}"
                + "},\"required\":[\"displayName\",\"tag\",\"persona\"]}},"
                + "\"props\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{"
                + "\"displayName\":{\"type\":\"string\"},"
                + "\"description\":{\"type\":\"string\"},"
                + "\"interactKind\":{\"type\":\"string\",\"enum\":[\"ignite\",\"board\",\"collapse\",\"take\",\"examine\"]},"
                + "\"effect\":{\"type\":\"string\"},"
                + "\"x\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"}"
                + "},\"required\":[\"displayName\",\"interactKind\"]}},"
                + layoutSpecSchema
                + "\"flow\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{"
                + "\"actorId\":{\"type\":\"string\"},"
                + "\"regionId\":{\"type\":\"string\",\"description\":\"square/church/market/field/harbor/residential\"},"
                + "\"description\":{\"type\":\"string\",\"description\":\"含坐标的具体动作\"},"
                + "\"trigger\":{\"type\":\"string\"},"
                + "\"effect\":{\"type\":\"string\"},"
                + "\"delaySeconds\":{\"type\":\"number\"}"
                + "},\"required\":[\"description\"]}}";

            return "{"
                + "\"type\":\"object\","
                + "\"properties\":{"
                + "\"narrative\":{\"type\":\"string\",\"description\":\"给玩家看的一幕叙述，120~260 字\"},"
                + "\"gameOver\":{\"type\":\"boolean\"},"
                + "\"endingSummary\":{\"type\":\"string\"},"
                + "\"endingAchievement\":{\"type\":\"string\"},"
                + "\"scenes\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{" + sceneProperties
                + "},\"required\":[\"sceneName\",\"layoutDescription\",\"flowDescription\",\"anchors\"]}}"
                + "},"
                + "\"required\":[\"narrative\",\"scenes\"]"
                + "}";
        }

        // ─────────────────────────── System Prompt ───────────────────────────

        public static string SystemPrompt()
        {
            var sb = new StringBuilder(2600);
            sb.Append("你是 EpicSpark 的「剧情&核心 Agent」。你负责把用户给出的历史事件，推演成一段玩家可以亲身走进去的历史剧情。\n");
            sb.Append("你只输出一个 JSON 对象，不要输出任何解释、前后缀、Markdown 代码围栏。\n\n");

            sb.Append("【世界观与坐标约定】\n");
            sb.Append("1. 场地大小完全由你按剧情需要决定：width 是世界 X 轴宽度，length 是世界 Z 轴纵深，单位米，两者可不同，不设固定模板或推荐上限。只有 <=1、NaN、Infinity 等无效值会被替换。X 轴向东、Z 轴向北，原点 (0,0) 在场地中心；所有坐标必须落在 ±width/2 与 ±length/2 内。小型室内/庭院可以只有几十米，大型战场/城区可以达到数百米或更大；不要为了填满场地而虚构无关内容。\n");
            sb.Append("2. 玩家出生点固定为 (0, 1.2, -24)，面朝正北（+Z）。\n");
            sb.Append("3. 方位用 north/south/east/west 或「东侧/北端」表达即可；只有剧情明确要求「必须在原地」的唯一道具才写 (x, z) 坐标。\n");
            sb.Append("4. 每个场景都要给 startYear：公元后为正、公元前为负，例如巴格达陷落为 1258、涿鹿传说可写 -2600；不需要等待知识库存在一个规范事件名。确实无法判断时才填 0。\n\n");

            sb.Append("【硬性质量要求】\n");
            sb.Append("1. layoutDescription 必须说清空间关系：地面材质、有几条街、街通向哪、哪个分区靠哪个地标、边界与出口在哪。不必逐栋写坐标（写了也会被求解器重排）。\n");
            sb.Append("2. flowDescription 必须精确到每一步如何推进：玩家从哪出发、走到哪个分区触发什么、NPC 沿哪条街做什么、延时多少秒、结果是什么。\n");
            sb.Append("3. 每个场景必须给出：3~5 个传送锚点（anchors）、2~4 个 NPC（npcs）、2~4 个可互动道具（props）、4~6 个流程步骤（flow）。\n");
            sb.Append("4. 每个锚点必须有 requirement（载体要求，如「必须是马车」「必须是城门」）和 timeDelta（y/d/h/min 单位，如 2h）。\n");
            sb.Append("5. 锚点必须与当前场景地理相关，并指向与后续剧情推进相关的下一场景：城镇广场只能通向教堂/集市/城门/官署，乡村通向田地/城镇，港口通向集市/街道；绝对不允许从广场直接跳到海上孤岛、从内陆跳到远洋。\n");
            sb.Append("6. 整条剧情弧共五幕：index 0 开端、1 冲突、2 转折、3 高潮、4 结局。到第 5 幕（index 4）时 gameOver 必须为 true，并给出 endingSummary 与 endingAchievement。\n");
            sb.Append("7. 玩家行为日志（recentActions）必须被真正使用：据此推断玩家意图与旁人反应，写进叙述与流程，不要忽略。\n");
            sb.Append("8. 严格尊重史料：不得把不同时代、不同地域的器物混在一起；不确定的细节用当时最常见的形制描写。\n\n");

            sb.Append("【给灰盒 Agent 的场景需求】\n");
            sb.Append("1. layoutDescription 是唯一必需的空间描述：逐区说清功能、开敞程度、绿化/路灯/人群/摊位等内容，以及道路、地标和入口的相对方位。灰盒 Agent 会理解它并调用工具，不会把散文当成最终施工图。\n");
            sb.Append("2. layoutSpec 只是可选的结构化提示，用来消除歧义；它不是必须逐项照摆的建筑清单。不要自己拆排屋，不要为了让 JSON 好看而编造密集坐标。\n");
            sb.Append("3. 若提供 zones/streets/landmarks：分区写功能和边界，街道写中心线与是否连续临街，地标写所在分区和立面方向。具体地块、开间、碰撞与入口位置由灰盒工具计算；轴对齐与斜向道路均可。\n");
            sb.Append("   纯踩踏路/土径使用 style=trodden_path 或 dirt_path，不生成街屋；只有 classical_row/shop_row/arcade 才要求连续建筑。\n");
            sb.Append("4. 广场要说明哪里必须开敞、哪里能绿化、人群在哪聚集；居民区要说明沿哪条街连续排屋；要塞要说明城墙/塔楼/大门/庭院关系。\n");
            sb.Append("5. 锚点写清载体与入口关系。剧情明确给出的唯一道具/锚点坐标会被尊重，只有埋入建筑或越界时灰盒工具才允许修正。\n");
            sb.Append("6. NPC 的走位应沿道路或开敞区，不要穿过建筑。\n\n");

            sb.Append("【输出 JSON 结构】\n");
            sb.Append("{\"narrative\":\"...\",\"gameOver\":false,\"endingSummary\":\"\",\"endingAchievement\":\"\",");
            sb.Append("\"scenes\":[{\"sceneName\":\"...\",\"era\":\"...\",\"region\":\"...\",");
            sb.Append("\"layoutDescription\":\"...\",\"flowDescription\":\"...\",\"buildRequirement\":\"...\",\"width\":140,\"length\":220,\"startYear\":1789,");
            sb.Append("\"anchors\":[{\"displayName\":\"...\",\"description\":\"...\",\"requirement\":\"...\",\"modelHint\":\"...\",");
            sb.Append("\"timeDelta\":\"2h\",\"targetSceneId\":\"s_xxx_1_a\",\"targetSceneName\":\"...\",\"triggerKind\":\"Placed\",\"triggerEvent\":\"\",\"x\":0,\"z\":96}],");
            sb.Append("\"npcs\":[{\"displayName\":\"...\",\"tag\":\"信使\",\"persona\":\"...\",\"storyRole\":\"...\",\"heldTool\":\"None\",\"x\":13,\"z\":5}],");
            sb.Append("\"props\":[{\"displayName\":\"...\",\"description\":\"...\",\"interactKind\":\"ignite\",\"effect\":\"...\",\"x\":-8,\"z\":-6}],");
            sb.Append("\"flow\":[{\"actorId\":\"player\",\"regionId\":\"square\",\"description\":\"...\",\"trigger\":\"...\",\"effect\":\"...\",\"delaySeconds\":0}],");
            sb.Append("\"layoutSpec\":{");
            sb.Append("\"zones\":[{\"name\":\"中央广场\",\"kind\":\"square\",\"x1\":-40,\"z1\":-40,\"x2\":40,\"z2\":40,\"openness\":0.9,\"material\":\"石板\"},");
            sb.Append("{\"name\":\"北街住宅区\",\"kind\":\"residential\",\"x1\":-40,\"z1\":40,\"x2\":40,\"z2\":90,\"openness\":0.2,\"material\":\"夯土\"}],");
            sb.Append("\"streets\":[{\"name\":\"主街\",\"nodes\":\"0,-90;0,90\",\"width\":9,\"frontageBoth\":true,\"style\":\"shop_row\",\"depth\":11,\"floors\":3}],");
            sb.Append("\"landmarks\":[{\"name\":\"城门\",\"kind\":\"gate\",\"on\":\"中央广场 南侧\",\"facing\":\"south\",\"hint\":\"stone city gate\",\"semantic\":\"玩家进城的入口，剧情起点\"},");
            sb.Append("{\"name\":\"教堂\",\"kind\":\"church\",\"on\":\"中央广场 北侧\",\"facing\":\"north\",\"hint\":\"gothic church\",\"semantic\":\"钟声报时，第三幕集合点\",\"anchorFor\":\"去教堂的马车\"}],");
            sb.Append("\"npcs\":[{\"name\":\"信使\",\"waypoints\":\"13,-5;20,10;13,15\"}]");
            sb.Append("}}]}\n\n");

            sb.Append("【ID 规则】\n");
            sb.Append("sceneId / anchorId / npcId / propId / stepId 一律留空字符串，由系统按事件哈希与幕序号生成稳定 id（保证同一事件、同一幕可复用同一场景）；你只需要给 displayName。\n");
            sb.Append("targetSceneId 可以填写你预测的下一场景 id（格式 s_<事件哈希>_<幕序号>_<模板码>），不确定就留空。\n\n");

            sb.Append("【语言】全部字段使用简体中文（modelHint 用英文关键词）。数值字段不要加单位后缀。\n");
            return sb.ToString();
        }

        // ─────────────────────────── 开局提示词 ───────────────────────────

        public static string BeginUserPrompt(string userEvent, string knowledgeContext)
        {
            var sb = new StringBuilder(1600);
            sb.Append("【用户选择的历史事件】\n").Append(string.IsNullOrEmpty(userEvent) ? "（未填写，请从史料上下文中判断）" : userEvent).Append("\n\n");

            sb.Append("【知识库检索到的史料上下文】\n");
            string context = string.IsNullOrEmpty(knowledgeContext)
                ? "（知识库没有返回上下文，请依据该事件最公认的史实自行推演）"
                : knowledgeContext;
            if (context.Length > 3200) context = context.Substring(0, 3200) + "…";
            sb.Append(context).Append("\n\n");

            sb.Append("【本次任务】生成整条剧情的第 1 幕（index 0，开端）。\n");
            sb.Append("1. 选择一个最能承载事件起点的场景模板：城镇广场 / 乡村 / 港口 / 宫廷官署 / 战场 / 教堂街区 / 集市。\n");
            sb.Append("2. 场景必须能让玩家一眼看出「这是哪个时代、哪个地点、我该往哪走」。\n");
            sb.Append("3. 锚点至少包含一条主线去向：马车 / 城门 / 船 / 教堂门 / 官署台阶，并在 description 里写清「由哪个坐标出发、多久后抵达什么场景」。\n");
            sb.Append("4. narrative 用第二人称写给玩家，交代事件、地点、当前局势与下一步方向。\n");
            sb.Append("5. gameOver=false，endingSummary/endingAchievement 留空。\n");
            sb.Append("6. 必须写详细的 layoutDescription；layoutSpec 仅作可选语义提示。不要输出逐栋施工图，灰盒 Agent 会通过工具完成空间组织与校验。\n\n");
            sb.Append("只输出 JSON 对象。");
            return sb.ToString();
        }

        // ─────────────────────────── 推进提示词 ───────────────────────────

        public static string ContinueUserPrompt(
            StoryContinueRequest request,
            string userEvent,
            string knowledgeContext,
            int nextIndex,
            string stageLabel,
            bool isEnding)
        {
            if (request == null) request = new StoryContinueRequest();
            var sb = new StringBuilder(2200);

            sb.Append("【历史事件】").Append(string.IsNullOrEmpty(userEvent) ? "（沿用上一幕的事件）" : userEvent).Append("\n\n");

            sb.Append("【玩家刚刚走出的场景】\n");
            sb.Append("场景 id：").Append(EmptyDash(request.currentSceneId)).Append('\n');
            sb.Append("场景名：").Append(EmptyDash(request.currentSceneName)).Append('\n');
            sb.Append("游戏内时间：").Append(GameClock.Display()).Append("（自开局累计 ")
              .Append(request.gameMinutes.ToString()).Append(" 分钟）\n\n");

            sb.Append("【玩家走进去的传送锚点】\n");
            sb.Append("锚点 id：").Append(EmptyDash(request.anchorId)).Append('\n');
            sb.Append("锚点名：").Append(EmptyDash(request.anchorName)).Append('\n');
            sb.Append("锚点描述：").Append(EmptyDash(request.anchorDescription)).Append('\n');
            sb.Append("载体要求：").Append(EmptyDash(request.anchorRequirement)).Append('\n');
            sb.Append("时间跳转量：").Append(EmptyDash(request.anchorTimeDelta)).Append("\n\n");

            sb.Append("【玩家在上一幕的行为日志（用于推断意图）】\n");
            sb.Append(FormatActions(request.recentActions)).Append('\n');

            if (!string.IsNullOrEmpty(knowledgeContext))
            {
                sb.Append("【史料上下文（节选）】\n");
                string context = knowledgeContext.Length > 1800 ? knowledgeContext.Substring(0, 1800) + "…" : knowledgeContext;
                sb.Append(context).Append("\n\n");
            }

            sb.Append("【本次任务】生成第 ").Append(nextIndex + 1).Append(" 幕（index ").Append(nextIndex)
              .Append("，").Append(stageLabel).Append("）。\n");
            sb.Append("1. 先推理历史发展：穿过这个锚点之后，局势自然演变成什么样；再推理玩家意图：行为日志说明玩家想做什么。\n");
            sb.Append("2. 新场景必须与「").Append(EmptyDash(request.anchorName))
              .Append("」这个载体在空间上合理衔接（它通向哪里，就是哪里），不能凭空换到无关地点。\n");
            sb.Append("3. 若时间跳转量较大（如 10d），要在叙述与布局里体现局势变化（布防、人群、封锁、天气、季节）。\n");
            sb.Append("4. 玩家在上一幕的行为要有后果：被记住、被追究、被利用，写进 narrative 与 flow。\n");
            sb.Append("5. 新场景同样给出 3~5 个锚点 / 2~4 个 NPC / 2~4 个道具 / 4~6 个流程步骤，锚点继续指向后续场景。\n");
            sb.Append("6. 必须写详细的 layoutDescription；layoutSpec 仅作可选语义提示。锚点/NPC/道具可给明确坐标，灰盒会尊重合法坐标并校验可达空间。\n");
            if (isEnding)
            {
                sb.Append("7. 这是整条剧情的最后一幕：gameOver=true，必须给出 endingSummary（一段总结）与 endingAchievement（一条成就名，形如【大革命·点火者】）。\n");
            }
            else
            {
                sb.Append("7. gameOver=false（本幕不是结局）。\n");
            }
            sb.Append("\n只输出 JSON 对象。");
            return sb.ToString();
        }

        private static string FormatActions(List<PlayerActionRecord> actions)
        {
            if (actions == null || actions.Count == 0) return "（玩家还没有任何行为记录）";
            var sb = new StringBuilder(600);
            int start = Math.Max(0, actions.Count - 12);
            int shown = 0;
            for (int i = start; i < actions.Count; i++)
            {
                PlayerActionRecord record = actions[i];
                if (record == null) continue;
                sb.Append("- [").Append(record.actionType).Append("] ");
                if (!string.IsNullOrEmpty(record.targetId)) sb.Append(record.targetId).Append(' ');
                if (!string.IsNullOrEmpty(record.detail)) sb.Append(record.detail);
                sb.Append("（第 ").Append(record.gameMinutes).Append(" 分钟，位置 (")
                  .Append(record.position.x.ToString("0.#")).Append(", ")
                  .Append(record.position.z.ToString("0.#")).Append("）\n");
                shown++;
            }
            return shown == 0 ? "（玩家还没有任何行为记录）" : sb.ToString();
        }

        private static string EmptyDash(string text)
        {
            return string.IsNullOrEmpty(text) ? "（未提供）" : text;
        }
    }
}
