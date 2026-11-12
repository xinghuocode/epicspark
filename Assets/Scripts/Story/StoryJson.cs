// 把模型返回的 JSON 解析成 StoryTurn（JsonUtility 不支持顶层数组，所以统一要求 {"narrative":..,"scenes":[..]}）。
// 解析失败返回 null + error 文本，调用方整体回退到确定性兜底。
using System;
using System.Collections.Generic;
using EpicSpark.AI.OpenAI;
using EpicSpark.Core;
using UnityEngine;

#pragma warning disable 0649

namespace EpicSpark.Story
{
    [Serializable]
    internal class StoryTurnDto
    {
        public string turnId;
        public string narrative;
        public StorySceneDto[] scenes;
        public int activeSceneIndex;
        public bool gameOver;
        public string endingSummary;
        public string endingAchievement;
    }

    [Serializable]
    internal class StorySceneDto
    {
        public string sceneId;
        public string sceneName;
        public string era;
        public string region;
        public string layoutDescription;
        public string flowDescription;
        public string buildRequirement;
        public float width;
        public float length;
        /// <summary>剧情指定的起始年；公元前为负数，0 表示未知。</summary>
        public int startYear;
        public StoryAnchorDto[] anchors;
        public StoryNpcDto[] npcs;
        public StoryPropDto[] props;
        public StoryFlowDto[] flow;
        /// <summary>结构化布局（可选但强烈建议）：灰盒的直接施工图。</summary>
        public StoryLayoutDto layoutSpec;
    }

    [Serializable]
    internal class StoryAnchorDto
    {
        public string anchorId;
        public string displayName;
        public string description;
        public string requirement;
        public string modelHint;
        public string timeDelta;
        public string targetSceneId;
        public string targetSceneName;
        public string triggerKind;
        public string triggerEvent;
        /// <summary>锚点载体的世界坐标（米）。</summary>
        public float x;
        public float z;
    }

    [Serializable]
    internal class StoryNpcDto
    {
        public string npcId;
        public string displayName;
        public string tag;
        public string persona;
        public string storyRole;
        public string heldTool;
        /// <summary>NPC 出场坐标（米）。</summary>
        public float x;
        public float z;
    }

    [Serializable]
    internal class StoryPropDto
    {
        public string propId;
        public string displayName;
        public string description;
        public string interactKind;
        public string effect;
        /// <summary>道具坐标（米）。</summary>
        public float x;
        public float z;
    }

    [Serializable]
    internal class StoryFlowDto
    {
        public string stepId;
        public string actorId;
        public string regionId;
        public string description;
        public string trigger;
        public string effect;
        public float delaySeconds;
    }

    [Serializable]
    internal class StoryEnvelopeDto
    {
        public StoryTurnDto story;
        public StoryTurnDto turn;
        public StoryTurnDto result;
        public StoryTurnDto data;
    }

    /// <summary>模型输出 → StoryTurn 的解析与清洗。</summary>
    public static class StoryJson
    {
        /// <summary>最近一次成功解析是否经过 JsonRepair；用于内容来源追踪，不参与剧情数据本身。</summary>
        public static bool LastUsedRepair { get; private set; }

