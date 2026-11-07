using System.Collections.Generic;
using EpicSpark.AI.StoryAgent;
using UnityEngine;

namespace EpicSpark.AI.Play
{
    /// <summary>场景内模型摆放: 模型库现有模型。</summary>
    public class PlayModelPlacement
    {
        public string modelId;          // model_library.json 中的 id
        public Vector3 pos;
        public float targetSize = 4f;   // 目标最大边长(米), 按包围盒等比缩放
        public float rotY;
    }

    /// <summary>场景内需调端点生成的模型(生成一次, 多次摆放复用)。</summary>
    public class PlayGenModel
    {
        public string key;              // 缓存键
        public string prompt;
        public Vector3 pos;
        public float targetSize = 10f;
        public float rotY;
        public int count = 1;
        public Vector3[] extraPositions; // count>1 时的额外摆放
    }

    /// <summary>土路条带(压平贴在地面上的装饰)。</summary>
    public class PlayPathStrip
    {
        public Vector3 pos;
        public Vector2 size; // x=长, y=宽
    }

    /// <summary>预制场景中的传送锚点。</summary>
    public class PlayAnchorDef
    {
        public string name, description, requirement, spendTime;
        public string targetScene;      // 目标场景名(同场景=原地时间跳转)
        public Vector3 pos;
    }

    /// <summary>预制场景: 由代码手工搭建的真实 Unity 场景(与场景Agent无关)。</summary>
    public class PlaySceneDef
    {
        public string sceneName;
        public string narrative;        // 进入时的叙述文字
        public string description;      // 给剧情Agent的目录简介
        public Vector3 playerSpawn = new Vector3(0, 1.2f, 0);
        public List<PlayModelPlacement> models = new List<PlayModelPlacement>();
        public List<PlayGenModel> genModels = new List<PlayGenModel>();
        public List<PlayAnchorDef> anchors = new List<PlayAnchorDef>();
        public List<PlayPathStrip> paths = new List<PlayPathStrip>();
        public bool paddyField;         // 场景2: 稻田水面块
        public bool riverNorth;         // 场景1: 北侧河流

        /// <summary>转为剧情Agent可用的目录项(锚点供其认知)。</summary>
        public PlotScenePreset ToCatalogEntry()
        {
            var p = new PlotScenePreset { name = sceneName, description = description };
            foreach (var a in anchors)
            {
                p.anchors.Add(new PlotAnchorPreset
                {
                    name = a.name,
                    description = a.description,
                    requirement = a.requirement,
                    spendTime = a.spendTime,
                });
            }
            return p;
        }
    }

    /// <summary>剧本: 一组预制场景 + 开场事件。</summary>
    public class PlayScript
    {
        public string name;
        public string description;
        public List<PlaySceneDef> scenes = new List<PlaySceneDef>();
    }

    /// <summary>内置剧本库。当前: 河姆渡文化(2个场景)。</summary>
    public static class PlayScripts
    {
        public static readonly List<PlayScript> All = new List<PlayScript> { Hemudu() };

