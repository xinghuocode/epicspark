// 真实灰盒入口验收：SceneSpec → GrayboxAgent.PlanAsync → 工具灰盒 → 纯素模 BuildResult。
// Unity.exe -batchmode -quit -projectPath <项目> -executeMethod EpicSpark.EditorTools.SceneGenProbe.Run -logFile -
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using EpicSpark.Anchors;
using EpicSpark.Core;
using EpicSpark.Graybox;
using EpicSpark.Loading;
using EpicSpark.SceneGen;
using EpicSpark.Story;
using UnityEditor;
using UnityEngine;

namespace EpicSpark.EditorTools
{
    public static class SceneGenProbe
    {
        private const string OutputDir = "Logs/scenegen";

        public static void Run()
        {
            int exitCode = 0;
            try { exitCode = Execute(); }
            catch (Exception e) { Debug.LogError("[SceneGenProbe] 异常: " + e); exitCode = 1; }
            if (Application.isBatchMode) EditorApplication.Exit(exitCode);
        }

        private static int Execute()
        {
            var failures = new List<string>();
            var report = new StringBuilder(4096);
            report.AppendLine("=== 工具灰盒 + 强制素模：真实 GrayboxAgent 入口验收 ===");

            SceneSpec scene = BuildProbeScene();
            string inputBefore = JsonUtility.ToJson(scene);
            GrayboxPlan first = Plan(scene, 24680);
            Check(failures, first != null, "综合场景通过真实 GrayboxAgent 生成");
            if (first != null)
            {
                GrayboxPlan second = Plan(BuildProbeScene(), 24680);
                Check(failures, second != null && JsonUtility.ToJson(first) == JsonUtility.ToJson(second), "同一输入 byte-equal");
                Check(failures, JsonUtility.ToJson(scene) == inputBefore, "SceneSpec 与原始 LayoutSpec 均不被修改");
                Check(failures, first.layoutSpec != null && first.layoutSpec.source == "graybox-tools", "灰盒来自工具工作区");
                Check(failures, first.segmentCount == 1, "素模一次完整落地");
                ValidateGeometry(first, failures);
                ValidatePrimitiveBuild(scene, first, failures);
                Check(failures, HasPrimitive(first, "fortress") && HasPrimitive(first, "church") && HasPrimitive(first, "gate"),
                    "真实 GrayboxAgent 不得丢失要塞/教堂/城门或 anchorFor 语义");
                Check(failures, CountRowGroups(first) >= 3, "只有明确城市输入才要求多组连续街屋");
                ValidateAssembled(scene, first, failures);
                report.AppendLine("综合场景: " + first.plannerNote);
                report.AppendLine("分区=" + first.regions.Count + " 路径=" + first.paths.Count + " 槽位=" + first.slots.Count);
            }

            SceneSpec description = BuildDescriptionOnlyScene();
            string descriptionBefore = JsonUtility.ToJson(description);
            GrayboxPlan described = Plan(description, 13579);
            Check(failures, described != null, "只有 layoutDescription、无 layoutSpec 也可生成");
            if (described != null)
            {
                Check(failures, description.layoutSpec == null, "探针没有预先放入 layoutSpec");
                Check(failures, JsonUtility.ToJson(description) == descriptionBefore, "自然语言入口不改写剧情输入");
                ValidateGeometry(described, failures);
                ValidatePrimitiveBuild(description, described, failures);
                LayoutItemSpec anchor = FindAnchor(described.layoutSpec, "anchor_hall");
                Check(failures, anchor != null && Vector2.Distance(anchor.center, new Vector2(28f, 14f)) < 0.01f,
                    "合法剧情锚点保持 (28,14)，不搬入建筑中心");
                Check(failures, HasPrimitive(described, "hall"), "详细描述中的市政厅载体存在");
                Check(failures, CountRowGroups(described) >= 2, "自然语言居民区两侧均有连续排屋");
                ValidateAssembled(description, described, failures);
                report.AppendLine("纯描述场景: " + described.plannerNote);
            }

            GrayboxPlan diagonal = Plan(BuildDiagonalScene(), 11224);
            Check(failures, diagonal != null, "斜向街道通过真实 GrayboxAgent 与排屋工具生成");
            if (diagonal != null)
            {
                int diagonalUnits = 0;
                for (int i = 0; i < diagonal.layoutSpec.items.Count; i++)
                    if (diagonal.layoutSpec.items[i].source == "graybox-tool-diagonal-row") diagonalUnits++;
                Check(failures, diagonalUnits >= 12, "斜街实际生成连续几何排屋");
                Check(failures, CountRowGroups(diagonal) >= 2, "斜街两侧各有一组连续排屋");
                ValidateGeometry(diagonal, failures);
                ValidatePrimitiveBuild(BuildDiagonalScene(), diagonal, failures);
                ValidateAssembled(BuildDiagonalScene(), diagonal, failures);
                report.AppendLine("斜向场景: " + diagonal.plannerNote + " · 斜向单元 " + diagonalUnits);
            }

            SceneSpec custom = ParseCustomSizeScene(36f, 52f);
            SceneSpec large = ParseCustomSizeScene(800f, 1200f);
            Check(failures, custom != null && Mathf.Abs(custom.width - 36f) < 0.001f && Mathf.Abs(custom.length - 52f) < 0.001f,
                "剧情 JSON 保留 36×52 米自定义尺寸");
            Check(failures, large != null && Mathf.Abs(large.width - 800f) < 0.001f && Mathf.Abs(large.length - 1200f) < 0.001f,
                "剧情 JSON 保留超过旧上限的 800×1200 米尺寸");
            GrayboxPlan customPlan = custom != null ? Plan(custom, 36052) : null;
            Check(failures, customPlan != null && customPlan.width == 36f && customPlan.length == 52f,
                "剧情尺寸经 GrayboxAgent 原样进入灰盒");
            if (customPlan != null)
            {
                ValidateGeometry(customPlan, failures);
                ValidatePrimitiveBuild(custom, customPlan, failures);
                ValidateAssembled(custom, customPlan, failures);
                report.AppendLine("自定义尺寸场景: " + customPlan.width + "×" + customPlan.length + " 米");
            }

            Directory.CreateDirectory(OutputDir);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            report.AppendLine();
            report.AppendLine(failures.Count == 0 ? "结论: PASS" : "结论: FAIL");
            for (int i = 0; i < failures.Count; i++) report.AppendLine("  FAIL " + failures[i]);
            string reportPath = Path.Combine(OutputDir, "report_" + stamp + ".txt");
            File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
            if (first != null)
                File.WriteAllText(Path.Combine(OutputDir, "graybox_" + stamp + ".json"), JsonUtility.ToJson(first, true), new UTF8Encoding(false));
            Debug.Log(report.ToString());
            Debug.Log("[SceneGenProbe] 报告写入 " + reportPath);
            return failures.Count == 0 ? 0 : 2;
        }