        public static StoryTurn ParseTurn(string raw, out string error)
        {
            error = "";
            LastUsedRepair = false;
            try
            {
                string json = OpenAIText.ExtractJsonObject(raw);
                if (string.IsNullOrEmpty(json) && !string.IsNullOrEmpty(raw) && raw.IndexOf('{') >= 0)
                {
                    // 括号不平衡时 ExtractJsonObject 会返回 null；先把原文交给修复器，
                    // 不能直接以“没有 JSON 对象”为由丢弃已经接近完整的剧情。
                    json = JsonRepair.Repair(raw);
                    if (!string.IsNullOrEmpty(json))
                    {
                        LastUsedRepair = true;
                        Debug.LogWarning("[Story] 原始输出括号未闭合，已先修复再提取剧情 JSON。");
                    }
                }
                if (string.IsNullOrEmpty(json))
                {
                    error = "模型输出中没有可修复的 JSON 对象";
                    return null;
                }

                StoryTurnDto dto = null;
                string firstError = "";
                try { dto = JsonUtility.FromJson<StoryTurnDto>(json); }
                catch (Exception e) { firstError = e.Message; }

                // 模型偶尔吐坏 JSON（多逗号/截断/少括号）：先尝试救援，别整幕退回兜底
                if (dto == null || dto.scenes == null || dto.scenes.Length == 0)
                {
                    string repaired = JsonRepair.Repair(json);
                    if (!string.IsNullOrEmpty(repaired) && !string.Equals(repaired, json, StringComparison.Ordinal))
                    {
                        try { dto = JsonUtility.FromJson<StoryTurnDto>(repaired); }
                        catch (Exception) { dto = null; }
                        if (dto != null && dto.scenes != null && dto.scenes.Length > 0)
                        {
                            LastUsedRepair = true;
                            Debug.LogWarning("[Story] 模型 JSON 有残缺，已自动修复并解析成功。");
                        }
                    }
                }
                if (dto == null && !string.IsNullOrEmpty(firstError))
                {
                    error = "JSON 解析失败: " + firstError;
                    return null;
                }

                // 兼容模型把结果包在 {"story":{...}} / {"turn":{...}} 里
                if (dto == null || dto.scenes == null || dto.scenes.Length == 0)
                {
                    try
                    {
                        StoryEnvelopeDto envelope = JsonUtility.FromJson<StoryEnvelopeDto>(json);
                        if (envelope != null)
                        {
                            if (envelope.story != null && envelope.story.scenes != null) dto = envelope.story;
                            else if (envelope.turn != null && envelope.turn.scenes != null) dto = envelope.turn;
                            else if (envelope.result != null && envelope.result.scenes != null) dto = envelope.result;
                            else if (envelope.data != null && envelope.data.scenes != null) dto = envelope.data;
                        }
                    }
                    catch (Exception)
                    {
                        // 忽略，按顶层失败处理
                    }
                }

                if (dto == null)
                {
                    error = "JSON 结构不是剧情回合对象";
                    return null;
                }
                if (dto.scenes == null || dto.scenes.Length == 0)
                {
                    error = "模型没有返回任何场景";
                    return null;
                }

                StoryTurn turn = Convert(dto);
                if (turn == null || turn.scenes.Count == 0)
                {
                    error = "没有可用的场景（布局/锚点信息缺失）";
                    return null;
                }
                return turn;
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
                return null;
            }
        }

        private static StoryTurn Convert(StoryTurnDto dto)
        {
            var turn = new StoryTurn
            {
                turnId = Clean(dto.turnId, 80),
                narrative = Clean(dto.narrative, 2000),
                activeSceneIndex = 0,
                gameOver = dto.gameOver,
                endingSummary = Clean(dto.endingSummary, 1200),
                endingAchievement = Clean(dto.endingAchievement, 160),
            };

            int sceneCount = Math.Min(dto.scenes.Length, 3);
            for (int i = 0; i < sceneCount; i++)
            {
                SceneSpec scene = ConvertScene(dto.scenes[i]);
                if (scene == null) continue;
                turn.scenes.Add(scene);
                if (turn.scenes.Count >= 3) break;
            }
            return turn;
        }

        private static SceneSpec ConvertScene(StorySceneDto dto)
        {
            if (dto == null) return null;
            string layout = Clean(dto.layoutDescription, 8000);
            string flow = Clean(dto.flowDescription, 8000);
            string name = Clean(dto.sceneName, 120);
            // 布局与流程都没有的场景无法复现，直接丢弃
            if (string.IsNullOrEmpty(layout) && string.IsNullOrEmpty(flow)) return null;

            var scene = new SceneSpec
            {
                sceneId = Clean(dto.sceneId, 64),
                sceneName = string.IsNullOrEmpty(name) ? "未命名场景" : name,
                era = Clean(dto.era, 80),
                region = Clean(dto.region, 80),
                layoutDescription = layout,
                flowDescription = flow,
                buildRequirement = Clean(dto.buildRequirement, 2000),
                width = SceneSize.SideOrDefault(dto.width),
                length = SceneSize.SideOrDefault(dto.length),
                startYear = dto.startYear,
                spawnPoint = new Vector3(0f, 1.2f, -24f),
                isReused = false,
            };

            if (dto.anchors != null)
            {
                for (int i = 0; i < dto.anchors.Length && scene.anchors.Count < 8; i++)
                {
                    SceneAnchorSpec anchor = ConvertAnchor(dto.anchors[i]);
                    if (anchor != null) scene.anchors.Add(anchor);
                }
            }

            if (dto.npcs != null)
            {
                for (int i = 0; i < dto.npcs.Length && scene.npcs.Count < 8; i++)
                {
                    NpcSpec npc = ConvertNpc(dto.npcs[i]);
                    if (npc != null) scene.npcs.Add(npc);
                }
            }

            if (dto.props != null)
            {
                for (int i = 0; i < dto.props.Length && scene.props.Count < 8; i++)
                {
                    PropInteraction prop = ConvertProp(dto.props[i]);
                    if (prop != null) scene.props.Add(prop);
                }
            }

            if (dto.flow != null)
            {
                for (int i = 0; i < dto.flow.Length && scene.flow.Count < 12; i++)
                {
                    FlowStep step = ConvertFlow(dto.flow[i]);
                    if (step != null) scene.flow.Add(step);
                }
            }

            // 结构化布局：剧情 Agent 直接给出的施工图。解析不出内容时为 null，
            // 上层（StoryLayoutSpecBuilder）会退回「散文 → 编译器」路径。
            scene.layoutSpec = StoryLayoutSpecJson.Convert(dto.layoutSpec, scene);
            return scene;
        }

