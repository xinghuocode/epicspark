// 确定性剧情生成器：没有 API Key / 远程失败时，仍要产出「可直接复现」的 SceneSpec。
// 设计依据：
//  · 场景描述精确到位置（东西放在哪）；
//  · 流程精确到每一步如何推进（谁在哪个坐标做什么）；
//  · 锚点载体必须有 requirement，且 targetSceneId 形成合理场景链（广场不可能跳到海上孤岛）；
//  · 开端 → 冲突 → 转折 → 高潮 → 结局，第 5 幕结束后 gameOver。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using EpicSpark.Core;
using UnityEngine;

namespace EpicSpark.Story
{
    /// <summary>
    /// 纯逻辑（不继承 MonoBehaviour）的确定性剧情兜底生成器。
    /// 同一事件 + 同一幕序号 + 同一游戏时间 → 完全相同的场景（sceneId 稳定、可复用）。
    /// </summary>
    public sealed class DeterministicStoryBuilder
    {
        /// <summary>一条剧情弧的场景总数（开端/冲突/转折/高潮/结局）。</summary>
        public const int MaxScenes = 5;

        private const float SceneSize = 60f;

        // ─────────────────────────── 对外：id / 解析 ───────────────────────────

        /// <summary>事件名的稳定哈希（FNV-1a，8 位小写十六进制）。</summary>
        public string EventHash(string userEvent)
        {
            return Hash8(NormalizeEventName(userEvent));
        }

        /// <summary>sceneId 形如 s_&lt;hash&gt;_&lt;index&gt;_&lt;code&gt;[_&lt;visit&gt;]。</summary>
        public string MakeSceneId(string eventHash, int index, StoryTemplate template, int visitCount)
        {
            string id = "s_" + eventHash + "_" + index.ToString(CultureInfo.InvariantCulture) + "_" + TemplateCode(template);
            if (visitCount >= 2) id += "_" + visitCount.ToString(CultureInfo.InvariantCulture);
            return id;
        }

        public string MakeTurnId(string eventHash, int index, int visitCount)
        {
            string id = "t_" + eventHash + "_" + index.ToString(CultureInfo.InvariantCulture);
            if (visitCount >= 2) id += "_" + visitCount.ToString(CultureInfo.InvariantCulture);
            return id;
        }