        private static GrayboxPlan Plan(SceneSpec scene, int seed)
        {
            var agent = new GrayboxAgent();
            return agent.PlanAsync(new GrayboxRequest { scene = scene, seed = seed }, CancellationToken.None).GetAwaiter().GetResult();
        }

        private static void ValidatePrimitiveBuild(SceneSpec scene, GrayboxPlan plan, List<string> failures)
        {
            BuildResult build = SceneGenPipeline.PlaceAll(scene, plan);
            Check(failures, build.primitiveMode, "BuildResult 为素模模式");
            int expected = 0;
            bool allPrimitive = true;
            for (int i = 0; i < plan.slots.Count; i++)
                if (plan.slots[i].kind != SlotKind.Anchor && plan.slots[i].kind != SlotKind.Npc) expected++;
            for (int i = 0; i < build.placed.Count; i++)
                allPrimitive &= build.placed[i].modelId.StartsWith("prim:", StringComparison.Ordinal)
                                && !string.IsNullOrEmpty(build.placed[i].primitive);
            Check(failures, build.placed.Count == expected, "搭建覆盖全部非 NPC/锚点槽位");
            Check(failures, allPrimitive, "所有输出均为 prim:*，美术库/GLB/资产生成未参与");
        }

        private static void ValidateAssembled(SceneSpec scene, GrayboxPlan plan, List<string> failures)
        {
            SceneInstance instance = null;
            try
            {
                BuildResult build = SceneGenPipeline.PlaceAll(scene, plan);
                instance = SceneAssembler.AssembleAsync(scene, plan, build, null, CancellationToken.None).GetAwaiter().GetResult();
                Check(failures, instance != null && instance.plan == plan, "装配实例保留权威灰盒计划");
                Check(failures, Mathf.Abs(instance.width - plan.width) < 0.001f
                    && Mathf.Abs(instance.length - plan.length) < 0.001f, "装配实例完整保留剧情自定义尺寸");
                Check(failures, instance.models.Count == build.placed.Count, "PrimitiveKit 实际模型数量与搭建结果一致");
                for (int i = 0; i < plan.slots.Count; i++)
                {
                    GrayboxSlot slot = plan.slots[i];
                    if (slot.kind != SlotKind.Anchor) continue;
                    GameObject actual = null;
                    for (int a = 0; a < instance.anchorObjects.Count; a++)
                    {
                        AnchorPoint point = instance.anchorObjects[a].GetComponent<AnchorPoint>();
                        if (point != null && point.Spec != null && point.Spec.anchorId == slot.bindId)
                        {
                            actual = instance.anchorObjects[a];
                            break;
                        }
                    }
                    Check(failures, actual != null, "装配后存在锚点 " + slot.bindId);
                    if (actual != null)
                        Check(failures, Vector3.Distance(actual.transform.position,
                            new Vector3(slot.position.x, 0.05f, slot.position.y)) < 0.001f,
                            "装配不得二次移动边缘锚点 " + slot.bindId);
                }
                ValidateDiagonalBodyBounds(instance, plan, failures);
            }
            finally
            {
                if (instance != null && instance.root != null) UnityEngine.Object.DestroyImmediate(instance.root);
            }
        }