        static PlayScript Hemudu()
        {
            var script = new PlayScript
            {
                name = "河姆渡文化",
                description = "约7000年前长江下游的新石器文化。干栏式建筑、稻作农业、独木舟与骨耜," +
                              "体验先民傍水而居、开垦稻田的生活。",
            };

            // ─────── 场景1: 河姆渡村落 ───────
            var village = new PlaySceneDef
            {
                sceneName = "河姆渡村落",
                narrative = "约7000年前, 姚江之畔。干栏式的木屋架在水面之上, 炊烟袅袅, 先民们正准备撑独木舟去稻田劳作。",
                description = "河姆渡先民的滨水村落。干栏式建筑、独木舟、篝火与林木, 展现新石器时代的聚居生活。",
                playerSpawn = new Vector3(0, 1.2f, 18),
                riverNorth = true,
            };
            // —— 中心广场: 水井 + 篝火圈 + 桌椅 ——
            village.models.Add(new PlayModelPlacement { modelId = "mdl_well_001", pos = new Vector3(0, 0, 0), targetSize = 3.5f });
            village.models.Add(new PlayModelPlacement { modelId = "mdl_campfire_001", pos = new Vector3(0, 0, 9), targetSize = 2.2f });
            village.models.Add(new PlayModelPlacement { modelId = "mdl_bench_001", pos = new Vector3(3, 0, 9), targetSize = 1.4f, rotY = 90 });
            village.models.Add(new PlayModelPlacement { modelId = "mdl_bench_001", pos = new Vector3(-3, 0, 9), targetSize = 1.4f, rotY = -90 });
            village.models.Add(new PlayModelPlacement { modelId = "mdl_table_001", pos = new Vector3(5, 0, 11), targetSize = 1.8f });
            village.models.Add(new PlayModelPlacement { modelId = "mdl_chair_001", pos = new Vector3(6.5f, 0, 11), targetSize = 1.1f, rotY = -90 });
            village.models.Add(new PlayModelPlacement { modelId = "mdl_bucket_001", pos = new Vector3(1.8f, 0, 0.6f), targetSize = 0.9f });
            // —— 干栏式建筑环绕广场(内置资产) ——
            village.models.Add(new PlayModelPlacement { modelId = "mdl_stilt_house_001", pos = new Vector3(-17, 0, -5), targetSize = 13, rotY = 20 });
            village.models.Add(new PlayModelPlacement { modelId = "mdl_stilt_house_001", pos = new Vector3(1, 0, -16), targetSize = 13, rotY = -8 });
            village.models.Add(new PlayModelPlacement { modelId = "mdl_stilt_house_001", pos = new Vector3(19, 0, -4), targetSize = 13, rotY = 205 });
            village.models.Add(new PlayModelPlacement { modelId = "mdl_stilt_house_001", pos = new Vector3(-8, 0, -22), targetSize = 11, rotY = 55 });
            // —— 杂物: 桶堆/推车/麻袋/招牌/火把 ——
            village.models.Add(new PlayModelPlacement { modelId = "mdl_barrel_001", pos = new Vector3(7, 0, 4), targetSize = 1.2f });
            village.models.Add(new PlayModelPlacement { modelId = "mdl_barrel_001", pos = new Vector3(8.2f, 0, 5), targetSize = 1.1f });
            village.models.Add(new PlayModelPlacement { modelId = "mdl_cart_001", pos = new Vector3(-9, 0, 12), targetSize = 4.2f, rotY = 30 });
            village.models.Add(new PlayModelPlacement { modelId = "mdl_sack_001", pos = new Vector3(-7, 0, 13.5f), targetSize = 1.4f });
            village.models.Add(new PlayModelPlacement { modelId = "mdl_sack_001", pos = new Vector3(-5.8f, 0, 12.6f), targetSize = 1.3f, rotY = 40 });
            village.models.Add(new PlayModelPlacement { modelId = "mdl_signpost_001", pos = new Vector3(3.5f, 0, 17), targetSize = 2.6f, rotY = -15 });
            village.models.Add(new PlayModelPlacement { modelId = "mdl_torch_001", pos = new Vector3(1.5f, 0, -13), targetSize = 2.2f });
            village.models.Add(new PlayModelPlacement { modelId = "mdl_torch_001", pos = new Vector3(-14, 0, -2), targetSize = 2.2f });
            village.models.Add(new PlayModelPlacement { modelId = "mdl_torch_001", pos = new Vector3(11, 0, 6), targetSize = 2.2f });
            // —— 码头: 双独木舟 ——
            village.models.Add(new PlayModelPlacement { modelId = "mdl_boat_001", pos = new Vector3(10, 0.05f, -33), targetSize = 6, rotY = 90 });
            village.models.Add(new PlayModelPlacement { modelId = "mdl_boat_001", pos = new Vector3(17, 0.05f, -32), targetSize = 6, rotY = 105 });
            // —— 栅栏圈地(南缘一排) ——
            for (int fx = -28; fx <= 28; fx += 7)
                village.models.Add(new PlayModelPlacement { modelId = "mdl_fence_001", pos = new Vector3(fx, 0, 26), targetSize = 4 });
            // —— 树林环绕(外圈) + 村内点缀 ——
            int[] treeRing = { -38, -20, 30, 44, -45, 38, 48, -30, 20, -48 };
            for (int i = 0; i < treeRing.Length; i++)
                village.models.Add(new PlayModelPlacement { modelId = "mdl_tree_001",
                    pos = new Vector3(treeRing[i], 0, i % 2 == 0 ? -28 - (i * 5) % 20 : 34 - (i * 7) % 18), targetSize = 8 + (i % 3) });
            string[] bushes = { "mdl_bush_001", "14", "-10", "mdl_bush_001", "-20", "8", "mdl_bush_001", "24", "14", "mdl_rock_001", "30", "8", "mdl_rock_001", "-26", "-12" };
            for (int i = 0; i < bushes.Length; i += 3)
                village.models.Add(new PlayModelPlacement { modelId = bushes[i],
                    pos = new Vector3(float.Parse(bushes[i + 1]), 0, float.Parse(bushes[i + 2])), targetSize = bushes[i] == "mdl_rock_001" ? 3 : 2.5f });
            // —— 土路: 出生点→广场→码头/东侧 ——
            village.paths.Add(new PlayPathStrip { pos = new Vector3(0, 0, 12), size = new Vector2(3, 22) });
            village.paths.Add(new PlayPathStrip { pos = new Vector3(0, 0, -20), size = new Vector2(3, 24) });
            village.paths.Add(new PlayPathStrip { pos = new Vector3(11, 0, 2), size = new Vector2(18, 3) });
            village.anchors.Add(new PlayAnchorDef
            {
                name = "乘独木舟前往稻田", description = "撑独木舟沿水路去稻田",
                requirement = "独木舟", spendTime = "15min",
                targetScene = "稻作农田", pos = new Vector3(10, 0, -30),
            });
            village.anchors.Add(new PlayAnchorDef
            {
                name = "屋前歇息", description = "在篝火旁歇息(时间跳转)",
                requirement = "营火", spendTime = "8h",
                targetScene = "河姆渡村落", pos = new Vector3(0, 0, 8),
            });

            // ─────── 场景2: 稻作农田 ───────
            var farm = new PlaySceneDef
            {
                sceneName = "稻作农田",
                narrative = "水网纵横的稻田。潴水育稻, 先民弯腰插秧, 远处干栏屋倒映在水面——这是世界上最早的稻作农业之一。",
                description = "河姆渡的水田景观。块块潴水稻田、草垛与农具, 展现原始稻作农业。",
                playerSpawn = new Vector3(0, 1.2f, 20),
                paddyField = true,
                riverNorth = true, // 北侧河道: 撑船回村落的锚点
            };
            // —— 六块稻田(2行x3列) —— (paddyField 由 builder 按 4 块默认布, 这里以模型点缀)
            // —— 收获物: 草垛/麻袋/推车 ——
            farm.models.Add(new PlayModelPlacement { modelId = "mdl_haystack_001", pos = new Vector3(-14, 0, 8), targetSize = 3.2f });
            farm.models.Add(new PlayModelPlacement { modelId = "mdl_haystack_001", pos = new Vector3(-19, 0, 12), targetSize = 3, rotY = 70 });
            farm.models.Add(new PlayModelPlacement { modelId = "mdl_haystack_001", pos = new Vector3(-11, 0, 13), targetSize = 2.8f, rotY = 130 });
            farm.models.Add(new PlayModelPlacement { modelId = "mdl_sack_001", pos = new Vector3(6, 0, 12), targetSize = 1.5f });
            farm.models.Add(new PlayModelPlacement { modelId = "mdl_sack_001", pos = new Vector3(8, 0, 13), targetSize = 1.5f, rotY = 30 });
            farm.models.Add(new PlayModelPlacement { modelId = "mdl_sack_001", pos = new Vector3(7, 0, 14.5f), targetSize = 1.4f, rotY = 80 });
            farm.models.Add(new PlayModelPlacement { modelId = "mdl_bucket_001", pos = new Vector3(4, 0, 13), targetSize = 0.9f });
            farm.models.Add(new PlayModelPlacement { modelId = "mdl_bucket_001", pos = new Vector3(10, 0, 11), targetSize = 0.9f });
            farm.models.Add(new PlayModelPlacement { modelId = "mdl_cart_001", pos = new Vector3(-8, 0, 16), targetSize = 4.2f, rotY = -15 });
            // —— 干栏屋 + 农舍杂物 ——
            farm.models.Add(new PlayModelPlacement { modelId = "mdl_stilt_house_001", pos = new Vector3(26, 0, -30), targetSize = 13, rotY = 45 });
            farm.models.Add(new PlayModelPlacement { modelId = "mdl_barrel_001", pos = new Vector3(22, 0, -26), targetSize = 1.2f });
            farm.models.Add(new PlayModelPlacement { modelId = "mdl_torch_001", pos = new Vector3(21, 0, -24), targetSize = 2.2f });
            // —— 栅栏与小路 ——
            for (int fz = 0; fz < 5; fz++)
                farm.models.Add(new PlayModelPlacement { modelId = "mdl_fence_001", pos = new Vector3(30, 0, -8 - fz * 6), targetSize = 4, rotY = 90 });
            farm.paths.Add(new PlayPathStrip { pos = new Vector3(0, 0, 8), size = new Vector2(3, 30) });
            farm.paths.Add(new PlayPathStrip { pos = new Vector3(-12, 0, 16), size = new Vector2(20, 3) });
            // —— 独木舟(撑船点) ——
            farm.models.Add(new PlayModelPlacement { modelId = "mdl_boat_001", pos = new Vector3(0, 0.05f, -34), targetSize = 6, rotY = 90 });
            // —— 树木点缀 ——
            farm.models.Add(new PlayModelPlacement { modelId = "mdl_tree_001", pos = new Vector3(28, 0, -14), targetSize = 8 });
            farm.models.Add(new PlayModelPlacement { modelId = "mdl_tree_001", pos = new Vector3(-30, 0, -16), targetSize = 7 });
            farm.models.Add(new PlayModelPlacement { modelId = "mdl_tree_001", pos = new Vector3(34, 0, 6), targetSize = 8, rotY = 60 });
            farm.models.Add(new PlayModelPlacement { modelId = "mdl_bush_001", pos = new Vector3(-26, 0, -8), targetSize = 2.5f });
            farm.models.Add(new PlayModelPlacement { modelId = "mdl_bush_001", pos = new Vector3(14, 0, 18), targetSize = 2.2f });
            farm.anchors.Add(new PlayAnchorDef
            {
                name = "撑船返回村落", description = "撑独木舟回村落",
                requirement = "独木舟", spendTime = "15min",
                targetScene = "河姆渡村落", pos = new Vector3(0, 0, -32),
            });
            farm.anchors.Add(new PlayAnchorDef
            {
                name = "收割归仓", description = "结束一天的劳作(时间跳转)",
                requirement = "手推车", spendTime = "2h",
                targetScene = "稻作农田", pos = new Vector3(-8, 0, 14),
            });

            script.scenes.Add(village);
            script.scenes.Add(farm);
            return script;
        }
    }
}