        /// <summary>从 sceneId 取幕序号；不是本生成器的 id 时返回 -1。</summary>
        public int ParseSceneIndex(string sceneId)
        {
            if (string.IsNullOrEmpty(sceneId)) return -1;
            string[] parts = sceneId.Split('_');
            if (parts.Length < 4 || parts[0] != "s") return -1;
            int index;
            if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out index)) return -1;
            return index;
        }

        /// <summary>从 sceneId 取模板族；失败返回 null。</summary>
        public StoryTemplate? ParseSceneTemplate(string sceneId)
        {
            if (string.IsNullOrEmpty(sceneId)) return null;
            string[] parts = sceneId.Split('_');
            if (parts.Length < 4 || parts[0] != "s" || parts[3].Length == 0) return null;
            return TemplateFromCode(parts[3][0]);
        }

        public StoryStage StageForIndex(int index)
        {
            return StoryWorld.StageForIndex(index);
        }

        public string DescribeStage(StoryStage stage)
        {
            return StoryWorld.StageLabel(stage);
        }

        // ─────────────────────────── 对外：整回合 ───────────────────────────

        /// <summary>开局：事件名 + RAG 上下文 → 第一幕（开端）。</summary>
        public StoryTurn BuildBeginTurn(string userEvent, string knowledgeContext, long gameMinutes)
        {
            string eventName = NormalizeEventName(userEvent);
            string hash = Hash8(eventName);
            EraProfile profile = StoryWorld.Profile(StoryWorld.Detect(eventName, knowledgeContext));
            long minutes = Math.Max(0L, gameMinutes);
            SeedRandom rng = new SeedRandom(SeedFor(hash, 0, minutes, 0));
            StoryTemplate template = ChooseOpeningTemplate(eventName, profile, rng);

            SceneSpec scene = BuildScene(eventName, knowledgeContext, 0, template, minutes, 1, null);
            var turn = new StoryTurn
            {
                turnId = MakeTurnId(hash, 0, 1),
                narrative = BuildNarrative(eventName, profile, scene, StoryStage.Opening, null, null),
                activeSceneIndex = 0,
                gameOver = false,
            };
            turn.scenes.Add(scene);
            return turn;
        }

        /// <summary>玩家走入锚点后：据当前场景 / 锚点 / 行为日志推进下一幕。</summary>
        public StoryTurn BuildContinueTurn(
            string userEvent,
            string knowledgeContext,
            StoryContinueRequest request,
            int sceneIndex,
            int visitCount,
            long gameMinutes)
        {
            if (request == null) request = new StoryContinueRequest();
            string eventName = NormalizeEventName(userEvent);
            if (string.IsNullOrEmpty(eventName) && !string.IsNullOrEmpty(request.currentSceneName))
                eventName = request.currentSceneName;
            string hash = Hash8(eventName);
            EraProfile profile = StoryWorld.Profile(StoryWorld.Detect(eventName, knowledgeContext));
            long minutes = Math.Max(0L, gameMinutes);
            int index = Mathf.Clamp(sceneIndex, 0, MaxScenes - 1);
            SeedRandom rng = new SeedRandom(SeedFor(hash, index, minutes, visitCount));

            StoryTemplate? current = ParseSceneTemplate(request.currentSceneId);
            StoryTemplate currentTemplate = current.HasValue
                ? current.Value
                : GuessTemplateFromText(request.currentSceneName, StoryTemplate.Square);

            StoryTemplate template = ChooseNextTemplate(currentTemplate, request, rng);
            SceneSpec scene = BuildScene(eventName, knowledgeContext, index, template, minutes, visitCount, request);
            StoryStage stage = StoryWorld.StageForIndex(index);
            ActionSummary actions = ActionSummary.From(request.recentActions);

            var turn = new StoryTurn
            {
                turnId = MakeTurnId(hash, index, visitCount),
                narrative = BuildNarrative(eventName, profile, scene, stage, actions, request),
                activeSceneIndex = 0,
                gameOver = index >= MaxScenes - 1,
            };
            turn.scenes.Add(scene);

            if (turn.gameOver)
            {
                turn.endingSummary = BuildEndingSummary(eventName, profile, scene, actions);
                turn.endingAchievement = BuildEndingAchievement(eventName, profile, actions);
            }
            return turn;
        }

        /// <summary>为远程模型返回的场景补齐 id / 坐标 / 锚点链；缺失严重时整场景替换为确定性场景。</summary>
        public void FinalizeRemoteTurn(
            StoryTurn turn,
            string userEvent,
            string knowledgeContext,
            int startIndex,
            long gameMinutes,
            bool forceEnding)
        {
            if (turn == null) return;
            string eventName = NormalizeEventName(userEvent);
            string hash = Hash8(eventName);
            EraProfile profile = StoryWorld.Profile(StoryWorld.Detect(eventName, knowledgeContext));
            long minutes = Math.Max(0L, gameMinutes);
            if (turn.scenes == null) turn.scenes = new List<SceneSpec>();

            if (turn.scenes.Count > 3)
                turn.scenes.RemoveRange(3, turn.scenes.Count - 3);

            for (int i = 0; i < turn.scenes.Count; i++)
            {
                SceneSpec scene = turn.scenes[i];
                int index = Mathf.Clamp(startIndex + i, 0, MaxScenes - 1);
                if (scene == null)
                {
                    turn.scenes[i] = BuildScene(eventName, knowledgeContext, index, StoryTemplate.Square, minutes, 1, null);
                    continue;
                }
                if (scene.anchors == null) scene.anchors = new List<SceneAnchorSpec>();
                if (scene.npcs == null) scene.npcs = new List<NpcSpec>();
                if (scene.props == null) scene.props = new List<PropInteraction>();
                if (scene.flow == null) scene.flow = new List<FlowStep>();

                // 锚点是继续剧情的前提：一个都没有就直接换成确定性场景
                if (scene.anchors.Count == 0)
                {
                    turn.scenes[i] = BuildScene(eventName, knowledgeContext, index, GuessTemplateFromText(
                        (scene.sceneName ?? "") + " " + (scene.layoutDescription ?? ""), StoryTemplate.Square),
                        minutes, 1, null);
                    continue;
                }

                if (string.IsNullOrEmpty(scene.sceneId))
                {
                    StoryTemplate guess = GuessTemplateFromText(
                        (scene.sceneName ?? "") + " " + (scene.layoutDescription ?? ""), StoryTemplate.Square);
                    scene.sceneId = MakeSceneId(hash, index, guess, 1);
                }
                if (string.IsNullOrEmpty(scene.sceneName)) scene.sceneName = StoryWorld.TemplateDisplay(ParseSceneTemplate(scene.sceneId) ?? StoryTemplate.Square);
                if (string.IsNullOrEmpty(scene.era)) scene.era = profile.eraName;
                if (string.IsNullOrEmpty(scene.region)) scene.region = profile.regionName;
                if (scene.width <= 0f) scene.width = SceneSize;
                if (scene.length <= 0f) scene.length = SceneSize;
                // 出生点必须落在南侧安全区；契约默认值 (0, 1.2, 0) 在场地中央，需要纠正
                Vector3 spawn = scene.spawnPoint;
                bool spawnValid = spawn.z <= -8f && spawn.z >= -29f && Mathf.Abs(spawn.x) <= 27f
                    && spawn.y > 0f && spawn.y < 5f;
                if (!spawnValid) scene.spawnPoint = new Vector3(0f, 1.2f, -24f);

                for (int a = 0; a < scene.anchors.Count; a++)
                {
                    SceneAnchorSpec anchor = scene.anchors[a];
                    if (anchor == null) continue;
                    if (string.IsNullOrEmpty(anchor.anchorId)) anchor.anchorId = "a_" + hash + "_" + index + "_" + a;
                    if (string.IsNullOrEmpty(anchor.displayName)) anchor.displayName = "传送锚点 " + (a + 1);
                    if (string.IsNullOrEmpty(anchor.requirement)) anchor.requirement = "必须有明确的场景载体（门/车/船等）供玩家走入";
                    if (string.IsNullOrEmpty(anchor.timeDelta) || !GameClock.TryParseDelta(anchor.timeDelta, out _))
                        anchor.timeDelta = "30min";
                    if (anchor.triggerKind == AnchorTriggerKind.Event && string.IsNullOrEmpty(anchor.triggerEvent))
                        anchor.triggerKind = AnchorTriggerKind.Placed;
                    if (string.IsNullOrEmpty(anchor.targetSceneId) && index < MaxScenes - 1)
                    {
                        StoryTemplate guess = GuessTemplateFromText(
                            (anchor.displayName ?? "") + " " + (anchor.description ?? "") + " " + (anchor.requirement ?? ""),
                            StoryTemplate.Square);
                        anchor.targetSceneId = MakeSceneId(hash, index + 1, guess, 1);
                        if (string.IsNullOrEmpty(anchor.targetSceneName))
                            anchor.targetSceneName = StoryWorld.TemplateDisplay(guess);
                    }
                }

                for (int n = 0; n < scene.npcs.Count; n++)
                {
                    NpcSpec npc = scene.npcs[n];
                    if (npc == null) continue;
                    if (string.IsNullOrEmpty(npc.npcId)) npc.npcId = "n_" + hash + "_" + index + "_" + n;
                    if (string.IsNullOrEmpty(npc.displayName)) npc.displayName = "无名者 " + (n + 1);
                    if (string.IsNullOrEmpty(npc.tag)) npc.tag = "平民";
                    if (npc.maxHp <= 0f) npc.maxHp = 100f;
                }

                for (int pr = 0; pr < scene.props.Count; pr++)
                {
                    PropInteraction prop = scene.props[pr];
                    if (prop == null) continue;
                    if (string.IsNullOrEmpty(prop.propId)) prop.propId = "p_" + hash + "_" + index + "_" + pr;
                    if (string.IsNullOrEmpty(prop.displayName)) prop.displayName = "可查看的物件";
                    if (!IsValidInteractKind(prop.interactKind)) prop.interactKind = "examine";
                }

                for (int f = 0; f < scene.flow.Count; f++)
                {
                    FlowStep step = scene.flow[f];
                    if (step == null) continue;
                    if (string.IsNullOrEmpty(step.stepId)) step.stepId = "f_" + hash + "_" + index + "_" + f;
                    if (string.IsNullOrEmpty(step.actorId)) step.actorId = "player";
                    if (string.IsNullOrEmpty(step.regionId)) step.regionId = RegionIdFor(ParseSceneTemplate(scene.sceneId) ?? StoryTemplate.Square);
                    if (string.IsNullOrEmpty(step.description)) step.description = "玩家移动到场景中心并观察环境";
                }

                SpreadZeroPositions(scene, index);
                if (string.IsNullOrEmpty(scene.layoutDescription))
                    scene.layoutDescription = StoryTemplates.Layout(
                        ParseSceneTemplate(scene.sceneId) ?? StoryTemplate.Square, profile, scene,
                        StoryWorld.StageForIndex(index));
                if (string.IsNullOrEmpty(scene.flowDescription))
                    scene.flowDescription = ComposeFlowText(scene);
                if (string.IsNullOrEmpty(scene.buildRequirement))
                    scene.buildRequirement = BuildBuildRequirement(profile, scene);
            }

            if (turn.scenes.Count == 0)
                turn.scenes.Add(BuildScene(eventName, knowledgeContext, Mathf.Clamp(startIndex, 0, MaxScenes - 1),
                    StoryTemplate.Square, minutes, 1, null));

            turn.activeSceneIndex = Mathf.Clamp(turn.activeSceneIndex, 0, turn.scenes.Count - 1);
            if (string.IsNullOrEmpty(turn.turnId)) turn.turnId = MakeTurnId(hash, startIndex, 1);

            if (forceEnding || startIndex >= MaxScenes - 1)
            {
                turn.gameOver = true;
                if (string.IsNullOrEmpty(turn.endingSummary))
                    turn.endingSummary = BuildEndingSummary(eventName, profile, turn.scenes[turn.activeSceneIndex], null);
                if (string.IsNullOrEmpty(turn.endingAchievement))
                    turn.endingAchievement = BuildEndingAchievement(eventName, profile, null);
            }
        }

        /// <summary>预测某个锚点通向的场景 id（与 ContinueAsync 实际生成的 id 对齐，用于回填 targetSceneId）。</summary>
        public string PredictNextSceneId(
            string userEvent,
            string currentSceneId,
            string anchorName,
            string anchorDescription,
            string anchorRequirement,
            int visitCount)
        {
            string eventName = NormalizeEventName(userEvent);
            string hash = Hash8(eventName);
            int index = ParseSceneIndex(currentSceneId);
            if (index < 0) index = 0;
            StoryTemplate? current = ParseSceneTemplate(currentSceneId);
            StoryTemplate currentTemplate = current.HasValue ? current.Value : StoryTemplate.Square;
            string text = (anchorName ?? "") + " " + (anchorDescription ?? "") + " " + (anchorRequirement ?? "");
            StoryTemplate target;
            if (!TryParseExplicitTarget(text, out target) || target == currentTemplate)
                target = GuessTemplateFromText(text, CurrentTransitionDefault(currentTemplate));
            return MakeSceneId(hash, Math.Min(index + 1, MaxScenes - 1), target, Math.Max(1, visitCount));
        }

        // ─────────────────────────── 场景组装 ───────────────────────────

        /// <summary>生成一个完整场景（布局 / 锚点 / NPC / 道具 / 流程 / 搭建要求）。</summary>
        public SceneSpec BuildScene(
            string userEvent,
            string knowledgeContext,
            int sceneIndex,
            StoryTemplate template,
            long gameMinutes,
            int visitCount,
            StoryContinueRequest request)
        {
            string eventName = NormalizeEventName(userEvent);
            string hash = Hash8(eventName);
            EraProfile profile = StoryWorld.Profile(StoryWorld.Detect(eventName, knowledgeContext));
            int index = Mathf.Clamp(sceneIndex, 0, MaxScenes - 1);
            int visits = Math.Max(1, visitCount);
            StoryStage stage = StoryWorld.StageForIndex(index);
            SeedRandom rng = new SeedRandom(SeedFor(hash, index, gameMinutes, visits));
            ActionSummary actions = ActionSummary.From(request != null ? request.recentActions : null);

            var scene = new SceneSpec
            {
                sceneId = MakeSceneId(hash, index, template, visits),
                sceneName = BuildSceneName(profile, template, stage, rng),
                era = profile.eraName,
                region = profile.regionName,
                width = SceneSize,
                length = SceneSize,
                spawnPoint = new Vector3(0f, 1.2f, -24f),
                isReused = false,
            };

            scene.npcs = BuildNpcs(profile, template, hash, index, rng);
            scene.anchors = BuildAnchors(profile, template, hash, index, rng);
            scene.props = BuildProps(profile, template, rng);
            scene.flow = BuildFlowSteps(scene, profile, template, index, stage, actions, request);

            scene.layoutDescription = StoryTemplates.Layout(template, profile, scene, stage);
            scene.flowDescription = ComposeFlowText(scene);
            scene.buildRequirement = BuildBuildRequirement(profile, scene);
            return scene;
        }

        private static string BuildSceneName(EraProfile profile, StoryTemplate template, StoryStage stage, SeedRandom rng)
        {
            string[] pool;
            switch (template)
            {
                case StoryTemplate.Square: pool = new[] { "市政广场", "中央广场", "旧城广场", "钟楼广场" }; break;
                case StoryTemplate.Country: pool = new[] { "西郊村落", "河谷农庄", "麦田村口" }; break;
                case StoryTemplate.Harbor: pool = new[] { "旧港码头", "北岸码头", "盐仓码头" }; break;
                case StoryTemplate.Court: pool = new[] { "府邸前庭", "宫城外庭", "官署正堂" }; break;
                case StoryTemplate.Battlefield: pool = new[] { "北线阵地", "石桥战场", "堑壕前沿" }; break;
                case StoryTemplate.Church: pool = new[] { "圣堂街区", "修道院前庭", "钟楼墓园" }; break;
                default: pool = new[] { "中央集市", "河岸集市", "布匹市场" }; break;
            }
            return profile.regionName + "·" + rng.Pick(pool) + "（" + StoryWorld.StageLabel(stage) + "）";
        }

        // ─────────────────────────── 锚点 ───────────────────────────

        private sealed class AnchorSeed
        {
            public string name;
            public string description;
            public string requirement;
            public string hint;
            public string timeDelta;
            public StoryTemplate target;
            public float x;
            public float z;
        }

        private static readonly AnchorSeed[] SquareAnchors =
        {
            new AnchorSeed
            {
                name = "南侧{gate}", description = "南侧城门的门洞，吊闸半落，门洞外是通往城外的土路，门内堆着两袋沙土。",
                requirement = "必须是可通行的城门门洞（宽 6 米、高 7 米），门扇斑驳、有铁钉与门轴",
                hint = "old city gate stone arch wooden door", timeDelta = "30min", target = StoryTemplate.Market, x = 0f, z = -27f,
            },
            new AnchorSeed
            {
                name = "{church}的侧门", description = "教堂侧面一扇半开的木门，门后有石阶通向下沉的走廊，门轴发涩。",
                requirement = "必须是一扇能推开的厚重教堂木门（宽 1.2 米、高 2.4 米），带铁质门环",
                hint = "old wooden church door arch ring", timeDelta = "20min", target = StoryTemplate.Church, x = 22f, z = 16f,
            },
            new AnchorSeed
            {
                name = "停在街角的{transport}", description = "一辆旧式{transport}停在石板路边，车夫坐在车辕上等人，车厢里铺着干草。",
                requirement = "必须是可登乘的{transport}车身：车厢长约 4.2 米、宽 1.8 米、高 2.4 米，有车轮与可坐的车辕",
                hint = "wooden carriage wagon horse cart", timeDelta = "2h", target = StoryTemplate.Country, x = -20f, z = -14f,
            },
            new AnchorSeed
            {
                name = "{townhall}前的石阶", description = "北侧{townhall}门前的五级石阶，台阶上散落着被人踩碎的纸片，门内透出灯光。",
                requirement = "必须是五级宽阔石阶（宽 6 米、每级高 0.18 米），通向一扇双开大门",
                hint = "stone steps government building entrance", timeDelta = "1h", target = StoryTemplate.Court, x = 0f, z = 16f,
            },
            new AnchorSeed
            {
                name = "通向兵营的窄巷", description = "广场东北角一条两米宽的巷子，尽头是挂着军旗的兵营木门，墙上贴着征召告示。",
                requirement = "必须是巷子尽头的一扇军用木门（宽 1.4 米、高 2.6 米），门上钉着褪色的军旗",
                hint = "barracks wooden gate alley flag", timeDelta = "1h", target = StoryTemplate.Battlefield, x = 24f, z = 24f,
            },
        };

        private static readonly AnchorSeed[] CountryAnchors =
        {
            new AnchorSeed
            {
                name = "通往镇上的{transport}", description = "村口土路旁停着一辆{transport}，车斗里装着麻袋，车夫正把最后一袋往上搬。",
                requirement = "必须是可登乘的{transport}：车斗 3.6×1.8 米，带两根车辕与木质车轮",
                hint = "wooden farm cart ox wagon sacks", timeDelta = "3h", target = StoryTemplate.Square, x = -26f, z = -8f,
            },
            new AnchorSeed
            {
                name = "田埂尽头的石桥", description = "田埂尽头一座单孔石桥，桥面宽 2.5 米，桥下是浅水沟，桥头有一块被磨亮的石头。",
                requirement = "必须是单孔石桥（跨度 6 米、宽 2.5 米、栏杆高 0.9 米）",
                hint = "small stone bridge over stream", timeDelta = "1h", target = StoryTemplate.Market, x = 12f, z = 2f,
            },
            new AnchorSeed
            {
                name = "小礼拜堂的钟楼小门", description = "小礼拜堂钟楼底部有一扇矮门，门内是堆放农具的杂物间，墙上有刻痕。",
                requirement = "必须是钟楼底部的一扇矮木门（高 1.8 米、宽 1 米），门框为石砌",
                hint = "small chapel bell tower lower door", timeDelta = "40min", target = StoryTemplate.Church, x = 24f, z = 18f,
            },
            new AnchorSeed
            {
                name = "庄园围墙上的侧门", description = "庄园围墙上一扇铁皮包边的侧门，门内传来狗叫，门缝里透出一线灯光。",
                requirement = "必须是围墙上的单扇侧门（高 2.2 米、宽 1.2 米），门上有铁条与门环",
                hint = "manor wall side gate iron ring", timeDelta = "2h", target = StoryTemplate.Court, x = -20f, z = 18f,
            },
            new AnchorSeed
            {
                name = "征兵路障", description = "土路北端用木栅与草垛堆成的路障，栅栏上挂着征召的布告，路障后是通往战场的车队。",
                requirement = "必须是可搬开的木栅路障（宽 5 米、高 1.6 米），由圆木与绳索捆扎",
                hint = "wooden barricade logs ropes checkpoint", timeDelta = "4h", target = StoryTemplate.Battlefield, x = 6f, z = 26f,
            },
        };

        private static readonly AnchorSeed[] HarborAnchors =
        {
            new AnchorSeed
            {
                name = "帆船的跳板", description = "一条帆船靠在中央栈桥，跳板搭在船头与栈桥之间，船舱里堆着贴封条的木箱。",
                requirement = "必须是可登乘的帆船与搭板（搭板长 3 米、宽 0.8 米，船身长 12 米、桅高 9 米）",
                hint = "sailing boat gangplank wooden ship mast", timeDelta = "3h", target = StoryTemplate.Market, x = 0f, z = 16f,
            },
            new AnchorSeed
            {
                name = "货栈大门", description = "货栈临街的一扇双开大门，门板上有盐渍与编号，门内是堆到屋顶的麻袋。",
                requirement = "必须是货栈双开木门（宽 3 米、高 3.5 米），可推开，门板有编号",
                hint = "warehouse double wooden door sacks", timeDelta = "1h", target = StoryTemplate.Square, x = -17f, z = -1f,
            },
            new AnchorSeed
            {
                name = "海关哨所的门", description = "海关哨所唯一的门，门口立着一块写着税则的木牌，门槛被踩出凹槽。",
                requirement = "海关哨所的单扇门（宽 1.4 米、高 2.4 米），门旁有税则木牌",
                hint = "customs house door wooden sign plaque", timeDelta = "30min", target = StoryTemplate.Court, x = 21f, z = -3f,
            },
            new AnchorSeed
            {
                name = "西栈桥的小渔船", description = "西侧栈桥下拴着一条小渔船，船上有双桨与半桶鱼，缆绳系在桩上。",
                requirement = "必须是可登乘的小渔船（长 5 米、宽 1.6 米、有双桨与缆绳）",
                hint = "small wooden fishing boat oars rope", timeDelta = "2h", target = StoryTemplate.Country, x = -12f, z = 12f,
            },
        };

        private static readonly AnchorSeed[] CourtAnchors =
        {
            new AnchorSeed
            {
                name = "后门的{transport}", description = "后门外停着一辆{transport}，车帘放下一半，车夫背对着门抽旱烟。",
                requirement = "必须是可登乘的{transport}，车厢封闭、有车帘与两扇小窗，长约 4 米",
                hint = "closed carriage wagon door curtain", timeDelta = "1h", target = StoryTemplate.Square, x = -22f, z = -10f,
            },
            new AnchorSeed
            {
                name = "回廊尽头的侧门", description = "东厢回廊尽头有一扇侧门，门后是通往教堂的卵石小路，门上挂着熄灭的提灯。",
                requirement = "必须是回廊尽头的单扇侧门（宽 1.2 米、高 2.6 米），带壁灯",
                hint = "corridor side door stone arch lantern", timeDelta = "20min", target = StoryTemplate.Church, x = 22f, z = 8f,
            },
            new AnchorSeed
            {
                name = "后院围墙的小门", description = "后院围墙上一扇包铁的小门，门外是菜地与通往村子的土路。",
                requirement = "必须是围墙上的包铁单门（宽 1.1 米、高 2 米），门栓在外侧",
                hint = "backyard iron gate wall plank", timeDelta = "2h", target = StoryTemplate.Country, x = -16f, z = 20f,
            },
            new AnchorSeed
            {
                name = "卫兵值房的门", description = "卫兵值房的门半开着，里面靠墙立着{weapon}，桌上摊着一张城防图，门外传来换岗的脚步声。",
                requirement = "必须是值房的单扇门（宽 1.1 米、高 2.2 米），门内有墙上的武器架",
                hint = "guard room door weapon rack map", timeDelta = "1h", target = StoryTemplate.Battlefield, x = 6f, z = 22f,
            },
        };

        private static readonly AnchorSeed[] BattlefieldAnchors =
        {
            new AnchorSeed
            {
                name = "后方辎重的{transport}", description = "阵地后方停着一辆{transport}，车斗里是空弹药箱与半车干草，车夫正给牲口喂水。",
                requirement = "必须是可登乘的辎重{transport}（车斗 4×2 米，带篷布与双辕）",
                hint = "supply wagon cart hay ammunition box", timeDelta = "3h", target = StoryTemplate.Country, x = 20f, z = -10f,
            },
            new AnchorSeed
            {
                name = "军帐的帘门", description = "最大的一顶军帐，帘门用皮带系在门柱上，帐内传出压低声音的争吵。",
                requirement = "必须是可掀开的军帐帘门（宽 1.8 米、高 2 米，帆布门帘与门柱）",
                hint = "military tent canvas entrance pole", timeDelta = "1h", target = StoryTemplate.Court, x = -14f, z = 4f,
            },
            new AnchorSeed
            {
                name = "救护所的白布帷幕", description = "白布救护所的入口只挂一层染血的帷幕，掀开能看到三张担架与一箱绷带。",
                requirement = "必须是可掀开的白色布帷幕（宽 2 米、高 2.2 米），门口有担架",
                hint = "medical tent white cloth stretcher bandage", timeDelta = "30min", target = StoryTemplate.Church, x = 24f, z = -22f,
            },
            new AnchorSeed
            {
                name = "交通壕尽头的土路", description = "交通壕向北拐进一条通往城镇的土路，路口插着褪色的路标，路面被车辙压得很实。",
                requirement = "必须是交通壕尽头可通行的土路出口（宽 3 米），路口有木质路标",
                hint = "trench exit dirt road signpost", timeDelta = "2h", target = StoryTemplate.Square, x = 0f, z = 22f,
            },
        };

        private static readonly AnchorSeed[] ChurchAnchors =
        {
            new AnchorSeed
            {
                name = "教堂正门", description = "教堂正面的大木门敞着一扇，门内的彩窗把光切成条状落在石地上。",
                requirement = "必须是教堂正门双扇木门（宽 3 米、高 5 米），门上有铁钉与浮雕",
                hint = "church main double wooden door relief", timeDelta = "10min", target = StoryTemplate.Square, x = 0f, z = 0f,
            },
            new AnchorSeed
            {
                name = "墓园小门", description = "墓园铁栏尽头的一扇小铁门，门轴生锈，推开时整片铁栏都在响。",
                requirement = "必须是墓园铁栏上的单扇小铁门（宽 1 米、高 1.6 米），带锈迹",
                hint = "cemetery iron gate fence rust", timeDelta = "40min", target = StoryTemplate.Country, x = -24f, z = -28f,
            },
            new AnchorSeed
            {
                name = "修士居所的侧门", description = "修士居所的一扇窄门，门上刻着编号，门后是通往官署后院的窄巷。",
                requirement = "必须是修士居所的单扇窄门（宽 0.9 米、高 2 米），门框为石砌",
                hint = "monastery narrow side door stone", timeDelta = "1h", target = StoryTemplate.Court, x = 20f, z = 10f,
            },
            new AnchorSeed
            {
                name = "钟楼楼梯下的矮门", description = "钟楼底部楼梯下的一扇矮木门，门后堆着绳索与铜钟的旧零件，地上有掉落的钟锤。",
                requirement = "必须是钟楼底部的一扇矮木门（高 1.6 米、宽 0.9 米），门后有绳索",
                hint = "bell tower lower wooden door rope", timeDelta = "30min", target = StoryTemplate.Market, x = -12f, z = 4f,
            },
        };

        private static readonly AnchorSeed[] MarketAnchors =
        {
            new AnchorSeed
            {
                name = "西端石拱门", description = "主街西端一座单孔石拱门，门洞上的浮雕被磨平，门外是通往广场的巷子。",
                requirement = "必须是可通行的石拱门洞（宽 6 米、高 5 米），门楣有浮雕",
                hint = "stone arch way town gate relief", timeDelta = "15min", target = StoryTemplate.Square, x = -28f, z = 0f,
            },
            new AnchorSeed
            {
                name = "运货的{transport}", description = "街尾停着一辆装满鱼筐的{transport}，车夫正在和摊主讨价还价，车斗里的水一路滴到街心。",
                requirement = "必须是可登乘的运货{transport}（车斗 3.8×1.8 米，装鱼筐与篷布）",
                hint = "cargo cart fish baskets water", timeDelta = "1h", target = StoryTemplate.Harbor, x = 16f, z = -16f,
            },
            new AnchorSeed
            {
                name = "布庄的里门", description = "布庄临街门面后面的里门，门内是账房，桌上摊着账本与一封没有署名的信。",
                requirement = "必须是商铺后进账房的单扇门（宽 1.1 米、高 2.2 米），门内有账桌",
                hint = "cloth shop inner door accounting desk", timeDelta = "40min", target = StoryTemplate.Court, x = -22f, z = 2f,
            },
            new AnchorSeed
            {
                name = "北支巷尽头的木栅", description = "北支巷尽头用木栅封住，木栅后是通往教堂的小径，栅条上挂着一串干辣椒。",
                requirement = "必须是可搬开的木栅（宽 2.5 米、高 1.5 米），由横木与竖条钉成",
                hint = "wooden fence alley pepper", timeDelta = "20min", target = StoryTemplate.Church, x = 10f, z = 26f,
            },
        };

        private List<SceneAnchorSpec> BuildAnchors(EraProfile p, StoryTemplate template, string hash, int index, SeedRandom rng)
        {
            AnchorSeed[] seeds = AnchorsFor(template);
            var chosen = new List<AnchorSeed>();
            if (seeds.Length > 0) chosen.Add(seeds[0]); // 主推进锚点恒定保留
            int want = Mathf.Clamp(rng.Next(3, 6), 3, seeds.Length);
            var used = new List<int> { 0 };
            while (chosen.Count < want && used.Count < seeds.Length)
            {
                int candidate = rng.Next(1, seeds.Length);
                if (used.Contains(candidate)) continue;
                used.Add(candidate);
                chosen.Add(seeds[candidate]);
            }

            var list = new List<SceneAnchorSpec>(chosen.Count);
            for (int i = 0; i < chosen.Count; i++)
            {
                AnchorSeed seed = chosen[i];
                bool hasNext = index < MaxScenes - 1;
                string targetName = hasNext ? StoryWorld.TemplateDisplay(seed.target) : "";
                var anchor = new SceneAnchorSpec
                {
                    anchorId = "a_" + hash + "_" + index + "_" + i,
                    displayName = p.Fill(seed.name),
                    requirement = p.Fill(seed.requirement),
                    modelHint = seed.hint,
                    timeDelta = seed.timeDelta,
                    targetSceneId = hasNext ? MakeSceneId(hash, index + 1, seed.target, 1) : "",
                    targetSceneName = targetName,
                    triggerKind = AnchorTriggerKind.Placed,
                    triggerEvent = "",
                    localPosition = new Vector3(seed.x, 0f, seed.z),
                };
                string baseDesc = p.Fill(seed.description);
                if (hasNext)
                {
                    baseDesc += " 离开方式：由 " + StoryTemplates.Pos(seed.x, seed.z) + " 出发，" + seed.timeDelta
                        + " 后抵达「" + targetName + "」场景。";
                }
                else
                {
                    baseDesc += " 离开方式：这是本幕的收束点，剧情走到这里即告结束。";
                }
                anchor.description = baseDesc;
                list.Add(anchor);
            }
            return list;
        }

        private static AnchorSeed[] AnchorsFor(StoryTemplate template)
        {
            switch (template)
            {
                case StoryTemplate.Square: return SquareAnchors;
                case StoryTemplate.Country: return CountryAnchors;
                case StoryTemplate.Harbor: return HarborAnchors;
                case StoryTemplate.Court: return CourtAnchors;
                case StoryTemplate.Battlefield: return BattlefieldAnchors;
                case StoryTemplate.Church: return ChurchAnchors;
                default: return MarketAnchors;
            }
        }

        // ─────────────────────────── NPC ───────────────────────────

        private sealed class NpcSeed
        {
            public string tag;
            public string storyRole;
            public string persona;
            public ToolKind tool;
        }

        private static NpcSeed[] NpcsFor(StoryTemplate template)
        {
            switch (template)
            {
                case StoryTemplate.Square:
                    return new[]
                    {
                        new NpcSeed { tag = "信使", storyRole = "带来下一幕的消息", tool = ToolKind.Satchel, persona = "在{region}的街道上跑了三年腿，认得每一个路口和每一张脸；说话快、爱用命令式的短句，总把{notice}卷成一卷塞在袖子里。" },
                        new NpcSeed { tag = "士兵", storyRole = "维持秩序的武力", tool = ToolKind.Gun, persona = "{soldier}，刚从前线调回城，制服上有洗不掉的泥点；说话直接、带点火气，对{crowd}又怕又瞧不上。" },
                        new NpcSeed { tag = "商人", storyRole = "提供物资与价钱情报", tool = ToolKind.Satchel, persona = "{merchant}，在{market}有一间铺面，算账很快；见到谁都先笑一下，最关心粮食和价钱。" },
                        new NpcSeed { tag = "告密者", storyRole = "把玩家行为上报给当权者", tool = ToolKind.None, persona = "没有正经身份的闲人，靠打听消息换钱；总是压低声音说话，喜欢先问对方「你站哪边」。" },
                    };
                case StoryTemplate.Country:
                    return new[]
                    {
                        new NpcSeed { tag = "平民", storyRole = "提供乡野视角", tool = ToolKind.None, persona = "{commoner}，一辈子没离开过这几块田；说话慢、常叹气，对{soldier}和{noble}都带着怕。" },
                        new NpcSeed { tag = "神职", storyRole = "安慰与劝告", tool = ToolKind.None, persona = "{priest}，小礼拜堂里唯一的神职人员；说话温和但固执，喜欢用「愿主保佑」开头。" },
                        new NpcSeed { tag = "贵族", storyRole = "催租与发号施令", tool = ToolKind.None, persona = "{noble}的管家，穿得比佃农整齐；开口就是租子和账目，对{crowd}居高临下。" },
                        new NpcSeed { tag = "士兵", storyRole = "征粮的威胁", tool = ToolKind.Gun, persona = "征粮的{soldier}，背着{weapon}；说话粗鲁、没耐心，饿了就会去敲农舍的门。" },
                    };
                case StoryTemplate.Harbor:
                    return new[]
                    {
                        new NpcSeed { tag = "商人", storyRole = "掌握船期与货单", tool = ToolKind.Satchel, persona = "船主兼货主，常年在{region}的码头讨生活；说话带着水汽和算盘味，最怕封港。" },
                        new NpcSeed { tag = "平民", storyRole = "码头工头，能调动人手", tool = ToolKind.None, persona = "码头工头，手上全是绳茧；嗓门大，管着二十来个装卸工，讲义气也记仇。" },
                        new NpcSeed { tag = "贵族", storyRole = "查私与扣押", tool = ToolKind.None, persona = "海关吏员，穿深色制服、随身带着税则册子；说话一字一顿，习惯先看人的手再看脸。" },
                        new NpcSeed { tag = "工匠", storyRole = "修船与藏东西", tool = ToolKind.None, persona = "修船的木匠，耳朵上别着铅笔；说话简短，喜欢摸木头的纹理判断好坏。" },
                    };
                case StoryTemplate.Court:
                    return new[]
                    {
                        new NpcSeed { tag = "平民", storyRole = "传话与带路", tool = ToolKind.None, persona = "府里的侍从，走路轻、说话轻，知道所有不该知道的走廊和门。" },
                        new NpcSeed { tag = "贵族", storyRole = "掌权者的代言人", tool = ToolKind.Satchel, persona = "书记官，随身带着墨水瓶和封蜡；说话绕弯，喜欢引用条例。" },
                        new NpcSeed { tag = "士兵", storyRole = "守门与盘问", tool = ToolKind.Gun, persona = "守门的{soldier}，站了六个时辰；说话一板一眼，只认口令不认人。" },
                        new NpcSeed { tag = "告密者", storyRole = "记录来往的人", tool = ToolKind.None, persona = "府里的密探，穿得像个普通文书；眼睛一直在数来往的人，从不多说一句。" },
                    };
                case StoryTemplate.Battlefield:
                    return new[]
                    {
                        new NpcSeed { tag = "工匠", storyRole = "救治与鉴定伤情", tool = ToolKind.Satchel, persona = "随军军医，袖口有洗不掉的血迹；说话冷静，习惯先看伤口再看人。" },
                        new NpcSeed { tag = "贵族", storyRole = "下达进攻命令", tool = ToolKind.Gun, persona = "前线军官，制服肩章磨损；说话短促，喜欢用手指在地图上敲。" },
                        new NpcSeed { tag = "平民", storyRole = "传递军令的鼓声", tool = ToolKind.None, persona = "鼓手，才十六岁；说话带着乡下口音，紧张时会不停擦鼓槌。" },
                        new NpcSeed { tag = "信使", storyRole = "带来援军或撤退的消息", tool = ToolKind.Satchel, persona = "传令兵，跑了一夜的路，靴子上全是泥；说话上气不接下气，只记得口令。" },
                    };
                case StoryTemplate.Church:
                    return new[]
                    {
                        new NpcSeed { tag = "神职", storyRole = "提供忏悔与庇护", tool = ToolKind.None, persona = "{priest}，在{church}服侍多年；说话缓慢温和，习惯把话题引向忏悔与宽恕。" },
                        new NpcSeed { tag = "神职", storyRole = "管着蜡烛与施舍箱", tool = ToolKind.None, persona = "修女，管着蜡烛与施舍箱；说话轻声细语，但记得每一个来求助的人。" },
                        new NpcSeed { tag = "平民", storyRole = "知道墓地里的秘密", tool = ToolKind.None, persona = "掘墓人，白天在墓园挖土；说话阴沉简短，对生死看得很淡。" },
                        new NpcSeed { tag = "贵族", storyRole = "寻求庇护的旧权贵", tool = ToolKind.Satchel, persona = "来告解的前贵族，衣着仍然体面但袖口磨破；说话小心翼翼，怕被人认出来。" },
                    };
                default:
                    return new[]
                    {
                        new NpcSeed { tag = "商人", storyRole = "买卖与讨价还价", tool = ToolKind.Satchel, persona = "布商，摊子上堆着各色布匹；说话又快又圆滑，最会看人下菜。" },
                        new NpcSeed { tag = "商人", storyRole = "市价情报", tool = ToolKind.None, persona = "鱼贩，围裙上全是鳞片；嗓门大，喜欢用价钱开头。" },
                        new NpcSeed { tag = "告密者", storyRole = "顺走东西并盯梢", tool = ToolKind.None, persona = "混在人群里的扒手兼眼线，手指细长；说话含糊，总往人多的地方靠。" },
                        new NpcSeed { tag = "士兵", storyRole = "巡逻与搜身", tool = ToolKind.Gun, persona = "市集巡逻的{soldier}，手里握着{weapon}；说话公事公办，眼睛盯着摊子底下。" },
                    };
            }
        }

        private List<NpcSpec> BuildNpcs(EraProfile p, StoryTemplate template, string hash, int index, SeedRandom rng)
        {
            NpcSeed[] seeds = NpcsFor(template);
            int count = Mathf.Clamp(rng.Next(2, 5), 2, seeds.Length);
            var positions = FanPositions(index, count);
            var usedNames = new List<string>();
            var list = new List<NpcSpec>(count);

            for (int i = 0; i < count; i++)
            {
                NpcSeed seed = seeds[i];
                string name = p.npcNames.Length > 0 ? p.npcNames[rng.Next(0, p.npcNames.Length)] : "无名者";
                if (usedNames.Contains(name)) name = name + "·" + (i + 1);
                usedNames.Add(name);

                var npc = new NpcSpec
                {
                    npcId = "n_" + hash + "_" + index + "_" + i,
                    displayName = name,
                    tag = seed.tag,
                    persona = p.Fill(seed.persona),
                    startingRegionId = RegionIdFor(template),
                    heldTool = seed.tool,
                    maxHp = seed.tag == "士兵" ? 120f : 100f,
                    localPosition = positions[i],
                    storyRole = seed.storyRole,
                };
                list.Add(npc);
            }
            return list;
        }

        /// <summary>在场景中心前方摆一团扇形位置，全部落在 60×60 的可通行区内。</summary>
        private static List<Vector3> FanPositions(int index, int count)
        {
            var result = new List<Vector3>(count);
            for (int i = 0; i < count; i++)
            {
                float angle = (-140f + (280f / Mathf.Max(1, count - 1 + 1)) * i + index * 7f) * Mathf.Deg2Rad;
                float radius = 8f + (i % 3) * 2.5f;
                float x = Mathf.Cos(angle) * radius;
                float z = Mathf.Sin(angle) * radius - 2f;
                result.Add(new Vector3(Mathf.Round(x * 2f) / 2f, 0f, Mathf.Round(z * 2f) / 2f));
            }
            return result;
        }

        // ─────────────────────────── 道具 ───────────────────────────

        private sealed class PropSeed
        {
            public string name;
            public string kind;
            public string description;
            public string effect;
        }

        private static PropSeed[] PropsFor(StoryTemplate template)
        {
            switch (template)
            {
                case StoryTemplate.Square:
                    return new[]
                    {
                        new PropSeed { name = "广场上的火药桶", kind = "ignite", description = "{weapon}用的火药桶，桶口封着油布，旁边散落着火绳。", effect = "点燃后炸开一团浓烟，附近 NPC 惊慌散开 3 秒。" },
                        new PropSeed { name = "{notice}", kind = "take", description = "有人刚塞过来的{notice}，墨迹还没干，边角被风吹得卷起来。", effect = "获得一份传单，可在与 NPC 对话时出示。" },
                        new PropSeed { name = "石阶旁的货箱", kind = "collapse", description = "三只叠放的空货箱，木板已经开裂，最上面一只歪着。", effect = "推倒后形成 1 米高的掩体。" },
                        new PropSeed { name = "{landmark}旁的石栏", kind = "examine", description = "被推倒一半的石栏，断口很新，碎石散在两米内。", effect = "查看后得知这里刚发生过一场冲撞。" },
                    };
                case StoryTemplate.Country:
                    return new[]
                    {
                        new PropSeed { name = "田边的干草车", kind = "ignite", description = "装满干草的板车，草已经晒得很干，车辕上挂着一盏马灯。", effect = "点燃后火光与浓烟从田埂升起，远处可见。" },
                        new PropSeed { name = "农舍门外的水桶", kind = "take", description = "木桶里还有半桶井水，桶沿上搭着一块粗布。", effect = "取水后可浇灭小火。" },
                        new PropSeed { name = "谷仓的木板门", kind = "collapse", description = "谷仓门被从外面顶住，门板可以整块卸下。", effect = "卸下后形成可通行的缺口。" },
                        new PropSeed { name = "村口石碑", kind = "examine", description = "碑文被磨掉了一半，只剩年份和两个字。", effect = "查看后得到一段零碎史料。" },
                    };
                case StoryTemplate.Harbor:
                    return new[]
                    {
                        new PropSeed { name = "油桶堆", kind = "ignite", description = "五只敞口油桶，气味刺鼻，桶身滚着一层盐霜。", effect = "点燃后沿栈桥形成火墙，阻挡通行 10 秒。" },
                        new PropSeed { name = "栈桥跳板", kind = "board", description = "搭在船头的跳板，木板被水泡得发软，中间垫着一块砖。", effect = "登上后可离开本场景。" },
                        new PropSeed { name = "缆绳架", kind = "collapse", description = "盘着缆绳的木架，架子已经倾斜，只靠两根桩撑着。", effect = "推倒后缆绳散落，可绊倒追兵。" },
                        new PropSeed { name = "鱼筐", kind = "take", description = "几只鱼筐，最下面那只压着一个油纸包。", effect = "翻找后得到一封信。" },
                    };
                case StoryTemplate.Court:
                    return new[]
                    {
                        new PropSeed { name = "廊柱下的烛台", kind = "ignite", description = "铜制烛台，蜡烛还剩半截，灯芯发黑。", effect = "点燃后照亮回廊，暴露阴影里的人物。" },
                        new PropSeed { name = "书记官的文件箱", kind = "take", description = "锁扣松动的木箱，里面是一卷一卷的卷宗。", effect = "取得卷宗，可在对话中作为把柄。" },
                        new PropSeed { name = "四扇绘屏", kind = "collapse", description = "绘着山水的四扇屏风，木框松动，一站上去就晃。", effect = "推倒后隔断视线 3 秒。" },
                        new PropSeed { name = "正殿前的铜钟", kind = "examine", description = "钟身上刻着年号与捐资者姓名，钟舌上缠着布。", effect = "查看后得知此地的真正归属。" },
                    };
                case StoryTemplate.Battlefield:
                    return new[]
                    {
                        new PropSeed { name = "盖着油布的火炮", kind = "ignite", description = "两门火炮的引信露在外面，炮膛里还有余温。", effect = "点火后炮声震耳，附近 NPC 全部卧倒。" },
                        new PropSeed { name = "弹药车", kind = "collapse", description = "弹药车车斗堆着木箱，轮子陷在泥里，只露出半截车辕。", effect = "推倒后形成掩体，并有火药洒落。" },
                        new PropSeed { name = "折断的军旗", kind = "take", description = "旗面破损但仍能辨认番号，旗杆断成两截。", effect = "取得军旗可作为身份凭证。" },
                        new PropSeed { name = "血迹未干的担架", kind = "examine", description = "担架上还留着绷带与一枚纽扣。", effect = "查看后得知伤员的去向。" },
                    };
                case StoryTemplate.Church:
                    return new[]
                    {
                        new PropSeed { name = "祭坛上的蜡烛", kind = "ignite", description = "三支长蜡烛，烛台是铜的，烛泪堆得很厚。", effect = "点燃后前庭变亮，仪式可以开始。" },
                        new PropSeed { name = "墙角的上锁铁箱", kind = "take", description = "施舍用的铁箱，箱盖有被撬过的痕迹。", effect = "撬开后取得几枚钱币与一张名单。" },
                        new PropSeed { name = "前排长椅", kind = "collapse", description = "一排木长椅，榫卯已经松脱，靠背歪向一侧。", effect = "推倒后堵住正门一半。" },
                        new PropSeed { name = "被摩挲得发亮的墓碑", kind = "examine", description = "墓碑上的名字被手指摩挲得发亮，碑脚压着一束干花。", effect = "查看后得到一个名字。" },
                    };
                default:
                    return new[]
                    {
                        new PropSeed { name = "油灯摊", kind = "ignite", description = "摊子上挂着三盏油灯，油壶就在旁边，灯罩熏得发黑。", effect = "点燃后火沿遮阳布蔓延，逼退人群。" },
                        new PropSeed { name = "支摊的木板", kind = "collapse", description = "支摊的木板只靠两根木棍撑着，上面摆着瓷碗。", effect = "掀翻后货物滚满主街，绊住追兵。" },
                        new PropSeed { name = "摊后的麻袋", kind = "take", description = "堆在摊后的麻袋，其中一只装着硬物。", effect = "翻找后得到钱袋或信件。" },
                        new PropSeed { name = "街心石柱上的铁笼", kind = "examine", description = "铁笼上过锁，笼底有暗色的痕迹，锁孔被塞了纸。", effect = "查看后得知此处曾用来示众。" },
                    };
            }
        }

        private List<PropInteraction> BuildProps(EraProfile p, StoryTemplate template, SeedRandom rng)
        {
            PropSeed[] seeds = PropsFor(template);
            int count = Mathf.Clamp(rng.Next(2, 5), 2, seeds.Length);
            var list = new List<PropInteraction>(count);
            var picked = new List<int>();
            while (picked.Count < count)
            {
                int candidate = rng.Next(0, seeds.Length);
                if (!picked.Contains(candidate)) picked.Add(candidate);
            }
            picked.Sort();
            for (int i = 0; i < picked.Count; i++)
            {
                PropSeed seed = seeds[picked[i]];
                list.Add(new PropInteraction
                {
                    propId = "prop_" + (i + 1),
                    displayName = p.Fill(seed.name),
                    description = p.Fill(seed.description),
                    interactKind = seed.kind,
                    effect = p.Fill(seed.effect),
                });
            }
            return list;
        }

        // ─────────────────────────── 流程 ───────────────────────────

        private List<FlowStep> BuildFlowSteps(
            SceneSpec scene,
            EraProfile profile,
            StoryTemplate template,
            int index,
            StoryStage stage,
            ActionSummary actions,
            StoryContinueRequest request)
        {
            var steps = new List<FlowStep>();
            string hash = Hash8(NormalizeEventName(scene.sceneId));
            string regionId = RegionIdFor(template);
            string road = MainPathName(template);
            string anchorNames = JoinAnchorNames(scene.anchors);
            NpcSpec firstNpc = scene.npcs.Count > 0 ? scene.npcs[0] : null;
            NpcSpec secondNpc = scene.npcs.Count > 1 ? scene.npcs[1] : firstNpc;
            PropInteraction firstProp = scene.props.Count > 0 ? scene.props[0] : null;

            int stepIndex = 0;
            var entry = new FlowStep
            {
                stepId = "f_" + hash + "_" + index + "_" + (stepIndex++),
                actorId = "player",
                regionId = regionId,
                description = "玩家在 (0, -24) 出生，面朝正北；沿" + road + "向北走到 (0, -8) 停下观察（行进 16 米，约 4 秒）。",
                trigger = "场景加载完成且 Phase=Playing",
                effect = "本幕「" + StoryWorld.StageLabel(stage) + "」开始，HUD 显示叙述文字",
                delaySeconds = 0f,
            };
            steps.Add(entry);

            if (actions != null && actions.HasAny)
            {
                steps.Add(new FlowStep
                {
                    stepId = "f_" + hash + "_" + index + "_" + (stepIndex++),
                    actorId = "player",
                    regionId = regionId,
                    description = "上一幕玩家" + actions.clause + "，因此本幕开始时" + actions.consequence + "（在出生点 6 米范围内表现）。",
                    trigger = "进入场景后 3 秒",
                    effect = "环境 NPC 播放一次「注意玩家」反应动画",
                    delaySeconds = 3f,
                });
            }

            if (firstNpc != null)
            {
                float npcX = firstNpc.localPosition.x;
                float npcZ = firstNpc.localPosition.z;
                steps.Add(new FlowStep
                {
                    stepId = "f_" + hash + "_" + index + "_" + (stepIndex++),
                    actorId = firstNpc.npcId,
                    regionId = regionId,
                    description = firstNpc.displayName + "（" + firstNpc.tag + "）从 " + StoryTemplates.Pos(npcX, npcZ)
                        + " 沿直线走向玩家，在 (0, -4) 停下并先开口（距离玩家 2 米）。",
                    trigger = "玩家走到 (0, -8)",
                    effect = index >= MaxScenes - 1
                        ? "触发 NPC 对话；本幕为结局，这段对话只作为收束前的最后一段交流"
                        : "触发 NPC 对话，剧情 Agent 的下一幕将引用这段对话简报",
                    delaySeconds = 0f,
                });
            }

            switch (template)
            {
                case StoryTemplate.Square:
                    steps.Add(new FlowStep
                    {
                        stepId = "f_" + hash + "_" + index + "_" + (stepIndex++),
                        actorId = secondNpc != null ? secondNpc.npcId : "player",
                        regionId = "square",
                        description = "玩家若在 (0, 0) 的地标前停留超过 20 秒，东侧铺面 (+16, 0) 走出两名" + profile.soldier
                            + "，沿 X=+8 横列向北推进到 (8, +4)，与玩家保持 6 米。",
                        trigger = "玩家在 (0, 0) 半径 4 米内停留 20 秒",
                        effect = "人群向广场中心收拢，东侧出口暂时被封锁",
                        delaySeconds = 20f,
                    });
                    break;
                case StoryTemplate.Country:
                    steps.Add(new FlowStep
                    {
                        stepId = "f_" + hash + "_" + index + "_" + (stepIndex++),
                        actorId = secondNpc != null ? secondNpc.npcId : "player",
                        regionId = "field",
                        description = "玩家若沿田埂走向 (-8, -22)，西侧麦田里的" + profile.commoner + "会放下镰刀跟上来，在 (-8, -18) 与玩家并行。",
                        trigger = "玩家进入田埂 (x∈[-10, -6])",
                        effect = "解锁乡野线索，触发一段低声谈话",
                        delaySeconds = 0f,
                    });
                    break;
                case StoryTemplate.Harbor:
                    steps.Add(new FlowStep
                    {
                        stepId = "f_" + hash + "_" + index + "_" + (stepIndex++),
                        actorId = secondNpc != null ? secondNpc.npcId : "player",
                        regionId = "harbor",
                        description = "玩家若走上中央栈桥 (0, +6)…(0, +20)，护岸 (+6, +6) 上的" + profile.messenger + "会沿护岸向东跑到 (+16, +6) 并挥手示警。",
                        trigger = "玩家踏上班桥 Z≥+6",
                        effect = "船只开始解缆，本幕进入限时窗口",
                        delaySeconds = 6f,
                    });
                    break;
                case StoryTemplate.Court:
                    steps.Add(new FlowStep
                    {
                        stepId = "f_" + hash + "_" + index + "_" + (stepIndex++),
                        actorId = secondNpc != null ? secondNpc.npcId : "player",
                        regionId = "residential",
                        description = "玩家若踏上中轴台阶走到 (0, +4)，殿门 (0, +8) 会开一条缝，一名" + profile.noble
                            + "侧身出来并在 (0, +5) 拦住玩家。",
                        trigger = "玩家到达台阶顶端 (0, +4) 半径 1.5 米",
                        effect = "触发盘问对话，玩家的行为日志被当场质问",
                        delaySeconds = 0f,
                    });
                    break;
                case StoryTemplate.Battlefield:
                    steps.Add(new FlowStep
                    {
                        stepId = "f_" + hash + "_" + index + "_" + (stepIndex++),
                        actorId = secondNpc != null ? secondNpc.npcId : "player",
                        regionId = "field",
                        description = "玩家若靠近土垒射击缺口 (+2, +11)，北侧土垒后会有一次齐射，弹着点落在 (0, +8) 到 (+6, +9) 一线。",
                        trigger = "玩家进入 (2, +11) 半径 3 米",
                        effect = "玩家受到威胁，战场噪音提高，NPC 卧倒",
                        delaySeconds = 0f,
                    });
                    break;
                case StoryTemplate.Church:
                    steps.Add(new FlowStep
                    {
                        stepId = "f_" + hash + "_" + index + "_" + (stepIndex++),
                        actorId = secondNpc != null ? secondNpc.npcId : "player",
                        regionId = "church",
                        description = "玩家若走到钟绳 (-14, +4) 旁并拉响铜钟，钟声会持续 12 秒，前庭所有人转向钟楼。",
                        trigger = "玩家与 (-14, +4) 交互",
                        effect = "全场注意转移到钟楼，守卫离开正门 8 秒",
                        delaySeconds = 12f,
                    });
                    break;
                default:
                    steps.Add(new FlowStep
                    {
                        stepId = "f_" + hash + "_" + index + "_" + (stepIndex++),
                        actorId = secondNpc != null ? secondNpc.npcId : "player",
                        regionId = "market",
                        description = "玩家若在主街 (0, 0) 停留超过 15 秒，南排摊主开始收摊，两名" + profile.soldier + "从西端拱门 (-28, 0) 向街心推进。",
                        trigger = "玩家在 (0, 0) 半径 5 米内停留 15 秒",
                        effect = "街道两侧封闭，玩家只能向东或向北离开",
                        delaySeconds = 15f,
                    });
                    break;
            }

            if (firstProp != null)
            {
                steps.Add(new FlowStep
                {
                    stepId = "f_" + hash + "_" + index + "_" + (stepIndex++),
                    actorId = "player",
                    regionId = regionId,
                    description = "玩家可对「" + firstProp.displayName + "」执行 " + firstProp.interactKind + "："
                        + firstProp.effect + "（道具落在布局描述所指的位置，由灰盒 Agent 就近摆放）。",
                    trigger = "玩家进入道具交互半径 2 米并按交互键",
                    effect = index >= MaxScenes - 1
                        ? "写入 PlayerActionRecord 并打印日志，作为结局结算的事实依据"
                        : "写入 PlayerActionRecord 并打印日志，供下一幕推理",
                    delaySeconds = 0f,
                });
            }

            bool isEndingScene = index >= MaxScenes - 1;
            string anchorStep = "玩家走向任一锚点载体（" + anchorNames + "）并踏入其外侧 1.5 米范围";
            string anchorEffect = isEndingScene
                ? "本幕为结局，踏入锚点即结算：停止推进剧情、显示 endingSummary 与 endingAchievement"
                : "按所选锚点的 timeDelta 推进游戏内时间（GameClock.AddDelta），剧情 Agent 生成下一幕";
            steps.Add(new FlowStep
            {
                stepId = "f_" + hash + "_" + index + "_" + (stepIndex++),
                actorId = "player",
                regionId = regionId,
                description = anchorStep + "。",
                trigger = "踏入锚点载体 1.5 米范围",
                effect = anchorEffect,
                delaySeconds = 0f,
            });

            return steps;
        }

        private static string ComposeFlowText(SceneSpec scene)
        {
            var sb = new StringBuilder(900);
            for (int i = 0; i < scene.flow.Count; i++)
            {
                FlowStep step = scene.flow[i];
                sb.Append(i + 1).Append(") ").Append(step.description);
                if (!string.IsNullOrEmpty(step.trigger)) sb.Append("｜触发：").Append(step.trigger);
                if (!string.IsNullOrEmpty(step.effect)) sb.Append("｜结果：").Append(step.effect);
                if (step.delaySeconds > 0f)
                    sb.Append("｜延时：").Append(step.delaySeconds.ToString("0.#", CultureInfo.InvariantCulture)).Append(" 秒");
                sb.Append('\n');
            }
            return sb.ToString().TrimEnd('\n');
        }

        // ─────────────────────────── 叙述 / 结局 / 搭建要求 ───────────────────────────

        private static string BuildNarrative(
            string eventName,
            EraProfile profile,
            SceneSpec scene,
            StoryStage stage,
            ActionSummary actions,
            StoryContinueRequest request)
        {
            var sb = new StringBuilder(500);
            sb.Append("【").Append(eventName).Append("·").Append(StoryWorld.StageLabel(stage)).Append("】");

            if (request != null && !string.IsNullOrEmpty(request.anchorName))
            {
                sb.Append("你穿过「").Append(request.anchorName).Append("」，");
                if (!string.IsNullOrEmpty(request.anchorTimeDelta)) sb.Append(request.anchorTimeDelta).Append(" 之后，");
                sb.Append("眼前换成了").Append(scene.sceneName).Append("。");
            }
            else
            {
                sb.Append("你站在").Append(scene.sceneName).Append("的入口，");
            }

            switch (stage)
            {
                case StoryStage.Opening:
                    sb.Append("地面是").Append(profile.ground).Append("，").Append(profile.landmark)
                      .Append("就在正前方 (0, 0)，").Append(profile.crowd).Append("从四面八方经过，没有人注意到你。")
                      .Append("但你知道，").Append(eventName).Append("已经开始，而你必须先弄清楚自己站在哪一边。");
                    break;
                case StoryStage.Conflict:
                    sb.Append(profile.soldier).Append("已经封住东侧路口，").Append(profile.crowd)
                      .Append("在西侧扎堆，两边隔着大约十米互相喊话。有人认出你，低声说了句什么，人群开始往你这边看。");
                    break;
                case StoryStage.Turn:
                    sb.Append("局势在这一幕被推翻了：原本守在").Append(profile.townhall)
                      .Append("的人突然撤走，另一队人从").Append(profile.market)
                      .Append("方向涌进来。你必须在他们分出敌我之前做出选择。");
                    break;
                case StoryStage.Climax:
                    sb.Append("所有线索在这里收拢，").Append(profile.landmark)
                      .Append("周围八米内挤满了人，喊声盖过一切。这一幕的每一个动作都会被写进记录。");
                    break;
                default:
                    sb.Append("人群向两端退开，只留下一条空出来的路。").Append(eventName)
                      .Append("在这里收束，你要么成为它的一部分，要么带着记忆离开。");
                    break;
            }

            if (actions != null && actions.HasAny)
            {
                sb.Append("（你刚才").Append(actions.clause).Append("，因此").Append(actions.consequence).Append("。）");
            }
            return sb.ToString();
        }

        private static string BuildEndingSummary(string eventName, EraProfile profile, SceneSpec scene, ActionSummary actions)
        {
            var sb = new StringBuilder(300);
            sb.Append("你走完了「").Append(eventName).Append("」的全程（").Append(profile.eraName).Append("）：开端、冲突、转折、高潮与结局，最后停在「")
              .Append(scene != null ? scene.sceneName : "最后的场景").Append("」。");
            if (actions != null && actions.HasAny) sb.Append("这一段路上，你").Append(actions.clause).Append("。");
            sb.Append(profile.endingTone).Append("。");
            return sb.ToString();
        }

        private static string BuildEndingAchievement(string eventName, EraProfile profile, ActionSummary actions)
        {
            string suffix = "亲历者";
            if (actions != null && actions.HasAny)
            {
                if (actions.TopType == "fire" || actions.TopType == "ignite") suffix = "点火者";
                else if (actions.TopType == "talk") suffix = "口舌之利";
                else if (actions.TopType == "take") suffix = "顺手牵羊";
                else if (actions.TopType == "collapse") suffix = "拆台的人";
                else if (actions.TopType == "damage") suffix = "带伤的人";
            }
            return "【" + profile.eraBrief + "·" + suffix + "】走完「" + eventName + "」的全部五幕";
        }

        private static string BuildBuildRequirement(EraProfile profile, SceneSpec scene)
        {
            var sb = new StringBuilder(500);
            sb.Append("按 layoutDescription 给出的坐标精确摆放（单位米，原点在场景中心，X 向东、Z 向北，出生点 (0, -24)）。")
              .Append("地面材质：").Append(profile.ground).Append("；建筑风格：").Append(profile.houseStyle)
              .Append("；整体风格标签：").Append(profile.styleTag).Append("。");
            sb.Append("必须承载：锚点 ").Append(scene.anchors.Count).Append(" 个（");
            for (int i = 0; i < scene.anchors.Count; i++)
            {
                if (i > 0) sb.Append("；");
                sb.Append(scene.anchors[i].displayName).Append("@")
                  .Append(StoryTemplates.Pos(scene.anchors[i].localPosition.x, scene.anchors[i].localPosition.z));
            }
            sb.Append("）、NPC ").Append(scene.npcs.Count).Append(" 名（");
            for (int i = 0; i < scene.npcs.Count; i++)
            {
                if (i > 0) sb.Append("；");
                sb.Append(scene.npcs[i].displayName).Append("@")
                  .Append(StoryTemplates.Pos(scene.npcs[i].localPosition.x, scene.npcs[i].localPosition.z));
            }
            sb.Append("）、可互动道具 ").Append(scene.props.Count).Append(" 件（");
            for (int i = 0; i < scene.props.Count; i++)
            {
                if (i > 0) sb.Append("、");
                sb.Append(scene.props[i].displayName);
            }
            sb.Append("）。锚点载体必须能辨认出 requirement 描述的形态，NPC 必须可被玩家辨认且不阻挡主路。")
              .Append("美术检索关键词：").Append(profile.styleHint).Append("。");
            return sb.ToString();
        }

        // ─────────────────────────── 模板选择 ───────────────────────────

        private StoryTemplate ChooseOpeningTemplate(string eventName, EraProfile profile, SeedRandom rng)
        {
            if (ContainsAny(eventName, "港", "海", "船", "码头", "贸易", "海关", "运")) return StoryTemplate.Harbor;
            if (ContainsAny(eventName, "战场", "战役", "会战", "前线", "战争", "攻城", "兵变", "起义")) return StoryTemplate.Battlefield;
            if (ContainsAny(eventName, "教堂", "宗教", "教会", "修道院", "教皇", "寺院", "祭祀")) return StoryTemplate.Church;
            if (ContainsAny(eventName, "宫廷", "加冕", "朝会", "朝廷", "议会", "登基", "宫廷政变")) return StoryTemplate.Court;
            if (ContainsAny(eventName, "集市", "市场", "商业", "市井", "商帮", "买卖")) return StoryTemplate.Market;
            if (ContainsAny(eventName, "乡村", "农村", "饥荒", "田", "农", "村庄", "庄园")) return StoryTemplate.Country;
            if (ContainsAny(eventName, "广场", "城门", "街道", "巷", "城")) return StoryTemplate.Square;

            switch (profile.era)
            {
                case StoryEra.AncientEurope: return StoryTemplate.Market;
                case StoryEra.ModernEurope: return StoryTemplate.Square;
                case StoryEra.ModernChina: return StoryTemplate.Market;
                default: return StoryTemplate.Square;
            }
        }

        private StoryTemplate ChooseNextTemplate(StoryTemplate current, StoryContinueRequest request, SeedRandom rng)
        {
            string anchorText = ((request.anchorName ?? "") + " " + (request.anchorDescription ?? "") + " "
                + (request.anchorRequirement ?? "")).Trim();

            // 锚点 description 里显式写了「抵达「XX」场景」时直接采信，保证 targetSceneId 与实际生成一致
            StoryTemplate explicitTarget;
            if (TryParseExplicitTarget(anchorText, out explicitTarget) && explicitTarget != current)
                return explicitTarget;

            if (anchorText.Length > 0)
            {
                StoryTemplate guessed = GuessTemplateFromText(anchorText, current);
                if (guessed != current) return guessed;
            }
            StoryTemplate[] transitions = Transitions(current);
            StoryTemplate pick = transitions[rng.Next(0, transitions.Length)];
            if (pick == current && transitions.Length > 1)
                pick = transitions[(Array.IndexOf(transitions, pick) + 1) % transitions.Length];
            return pick;
        }

        /// <summary>解析锚点描述里「抵达「XX」场景」的显式目标（生成器自己写下的确定信息）。</summary>
        private static bool TryParseExplicitTarget(string text, out StoryTemplate template)
        {
            template = StoryTemplate.Square;
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i <= (int)StoryTemplate.Market; i++)
            {
                StoryTemplate candidate = (StoryTemplate)i;
                string display = StoryWorld.TemplateDisplay(candidate);
                if (string.IsNullOrEmpty(display)) continue;
                if (text.IndexOf("抵达「" + display + "」", StringComparison.Ordinal) >= 0)
                {
                    template = candidate;
                    return true;
                }
            }
            return false;
        }

        private static StoryTemplate[] Transitions(StoryTemplate template)
        {
            switch (template)
            {
                case StoryTemplate.Square: return new[] { StoryTemplate.Market, StoryTemplate.Church, StoryTemplate.Country, StoryTemplate.Court, StoryTemplate.Battlefield };
                case StoryTemplate.Country: return new[] { StoryTemplate.Square, StoryTemplate.Market, StoryTemplate.Church, StoryTemplate.Court, StoryTemplate.Battlefield };
                case StoryTemplate.Harbor: return new[] { StoryTemplate.Market, StoryTemplate.Square, StoryTemplate.Court };
                case StoryTemplate.Court: return new[] { StoryTemplate.Square, StoryTemplate.Church, StoryTemplate.Country, StoryTemplate.Battlefield };
                case StoryTemplate.Battlefield: return new[] { StoryTemplate.Country, StoryTemplate.Square, StoryTemplate.Court, StoryTemplate.Church };
                case StoryTemplate.Church: return new[] { StoryTemplate.Square, StoryTemplate.Country, StoryTemplate.Court, StoryTemplate.Market };
                default: return new[] { StoryTemplate.Harbor, StoryTemplate.Square, StoryTemplate.Court, StoryTemplate.Church };
            }
        }

        private static StoryTemplate CurrentTransitionDefault(StoryTemplate current)
        {
            StoryTemplate[] transitions = Transitions(current);
            return transitions.Length > 0 ? transitions[0] : StoryTemplate.Square;
        }

        /// <summary>从任意中文文本猜模板族；猜不出时返回 fallback。</summary>
        public static StoryTemplate GuessTemplateFromText(string text, StoryTemplate fallback)
        {
            if (string.IsNullOrEmpty(text)) return fallback;
            if (ContainsAny(text, "船", "帆", "码头", "栈桥", "水面", "港口", "跳板", "缆绳")) return StoryTemplate.Harbor;
            if (ContainsAny(text, "教堂", "钟楼", "墓园", "神庙", "寺院", "礼拜", "神父", "僧人", "修女", "墓碑")) return StoryTemplate.Church;
            if (ContainsAny(text, "集市", "市场", "摊位", "摊", "市集", "南市", "东市", "街心石柱")) return StoryTemplate.Market;
            if (ContainsAny(text, "宫", "殿", "府", "衙", "朝廷", "议事", "前庭", "正堂", "廊柱", "台阶")) return StoryTemplate.Court;
            if (ContainsAny(text, "战场", "阵地", "战壕", "前线", "军帐", "炮", "弹药", "军旗")) return StoryTemplate.Battlefield;
            if (ContainsAny(text, "乡", "村", "田", "农舍", "谷仓", "麦", "庄稼", "庄园")) return StoryTemplate.Country;
            if (ContainsAny(text, "广场", "城门", "街道", "街", "巷")) return StoryTemplate.Square;
            return fallback;
        }

        // ─────────────────────────── 杂项 ───────────────────────────

        private static string NormalizeEventName(string userEvent)
        {
            if (string.IsNullOrEmpty(userEvent)) return "未命名的历史事件";
            string name = userEvent.Trim();
            return name.Length == 0 ? "未命名的历史事件" : name;
        }

        private static string Hash8(string text)
        {
            unchecked
            {
                uint hash = 2166136261u;
                for (int i = 0; i < text.Length; i++)
                {
                    hash ^= text[i];
                    hash *= 16777619u;
                }
                return hash.ToString("x8", CultureInfo.InvariantCulture);
            }
        }

        private static int SeedFor(string hash8, int index, long gameMinutes, int visitCount)
        {
            unchecked
            {
                uint h = 2166136261u;
                for (int i = 0; i < hash8.Length; i++)
                {
                    h ^= hash8[i];
                    h *= 16777619u;
                }
                h ^= (uint)index; h *= 16777619u;
                h ^= (uint)(gameMinutes & 0xFFFFFFFF); h *= 16777619u;
                h ^= (uint)visitCount; h *= 16777619u;
                // 游戏时间是「同一锚点第二次走进去要不一样」的另一路输入
                h ^= (uint)((gameMinutes >> 32) & 0xFFFFFFFF); h *= 16777619u;
                return (int)(h & 0x7FFFFFFF);
            }
        }

        private static string TemplateCode(StoryTemplate template)
        {
            switch (template)
            {
                case StoryTemplate.Square: return "a";
                case StoryTemplate.Country: return "b";
                case StoryTemplate.Harbor: return "c";
                case StoryTemplate.Court: return "d";
                case StoryTemplate.Battlefield: return "e";
                case StoryTemplate.Church: return "f";
                default: return "g";
            }
        }

        private static StoryTemplate? TemplateFromCode(char code)
        {
            switch (code)
            {
                case 'a': return StoryTemplate.Square;
                case 'b': return StoryTemplate.Country;
                case 'c': return StoryTemplate.Harbor;
                case 'd': return StoryTemplate.Court;
                case 'e': return StoryTemplate.Battlefield;
                case 'f': return StoryTemplate.Church;
                case 'g': return StoryTemplate.Market;
                default: return null;
            }
        }

        private static string RegionIdFor(StoryTemplate template)
        {
            switch (template)
            {
                case StoryTemplate.Square: return "square";
                case StoryTemplate.Country: return "field";
                case StoryTemplate.Harbor: return "harbor";
                case StoryTemplate.Court: return "residential";
                case StoryTemplate.Battlefield: return "field";
                case StoryTemplate.Church: return "church";
                default: return "market";
            }
        }

        private static string MainPathName(StoryTemplate template)
        {
            switch (template)
            {
                case StoryTemplate.Square: return "南北副路";
                case StoryTemplate.Country: return "土路与田埂";
                case StoryTemplate.Harbor: return "护岸栈道";
                case StoryTemplate.Court: return "中轴石板道";
                case StoryTemplate.Battlefield: return "交通壕";
                case StoryTemplate.Church: return "前庭小径";
                default: return "主街";
            }
        }

        /// <summary>远程场景没给坐标时，按扇形补齐；已经有坐标的保持不动。</summary>
        private static void SpreadZeroPositions(SceneSpec scene, int sceneIndex)
        {
            bool anchorsEmpty = true;
            for (int i = 0; i < scene.anchors.Count; i++)
            {
                if (scene.anchors[i] != null && scene.anchors[i].localPosition.sqrMagnitude > 0.0001f)
                {
                    anchorsEmpty = false;
                    break;
                }
            }
            if (anchorsEmpty)
            {
                int count = scene.anchors.Count;
                for (int i = 0; i < count; i++)
                {
                    if (scene.anchors[i] == null) continue;
                    float angle = (20f + (320f / Mathf.Max(1, count)) * i + sceneIndex * 11f) * Mathf.Deg2Rad;
                    float radius = 14f;
                    float x = Mathf.Round(Mathf.Cos(angle) * radius);
                    float z = Mathf.Round(Mathf.Sin(angle) * radius);
                    scene.anchors[i].localPosition = new Vector3(x, 0f, z);
                }
            }

            bool npcsEmpty = true;
            for (int i = 0; i < scene.npcs.Count; i++)
            {
                if (scene.npcs[i] != null && scene.npcs[i].localPosition.sqrMagnitude > 0.0001f)
                {
                    npcsEmpty = false;
                    break;
                }
            }
            if (npcsEmpty)
            {
                List<Vector3> positions = FanPositions(sceneIndex, Math.Max(1, scene.npcs.Count));
                for (int i = 0; i < scene.npcs.Count && i < positions.Count; i++)
                {
                    if (scene.npcs[i] == null) continue;
                    scene.npcs[i].localPosition = positions[i];
                }
            }
        }

        private static bool IsValidInteractKind(string kind)
        {
            return kind == "ignite" || kind == "board" || kind == "collapse" || kind == "take" || kind == "examine";
        }

        private static bool ContainsAny(string text, params string[] keywords)
        {
            if (string.IsNullOrEmpty(text) || keywords == null) return false;
            for (int i = 0; i < keywords.Length; i++)
            {
                if (!string.IsNullOrEmpty(keywords[i]) &&
                    text.IndexOf(keywords[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private static string JoinAnchorNames(List<SceneAnchorSpec> anchors)
        {
            if (anchors == null || anchors.Count == 0) return "无";
            var sb = new StringBuilder(120);
            for (int i = 0; i < anchors.Count; i++)
            {
                if (i > 0) sb.Append("、");
                sb.Append(anchors[i].displayName);
            }
            return sb.ToString();
        }

        // ─────────────────────────── 行为日志摘要 ───────────────────────────

        /// <summary>把玩家最近行为压成一句可读的话，用于叙述与流程。</summary>
        private sealed class ActionSummary
        {
            public bool HasAny;
            public int Count;
            public string TopType = "";
            public string clause = "";
            public string consequence = "";

            public static ActionSummary From(List<PlayerActionRecord> records)
            {
                var summary = new ActionSummary();
                if (records == null || records.Count == 0) return summary;

                var verbs = new List<string>();
                var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                int start = Math.Max(0, records.Count - 6);
                for (int i = start; i < records.Count; i++)
                {
                    PlayerActionRecord record = records[i];
                    if (record == null) continue;
                    string verb = VerbOf(record.actionType);
                    string detail = record.detail;
                    if (string.IsNullOrEmpty(detail)) detail = record.targetId;
                    string text = string.IsNullOrEmpty(detail) ? verb : verb + "「" + Shorten(detail, 18) + "」";
                    if (!verbs.Contains(text)) verbs.Add(text);
                    string type = record.actionType ?? "";
                    int count;
                    counts.TryGetValue(type, out count);
                    counts[type] = count + 1;
                }
                if (verbs.Count == 0) return summary;

                summary.HasAny = true;
                summary.Count = records.Count;
                int limit = Math.Min(3, verbs.Count);
                for (int i = 0; i < limit; i++)
                {
                    if (i > 0) summary.clause += "、";
                    summary.clause += verbs[i];
                }

                int best = -1;
                foreach (KeyValuePair<string, int> pair in counts)
                {
                    if (pair.Value > best)
                    {
                        best = pair.Value;
                        summary.TopType = pair.Key;
                    }
                }
                summary.consequence = ConsequenceOf(summary.TopType);
                return summary;
            }

            private static string VerbOf(string actionType)
            {
                switch (actionType)
                {
                    case "fire": return "开了枪";
                    case "talk": return "与人交谈";
                    case "enter_anchor": return "穿过传送锚点";
                    case "ignite": return "点燃了火";
                    case "collapse": return "推倒了结构";
                    case "take": return "拿走了东西";
                    case "damage": return "受了伤";
                    case "mount": return "登上载具";
                    case "examine": return "查看了物件";
                    case "move": return "在街上走动";
                    case "interact": return "动了手边的东西";
                    case "attack": return "动了手";
                    case "shoot": return "开了枪";
                    case "kill": return "击倒了人";
                    case "pickup": return "拾起了东西";
                    case "climb": return "攀爬过障碍";
                    case "open": return "打开了门";
                    case "use": return "使用了物件";
                    default:
                        if (string.IsNullOrEmpty(actionType)) return "行动";
                        // 未知类型不要直接把英文标识塞进玩家可见文本
                        return IsAsciiWord(actionType) ? "有所动作" : actionType;
                }
            }

            private static bool IsAsciiWord(string text)
            {
                if (string.IsNullOrEmpty(text)) return false;
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_'))
                        return false;
                }
                return true;
            }

            private static string ConsequenceOf(string type)
            {
                switch (type)
                {
                    case "fire": return "枪声把周围的人都惊动了，有人朝你这边看过来";
                    case "ignite": return "火光与烟让半条街的人都注意到了你";
                    case "talk": return "你刚才的对话已经传开，有人开始躲着你的目光";
                    case "take": return "丢了东西的人在四处打听，眼神在人群里扫";
                    case "collapse": return "倒塌的声音引来了维持秩序的人";
                    case "damage": return "你身上的伤让旁人不敢靠得太近";
                    case "mount": return "载具的声音暴露了你的位置";
                    case "enter_anchor": return "你出现的时机太巧，被人记住了";
                    default: return "你的举动被角落里的人记了下来";
                }
            }

            private static string Shorten(string text, int max)
            {
                if (string.IsNullOrEmpty(text)) return "";
                string s = text.Replace("\r", " ").Replace("\n", " ").Trim();
                return s.Length <= max ? s : s.Substring(0, max) + "…";
            }
        }
    }
}