        private static void ValidateDiagonalBodyBounds(SceneInstance instance, GrayboxPlan plan, List<string> failures)
        {
            for (int i = 0; i < plan.layoutSpec.items.Count; i++)
            {
                LayoutItemSpec item = plan.layoutSpec.items[i];
                if (item.source != "graybox-tool-diagonal-row") continue;
                ModelView view = null;
                for (int m = 0; m < instance.models.Count; m++)
                    if (instance.models[m].slotId == "slot_" + item.id) { view = instance.models[m]; break; }
                Check(failures, view != null && view.gameObject != null, "斜向几何体已装配 " + item.id);
                if (view == null || view.gameObject == null) continue;
                MeshRenderer body = view.gameObject.GetComponentInChildren<MeshRenderer>();
                Check(failures, body != null, "斜向几何体存在主体 Renderer " + item.id);
                if (body == null) continue;
                Vector3 actual = body.bounds.size;
                Check(failures, Mathf.Abs(actual.x - item.size.x) < 0.25f && Mathf.Abs(actual.z - item.size.y) < 0.25f,
                    "斜向主体世界 Bounds 必须匹配灰盒 AABB " + item.id);
            }
        }

        private static void ValidateGeometry(GrayboxPlan plan, List<string> failures)
        {
            var items = plan.layoutSpec.items;
            Rect site = SceneGenRect.FromCenter(Vector2.zero, plan.width, plan.length);
            for (int i = 0; i < items.Count; i++)
            {
                LayoutItemSpec a = items[i];
                Rect ra = SceneGenRect.FromCenter(a.center, a.size.x, a.size.y);
                Check(failures, Contains(site, ra), a.name + " 不得出界");
                if (a.kind == SlotKind.Anchor)
                {
                    for (int j = 0; j < items.Count; j++)
                        if (items[j].kind == SlotKind.Building)
                            Check(failures, SceneGenRect.OverlapArea(ra, SceneGenRect.FromCenter(items[j].center, items[j].size.x, items[j].size.y)) <= 0.05f,
                                "锚点不得埋入建筑：" + a.name);
                }
                if (a.kind != SlotKind.Building) continue;
                for (int j = i + 1; j < items.Count; j++)
                {
                    LayoutItemSpec b = items[j];
                    if (b.kind != SlotKind.Building) continue;
                    Check(failures, !SceneGenOBB.Overlaps(a, b, 0.05f), "建筑不得重叠：" + a.name + " × " + b.name);
                }
            }

            var rows = new Dictionary<string, List<LayoutItemSpec>>();
            for (int i = 0; i < items.Count; i++)
            {
                if (string.IsNullOrEmpty(items[i].rowId)) continue;
                if (!rows.ContainsKey(items[i].rowId)) rows[items[i].rowId] = new List<LayoutItemSpec>();
                rows[items[i].rowId].Add(items[i]);
            }
            foreach (KeyValuePair<string, List<LayoutItemSpec>> pair in rows)
            {
                pair.Value.Sort((a, b) => a.rowIndex.CompareTo(b.rowIndex));
                for (int i = 0; i + 1 < pair.Value.Count; i++)
                {
                    LayoutItemSpec a = pair.Value[i], b = pair.Value[i + 1];
                    Vector2 delta = b.center - a.center;
                    float actual = delta.magnitude;
                    Vector2 direction = actual > 0.001f ? delta / actual : Vector2.right;
                    float expected = SceneGenOBB.ProjectedHalfWidth(a, direction)
                                     + SceneGenOBB.ProjectedHalfWidth(b, direction);
                    float gap = actual - expected;
                    Check(failures, gap >= -0.05f && gap <= 0.2f, pair.Key + " 连续且不相压");
                }
            }
        }

