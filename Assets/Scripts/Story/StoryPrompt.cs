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
            const string sceneProperties =
                "\"sceneName\":{\"type\":\"string\",\"description\":\"给玩家看的地名，例如 巴黎·市政广场\"},"
                + "\"era\":{\"type\":\"string\"},"
                + "\"region\":{\"type\":\"string\"},"
                + "\"layoutDescription\":{\"type\":\"string\",\"description\":\"精确到位置的布局：方位、道路、建筑、地标的确切坐标（米）\"},"
                + "\"flowDescription\":{\"type\":\"string\",\"description\":\"精确到每一步的流程：谁在哪个坐标做什么、触发条件、结果\"},"
                + "\"buildRequirement\":{\"type\":\"string\"},"
                + "\"width\":{\"type\":\"number\"},"
                + "\"length\":{\"type\":\"number\"},"
                + "\"anchors\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{"
                + "\"displayName\":{\"type\":\"string\"},"
                + "\"description\":{\"type\":\"string\"},"
                + "\"requirement\":{\"type\":\"string\",\"description\":\"载体要求，例如 必须是马车 / 必须是城门\"},"
                + "\"modelHint\":{\"type\":\"string\",\"description\":\"检索美术资产的英文关键词\"},"
                + "\"timeDelta\":{\"type\":\"string\",\"description\":\"时间跳转量，单位 y/d/h/min，例如 2h\"},"
                + "\"targetSceneId\":{\"type\":\"string\"},"
                + "\"targetSceneName\":{\"type\":\"string\"},"
                + "\"triggerKind\":{\"type\":\"string\",\"enum\":[\"Placed\",\"Event\"]},"
                + "\"triggerEvent\":{\"type\":\"string\"}"
                + "},\"required\":[\"displayName\",\"requirement\",\"timeDelta\"]}},"
                + "\"npcs\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{"
                + "\"displayName\":{\"type\":\"string\"},"
                + "\"tag\":{\"type\":\"string\",\"description\":\"身份标签，例如 信使/士兵/商人/平民/神职/贵族/工匠/告密者\"},"
                + "\"persona\":{\"type\":\"string\"},"
                + "\"storyRole\":{\"type\":\"string\"},"
                + "\"heldTool\":{\"type\":\"string\",\"enum\":[\"None\",\"Gun\",\"Satchel\"]}"
                + "},\"required\":[\"displayName\",\"tag\",\"persona\"]}},"
                + "\"props\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{"
                + "\"displayName\":{\"type\":\"string\"},"
                + "\"description\":{\"type\":\"string\"},"
                + "\"interactKind\":{\"type\":\"string\",\"enum\":[\"ignite\",\"board\",\"collapse\",\"take\",\"examine\"]},"
                + "\"effect\":{\"type\":\"string\"}"
                + "},\"required\":[\"displayName\",\"interactKind\"]}},"
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
            sb.Append("1. 场地大小由你决定：在 width/length 里填这个场景真正需要的边长（米，40…300），X 轴为东、Z 轴为北，原点 (0,0) 在场地中心；坐标必须落在 ±width/2 与 ±length/2 之内。小广场就写 60，带城墙和郊区的街区可以写 140。\n");
            sb.Append("2. 玩家出生点固定为 (0, 1.2, -24)，面朝正北（+Z）。\n");
            sb.Append("3. 所有位置必须写成 (x, z) 形式的具体坐标，不得只写「旁边」「附近」。\n\n");

            sb.Append("【硬性质量要求】\n");
            sb.Append("1. layoutDescription 必须精确到位置：地面材质、每条道路的宽度与起止坐标、每栋建筑的占地范围与朝向、地标的确切坐标与尺寸、边界与出口坐标、锚点载体的坐标。地面分区要写出范围，用 (x1,z1)—(x2,z2) 表示矩形。\n");
            sb.Append("2. flowDescription 必须精确到每一步如何推进：玩家从哪个坐标出发、走到哪个坐标触发什么、NPC 从哪个坐标到哪个坐标做什么、延时多少秒、结果是什么。\n");
            sb.Append("3. 每个场景必须给出：3~5 个传送锚点（anchors）、2~4 个 NPC（npcs）、2~4 个可互动道具（props）、4~6 个流程步骤（flow）。\n");
            sb.Append("4. 每个锚点必须有 requirement（载体要求，如「必须是马车」「必须是城门」）和 timeDelta（y/d/h/min 单位，如 2h）。\n");
            sb.Append("5. 锚点必须与当前场景地理相关，并指向与后续剧情推进相关的下一场景：城镇广场只能通向教堂/集市/城门/官署，乡村通向田地/城镇，港口通向集市/街道；绝对不允许从广场直接跳到海上孤岛、从内陆跳到远洋。\n");
            sb.Append("6. 整条剧情弧共五幕：index 0 开端、1 冲突、2 转折、3 高潮、4 结局。到第 5 幕（index 4）时 gameOver 必须为 true，并给出 endingSummary 与 endingAchievement。\n");
            sb.Append("7. 玩家行为日志（recentActions）必须被真正使用：据此推断玩家意图与旁人反应，写进叙述与流程，不要忽略。\n");
            sb.Append("8. 严格尊重史料：不得把不同时代、不同地域的器物混在一起；不确定的细节用当时最常见的形制描写。\n\n");

            sb.Append("【输出 JSON 结构】\n");
            sb.Append("{\"narrative\":\"...\",\"gameOver\":false,\"endingSummary\":\"\",\"endingAchievement\":\"\",");
            sb.Append("\"scenes\":[{\"sceneName\":\"...\",\"era\":\"...\",\"region\":\"...\",");
            sb.Append("\"layoutDescription\":\"...\",\"flowDescription\":\"...\",\"buildRequirement\":\"...\",\"width\":80,\"length\":80,");
            sb.Append("\"anchors\":[{\"displayName\":\"...\",\"description\":\"...\",\"requirement\":\"...\",\"modelHint\":\"...\",");
            sb.Append("\"timeDelta\":\"2h\",\"targetSceneId\":\"s_xxx_1_a\",\"targetSceneName\":\"...\",\"triggerKind\":\"Placed\",\"triggerEvent\":\"\"}],");
            sb.Append("\"npcs\":[{\"displayName\":\"...\",\"tag\":\"信使\",\"persona\":\"...\",\"storyRole\":\"...\",\"heldTool\":\"None\"}],");
            sb.Append("\"props\":[{\"displayName\":\"...\",\"description\":\"...\",\"interactKind\":\"ignite\",\"effect\":\"...\"}],");
            sb.Append("\"flow\":[{\"actorId\":\"player\",\"regionId\":\"square\",\"description\":\"...\",\"trigger\":\"...\",\"effect\":\"...\",\"delaySeconds\":0}]}]}\n\n");

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
            sb.Append("5. gameOver=false，endingSummary/endingAchievement 留空。\n\n");
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
            if (isEnding)
            {
                sb.Append("6. 这是整条剧情的最后一幕：gameOver=true，必须给出 endingSummary（一段总结）与 endingAchievement（一条成就名，形如【大革命·点火者】）。\n");
            }
            else
            {
                sb.Append("6. gameOver=false（本幕不是结局）。\n");
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