        private static SceneAnchorSpec ConvertAnchor(StoryAnchorDto dto)
        {
            if (dto == null) return null;
            string name = Clean(dto.displayName, 120);
            string requirement = Clean(dto.requirement, 240);
            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(requirement)) return null;

            var anchor = new SceneAnchorSpec
            {
                anchorId = Clean(dto.anchorId, 64),
                displayName = string.IsNullOrEmpty(name) ? "传送锚点" : name,
                description = Clean(dto.description, 800),
                requirement = string.IsNullOrEmpty(requirement) ? "必须有明确的场景载体供玩家走入" : requirement,
                modelHint = Clean(dto.modelHint, 160),
                timeDelta = Clean(dto.timeDelta, 16),
                targetSceneId = Clean(dto.targetSceneId, 64),
                targetSceneName = Clean(dto.targetSceneName, 120),
                triggerKind = ParseTriggerKind(dto.triggerKind),
                triggerEvent = Clean(dto.triggerEvent, 240),
            };
            if (dto.x != 0f || dto.z != 0f) anchor.localPosition = new Vector3(dto.x, 0f, dto.z);
            if (string.IsNullOrEmpty(anchor.timeDelta) || !GameClock.TryParseDelta(anchor.timeDelta, out _))
                anchor.timeDelta = "30min";
            return anchor;
        }

        private static NpcSpec ConvertNpc(StoryNpcDto dto)
        {
            if (dto == null) return null;
            string name = Clean(dto.displayName, 80);
            string tag = Clean(dto.tag, 40);
            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(tag)) return null;

            return new NpcSpec
            {
                npcId = Clean(dto.npcId, 64),
                displayName = string.IsNullOrEmpty(name) ? "无名者" : name,
                tag = string.IsNullOrEmpty(tag) ? "平民" : tag,
                persona = Clean(dto.persona, 600),
                storyRole = Clean(dto.storyRole, 200),
                heldTool = ParseToolKind(dto.heldTool),
                maxHp = 100f,
                localPosition = dto.x != 0f || dto.z != 0f ? new Vector3(dto.x, 0f, dto.z) : Vector3.zero,
            };
        }

        private static PropInteraction ConvertProp(StoryPropDto dto)
        {
            if (dto == null) return null;
            string name = Clean(dto.displayName, 120);
            if (string.IsNullOrEmpty(name)) return null;
            string kind = (dto.interactKind ?? "").Trim().ToLowerInvariant();
            if (kind != "ignite" && kind != "board" && kind != "collapse" && kind != "take" && kind != "examine")
                kind = "examine";
            return new PropInteraction
            {
                propId = Clean(dto.propId, 64),
                displayName = name,
                description = Clean(dto.description, 500),
                interactKind = kind,
                effect = Clean(dto.effect, 500),
                localPosition = dto.x != 0f || dto.z != 0f ? new Vector3(dto.x, 0f, dto.z) : Vector3.zero,
            };
        }

        private static FlowStep ConvertFlow(StoryFlowDto dto)
        {
            if (dto == null) return null;
            string description = Clean(dto.description, 600);
            if (string.IsNullOrEmpty(description)) return null;
            return new FlowStep
            {
                stepId = Clean(dto.stepId, 64),
                actorId = Clean(dto.actorId, 64),
                regionId = Clean(dto.regionId, 40),
                description = description,
                trigger = Clean(dto.trigger, 300),
                effect = Clean(dto.effect, 300),
                delaySeconds = dto.delaySeconds >= 0f && dto.delaySeconds < 600f ? dto.delaySeconds : 0f,
            };
        }

        private static AnchorTriggerKind ParseTriggerKind(string text)
        {
            if (string.IsNullOrEmpty(text)) return AnchorTriggerKind.Placed;
            string value = text.Trim().ToLowerInvariant();
            return value == "event" || value == "1" ? AnchorTriggerKind.Event : AnchorTriggerKind.Placed;
        }

        private static ToolKind ParseToolKind(string text)
        {
            if (string.IsNullOrEmpty(text)) return ToolKind.None;
            string value = text.Trim().ToLowerInvariant();
            if (value == "gun" || value == "1" || value == "枪") return ToolKind.Gun;
            if (value == "satchel" || value == "2" || value == "挎包") return ToolKind.Satchel;
            return ToolKind.None;
        }

        private static string Clean(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string value = text.Trim();
            if (max > 0 && value.Length > max) value = value.Substring(0, max);
            return value;
        }
    }
}

#pragma warning restore 0649