        private static bool Contains(Rect outer, Rect inner)
        {
            return inner.xMin >= outer.xMin - 0.05f && inner.xMax <= outer.xMax + 0.05f
                   && inner.yMin >= outer.yMin - 0.05f && inner.yMax <= outer.yMax + 0.05f;
        }

        private static void Check(List<string> failures, bool condition, string message)
        {
            if (!condition) failures.Add(message);
        }

        private static LayoutItemSpec FindAnchor(LayoutSpec spec, string bindId)
        {
            for (int i = 0; i < spec.items.Count; i++)
                if (spec.items[i].kind == SlotKind.Anchor && spec.items[i].bindId == bindId) return spec.items[i];
            return null;
        }

        private static int CountRowGroups(GrayboxPlan plan)
        {
            var rows = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < plan.layoutSpec.items.Count; i++)
                if (!string.IsNullOrEmpty(plan.layoutSpec.items[i].rowId)) rows.Add(plan.layoutSpec.items[i].rowId);
            return rows.Count;
        }

        private static bool HasPrimitive(GrayboxPlan plan, string primitive)
        {
            for (int i = 0; i < plan.slots.Count; i++) if (plan.slots[i].primitive == primitive) return true;
            return false;
        }

        private static SceneSpec BuildDescriptionOnlyScene()
        {
            var scene = new SceneSpec
            {
                sceneId = "description_only_editor",
                sceneName = "市政厅前广场",
                width = 120f,
                length = 120f,
                spawnPoint = new Vector3(0f, 1.2f, -24f),
                layoutDescription = "【地面分区】中央广场，粗凿石板，有绿化、路灯和抗议人群，矩形(-45,-40)—(45,40)；"
                                    + "南侧欧洲古典居民区，街边房屋连成一排，矩形(-45,-60)—(45,-40)。\n"
                                    + "【道路】主街，宽8米，从(0,-58)到(0,38)。\n"
                                    + "【建筑与地标】市政厅：中心(28,22)，占地16×12米，高12米，正立面朝南。",
                buildRequirement = "广场保留集会和通行空间，居民区沿主街连续排屋。",
            };
            scene.anchors.Add(new SceneAnchorSpec
            {
                anchorId = "anchor_hall",
                displayName = "市政厅正门",
                requirement = "必须是官署入口",
                localPosition = new Vector3(28f, 0f, 14f),
            });
            return scene;
        }

        private static SceneSpec ParseCustomSizeScene(float width, float length)
        {
            const string template = "{\"narrative\":\"这是一段用于验证自定义场景尺寸的完整剧情叙述，玩家需要找到庭院中的出口。\","
                + "\"scenes\":[{\"sceneName\":\"自定义庭院\",\"era\":\"\",\"region\":\"\","
                + "\"layoutDescription\":\"一座小型庭院，中央有一处石标，地面由石板铺成，四周保留完整通道，玩家可以清楚辨认出口方向。\","
                + "\"flowDescription\":\"玩家观察石标并走向出口。\",\"buildRequirement\":\"保持庭院开敞。\","
                + "\"width\":__WIDTH__,\"length\":__LENGTH__,\"anchors\":[{\"displayName\":\"庭院出口\","
                + "\"description\":\"离开庭院\",\"requirement\":\"出口\",\"timeDelta\":\"30min\",\"x\":0,\"z\":-10}],"
                + "\"npcs\":[],\"props\":[],\"flow\":[]}]}";
            string raw = template.Replace("__WIDTH__", width.ToString("R", CultureInfo.InvariantCulture))
                .Replace("__LENGTH__", length.ToString("R", CultureInfo.InvariantCulture));
            string error;
            StoryTurn turn = StoryJson.ParseTurn(raw, out error);
            if (turn == null || turn.scenes == null || turn.scenes.Count == 0)
            {
                Debug.LogWarning("[SceneGenProbe] 自定义尺寸剧情解析失败: " + error);
                return null;
            }
            return turn.scenes[0];
        }

        private static SceneSpec BuildDiagonalScene()
        {
            var scene = new SceneSpec { sceneId = "diagonal_editor", sceneName = "斜街居住区", width = 140f, length = 140f };
            var spec = new LayoutSpec { sceneId = scene.sceneId };
            spec.zones.Add(Zone("斜街居住区", "residential", -65, -65, 65, 65, 0.15f, "夯土"));
            var street = Street("斜向主街", "-45,-30;40,35", 8f, true, "classical_row", 10f, 2);
            street.pathId = "diagonal_main";
            spec.streets.Add(street);
            scene.layoutSpec = spec;
            return scene;
        }

        private static SceneSpec BuildProbeScene()
        {
            var scene = new SceneSpec
            {
                sceneId = "tool_city_editor",
                sceneName = "革命广场与连续街区",
                width = 200f,
                length = 220f,
                spawnPoint = new Vector3(0f, 1.2f, -24f),
                layoutDescription = "中央广场有绿化、路灯与抗议人群；南北住宅区为欧洲古典连续街屋；北端要塞。",
            };
            var spec = new LayoutSpec { sceneId = scene.sceneId, source = "story-semantic" };
            spec.zones.Add(Zone("中央广场，有绿化、路灯和抗议人群", "square", -70, -60, 70, 60, 0.9f, "石板"));
            spec.zones.Add(Zone("北街住宅区", "residential", -100, 60, 100, 110, 0.15f, "夯土"));
            spec.zones.Add(Zone("南街住宅区", "residential", -100, -110, 100, -60, 0.15f, "夯土"));
            spec.zones.Add(Zone("西侧市场", "market", -100, -60, -70, 60, 0.6f, "泥土"));
            spec.zones.Add(Zone("东侧居住区", "residential", 70, -60, 100, 60, 0.2f, "夯土"));
            spec.streets.Add(Street("主街", "0,-100;0,100", 9f, true, "shop_row", 11f, 3));
            spec.streets.Add(Street("广场横街", "-90,0;90,0", 8f, true, "classical_row", 10f, 3));
            spec.streets.Add(Street("南街", "-90,-86;90,-86", 7f, true, "classical_row", 9f, 2));
            spec.landmarks.Add(Landmark("北端要塞", "fortress", "北街住宅区 北侧", "north", ""));
            spec.landmarks.Add(Landmark("圣马丁教堂", "church", "中央广场 北侧", "south", "去教堂的马车"));
            spec.landmarks.Add(Landmark("南城门", "gate", "南街住宅区 南侧", "south", "出城的城门"));
            spec.landmarks.Add(Landmark("市政厅", "hall", "中央广场 东侧", "east", "市政厅侧门"));
            scene.anchors.Add(new SceneAnchorSpec { anchorId = "anchor_church", displayName = "去教堂的马车", requirement = "教堂入口", timeDelta = "2h" });
            scene.anchors.Add(new SceneAnchorSpec { anchorId = "anchor_gate", displayName = "出城的城门", requirement = "城门", timeDelta = "1d" });
            scene.anchors.Add(new SceneAnchorSpec { anchorId = "anchor_hall", displayName = "市政厅侧门", requirement = "官署入口", timeDelta = "3h" });
            scene.npcs.Add(new NpcSpec { npcId = "npc_courier", displayName = "信使", tag = "信使", persona = "跑遍全城送信" });
            scene.npcs.Add(new NpcSpec { npcId = "npc_guard", displayName = "城门卫兵", tag = "士兵", persona = "守着南门" });
            scene.props.Add(new PropInteraction { propId = "prop_well", displayName = "广场水井", description = "可以打水", interactKind = "examine" });
            scene.layoutSpec = spec;
            return scene;
        }

        private static LayoutZoneSpec Zone(string name, string kind, float x1, float z1, float x2, float z2, float openness, string material)
        {
            return new LayoutZoneSpec
            {
                zoneId = "zone_" + name,
                description = name,
                kind = kind,
                openness = openness,
                material = material,
                polygon = new List<Vector2> { new Vector2(x1, z1), new Vector2(x2, z1), new Vector2(x2, z2), new Vector2(x1, z2) },
            };
        }

        private static LayoutStreetSpec Street(string name, string nodes, float width, bool both, string style, float depth, int floors)
        {
            var street = new LayoutStreetSpec
            {
                pathId = "street_" + name,
                name = name,
                width = width,
                frontageBoth = both,
                style = style,
                depth = depth,
                floors = floors,
                continuous = true,
            };
            string[] chunks = nodes.Split(';');
            for (int i = 0; i < chunks.Length; i++)
            {
                string[] pair = chunks[i].Split(',');
                if (pair.Length < 2) continue;
                float x, z;
                if (float.TryParse(pair[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                    && float.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out z)) street.nodes.Add(new Vector2(x, z));
            }
            return street;
        }

        private static LayoutLandmarkSpec Landmark(string name, string kind, string on, string facing, string anchorFor)
        {
            return new LayoutLandmarkSpec { name = name, kind = kind, on = on, facing = facing, semantic = name, anchorBinding = anchorFor };
        }
    }
}
